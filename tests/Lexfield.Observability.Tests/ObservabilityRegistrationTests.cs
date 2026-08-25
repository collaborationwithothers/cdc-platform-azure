using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Lexfield.Observability;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lexfield.Observability.Tests;

public sealed class ObservabilityRegistrationTests
{
    [Fact]
    public void AddLexfieldObservability_EnrichesEveryCapturedLineWithOperationFields()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Lexfield:Observability:Port"] = "18080";
        builder.AddLexfieldObservability("TaskApi");

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("tests");
        var output = new StringWriter();
        var originalOutput = Console.Out;
        var originalIdFormat = Activity.DefaultIdFormat;
        var originalForceDefaultIdFormat = Activity.ForceDefaultIdFormat;
        try
        {
            Console.SetOut(output);
            Activity.DefaultIdFormat = ActivityIdFormat.W3C;
            Activity.ForceDefaultIdFormat = true;
            using var activity = new Activity("transition").SetIdFormat(ActivityIdFormat.W3C).Start();
            using (logger.BeginScope(new Dictionary<string, object?>
            {
                ["eventName"] = "TaskApi.TransitionCommitted",
                ["tenantId"] = "lexfield-001",
                ["taskId"] = 4711,
                ["version"] = 7
            }))
            {
                logger.LogInformation("Transition committed");
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
            Activity.DefaultIdFormat = originalIdFormat;
            Activity.ForceDefaultIdFormat = originalForceDefaultIdFormat;
        }
        using var document = JsonDocument.Parse(output.ToString());
        var fields = document.RootElement;
        Assert.Equal(JsonValueKind.String, fields.GetProperty("timestamp").ValueKind);
        Assert.Equal("TaskApi", fields.GetProperty("service").GetString());
        Assert.Equal("Information", fields.GetProperty("level").GetString());
        Assert.Equal("TaskApi.TransitionCommitted", fields.GetProperty("eventName").GetString());
        Assert.Equal("lexfield-001", fields.GetProperty("tenantId").GetString());
        Assert.Equal(4711, fields.GetProperty("taskId").GetInt32());
        Assert.Equal(7, fields.GetProperty("version").GetInt32());
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$", fields.GetProperty("traceparent").GetString());
    }
    [Fact]
    public void AddLexfieldObservability_RegistersNamedSourcesAndExplicitSamplerSettings()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddLexfieldObservability("QueueBuilder");

        using var host = builder.Build();
        var services = host.Services;
        var source = services.GetRequiredService<ActivitySource>();
        var meter = services.GetRequiredService<Meter>();

        Assert.Equal("Lexfield.QueueBuilder", source.Name);
        Assert.Equal("Lexfield.QueueBuilder", meter.Name);
        Assert.Equal("microsoft.fixed_percentage", builder.Configuration["OTEL_TRACES_SAMPLER"]);
        Assert.Equal("1.0", builder.Configuration["OTEL_TRACES_SAMPLER_ARG"]);
        Assert.True(services.GetRequiredService<IOptions<OpenTelemetryLoggerOptions>>().Value.IncludeScopes);
        var exported = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meter.Name)
            .AddInMemoryExporter(exported, options =>
                options.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = -1)
            .Build();
        meter.CreateCounter<long>("TaskApi.TransitionCommitted").Add(1, MetricTags("TaskApi.TransitionCommitted"));
        meter.CreateCounter<long>("TaskApi.OutboxWritten").Add(1, MetricTags("TaskApi.OutboxWritten"));
        Assert.True(meterProvider.ForceFlush());
        Assert.Equal(
            new[] { "TaskApi.OutboxWritten", "TaskApi.TransitionCommitted" },
            exported.Select(metric => metric.Name).Order());
        foreach (var metric in exported)
        {
            var points = metric.GetMetricPoints().GetEnumerator();
            Assert.True(points.MoveNext());
            var tags = new Dictionary<string, object?>();
            foreach (var tag in points.Current.Tags) tags[tag.Key] = tag.Value;
            Assert.Equal("lexfield-001", tags["tenantId"]);
            Assert.Equal(metric.Name, tags["eventName"]);
            Assert.Equal(4711, tags["taskId"]);
            Assert.Equal(7, tags["version"]);
        }
    }
    [Fact]
    public async Task AddLexfieldObservability_ServesWorkerHealthAndReadinessEndpoints()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Lexfield:Observability:Port"] = port.ToString();
        builder.AddLexfieldObservability("Notifier");

        using var host = builder.Build();
        await host.StartAsync();
        using var client = new HttpClient();
        Assert.Equal("ok\n", await client.GetStringAsync($"http://localhost:{port}/healthz"));
        Assert.Equal("ready\n", await client.GetStringAsync($"http://localhost:{port}/readyz"));

        await host.StopAsync();
    }
    private static KeyValuePair<string, object?>[] MetricTags(string eventName) =>
    [
        new("tenantId", "lexfield-001"),
        new("eventName", eventName),
        new("taskId", 4711),
        new("version", 7)
    ];
}
