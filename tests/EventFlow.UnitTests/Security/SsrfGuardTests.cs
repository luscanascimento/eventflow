using System.Net;
using EventFlow.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace EventFlow.UnitTests.Security;

public class SsrfGuardTests
{
    [Theory]
    [InlineData("https://8.8.8.8/events")]
    [InlineData("https://1.1.1.1/post")]
    [InlineData("http://93.184.216.34/webhook")]
    public void IsSafeTarget_WhenPublicHttpUrl_ShouldReturnTrue(string url)
    {
        bool isSafe = SsrfGuard.IsSafeTarget(new Uri(url));

        isSafe.Should().BeTrue();
    }

    [Theory]
    [InlineData("ftp://ftp.example.com/file.txt")]
    [InlineData("file:///etc/shadow")]
    [InlineData("gopher://gopher.example.com")]
    [InlineData("ldap://ldap.example.com")]
    public void IsSafeTarget_WhenInvalidScheme_ShouldReturnFalse(string url)
    {
        bool isSafe = SsrfGuard.IsSafeTarget(new Uri(url));

        isSafe.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://localhost/webhook")]
    [InlineData("http://127.0.0.1/api")]
    [InlineData("http://0.0.0.0/api")]
    [InlineData("http://[::]/api")]
    [InlineData("http://[::1]/api")]
    [InlineData("http://[::127.0.0.1]/api")]
    [InlineData("http://10.0.0.1/admin")]
    [InlineData("http://192.168.1.1/internal")]
    [InlineData("http://172.16.0.1/secrets")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.100.100.200/latest/meta-data/")]
    [InlineData("http://198.18.0.1/benchmark")]
    public void IsSafeTarget_WhenPrivateOrLoopback_ShouldReturnFalseByDefault(string url)
    {
        bool isSafe = SsrfGuard.IsSafeTarget(new Uri(url));

        isSafe.Should().BeFalse();
    }

    [Fact]
    public void IsSafeTarget_WhenExplicitlyAllowedInSsrfOptions_ShouldReturnTrue()
    {
        var options = new SsrfOptions
        {
            AllowedHosts = { "10.0.0.1", "internal.corp.local" }
        };

        bool isAllowed = SsrfGuard.IsSafeTarget(new Uri("http://10.0.0.1/webhook"), options);
        bool isHostAllowed = SsrfGuard.IsSafeTarget(new Uri("http://internal.corp.local/webhook"), options);

        isAllowed.Should().BeTrue();
        isHostAllowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("172.16.5.10", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("142.250.190.46", false)]
    public void IsPrivateOrReserved_ShouldEvaluateAddressAccurately(string ipString, bool expectedPrivate)
    {
        var ip = IPAddress.Parse(ipString);

        bool isPrivate = SsrfGuard.IsPrivateOrReserved(ip);

        isPrivate.Should().Be(expectedPrivate);
    }
}
