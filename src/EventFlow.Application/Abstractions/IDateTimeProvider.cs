namespace EventFlow.Application.Abstractions;

/// <summary>
/// Provides access to the current system time in a testable, deterministic manner.
/// </summary>
public interface IDateTimeProvider
{
    DateTimeOffset UtcNow { get; }
}
