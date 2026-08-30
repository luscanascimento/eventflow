using System.Diagnostics;
using EventFlow.Domain.Diagnostics;
using FluentAssertions;
using Xunit;

namespace EventFlow.UnitTests.Diagnostics;

public class EventFlowDiagnosticsTests
{
    [Fact]
    public void Diagnostics_ShouldHaveValidNamesAndInstruments()
    {
        EventFlowDiagnostics.DiagnosticSourceName.Should().Be("EventFlow.Engine");
        EventFlowDiagnostics.ActivitySource.Name.Should().Be("EventFlow.Engine");
        EventFlowDiagnostics.Meter.Name.Should().Be("EventFlow.Engine");

        EventFlowDiagnostics.EventsIngested.Should().NotBeNull();
        EventFlowDiagnostics.EventsProcessed.Should().NotBeNull();
        EventFlowDiagnostics.EventProcessingDuration.Should().NotBeNull();
        EventFlowDiagnostics.OutboxDispatched.Should().NotBeNull();
        EventFlowDiagnostics.IdempotencyHits.Should().NotBeNull();
        EventFlowDiagnostics.DlqReplayed.Should().NotBeNull();
        EventFlowDiagnostics.DlqPurged.Should().NotBeNull();
    }

    [Fact]
    public void ActivitySource_WhenListenerAttached_ShouldCreateActivityWithTags()
    {
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == EventFlowDiagnostics.DiagnosticSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };

        ActivitySource.AddActivityListener(activityListener);

        using var activity = EventFlowDiagnostics.ActivitySource.StartActivity("EventFlow.UnitTestSpan");

        activity.Should().NotBeNull();
        activity!.SetTag("test.tag", "unit_test_value");
        activity.SetStatus(ActivityStatusCode.Ok);

        activity.Tags.Should().Contain(t => t.Key == "test.tag" && t.Value == "unit_test_value");
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }
}
