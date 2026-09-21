using Lexfield.Observability;
using Lexfield.QueueStore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lexfield.QueueReconciler;

internal static class QueueReconcilerHostExtensions
{
    public static IHostApplicationBuilder AddQueueReconciler(this IHostApplicationBuilder builder)
    {
        builder.AddLexfieldObservability("QueueReconciler");
        var settings = SweepSettings.From(builder.Configuration);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(new ReconcilerStateStore(settings.QueueStoreConnectionString));
        builder.Services.AddSingleton(new QueueStateStore(settings.QueueStoreConnectionString));
        builder.Services.AddSingleton(new TaskApiTokenProvider(settings.TaskApiBearerTokens));
        builder.Services.AddHttpClient<TaskApiChangesClient>(client =>
            client.BaseAddress = settings.TaskApiBaseAddress);
        builder.Services.AddSingleton<PassOne>();
        builder.Services.AddSingleton<SweepHost>();
        builder.Services.AddHostedService(services => services.GetRequiredService<SweepHost>());
        return builder;
    }
}
