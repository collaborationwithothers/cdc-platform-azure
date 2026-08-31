using Confluent.Kafka;
using Lexfield.Observability;
using Lexfield.QueueStore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lexfield.Notifier;

public static class NotifierHostExtensions
{
    public static IHostApplicationBuilder AddNotifier(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddLexfieldObservability("Notifier");
        var settings = NotifierSettings.From(builder.Configuration);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(
            new SentNotificationStore(settings.QueueStoreConnectionString));
        builder.Services.AddSingleton<ISender, LogSender>();
        builder.Services.AddSingleton<IConsumer<string, string>>(_ =>
            new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = settings.BootstrapServers,
                GroupId = "notifier",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false,
                PartitionAssignmentStrategy = PartitionAssignmentStrategy.RoundRobin
            }).Build());
        builder.Services.AddSingleton<NotificationProcessor>();
        builder.Services.AddHostedService<NotifierWorker>();
        return builder;
    }
}

internal sealed record NotifierSettings(
    string BootstrapServers,
    string QueueStoreConnectionString,
    string[] Topics)
{
    public static NotifierSettings From(IConfiguration configuration)
    {
        var bootstrapServers = Required(
            configuration["Notifier:BootstrapServers"],
            "Notifier cannot start because 'Notifier:BootstrapServers' is missing.");
        var connectionString = Required(
            configuration.GetConnectionString("QueueStore"),
            "Notifier cannot start because connection string 'QueueStore' is missing.");
        var topics = configuration.GetSection("Notifier:Topics")
            .GetChildren()
            .Select(topic => topic.Value)
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Cast<string>()
            .ToArray();

        if (topics.Length == 0)
        {
            throw new InvalidOperationException(
                "Notifier cannot start because 'Notifier:Topics' contains no Kafka topics.");
        }

        return new NotifierSettings(bootstrapServers, connectionString, topics);
    }

    private static string Required(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;
}
