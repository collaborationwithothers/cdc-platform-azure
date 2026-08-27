using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public void AddLexfieldObservability_WritesServiceAndTaskContextIntoEachJsonLog()
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
                logger.LogInformation("TaskApi recorded the committed workflow transition for the tenant task.");
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
            Activity.DefaultIdFormat = originalIdFormat;
            Activity.ForceDefaultIdFormat = originalForceDefaultIdFormat;
        }
        using var document = JsonDocument.Parse(output.ToString());
        var logFields = document.RootElement;
        AssertField(JsonValueKind.String, logFields.GetProperty("timestamp").ValueKind,
            "Each log line names when the observability event was written.");
        AssertField("TaskApi", logFields.GetProperty("service").GetString(),
            "The log identifies the service that handled the workflow event.");
        AssertField("Information", logFields.GetProperty("level").GetString(),
            "The log preserves the event severity.");
        AssertField("TaskApi.TransitionCommitted", logFields.GetProperty("eventName").GetString(),
            "The log identifies the workflow behavior that occurred.");
        AssertField("lexfield-001", logFields.GetProperty("tenantId").GetString(),
            "The log identifies the tenant whose event was handled.");
        AssertField(4711, logFields.GetProperty("taskId").GetInt32(),
            "The log identifies the task whose state changed.");
        AssertField(7, logFields.GetProperty("version").GetInt32(),
            "The log identifies the task change version.");
        Assert.True(Regex.IsMatch(logFields.GetProperty("traceparent").GetString() ?? string.Empty,
            "^00-[0-9a-f]{32}-[0-9a-f]{16}-0[01]$"),
            "The traceparent field links this log to the current distributed trace.");
    }
    [Fact]
    public void AddLexfieldObservability_RegistersServiceTraceAndMetricSourcesWithSharedSampling()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddLexfieldObservability("QueueBuilder");

        using var host = builder.Build();
        var services = host.Services;
        var source = services.GetRequiredService<ActivitySource>();
        var meter = services.GetRequiredService<Meter>();

        AssertField("Lexfield.QueueBuilder", source.Name,
            "The trace source identifies the queue consumer service.");
        AssertField("Lexfield.QueueBuilder", meter.Name,
            "The metric source identifies the queue consumer service.");
        AssertField("microsoft.fixed_percentage", builder.Configuration["OTEL_TRACES_SAMPLER"],
            "Every service uses the shared trace sampling rule.");
        AssertField("1.0", builder.Configuration["OTEL_TRACES_SAMPLER_ARG"],
            "The shared sampling rule keeps its configured argument.");
        Assert.True(services.GetRequiredService<IOptions<OpenTelemetryLoggerOptions>>().Value.IncludeScopes,
            "Log scopes carry the tenant and task context into the JSON log.");
        var exported = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(meter.Name)
            .AddInMemoryExporter(exported, options =>
                options.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = -1)
            .Build();
        meter.CreateCounter<long>("TaskApi.TransitionCommitted").Add(1, MetricTags("TaskApi.TransitionCommitted"));
        meter.CreateCounter<long>("TaskApi.OutboxWritten").Add(1, MetricTags("TaskApi.OutboxWritten"));
        Assert.True(meterProvider.ForceFlush());
        Assert.True(new[] { "TaskApi.OutboxWritten", "TaskApi.TransitionCommitted" }
            .SequenceEqual(exported.Select(metric => metric.Name).Order()),
            "The metric source exposes each workflow event measurement.");
        foreach (var metric in exported)
        {
            var points = metric.GetMetricPoints().GetEnumerator();
            Assert.True(points.MoveNext(),
                $"Metric '{metric.Name}' exposes one measurement point.");
            var tags = new Dictionary<string, object?>();
            foreach (var tag in points.Current.Tags) tags[tag.Key] = tag.Value;
            AssertField("lexfield-001", tags["tenantId"],
                "The metric identifies the tenant whose event was measured.");
            AssertField(metric.Name, tags["eventName"],
                "The metric identifies the workflow event that was measured.");
            AssertField(4711, tags["taskId"],
                "The metric identifies the task whose event was measured.");
            AssertField(7, tags["version"],
                "The metric identifies the task change version.");
        }
    }
    [Fact]
    public async Task AddLexfieldObservability_ServesLivenessAndReadinessProbeBodies()
    {
        using var host = CreateEndpointHost(out var port);
        await host.StartAsync();
        using var client = new HttpClient();
        Assert.True("ok\n" == await client.GetStringAsync($"http://localhost:{port}/healthz"),
            "The /healthz liveness probe must keep its protocol body and only prove that this process's listener answered.");
        Assert.True("ready\n" == await client.GetStringAsync($"http://localhost:{port}/readyz"),
            "The /readyz readiness probe must keep its protocol body; it does not check downstream dependencies.");

        await host.StopAsync();
    }
    [Fact]
    public async Task AddLexfieldObservability_CompletesShutdownAfterEndpointDispose()
    {
        using var host = CreateEndpointHost(out _);
        await host.StartAsync();
        var endpoint = Assert.Single(
            host.Services.GetServices<IHostedService>(),
            service => service.GetType().Assembly == typeof(LexfieldObservabilityExtensions).Assembly);

        ((IDisposable)endpoint).Dispose();
        await endpoint.StopAsync(CancellationToken.None);
        ((IDisposable)endpoint).Dispose();
    }
    [Theory]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("65536")]
    public void AddLexfieldObservability_ExplainsInvalidObservabilityPort(string configuredPort)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Lexfield:Observability:Port"] = configuredPort;

        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.AddLexfieldObservability("QueueBuilder"));

        Assert.True(exception.Message.Contains("QueueBuilder observability endpoint cannot start"),
            "The configuration error identifies the service observability endpoint.");
        Assert.True(exception.Message.Contains($"received '{configuredPort}'"),
            "The configuration error identifies the invalid input.");
        Assert.True(exception.Message.Contains("/healthz liveness and /readyz readiness probes"),
            "The configuration error explains which service behavior is affected.");
    }
    [Fact]
    public async Task AddLexfieldObservability_ExplainsWhenProbeListenerCannotBind()
    {
        var port = ReservePort();
        using var occupied = new HttpListener();
        occupied.Prefixes.Add($"http://*:{port}/");
        occupied.Start();

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Lexfield:Observability:Port"] = port.ToString();
        builder.AddLexfieldObservability("QueueBuilder");
        using var host = builder.Build();
        var endpoint = Assert.Single(
            host.Services.GetServices<IHostedService>(),
            service => service.GetType().Assembly == typeof(LexfieldObservabilityExtensions).Assembly);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => endpoint.StartAsync(CancellationToken.None));

        Assert.True(exception.Message.Contains("QueueBuilder observability health endpoint cannot start"),
            "The startup error identifies the service probe endpoint.");
        Assert.True(exception.Message.Contains("Underlying listener error"),
            "The startup error includes the operating system listener reason.");
        Assert.True(exception.InnerException is HttpListenerException,
            "The startup error preserves the listener exception for diagnosis.");
        Assert.True(exception.Message.Contains("configured address, process permission"),
            "The startup error names address and permission checks.");
        Assert.True(exception.Message.Contains("another process"),
            "The startup error names a port conflict as one possible cause.");
        Assert.True(exception.Message.Contains("unused port"),
            "The startup error gives a corrective action for a port conflict.");
    }
    private static IHost CreateEndpointHost(out int port)
    {
        port = ReservePort();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Lexfield:Observability:Port"] = port.ToString();
        builder.AddLexfieldObservability("Notifier");
        return builder.Build();
    }
    private static int ReservePort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }
    private static void AssertField<T>(T expected, T actual, string explanation)
    {
        Assert.True(EqualityComparer<T>.Default.Equals(expected, actual), explanation);
    }
    private static KeyValuePair<string, object?>[] MetricTags(string eventName) =>
    [
        new("tenantId", "lexfield-001"),
        new("eventName", eventName),
        new("taskId", 4711),
        new("version", 7)
    ];
}
