using Confluent.Kafka;
using Lexfield.Observability;
using Lexfield.QueueStore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lexfield.QueueBuilder;

public static class QueueBuilderHostExtensions
{
    public static IHostApplicationBuilder AddQueueBuilder(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddLexfieldObservability("QueueBuilder");
        var settings = QueueBuilderSettings.From(builder.Configuration);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(new QueueStateStore(settings.QueueStoreConnectionString));
        builder.Services.AddSingleton<IConsumer<string, string>>(_ =>
            new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = settings.BootstrapServers,
                GroupId = "queue-builder",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false
            }).Build());
        builder.Services.AddSingleton<TransitionProjector>();
        builder.Services.AddHostedService<QueueBuilderWorker>();
        return builder;
    }
}

internal sealed record QueueBuilderSettings(
    string BootstrapServers,
    string QueueStoreConnectionString,
    string[] Topics)
{
    public static QueueBuilderSettings From(IConfiguration configuration)
    {
        var bootstrapServers = Required(
            configuration["QueueBuilder:BootstrapServers"],
            "QueueBuilder cannot start because 'QueueBuilder:BootstrapServers' is missing.");
        var connectionString = Required(
            configuration.GetConnectionString("QueueStore"),
            "QueueBuilder cannot start because connection string 'QueueStore' is missing.");
        var topics = configuration.GetSection("QueueBuilder:Topics")
            .GetChildren()
            .Select(topic => topic.Value)
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Cast<string>()
            .ToArray();

        if (topics.Length == 0)
        {
            throw new InvalidOperationException(
                "QueueBuilder cannot start because 'QueueBuilder:Topics' contains no Kafka topics.");
        }

        return new QueueBuilderSettings(bootstrapServers, connectionString, topics);
    }

    private static string Required(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;
}
