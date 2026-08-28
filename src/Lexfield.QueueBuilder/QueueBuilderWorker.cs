using Confluent.Kafka;
using Microsoft.Extensions.Hosting;

namespace Lexfield.QueueBuilder;

internal sealed class QueueBuilderWorker(
    IConsumer<string, string> consumer,
    QueueBuilderSettings settings,
    TransitionProjector projector) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        consumer.Subscribe(settings.Topics);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = consumer.Consume(stoppingToken);
                await projector.ApplyAsync(result.Message, stoppingToken);
                // A crash after the SQL write but before this commit redelivers
                // the event. QueueStore's version guard makes that replay a no-op.
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
