using System.Security.Cryptography;
using System.Text;

namespace EventFlow.Api.Security;

/// <summary>
/// Validates API Keys using constant-time comparisons over SHA-256 digests to prevent side-channel timing and length attacks.
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

        byte[] providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey));
        byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));

        return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
    }
}
