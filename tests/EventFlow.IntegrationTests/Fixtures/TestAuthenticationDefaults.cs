namespace EventFlow.IntegrationTests.Fixtures;

public static class TestAuthenticationDefaults
{
    public const string ValidApiKey = "test-api-key-eventflow-super-secret-12345";
    public const string InvalidApiKey = "invalid-api-key-wrong-secret";
    public const string ApiKeyHeaderName = "X-Api-Key";
    public const string TenantIdHeaderName = "X-Tenant-Id";
    public const string DefaultTenantId = "tenant_acme_corp";
}
