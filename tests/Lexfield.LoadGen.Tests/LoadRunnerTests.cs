using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexfield.Contracts;

namespace Lexfield.LoadGen.Tests;

public class LoadRunnerTests
{
    [Fact]
    public async Task A_run_marks_every_generated_task_field_as_synthetic()
    {
        var (report, requests, stageZero, _) = await RunAsync("uniform", tenants: 1, events: 3);

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
    public async Task Each_stage_zero_record_contains_the_client_request_time()
    {
        var (_, _, stageZero, _) = await RunAsync("uniform", tenants: 2, events: 4);

        Assert.Equal(4, stageZero.Count);
        Assert.All(stageZero, line => Assert.True(
            JsonDocument.Parse(line).RootElement.GetProperty("t0").TryGetDateTimeOffset(out _)));
    }

    [Fact]
    public async Task An_untraced_run_sends_no_trace_context_to_task_api()
    {
        var (_, requests, _, _) = await RunAsync("uniform", tenants: 1, events: 3);

        // Trace context links one request to related logs and messages.
        // task-api writes Activity.Current?.Id into the outbox row, so a client
        // that starts no activity is what makes it write a null TraceParent.
        Assert.All(requests, request => Assert.False(request.Traced));
        Assert.All(requests, request => Assert.False(request.HasTraceparent));
    }

    [Fact]
    public async Task Progress_output_names_stage_zero_and_explains_a_rejected_transition()
    {
        var (_, _, _, progress) = await RunAsync(
            "uniform", tenants: 1, events: 2, transitionStatus: HttpStatusCode.Conflict);

        Assert.Contains("Create stage:", progress, StringComparison.Ordinal);
        Assert.Contains("Transition stage:", progress, StringComparison.Ordinal);
        Assert.Contains("client-side request time", progress, StringComparison.Ordinal);
        Assert.Contains("before task-api processes this synthetic transition", progress, StringComparison.Ordinal);
        Assert.Contains("task-api is the HTTP service", progress, StringComparison.Ordinal);
        Assert.Contains("A workflow transition moves a task", progress, StringComparison.Ordinal);
        Assert.Contains("change data capture (CDC) path", progress, StringComparison.Ordinal);
        Assert.Contains("These measurements matter", progress, StringComparison.Ordinal);
        Assert.Contains("HTTP 409", progress, StringComparison.Ordinal);
        Assert.Contains("was not accepted", progress, StringComparison.Ordinal);
        Assert.Contains(
            "Check the current task version for a concurrent update before retrying.",
            progress,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_output_names_a_failed_create_and_its_http_status()
    {
        var (_, _, _, progress) = await RunAsync(
            "uniform", tenants: 1, events: 1, createStatus: HttpStatusCode.NotFound);

        Assert.Contains("Create stage:", progress, StringComparison.Ordinal);
        Assert.Contains("HTTP 404", progress, StringComparison.Ordinal);
        Assert.Contains("Check the task-api base address and that the synthetic route exists before retrying.", progress, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    public async Task A_create_response_without_a_positive_identity_stops_before_a_duplicate(string responseBody)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, createBody: responseBody);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://task-api.test") };
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await LoadgenCli.RunAsync(
            ["--base-address", "http://task-api.test", "--events", "2"],
            client, "test-token", stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Single(handler.Requests);
        Assert.Equal("/tenants/synthetic-tenant-0001/tasks", handler.Requests[0].Path);
        Assert.Contains("Create stage:", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("response was unusable", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("may have committed", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("no duplicate create was sent", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("no valid positive taskId and version", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("JsonException", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_output_explains_an_illegal_transition_and_states()
    {
        var (_, _, _, progress) = await RunAsync(
            "uniform", tenants: 1, events: 2, transitionStatus: HttpStatusCode.UnprocessableEntity);

        Assert.Contains("Transition stage:", progress, StringComparison.Ordinal);
        Assert.Contains("task-api rejected this illegal workflow transition", progress, StringComparison.Ordinal);
        Assert.Contains("runner's last-known state 'Created' to requested state 'Assigned'. Check task-api's current state and the request before retrying.", progress, StringComparison.Ordinal);
        Assert.Contains("requested state 'Assigned'", progress, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_help_names_the_synthetic_test_and_its_output_streams()
    {
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.OK));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await LoadgenCli.RunAsync(["--help"], client, null, stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.All(["synthetic workflow-task transitions", "bearer token", "trace context", "rate schedule", "tenant distribution", "stdout", "stderr"],
            phrase => Assert.Contains(phrase, stdout.ToString(), StringComparison.Ordinal));
        Assert.Empty(stderr.ToString());
    }

    [Theory]
    [InlineData("--base-address", "file:///tmp/task-api")]
    [InlineData("--tenants", "0")]
    [InlineData("--tenants", "2147483648")]
    [InlineData("--rate", "NaN")]
    [InlineData("--rate", "Infinity")]
    [InlineData("--rate", "-1")]
    [InlineData("--events", "-1")]
    [InlineData("--events", "2147483648")]
    [InlineData("--seed", "2147483648")]
    [InlineData("--distribution", "hot:2147483648:0.5")]
    [InlineData("--distribution", "hot:2:NaN")]
    public void Cli_rejects_invalid_values_and_names_the_option(string option, string value)
    {
        var parsed = LoadgenCli.TryParse([option, value], out _, out var error);

        Assert.False(parsed);
        Assert.Contains($"option '{option}'", error, StringComparison.Ordinal);
        Assert.Contains($"Expected form: {option} {(option == "--rate" ? "EVENTS_PER_SECOND" : option == "--distribution" ? "uniform" : option == "--base-address" ? "URL" : "N")}", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_report_separates_configured_observed_and_derived_values()
    {
        var options = new LoadOptions
        {
            Distribution = TenantKeyDistribution.Parse("uniform", 2),
            EventsPerSecond = 10,
            EventCount = 3,
        };
        var report = new LoadReport(
            3, 2, 1, TimeSpan.FromSeconds(1),
            new Dictionary<string, int> { ["synthetic-tenant-0001"] = 3 });

        var output = LoadgenCli.FormatReport(report, options);

        var observedStart = output.IndexOf("Observed measurements:", StringComparison.Ordinal);
        var derivedStart = output.IndexOf("Derived values:", StringComparison.Ordinal);
        Assert.True(observedStart > output.IndexOf("Configured inputs:", StringComparison.Ordinal)
            && observedStart < derivedStart);
        Assert.Contains("events requested: 3", output[..observedStart], StringComparison.Ordinal);
        Assert.Contains("succeeded:        2", output[observedStart..derivedStart], StringComparison.Ordinal);
        Assert.Contains("observed rate:    3/s", output[derivedStart..], StringComparison.Ordinal);
        Assert.All(["task-api is the HTTP service", "A workflow transition moves a task", "change data capture (CDC) path", "These measurements matter"],
            phrase => Assert.Contains(phrase, output, StringComparison.Ordinal));
        Assert.Contains("Generated tenant keys and task payloads are synthetic.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_reports_transport_failure_with_stage_endpoint_and_safe_action()
    {
        using var client = new HttpClient(new ThrowingHandler());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await LoadgenCli.RunAsync(
            ["--base-address", "http://task-api.test", "--events", "1"],
            client, "test-token", stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("create stage", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("POST http://task-api.test/tenants/synthetic-tenant-0001/tasks", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("stopped before this event outcome", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("check that task-api is running", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public async Task Cli_returns_exit_code_zero_for_a_successful_run_and_keeps_output_streams_separate()
    {
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.OK));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await LoadgenCli.RunAsync(
            ["--base-address", "http://task-api.test", "--events", "1"],
            client, "test-token", stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"t0\"", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("Create stage:", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Configured inputs:", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"t0\"", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_returns_exit_code_one_for_a_rejected_response()
    {
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.Conflict));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await LoadgenCli.RunAsync(
            ["--base-address", "http://task-api.test", "--events", "2"],
            client, "test-token", stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("HTTP 409", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_returns_exit_code_two_and_names_a_missing_option_value()
    {
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.OK));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await LoadgenCli.RunAsync(
            ["--rate", "--events", "1"], client, "test-token", stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Contains("Option '--rate' is missing its value", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Expected form: --rate EVENTS_PER_SECOND", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_selected_distribution_controls_which_synthetic_tenants_receive_events()
    {
        var (report, _, _, _) = await RunAsync("hot:1:1.0", tenants: 4, events: 20);

        var only = Assert.Single(report.EventsPerTenant);
        Assert.Equal("synthetic-tenant-0001", only.Key);
        Assert.Equal(20, only.Value);
    }

    [Fact]
    public async Task Each_tenant_creates_one_task_then_follows_valid_state_transitions()
    {
        var (_, requests, _, _) = await RunAsync("uniform", tenants: 1, events: 6);

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
    public async Task Transition_requests_do_not_send_a_caller_supplied_actor()
    {
        var (report, requests, _, _) = await RunAsync("uniform", tenants: 1, events: 2);

        Assert.Equal(2, report.Succeeded);
        Assert.All(
            requests.Skip(1),
            request => Assert.False(request.Body.TryGetProperty("actor", out _)));
    }

    [Fact]
    public async Task A_rejected_transition_is_reported_without_advancing_the_local_version()
    {
        var (report, requests, _, _) = await RunAsync(
            "uniform", tenants: 1, events: 4, transitionStatus: HttpStatusCode.Conflict);

        Assert.Equal(3, report.Failed);
        Assert.Equal(
            [1, 1, 1],
            requests.Skip(1).Select(request =>
                request.Body.GetProperty("expectedVersion").GetInt32()).ToArray());
    }

    private static async Task<(
        LoadReport Report,
        List<Recorded> Requests,
        List<string> StageZero,
        string Progress)> RunAsync(
        string distribution,
        int tenants,
        int events,
        HttpStatusCode transitionStatus = HttpStatusCode.OK,
        HttpStatusCode createStatus = HttpStatusCode.Created)
    {
        var handler = new RecordingHandler(transitionStatus, createStatus);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://task-api.test") };
        var stageZero = new StringWriter();
        var progress = new StringWriter();
        var options = new LoadOptions
        {
            Distribution = TenantKeyDistribution.Parse(distribution, tenants),
            // Fast enough that the pacing never dominates the test, slow enough
            // that the limiter is still on the path being exercised.
            EventsPerSecond = 10_000,
            EventCount = events,
        };

        var report = await new LoadRunner(client, options, TimeProvider.System, stageZero, progress)
            .RunAsync(CancellationToken.None);
        var lines = stageZero.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        return (report, handler.Requests, lines, progress.ToString());
    }

    private sealed record Recorded(string Path, JsonElement Body, bool Traced, bool HasTraceparent);

    private sealed class RecordingHandler(
        HttpStatusCode transitionStatus,
        HttpStatusCode createStatus = HttpStatusCode.Created,
        string? createBody = null) : HttpMessageHandler
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
                return new HttpResponseMessage(createStatus)
                {
                    Content = createBody is null
                        ? JsonContent.Create(new { taskId = nextTaskId++, version = 1 })
                        : new StringContent(createBody, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(transitionStatus)
            {
                Content = JsonContent.Create(new { taskId = 1, version = 2 }),
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("task-api is unreachable");
    }
}
