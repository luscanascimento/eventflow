using System.Text.Json;
using System.Text.RegularExpressions;
using FluentValidation;

namespace EventFlow.Application.Events.Commands;

/// <summary>
/// Strict input validator for <see cref="IngestEventCommand"/> enforcing OWASP security constraints,
/// payload boundaries, and W3C standard traceparent formats.
/// </summary>
public partial class IngestEventValidator : AbstractValidator<IngestEventCommand>
{
    private const int MaxPayloadBytes = 256 * 1024; // 256 KB maximum payload size

    public IngestEventValidator()
    {
        RuleFor(x => x.TenantId)
            .NotEmpty().WithMessage("TenantId is required.")
            .Length(3, 64).WithMessage("TenantId must be between 3 and 64 characters.")
            .Must(tenant => TenantIdRegex().IsMatch(tenant))
            .WithMessage("TenantId contains invalid characters. Only alphanumeric, dashes, and underscores are allowed.");

        RuleFor(x => x.EventType)
            .NotEmpty().WithMessage("EventType is required.")
            .Length(3, 128).WithMessage("EventType must be between 3 and 128 characters.")
            .Must(type => EventTypeRegex().IsMatch(type))
            .WithMessage("EventType must follow lowercase dot-notation (e.g. 'payment.settled.v1').");

        RuleFor(x => x.Source)
            .NotEmpty().WithMessage("Source is required.")
            .Length(3, 128).WithMessage("Source must be between 3 and 128 characters.");

        RuleFor(x => x.IdempotencyKey)
            .NotEmpty().WithMessage("IdempotencyKey is required.")
            .Length(3, 128).WithMessage("IdempotencyKey must be between 3 and 128 characters.")
            .Must(key => IdempotencyKeyRegex().IsMatch(key))
            .WithMessage("IdempotencyKey contains invalid characters.");

        When(x => x.Version.HasValue, () =>
        {
            RuleFor(x => x.Version!.Value)
                .GreaterThanOrEqualTo(1)
                .WithMessage("Version must be greater than or equal to 1.");
        });

        RuleFor(x => x.Payload)
            .Must(BeValidJsonPayload)
            .WithMessage("Payload must be a valid non-empty JSON object or array.")
            .Must(BeWithinMaxPayloadSize)
            .WithMessage($"Payload size exceeds the maximum allowed limit of {MaxPayloadBytes / 1024} KB.");

        When(x => !string.IsNullOrWhiteSpace(x.TraceParent), () =>
        {
            RuleFor(x => x.TraceParent!)
                .Must(trace => TraceParentRegex().IsMatch(trace))
                .WithMessage("TraceParent must follow the W3C TraceContext format (e.g. '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01').");
        });
    }

    private static bool BeValidJsonPayload(JsonElement payload)
    {
        return payload.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
    }

    private static bool BeWithinMaxPayloadSize(JsonElement payload)
    {
        string raw = payload.GetRawText();
        return System.Text.Encoding.UTF8.GetByteCount(raw) <= MaxPayloadBytes;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TenantIdRegex();

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9_\-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex EventTypeRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.:#]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdempotencyKeyRegex();

    [GeneratedRegex(@"^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex TraceParentRegex();
}
