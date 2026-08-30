using System.Security.Cryptography;
using System.Text;

namespace EventFlow.Api.Security;

/// <summary>
/// Validates API Keys using constant-time comparisons to prevent side-channel timing attacks.
/// </summary>
public interface IApiKeyValidator
{
    /// <summary>
    /// Compares the provided API key with the expected key using constant-time evaluation.
    /// </summary>
    bool ValidateApiKey(string? providedKey, string expectedKey);
}

/// <inheritdoc cref="IApiKeyValidator"/>
public sealed class ApiKeyValidator : IApiKeyValidator
{
    public bool ValidateApiKey(string? providedKey, string expectedKey)
    {
        if (string.IsNullOrWhiteSpace(providedKey) || string.IsNullOrWhiteSpace(expectedKey))
        {
            return false;
        }

        byte[] providedBytes = Encoding.UTF8.GetBytes(providedKey);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedKey);

        if (providedBytes.Length != expectedBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
