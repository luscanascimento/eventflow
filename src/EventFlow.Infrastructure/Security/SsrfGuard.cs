using System.Net;
using System.Net.Sockets;
using System.Security;

namespace EventFlow.Infrastructure.Security;

/// <summary>
/// Defensive utility protecting outgoing HTTP requests and webhooks against Server-Side Request Forgery (SSRF)
/// and DNS rebinding attacks.
/// </summary>
public static class SsrfGuard
{
    /// <summary>
    /// Validates that a URI is safe for outbound dispatch, checking schemes, blocking private/reserved IP ranges,
    /// and verifying against configurable allowlists.
    /// </summary>
    public static bool IsSafeTarget(Uri targetUri, SsrfOptions? options = null)
    {
        if (targetUri == null || !targetUri.IsAbsoluteUri)
        {
            return false;
        }

        // 1. Only HTTP and HTTPS schemes are permitted
        if (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetUri.Host))
        {
            return false;
        }

        // 2. Explicit localhost checks
        if (string.Equals(targetUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            targetUri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            targetUri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            targetUri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return options?.AllowedHosts.Contains(targetUri.Host) == true || (options?.AllowPrivateNetworks == true);
        }

        if (options?.AllowedHosts.Contains(targetUri.Host) == true)
        {
            return true;
        }

        // 3. Resolve host IPs and verify against private / dangerous ranges
        try
        {
            if (IPAddress.TryParse(targetUri.Host, out var directIp))
            {
                return IsIpAllowed(directIp, targetUri.Host, options);
            }

            var addresses = Dns.GetHostAddresses(targetUri.Host);
            if (addresses.Length == 0)
            {
                return false;
            }

            foreach (var ip in addresses)
            {
                if (!IsIpAllowed(ip, targetUri.Host, options))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // DNS resolution failure fails closed
            return false;
        }
    }

    /// <summary>
    /// Checks whether an IP address is permitted based on SSRF defense rules and optional allowlists.
    /// </summary>
    public static bool IsIpAllowed(IPAddress ip, string host, SsrfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (options?.AllowedHosts.Contains(host) == true ||
            options?.AllowedHosts.Contains(ip.ToString()) == true)
        {
            return true;
        }

        if (options?.AllowPrivateNetworks == true)
        {
            return true;
        }

        return !IsPrivateOrReserved(ip);
    }

    /// <summary>
    /// Evaluates if an IP address belongs to RFC 1918 private ranges, loopback, link-local, or cloud metadata ranges.
    /// </summary>
    public static bool IsPrivateOrReserved(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv6 link-local (fe80::/10), site-local, multicast, unique local (fc00::/7)
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            {
                return true;
            }

            byte[] v6Bytes = ip.GetAddressBytes();
            // Unique local address fc00::/7 (first byte 0xfc or 0xfd)
            if ((v6Bytes[0] & 0xfe) == 0xfc)
            {
                return true;
            }

            // IPv4-mapped IPv6 address (::ffff:x.x.x.x)
            if (ip.IsIPv4MappedToIPv6)
            {
                return IsPrivateOrReserved(ip.MapToIPv4());
            }

            // IPv4-compatible IPv6 address (::x.x.x.x where first 12 bytes are 0)
            bool isIpv4Compatible = true;
            for (int i = 0; i < 12; i++)
            {
                if (v6Bytes[i] != 0)
                {
                    isIpv4Compatible = false;
                    break;
                }
            }

            if (isIpv4Compatible)
            {
                byte[] v4Bytes = new byte[4];
                Array.Copy(v6Bytes, 12, v4Bytes, 0, 4);
                return IsPrivateOrReserved(new IPAddress(v4Bytes));
            }

            return false;
        }

        byte[] bytes = ip.GetAddressBytes();

        // 0.0.0.0/8 (Current network)
        if (bytes[0] == 0)
        {
            return true;
        }

        // 10.0.0.0/8 (RFC 1918 Private)
        if (bytes[0] == 10)
        {
            return true;
        }

        // 100.64.0.0/10 (RFC 6598 Shared Address Space / CGNAT & Alibaba Cloud Metadata 100.100.100.200)
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
        {
            return true;
        }

        // 127.0.0.0/8 (Loopback)
        if (bytes[0] == 127)
        {
            return true;
        }

        // 169.254.0.0/16 (Link-Local & Cloud Metadata e.g. AWS/GCP/Azure 169.254.169.254)
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }

        // 172.16.0.0/12 (RFC 1918 Private: 172.16.0.0 - 172.31.255.255)
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            return true;
        }

        // 192.168.0.0/16 (RFC 1918 Private)
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return true;
        }

        // 198.18.0.0/15 (RFC 2544 Benchmarking)
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
        {
            return true;
        }

        // 224.0.0.0/4 (Multicast) & 240.0.0.0/4 (Reserved)
        if (bytes[0] >= 224)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates a hardened SocketsHttpHandler configured to validate resolved socket IP addresses
    /// at connect time to prevent DNS rebinding attacks and disallow automatic open redirects.
    /// </summary>
    public static SocketsHttpHandler CreateSafeSocketsHttpHandler(SsrfOptions? options = null)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                var entry = await Dns.GetHostEntryAsync(host, cancellationToken);
                var validIp = entry.AddressList.FirstOrDefault(ip => IsIpAllowed(ip, host, options));

                if (validIp == null)
                {
                    throw new SecurityException($"Connection to destination '{SanitizeUrlForLogging(new Uri($"http://{host}"))}' was blocked by SSRF defense policies.");
                }

                var socket = new Socket(validIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                try
                {
                    await socket.ConnectAsync(new IPEndPoint(validIp, port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    /// <summary>
    /// Sanitizes a URL for structured logging by stripping query strings and user credentials.
    /// </summary>
    public static string SanitizeUrlForLogging(Uri uri)
    {
        if (uri == null)
        {
            return string.Empty;
        }

        try
        {
            var builder = new UriBuilder(uri)
            {
                Query = string.Empty,
                UserName = string.Empty,
                Password = string.Empty
            };
            return builder.Uri.ToString();
        }
        catch
        {
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";
        }
    }
}
