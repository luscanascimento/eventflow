using Xunit;

namespace EventFlow.IntegrationTests.Fixtures;

/// <summary>
/// Custom xUnit <see cref="FactAttribute"/> that skips test execution when Docker daemon is not active on the host machine.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerTestcontainersFixture.IsDockerRunning)
        {
            Skip = "Docker daemon is not running on this host. Testcontainers integration test skipped.";
        }
    }
}
