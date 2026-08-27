using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Lexfield.Contracts;
using Lexfield.QueueStore;
using Microsoft.Extensions.Logging;
using ContractHeaders = Lexfield.Contracts.Headers;
using KafkaHeaders = Confluent.Kafka.Headers;

namespace Lexfield.QueueBuilder;

internal sealed class TransitionProjector(
    QueueStateStore store,
    ActivitySource activitySource,
    ILogger<TransitionProjector> logger)
{
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
        var applied = await store.ApplyAsync(new QueueStateUpdate(
            transition.TenantId,
            taskEvent.TaskId,
            taskEvent.To,
            taskEvent.Version,
            taskEvent.TeamId,
            taskEvent.AssigneeId), cancellationToken);
        Log(
            applied ? "QueueBuilder.EventApplied" : "QueueBuilder.DuplicateSkipped",
            transition.TenantId,
            taskEvent,
            applied
                ? "QueueBuilder applied the workflow transition to QueueState, its work-queue projection."
                : "QueueBuilder skipped a workflow transition whose version was already stored.");
    }

    private void Log(string eventName, string tenantId, TransitionEvent taskEvent, string message)
    {
        try
        {
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["eventName"] = eventName,
                ["tenantId"] = tenantId,
                ["taskId"] = taskEvent.TaskId,
                ["version"] = taskEvent.Version
            })) logger.LogInformation(message);
        }
        catch
        {
        }
    }

}

internal sealed record DecodedTransition(string TenantId, TransitionEvent Event, ActivityContext? Parent);

internal static class TransitionMessageDecoder
{
    public static DecodedTransition Decode(Message<string, string> message)
    {
        var tenantId = RequiredHeader(message.Headers, ContractHeaders.TenantId);
        var taskEvent = JsonSerializer.Deserialize<TransitionEvent>(message.Value)
            ?? throw new JsonException("The transition event value was JSON null.");
        var traceParent = OptionalHeader(message.Headers, ContractHeaders.TraceParent);
        ActivityContext? parent = traceParent is not null &&
            ActivityContext.TryParse(traceParent, null, true, out var parsed) ? parsed : null;
        return new DecodedTransition(tenantId, taskEvent, parent);
    }

    private static string RequiredHeader(KafkaHeaders headers, string name) =>
        OptionalHeader(headers, name)
        ?? throw new InvalidDataException($"The Kafka message is missing required header '{name}'.");

    private static string? OptionalHeader(KafkaHeaders headers, string name) =>
        headers.TryGetLastBytes(name, out var value) && value is { Length: > 0 }
            ? Encoding.UTF8.GetString(value) : null;
}
