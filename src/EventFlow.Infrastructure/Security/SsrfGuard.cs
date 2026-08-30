using System.Net;
using System.Net.Sockets;

namespace EventFlow.Infrastructure.Security;

/// <summary>
/// Defensive utility protecting outgoing HTTP requests and webhooks against Server-Side Request Forgery (SSRF).
/// </summary>
public static class SsrfGuard
{
    /// <summary>
    /// Validates that a URI is safe for outbound dispatch, checking schemes and blocking private, loopback,
    /// or cloud metadata IP addresses when <paramref name="allowPrivateAddresses"/> is false.
    /// </summary>
    public static bool IsSafeTarget(Uri targetUri, bool allowPrivateAddresses = false)
    {
        if (targetUri == null || !targetUri.IsAbsoluteUri)
        {
            return false;
        }

        // 1. Only HTTP and HTTPS schemes are allowed
        if (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetUri.Host))
        {
            return false;
        }

        if (allowPrivateAddresses)
        {
            return true;
        }

        // 2. Check for explicit localhost names
        if (string.Equals(targetUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 3. Resolve host IPs and verify against private / dangerous ranges
        try
        {
            if (IPAddress.TryParse(targetUri.Host, out var directIp))
            {
                return !IsPrivateOrReserved(directIp);
            }

            var addresses = Dns.GetHostAddresses(targetUri.Host);
            if (addresses.Length == 0)
            {
                return false;
            }

            foreach (var ip in addresses)
            {
                if (IsPrivateOrReserved(ip))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // DNS resolution failure or network error fails safely (closed)
            return false;
        }
    }

    /// <summary>
    /// Evaluates if an IP address belongs to RFC 1918 private ranges, loopback, link-local, or cloud metadata ranges.
    /// </summary>
    public static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast;
        }

        byte[] bytes = ip.GetAddressBytes();

        // 0.0.0.0/8 (Broadcast/Current network)
        if (bytes[0] == 0)
        {
            return true;
        }

        // 10.0.0.0/8 (RFC 1918 Private)
        if (bytes[0] == 10)
        {
            return true;
        }

        // 127.0.0.0/8 (Loopback)
        if (bytes[0] == 127)
        {
            return true;
        }

        // 169.254.0.0/16 (Link-Local & Cloud Metadata e.g. AWS/Azure 169.254.169.254)
        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return true;
        }

        // 172.16.0.0/12 (RFC 1918 Private)
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            return true;
        }

        // 192.168.0.0/16 (RFC 1918 Private)
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return true;
        }

        // 224.0.0.0/4 (Multicast)
        if (bytes[0] >= 224)
        {
            return true;
        }

        return false;
    }
}
