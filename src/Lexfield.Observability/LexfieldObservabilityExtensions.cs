using System.Diagnostics;
using System.Diagnostics.Metrics;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
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
        // A trace links timed operations across services. The sampler decides
        // which traces are kept, so all four services use the same setting or
        // an operator can lose part of the trace at the Kafka handoff.
        builder.Configuration["OTEL_TRACES_SAMPLER"] = SamplerName;
        builder.Configuration["OTEL_TRACES_SAMPLER_ARG"] = SamplerArgument;
        AddTelemetry(builder.Services, serviceName, sourceName, builder.Configuration);
        var endpointPort = ReadEndpointPort(builder.Configuration, serviceName);
        builder.Services.AddSingleton(new LexfieldEndpointOptions(endpointPort, serviceName));
        builder.Services.AddHostedService<LexfieldEndpointHostedService>();
        return builder;
    }
    private static void AddTelemetry(
        IServiceCollection services,
        string serviceName,
        string sourceName,
        IConfiguration configuration)
    {
        // ActivitySource creates the named source for traces, and Meter creates
        // the named source for numeric measurements. The logger provider adds
        // correlation fields to each service's JSON log line.
        services.AddSingleton(new ActivitySource(sourceName));
        services.AddSingleton(new Meter(sourceName));

        var openTelemetry = services.AddOpenTelemetry();
        var connectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
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
        services.Configure<OpenTelemetryLoggerOptions>(options => options.IncludeScopes = true);

        services.AddSingleton<ILoggerProvider>(_ => new LexfieldLogEnricherProvider(serviceName));
    }
    private static int ReadEndpointPort(IConfiguration configuration, string serviceName)
    {
        var configuredPort = configuration["Lexfield:Observability:Port"];
        if (configuredPort is null)
        {
            return DefaultEndpointPort;
        }
        if (!int.TryParse(configuredPort, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{serviceName} observability endpoint cannot start because configuration " +
                $"'Lexfield:Observability:Port' must be a whole TCP port from 1 through 65535; " +
                $"received '{configuredPort}'. Set this value to an unused port so {serviceName} " +
                "can answer the /healthz liveness and /readyz readiness probes.");
        }
        return port;
    }
}
