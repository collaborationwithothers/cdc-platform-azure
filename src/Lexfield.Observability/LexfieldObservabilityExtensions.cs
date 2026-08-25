using System.Diagnostics;
using System.Diagnostics.Metrics;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Lexfield.Observability;

public static class LexfieldObservabilityExtensions
{
    internal const string SamplerName = "microsoft.fixed_percentage";
    internal const string SamplerArgument = "1.0";
    internal const int DefaultEndpointPort = 8080;

    public static IHostApplicationBuilder AddLexfieldObservability(
        this IHostApplicationBuilder builder,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var sourceName = $"Lexfield.{serviceName}";

        builder.Logging.ClearProviders();

        // All four services must use the same sampler or traces break at the Kafka hop.
        builder.Configuration["OTEL_TRACES_SAMPLER"] = SamplerName;
        builder.Configuration["OTEL_TRACES_SAMPLER_ARG"] = SamplerArgument;

        AddTelemetry(builder.Services, serviceName, sourceName, builder.Configuration);

        var endpointPort = ReadEndpointPort(builder.Configuration);
        builder.Services.AddSingleton(new LexfieldEndpointOptions(serviceName, endpointPort));
        builder.Services.AddHostedService<LexfieldEndpointHostedService>();

        return builder;
    }

    public static IServiceCollection AddLexfieldObservability(
        this IServiceCollection services,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var sourceName = $"Lexfield.{serviceName}";
        services.AddLogging(logging => logging.ClearProviders());
        AddTelemetry(services, serviceName, sourceName, configuration: null);
        services.AddSingleton(new LexfieldEndpointOptions(serviceName, LexfieldObservabilityExtensions.DefaultEndpointPort));
        services.AddHostedService<LexfieldEndpointHostedService>();
        return services;
    }

    private static void AddTelemetry(
        IServiceCollection services,
        string serviceName,
        string sourceName,
        IConfiguration? configuration)
    {
        services.AddSingleton(new ActivitySource(sourceName));
        services.AddSingleton(new Meter(sourceName));

        var openTelemetry = services.AddOpenTelemetry();
        var connectionString = configuration?["APPLICATIONINSIGHTS_CONNECTION_STRING"]
            ?? Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            openTelemetry.UseAzureMonitor(options =>
            {
                options.SamplingRatio = 1.0f;
                options.TracesPerSecond = null;
            });
        }
        openTelemetry.ConfigureResource(resource => resource.AddService(sourceName));
        services.ConfigureOpenTelemetryTracerProvider((_, traces) => traces.AddSource(sourceName));
        services.ConfigureOpenTelemetryMeterProvider((_, meters) => meters.AddMeter(sourceName));

        services.AddSingleton<ILoggerProvider>(_ => new LexfieldLogEnricherProvider(serviceName));
    }

    private static int ReadEndpointPort(IConfiguration configuration)
    {
        var configuredPort = configuration["Lexfield:Observability:Port"];
        if (configuredPort is null)
        {
            return DefaultEndpointPort;
        }

        if (!int.TryParse(configuredPort, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"Lexfield:Observability:Port must be between 1 and 65535; received '{configuredPort}'.");
        }

        return port;
    }
}
