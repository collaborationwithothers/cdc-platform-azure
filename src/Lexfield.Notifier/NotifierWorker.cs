using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lexfield.Notifier;

internal sealed class NotifierWorker(
    IConsumer<string, string> consumer,
    NotifierSettings settings,
    NotificationProcessor processor,
    ILogger<NotifierWorker> logger) : BackgroundService
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private readonly Dictionary<TopicPartition, PartitionState> partitions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        consumer.Subscribe(settings.Topics);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await CompleteProcessingAsync(stoppingToken);
                ResumeExpiredPartitions();

                var result = consumer.Consume(TimeSpan.FromMilliseconds(100));
                if (result is not null)
                    StartOrQueue(result, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    private void StartOrQueue(
        ConsumeResult<string, string> result,
        CancellationToken stoppingToken)
    {
        if (!partitions.TryGetValue(result.TopicPartition, out var state))
        {
            state = new PartitionState(result);
            partitions.Add(result.TopicPartition, state);
            state.Processing = ProcessWithRetryAsync(result.Message, stoppingToken);
            return;
        }

        // The consumer's position can advance while an earlier record is
        // retrying. Keep later records in order and never commit past a retry.
        state.Pending.Enqueue(result);
    }

    private async Task CompleteProcessingAsync(CancellationToken stoppingToken)
    {
        foreach (var (partition, state) in partitions.ToArray())
        {
            if (state.Paused || state.Processing is not { IsCompleted: true })
                continue;

            try
            {
                await state.Processing;
                // The offset moves only after sending and recording. A failure
                // before this point leaves the event available for redelivery.
                consumer.Commit(state.Current);
                if (state.Pending.TryDequeue(out var next))
                {
                    state.Current = next;
                    state.Processing = ProcessWithRetryAsync(next.Message, stoppingToken);
                }
                else
                {
                    partitions.Remove(partition);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Pause(partition, state, exception);
            }
        }
    }

    private void Pause(
        TopicPartition partition,
        PartitionState state,
        Exception exception)
    {
        consumer.Pause([partition]);
        consumer.Seek(new TopicPartitionOffset(partition, state.Current.Offset));
        state.Pending.Clear();
        state.Paused = true;
        state.Processing = null;
        state.ResumeAt = DateTimeOffset.UtcNow + settings.PauseDuration;

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["eventName"] = "Notifier.PartitionPaused",
            ["topic"] = partition.Topic,
            ["partition"] = partition.Partition.Value,
            ["offset"] = state.Current.Offset.Value,
            ["attempts"] = MaxAttempts
        }))
        {
            logger.LogWarning(
                exception,
                "Notifier paused a Kafka partition after processing retries were exhausted.");
        }
    }

    private void ResumeExpiredPartitions()
    {
        foreach (var (partition, state) in partitions.ToArray())
        {
            if (!state.Paused || DateTimeOffset.UtcNow < state.ResumeAt) continue;
            consumer.Resume([partition]);
            partitions.Remove(partition);
        }
    }

    private async Task ProcessWithRetryAsync(
        Message<string, string> message,
        CancellationToken stoppingToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await processor.ProcessAsync(message, stoppingToken);
                return;
            }
            catch (Exception exception) when (
                !(exception is OperationCanceledException && stoppingToken.IsCancellationRequested))
            {
                lastException = exception;
                if (attempt == MaxAttempts) break;
                await Task.Delay(RetryDelay(attempt), stoppingToken);
            }
        }

        throw new InvalidOperationException(
            $"Notifier could not process a message after {MaxAttempts} attempts.",
            lastException);
    }

    private TimeSpan RetryDelay(int failedAttempt)
    {
        var multiplier = Math.Pow(2, failedAttempt - 1);
        var ticks = settings.RetryBaseDelay.Ticks * multiplier;
        return TimeSpan.FromTicks((long)Math.Min(ticks, MaximumRetryDelay.Ticks));
    }

    private sealed class PartitionState(ConsumeResult<string, string> current)
    {
        public ConsumeResult<string, string> Current { get; set; } = current;
        public Queue<ConsumeResult<string, string>> Pending { get; } = [];
        public Task? Processing { get; set; }
        public bool Paused { get; set; }
        public DateTimeOffset ResumeAt { get; set; }
    }
}
