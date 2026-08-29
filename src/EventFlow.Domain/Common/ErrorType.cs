namespace EventFlow.Domain.Common;

/// <summary>
/// Categorization of domain and application errors for explicit HTTP and operational mapping.
/// </summary>
public enum ErrorType
{
    Failure = 0,
    Validation = 1,
    NotFound = 2,
    Conflict = 3,
    Unauthorized = 4,
    Forbidden = 5
}
