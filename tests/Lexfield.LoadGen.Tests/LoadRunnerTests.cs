using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexfield.Contracts;

namespace Lexfield.LoadGen.Tests;

public class LoadRunnerTests
{
    [Fact]
    public async Task Every_generated_field_is_labelled_synthetic()
    {
        var (report, requests, stageZero) = await RunAsync("uniform", tenants: 1, events: 3);

        Assert.Equal(3, report.Succeeded);
        Assert.All(requests, request =>
        {
            Assert.StartsWith("/tenants/synthetic-tenant-0001/", request.Path, StringComparison.Ordinal);
            Assert.Equal(LoadRunner.SyntheticActor, request.Body.GetProperty("assigneeId").GetString());
            Assert.Equal("synthetic-tenant-0001-team", request.Body.GetProperty("teamId").GetString());
        });
        Assert.All(
            stageZero,
            line => Assert.True(JsonDocument.Parse(line).RootElement.GetProperty("synthetic").GetBoolean()));
    }

    [Fact]
    public async Task Every_event_carries_a_client_side_issue_time()
    {
        var (_, _, stageZero) = await RunAsync("uniform", tenants: 2, events: 4);

        Assert.Equal(4, stageZero.Count);
        Assert.All(stageZero, line => Assert.True(
            JsonDocument.Parse(line).RootElement.GetProperty("t0").TryGetDateTimeOffset(out _)));
    }

    [Fact]
    public async Task The_run_starts_no_trace_and_sends_no_traceparent()
    {
        var (_, requests, _) = await RunAsync("uniform", tenants: 1, events: 3);

        // task-api writes Activity.Current?.Id into the outbox row, so a client
        // that starts no activity is what makes it write a null TraceParent.
        Assert.All(requests, request => Assert.False(request.Traced));
        Assert.All(requests, request => Assert.False(request.HasTraceparent));
    }

    [Fact]
    public async Task The_configured_distribution_decides_which_tenants_are_driven()
    {
        var (report, _, _) = await RunAsync("hot:1:1.0", tenants: 4, events: 20);

        var only = Assert.Single(report.EventsPerTenant);
        Assert.Equal("synthetic-tenant-0001", only.Key);
        Assert.Equal(20, only.Value);
    }

    [Fact]
    public async Task A_task_is_created_once_and_then_walked_along_the_legal_edges()
    {
        var (_, requests, _) = await RunAsync("uniform", tenants: 1, events: 6);

        Assert.Equal("/tenants/synthetic-tenant-0001/tasks", requests[0].Path);
        Assert.Equal(
            [TaskState.Assigned, TaskState.InProgress, TaskState.Submitted, TaskState.QA, TaskState.InProgress],
            requests.Skip(1).Select(request =>
                Enum.Parse<TaskState>(request.Body.GetProperty("to").GetString()!)).ToArray());
        Assert.Equal(
            [1, 2, 3, 4, 5],
            requests.Skip(1).Select(request =>
                request.Body.GetProperty("expectedVersion").GetInt32()).ToArray());
    }

    [Fact]
    public async Task A_rejected_transition_is_reported_and_does_not_advance_the_version()
    {
        var (report, requests, _) = await RunAsync(
            "uniform", tenants: 1, events: 4, transitionStatus: HttpStatusCode.Conflict);

        Assert.Equal(3, report.Failed);
        Assert.Equal(
            [1, 1, 1],
            requests.Skip(1).Select(request =>
                request.Body.GetProperty("expectedVersion").GetInt32()).ToArray());
    }

    private static async Task<(LoadReport Report, List<Recorded> Requests, List<string> StageZero)> RunAsync(
        string distribution,
        int tenants,
        int events,
        HttpStatusCode transitionStatus = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler(transitionStatus);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://task-api.test") };
        var stageZero = new StringWriter();
        var options = new LoadOptions
        {
            Distribution = TenantKeyDistribution.Parse(distribution, tenants),
            // Fast enough that the pacing never dominates the test, slow enough
            // that the limiter is still on the path being exercised.
            EventsPerSecond = 10_000,
            EventCount = events,
        };

        var report = await new LoadRunner(client, options, TimeProvider.System, stageZero)
            .RunAsync(CancellationToken.None);
        var lines = stageZero.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        return (report, handler.Requests, lines);
    }

    private sealed record Recorded(string Path, JsonElement Body, bool Traced, bool HasTraceparent);

    private sealed class RecordingHandler(HttpStatusCode transitionStatus) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        private int nextTaskId = 1;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonDocument
                .Parse(await request.Content!.ReadAsStringAsync(cancellationToken))
                .RootElement.Clone();
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new Recorded(
                path, body, Activity.Current is not null, request.Headers.Contains("traceparent")));

            if (!path.EndsWith("/transitions", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new { taskId = nextTaskId++, version = 1 }),
                };
            }

            return new HttpResponseMessage(transitionStatus)
            {
                Content = JsonContent.Create(new { taskId = 1, version = 2 }),
            };
        }
    }
}
