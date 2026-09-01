using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Lexfield.Contracts;
using Lexfield.QueueStore;
using Microsoft.Extensions.Logging;
using ContractHeaders = Lexfield.Contracts.Headers;
using KafkaHeaders = Confluent.Kafka.Headers;

namespace Lexfield.Notifier;

public interface ISender
{
    Task SendAsync(
        string tenantId,
        TransitionEvent taskEvent,
        CancellationToken cancellationToken = default);
}

public sealed class LogSender(ILogger<LogSender> logger) : ISender
{
    public Task SendAsync(
        string tenantId,
        TransitionEvent taskEvent,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Notifier logged delivery for tenant {TenantId}, task {TaskId}, " +
            "from {From}, to {To}, assignee {AssigneeId}.",
            tenantId,
            taskEvent.TaskId,
            taskEvent.From,
            taskEvent.To,
            taskEvent.AssigneeId);
        return Task.CompletedTask;
    }
}

internal sealed class NotificationProcessor(
    SentNotificationStore store,
    ISender sender,
    Meter meter,
    ActivitySource activitySource,
    ILogger<NotificationProcessor> logger)
{
    private readonly Counter<long> _sentCounter =
        meter.CreateCounter<long>("notifier.sent");
    private readonly Counter<long> _duplicateCounter =
        meter.CreateCounter<long>("notifier.skipped_duplicate");
    private readonly Counter<long> _conflictCounter =
        meter.CreateCounter<long>("notifier.record_conflict");

    public async Task ProcessAsync(
        Message<string, string> message,
        CancellationToken cancellationToken)
    {
        var transition = TransitionMessageDecoder.Decode(message);
        var taskEvent = transition.Event;
        using var activity = transition.Parent is { } parent
            ? activitySource.StartActivity("Notifier.Consume", ActivityKind.Consumer, parent)
            : activitySource.StartActivity("Notifier.Consume", ActivityKind.Consumer);
        activity?.SetTag("tenantId", transition.TenantId);
        activity?.SetTag("taskId", taskEvent.TaskId);
        activity?.SetTag("version", taskEvent.Version);

        Log(
            "Notifier.EventReceived",
            transition.TenantId,
            taskEvent,
            "Notifier received a workflow transition from Kafka, a named stream of messages.");

        if (await store.HasBeenSentAsync(
                transition.TenantId,
                taskEvent.TaskId,
                taskEvent.Version,
                cancellationToken))
        {
            _duplicateCounter.Add(1, Tags(transition.TenantId));
            Log(
                "Notifier.DuplicateSkipped",
                transition.TenantId,
                taskEvent,
                "Notifier skipped a workflow transition whose notification was already recorded.");
            return;
        }

        // ADR-008 deliberately sends before recording so a crash redelivers
        // rather than dropping a notification that was never delivered.
        await sender.SendAsync(transition.TenantId, taskEvent, cancellationToken);
        _sentCounter.Add(1, Tags(transition.TenantId));
        Log(
            "Notifier.NotificationSent",
            transition.TenantId,
            taskEvent,
            "Notifier sent the workflow transition through its delivery interface.");

        var recordResult = await store.TryRecordAsync(
            transition.TenantId,
            taskEvent.TaskId,
            taskEvent.Version,
            cancellationToken);
        if (recordResult == SentNotificationRecordResult.AlreadyRecorded)
        {
            _conflictCounter.Add(1, Tags(transition.TenantId));
            Log(
                "Notifier.SendRecorded",
                transition.TenantId,
                taskEvent,
                "Notifier confirmed the notification record after another instance inserted it concurrently.");
            return;
        }

        Log(
            "Notifier.SendRecorded",
            transition.TenantId,
            taskEvent,
            "Notifier inserted the notification record after sending.");
    }

    private void Log(
        string eventName,
        string tenantId,
        TransitionEvent taskEvent,
        string message)
    {
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["eventName"] = eventName,
            ["tenantId"] = tenantId,
            ["taskId"] = taskEvent.TaskId,
            ["version"] = taskEvent.Version
        }))
        {
            logger.LogInformation(message);
        }
    }

    private static TagList Tags(string tenantId) => new()
    {
        { "tenantId", tenantId }
    };
}

internal sealed record DecodedTransition(
    string TenantId,
    TransitionEvent Event,
    ActivityContext? Parent);

internal static class TransitionMessageDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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

    private static string RequiredHeader(KafkaHeaders headers, string name)
    {
        if (!headers.TryGetLastBytes(name, out var value) || value is not { Length: > 0 })
        {
            throw new InvalidDataException(
                $"The Kafka message is missing required header '{name}'.");
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"The Kafka message header '{name}' is not valid UTF-8.", exception);
        }

        return string.IsNullOrWhiteSpace(decoded)
            ? throw new InvalidDataException(
                $"The Kafka message header '{name}' must not be blank.")
            : decoded;
    }

    private static string? OptionalHeader(KafkaHeaders headers, string name) =>
        headers.TryGetLastBytes(name, out var value) && value is { Length: > 0 }
            ? Encoding.UTF8.GetString(value) : null;
}
