using EventFlow.Api.Security;
using FluentAssertions;
using Xunit;

namespace EventFlow.UnitTests.Security;

public class ApiKeyValidatorTests
{
    private readonly ApiKeyValidator _sut = new();

    [Fact]
    public void ValidateApiKey_WhenKeysMatchExactly_ShouldReturnTrue()
    {
        string expected = "ef_live_secret_key_1234567890abcdef";
        string provided = "ef_live_secret_key_1234567890abcdef";

        bool isValid = _sut.ValidateApiKey(provided, expected);

        isValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("wrong_key", "expected_key")]
    [InlineData("almost_matching_key_1", "almost_matching_key_2")]
    [InlineData("", "expected_key")]
    [InlineData(null, "expected_key")]
    [InlineData("key", "")]
    public void ValidateApiKey_WhenKeysDoNotMatchOrEmpty_ShouldReturnFalse(string? provided, string expected)
    {
        bool isValid = _sut.ValidateApiKey(provided, expected);

        isValid.Should().BeFalse();
    }
}
