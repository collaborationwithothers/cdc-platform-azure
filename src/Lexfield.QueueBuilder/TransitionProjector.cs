using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Lexfield.Contracts;
using Lexfield.QueueBuilder.Gaps;
using Lexfield.QueueStore;
using Microsoft.Extensions.Logging;
using ContractHeaders = Lexfield.Contracts.Headers;
using KafkaHeaders = Confluent.Kafka.Headers;

namespace Lexfield.QueueBuilder;

internal sealed class TransitionProjector(
    QueueStateStore store,
    IGapDetector gapDetector,
    Meter meter,
    ActivitySource activitySource,
    ILogger<TransitionProjector> logger)
{
    private readonly Counter<long> _gapCounter =
        meter.CreateCounter<long>("QueueBuilder.GapDetected");
    private readonly Counter<long> _headLossCounter =
        meter.CreateCounter<long>("QueueBuilder.HeadLossDetected");

    public async Task ApplyAsync(Message<string, string> message, CancellationToken cancellationToken)
    {
        var transition = TransitionMessageDecoder.Decode(message);
        var taskEvent = transition.Event;
        using var activity = transition.Parent is { } parent
            ? activitySource.StartActivity("QueueBuilder.Consume", ActivityKind.Consumer, parent)
            : activitySource.StartActivity("QueueBuilder.Consume", ActivityKind.Consumer);
        activity?.SetTag("tenantId", transition.TenantId);
        activity?.SetTag("taskId", taskEvent.TaskId);
        activity?.SetTag("version", taskEvent.Version);

        Log("QueueBuilder.EventReceived", transition.TenantId, taskEvent,
            "QueueBuilder received a workflow transition from Kafka, a named stream of messages.");
        // Kafka partition ownership and the worker's single consume loop serialize
        // QueueBuilder writes for one task today. A future writer outside that
        // ownership must revisit this read-classify-write boundary; the guarded
        // upsert prevents version regression but does not make classification atomic.
        var stored = await store.GetAsync(
            transition.TenantId, taskEvent.TaskId, cancellationToken);
        var gap = gapDetector.Detect(stored?.Version, taskEvent.Version);
        var applied = await store.ApplyAsync(new QueueStateUpdate(
            transition.TenantId,
            taskEvent.TaskId,
            taskEvent.To,
            taskEvent.Version,
            taskEvent.TeamId,
            taskEvent.AssigneeId), cancellationToken);
        if (applied)
            RecordGap(gap, transition.TenantId, taskEvent, stored?.Version);
        Log(
            applied ? "QueueBuilder.EventApplied" : "QueueBuilder.DuplicateSkipped",
            transition.TenantId,
            taskEvent,
            applied
                ? "QueueBuilder applied the workflow transition to QueueState, its work-queue projection."
                : "QueueBuilder skipped a workflow transition whose version was already stored.");
    }

    private void RecordGap(
        GapKind gap,
        string tenantId,
        TransitionEvent taskEvent,
        int? storedVersion)
    {
        if (gap == GapKind.None) return;

        var tags = new TagList
        {
            { "tenantId", tenantId }
        };
        var eventName = gap == GapKind.Jump
            ? "QueueBuilder.GapDetected"
            : "QueueBuilder.HeadLossDetected";
        if (gap == GapKind.Jump) _gapCounter.Add(1, tags);
        else _headLossCounter.Add(1, tags);

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["eventName"] = eventName,
            ["tenantId"] = tenantId,
            ["taskId"] = taskEvent.TaskId,
            ["version"] = taskEvent.Version,
            ["storedVersion"] = storedVersion
        }))
        {
            logger.LogWarning(gap == GapKind.Jump
                ? "QueueBuilder detected missing versions between the stored and incoming workflow transitions for this task."
                : "QueueBuilder first observed this task above version 1, so its initial workflow transitions are missing.");
        }
    }

    private void Log(string eventName, string tenantId, TransitionEvent taskEvent, string message)
    {
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["eventName"] = eventName,
            ["tenantId"] = tenantId,
            ["taskId"] = taskEvent.TaskId,
            ["version"] = taskEvent.Version
        })) logger.LogInformation(message);
    }
}

internal sealed record DecodedTransition(string TenantId, TransitionEvent Event, ActivityContext? Parent);

internal static class TransitionMessageDecoder
{
    public static DecodedTransition Decode(Message<string, string> message)
    {
        message.Headers.TryGetLastBytes(ContractHeaders.TenantId, out var tenantHeader);
        var tenantId = TenantHeader.Decode(tenantHeader);
        var taskEvent = JsonSerializer.Deserialize<TransitionEvent>(message.Value)
            ?? throw new JsonException("The transition event value was JSON null.");
        var traceParent = OptionalHeader(message.Headers, ContractHeaders.TraceParent);
        ActivityContext? parent = traceParent is not null &&
            ActivityContext.TryParse(traceParent, null, true, out var parsed) ? parsed : null;
        return new DecodedTransition(tenantId, taskEvent, parent);
    }

    private static string? OptionalHeader(KafkaHeaders headers, string name) =>
        headers.TryGetLastBytes(name, out var value) && value is { Length: > 0 }
            ? Encoding.UTF8.GetString(value) : null;
}
