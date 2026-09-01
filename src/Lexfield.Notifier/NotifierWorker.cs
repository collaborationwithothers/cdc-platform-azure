using Confluent.Kafka;
using Microsoft.Extensions.Hosting;

namespace Lexfield.Notifier;

internal sealed class NotifierWorker(
    IConsumer<string, string> consumer,
    NotifierSettings settings,
    NotificationProcessor processor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        consumer.Subscribe(settings.Topics);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = consumer.Consume(stoppingToken);
                await processor.ProcessAsync(result.Message, stoppingToken);
                // The offset moves only after sending and recording. A failure
                // before this point leaves the event available for redelivery.
                consumer.Commit(result);
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
}
