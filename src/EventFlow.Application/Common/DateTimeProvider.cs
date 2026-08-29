using EventFlow.Application.Abstractions;

namespace EventFlow.Application.Common;

/// <summary>
/// Default system implementation of <see cref="IDateTimeProvider"/>.
/// </summary>
public sealed class DateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
