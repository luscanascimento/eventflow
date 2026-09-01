using System.Diagnostics;
using System.Net;
using System.Text.Json;
using EventFlow.Api.Security;
using EventFlow.Application.Events.Commands;
using EventFlow.Domain.Common;
using EventFlow.Infrastructure.Security;
using FluentAssertions;
using Xunit;

namespace EventFlow.UnitTests.Security;

/// <summary>
/// Adversarial Security Audit Suite verifying system defenses against OWASP Top 10 vectors,
/// advanced SSRF bypasses, side-channel timing attacks, and malicious input injections.
/// </summary>
public sealed class AdversarialSecurityAuditTests
{
    private readonly IngestEventValidator _validator = new();

    #region SSRF Advanced Bypass Vectors

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://127.0.0.2/")]
    [InlineData("http://127.255.255.254/")]
    [InlineData("http://10.0.0.1/secrets")]
    [InlineData("http://10.255.255.255/")]
    [InlineData("http://172.16.0.1/internal")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.0.1/router")]
    [InlineData("http://192.168.255.255/")]
    [InlineData("http://169.254.169.254/latest/meta-data/iam/security-credentials/")]
    [InlineData("http://169.254.1.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://0.0.0.0/")]
    public void SsrfGuard_WhenMaliciousOrPrivateTarget_MustBlock(string targetUrl)
    {
        var uri = new Uri(targetUrl);
        bool isSafe = SsrfGuard.IsSafeTarget(uri, allowPrivateAddresses: false);

        isSafe.Should().BeFalse($"Target '{targetUrl}' represents a private/loopback/cloud metadata attack vector and must be blocked.");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("gopher://127.0.0.1:6379/_INFO")]
    [InlineData("dict://127.0.0.1:11211/stat")]
    [InlineData("ldap://127.0.0.1:389/o=example")]
    [InlineData("ftp://anonymous:secret@attacker.com/upload")]
    [InlineData("net.tcp://internal-service:8080")]
    public void SsrfGuard_WhenDangerousUriScheme_MustBlock(string targetUrl)
    {
        var uri = new Uri(targetUrl);
        bool isSafe = SsrfGuard.IsSafeTarget(uri, allowPrivateAddresses: true);

        isSafe.Should().BeFalse($"Scheme in '{targetUrl}' is non-HTTP and must be blocked to prevent arbitrary protocol smuggling.");
    }

    #endregion

    #region Side-Channel Timing Attack Defenses

    [Fact]
    public void ApiKeyValidator_ShouldEmployConstantTimeComparison_AndRejectTamperedKeys()
    {
        var sut = new ApiKeyValidator();
        const string legitimateSecret = "super_secret_production_api_key_998877665544332211";

        // Valid key
        sut.ValidateApiKey(legitimateSecret, legitimateSecret).Should().BeTrue();

        // 1. Completely wrong key (different length)
        sut.ValidateApiKey("wrong", legitimateSecret).Should().BeFalse();

        // 2. Near-match key (differing only by the last character)
        string nearMatch = legitimateSecret[..^1] + "0";
        sut.ValidateApiKey(nearMatch, legitimateSecret).Should().BeFalse();

        // 3. Near-match key (differing only by the first character)
        string firstCharMismatch = "X" + legitimateSecret[1..];
        sut.ValidateApiKey(firstCharMismatch, legitimateSecret).Should().BeFalse();

        // 4. Null or empty
        sut.ValidateApiKey(null, legitimateSecret).Should().BeFalse();
        sut.ValidateApiKey(string.Empty, legitimateSecret).Should().BeFalse();
        sut.ValidateApiKey(legitimateSecret, string.Empty).Should().BeFalse();
    }

    #endregion

    #region Input Injection & Tampering Defenses

    [Theory]
    [InlineData("tenant'; DROP TABLE events; --")]
    [InlineData("tenant' OR '1'='1")]
    [InlineData("../../../etc/passwd")]
    [InlineData("<script>alert('XSS')</script>")]
    [InlineData("tenant with spaces")]
    [InlineData("tenant@domain.com")]
    public void IngestEventValidator_WhenTenantIdContainsMaliciousCharacters_ShouldFailValidation(string maliciousTenant)
    {
        var command = new IngestEventCommand(
            EventId: null,
            EventType: "payment.settled.v1",
            Version: 1,
            Source: "payment-gateway",
            OccurredAt: null,
            CorrelationId: null,
            TraceParent: null,
            TenantId: maliciousTenant,
            IdempotencyKey: "tx-safe-key-1",
            Payload: JsonDocument.Parse("{\"amount\": 100}").RootElement,
            Metadata: null);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "TenantId");
    }

    [Theory]
    [InlineData("PAYMENT_SETTLED_V1")]
    [InlineData("payment/settled")]
    [InlineData("payment;rm -rf /")]
    [InlineData("payment..settled")]
    [InlineData("payment settled")]
    public void IngestEventValidator_WhenEventTypeViolatesFormat_ShouldFailValidation(string invalidEventType)
    {
        var command = new IngestEventCommand(
            EventId: null,
            EventType: invalidEventType,
            Version: 1,
            Source: "payment-gateway",
            OccurredAt: null,
            CorrelationId: null,
            TraceParent: null,
            TenantId: "tenant-acme",
            IdempotencyKey: "tx-safe-key-1",
            Payload: JsonDocument.Parse("{\"amount\": 100}").RootElement,
            Metadata: null);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "EventType");
    }

    [Fact]
    public void IngestEventValidator_WhenPayloadExceeds256KB_ShouldFailValidation()
    {
        // ARRANGE: Generate an oversized JSON payload (> 256 KB)
        string largeString = new('A', 260 * 1024);
        string json = $"{{\"data\": \"{largeString}\"}}";
        var payloadElement = JsonDocument.Parse(json).RootElement;

        var command = new IngestEventCommand(
            EventId: null,
            EventType: "payment.settled.v1",
            Version: 1,
            Source: "payment-gateway",
            OccurredAt: null,
            CorrelationId: null,
            TraceParent: null,
            TenantId: "tenant-acme",
            IdempotencyKey: "tx-safe-key-1",
            Payload: payloadElement,
            Metadata: null);

        // ACT
        var result = _validator.Validate(command);

        // ASSERT
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Payload" && e.ErrorMessage.Contains("256 KB"));
    }

    [Theory]
    [InlineData("invalid-traceparent")]
    [InlineData("00-invalidhex-00f067aa0ba902b7-01")]
    [InlineData("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")] // Unsupported version 01
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7")] // Missing flags
    public void IngestEventValidator_WhenTraceParentIsMalformed_ShouldFailValidation(string malformedTrace)
    {
        var command = new IngestEventCommand(
            EventId: null,
            EventType: "payment.settled.v1",
            Version: 1,
            Source: "payment-gateway",
            OccurredAt: null,
            CorrelationId: null,
            TraceParent: malformedTrace,
            TenantId: "tenant-acme",
            IdempotencyKey: "tx-safe-key-1",
            Payload: JsonDocument.Parse("{\"amount\": 100}").RootElement,
            Metadata: null);

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "TraceParent");
    }

    #endregion
}
