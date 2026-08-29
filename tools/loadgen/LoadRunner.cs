using System.Net.Http.Json;
using System.Text.Json;
using Lexfield.Contracts;

namespace Lexfield.LoadGen;

public sealed record LoadOptions
{
    public required TenantKeyDistribution Distribution { get; init; }
    public required double EventsPerSecond { get; init; }
    public required int EventCount { get; init; }
    public int Seed { get; init; } = 1;
}

public sealed record LoadReport(
    int Issued,
    int Succeeded,
    int Failed,
    TimeSpan Elapsed,
    IReadOnlyDictionary<string, int> EventsPerTenant);

public sealed class LoadGenTransportException(string stage, string endpoint, Exception innerException)
    : Exception($"Transport failure during {stage} for {endpoint}.", innerException)
{
    public string Stage { get; } = stage;
    public string Endpoint { get; } = endpoint;
}

public sealed class LoadGenResponseException(
    string stage, string endpoint, int status, string correction) : Exception(correction)
{
    public string Stage { get; } = stage;
    public string Endpoint { get; } = endpoint;
    public int Status { get; } = status;
    public string Correction { get; } = correction;
}

/// <summary>
/// Drives transitions through task-api's HTTP surface at the configured rate,
/// stamping the client-side issue time of every event so a latency measurement
/// has a stage zero.
/// </summary>
/// <remarks>
/// The runner starts no <see cref="System.Diagnostics.Activity"/> and attaches
/// no listener. task-api writes <c>Activity.Current?.Id</c> into the outbox row
/// inside the transaction, so an untraced client is what makes it write a null
/// <c>TraceParent</c>. Every load run therefore exercises the untraced write
/// path, which is the path a regression would otherwise hide in.
/// </remarks>
public sealed class LoadRunner
{
    /// <summary>Used in generated request payloads so those values are synthetic.</summary>
    public const string SyntheticActor = "synthetic:loadgen";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient client;
    private readonly TimeProvider time;
    private readonly LoadOptions options;
    private readonly TextWriter stageZero;
    private readonly TextWriter progress;
    private readonly Dictionary<string, TaskProgress> tasks = [];

    public LoadRunner(
        HttpClient client,
        LoadOptions options,
        TimeProvider time,
        TextWriter stageZero,
        TextWriter? progress = null)
    {
        this.client = client;
        this.options = options;
        this.time = time;
        this.stageZero = stageZero;
        this.progress = progress ?? TextWriter.Null;
    }

    public async Task<LoadReport> RunAsync(CancellationToken cancellationToken)
    {
        var limiter = new RateLimiter(options.EventsPerSecond, time);
        var random = new Random(options.Seed);
        var perTenant = new Dictionary<string, int>();
        var started = time.GetTimestamp();
        int succeeded = 0, failed = 0;

        for (var issued = 0; issued < options.EventCount; issued++)
        {
            await limiter.WaitForNextAsync(cancellationToken);
            var tenantId = options.Distribution.Next(random);
            perTenant[tenantId] = perTenant.GetValueOrDefault(tenantId) + 1;

            var t0 = time.GetUtcNow();
            var outcome = tasks.TryGetValue(tenantId, out var progress)
                ? await TransitionAsync(tenantId, progress, cancellationToken)
                : await CreateAsync(tenantId, cancellationToken);

            if (outcome.Succeeded) succeeded++; else failed++;
            WriteStageZero(t0, tenantId, outcome);
        }

        return new LoadReport(
            options.EventCount, succeeded, failed, time.GetElapsedTime(started), perTenant);
    }

    private async Task<EventOutcome> CreateAsync(string tenantId, CancellationToken cancellationToken)
    {
        var body = new { teamId = $"{tenantId}-team", assigneeId = SyntheticActor };
        var endpoint = $"/tenants/{tenantId}/tasks";
        using var response = await PostAsync(endpoint, body, "create", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new EventOutcome(false, (int)response.StatusCode, null, TaskState.Created, "create", endpoint);
        }

        CreatedTask? created;
        try
        {
            created = await response.Content.ReadFromJsonAsync<CreatedTask>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            throw InvalidCreateResponse((int)response.StatusCode, endpoint);
        }

        if (created is null || created.TaskId <= 0 || created.Version <= 0)
        {
            throw InvalidCreateResponse((int)response.StatusCode, endpoint);
        }

        tasks[tenantId] = new TaskProgress(created.TaskId, created.Version, TaskState.Created);
        return new EventOutcome(true, (int)response.StatusCode, created.TaskId, TaskState.Created, "create", endpoint);
    }

    private async Task<EventOutcome> TransitionAsync(
        string tenantId, TaskProgress progress, CancellationToken cancellationToken)
    {
        var to = NextState(progress.State);
        var body = new
        {
            to,
            expectedVersion = progress.Version,
            teamId = $"{tenantId}-team",
            assigneeId = SyntheticActor,
        };
        var endpoint = $"/tenants/{tenantId}/tasks/{progress.TaskId}/transitions";
        using var response = await PostAsync(endpoint, body, "transition", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The local sequence is not advanced on failure. The server state is
            // not inferred from a failed response.
            var correction = (int)response.StatusCode == 422
                ? "task-api rejected this illegal workflow transition from the runner's " +
                  $"last-known state '{progress.State}' to requested state '{to}'. Check " +
                  "task-api's current state and the request before retrying."
                : null;
            return new EventOutcome(
                false, (int)response.StatusCode, progress.TaskId, to, "transition", endpoint, correction);
        }

        tasks[tenantId] = progress with { Version = progress.Version + 1, State = to };
        return new EventOutcome(true, (int)response.StatusCode, progress.TaskId, to, "transition", endpoint);
    }

    private async Task<HttpResponseMessage> PostAsync(
        string endpoint,
        object body,
        string stage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.PostAsJsonAsync(endpoint, body, Json, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new LoadGenTransportException(stage, endpoint, exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LoadGenTransportException(stage, endpoint, exception);
        }
    }

    /// <summary>
    /// Walks the legal edges, using the QA to InProgress rework edge as a cycle
    /// so a fixed task pool produces unbounded transitions. The generator never
    /// drives a task to Completed or Delivered; a run that needs those states
    /// needs a different tool.
    /// </summary>
    private static TaskState NextState(TaskState current) => current switch
    {
        TaskState.Created => TaskState.Assigned,
        TaskState.Assigned => TaskState.InProgress,
        TaskState.InProgress => TaskState.Submitted,
        TaskState.Submitted => TaskState.QA,
        TaskState.QA => TaskState.InProgress,
        _ => throw new InvalidOperationException(
            $"The synthetic load generator cannot create a valid next transition from " +
            $"task state '{current}'. Supported transitions cycle through Created, " +
            "Assigned, InProgress, Submitted, QA, and back to InProgress; " +
            "Completed and Delivered are not generated by this tool."),
    };

    private void WriteStageZero(DateTimeOffset t0, string tenantId, EventOutcome outcome)
    {
        stageZero.WriteLine(JsonSerializer.Serialize(new
        {
            t0,
            tenantId,
            taskId = outcome.TaskId,
            to = outcome.To,
            status = outcome.Status,
            synthetic = true,
        }, Json));

        var result = outcome.Succeeded
            ? $"The synthetic task {outcome.TaskId} was accepted by task-api."
            : outcome.Correction is not null
                ? $"The requested synthetic {StageDescription(outcome.Stage)} was rejected by task-api; the stage-zero record was still written locally. " +
                  outcome.Correction
                : $"The synthetic {StageDescription(outcome.Stage)} was not accepted by task-api. " +
                  (outcome.Stage == "create"
                      ? "The runner did not record task creation locally. "
                      : "The runner did not advance its local sequence. ") +
                  FailureAction(outcome.Status, outcome.Stage);
        progress.WriteLine(
            $"{LoadgenCli.OutputContext} {Cap(outcome.Stage)} stage: POST {outcome.Endpoint} returned HTTP {outcome.Status}. " +
            $"Stage zero records the client-side request time before task-api processes this " +
            $"synthetic {StageDescription(outcome.Stage)}. {result}");
    }

    private static string StageDescription(string stage) => stage == "create"
        ? "task creation"
        : "transition";

    private static string FailureAction(int status, string stage) => status switch
    {
        404 => $"Check the task-api base address and that the synthetic " +
            (stage == "transition" ? "task" : "route") + " exists before retrying.",
        409 when stage == "transition" =>
            "Check the current task version for a concurrent update before retrying.",
        409 => "Check whether the synthetic task already exists before retrying.",
        422 => "Check the requested state and synthetic payload against task-api before retrying.",
        _ => "Check the task-api response and service logs before retrying.",
    };

    private static string Cap(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private sealed record TaskProgress(int TaskId, int Version, TaskState State);

    private sealed record CreatedTask(int TaskId, int Version);

    private static LoadGenResponseException InvalidCreateResponse(int status, string endpoint) =>
        new(
            "create",
            endpoint,
            status,
            "task-api returned no valid positive taskId and version for the accepted create. " +
            "Check the task-api response contract and service logs before retrying.");

    private sealed record EventOutcome(
        bool Succeeded,
        int Status,
        int? TaskId,
        TaskState To,
        string Stage,
        string Endpoint,
        string? Correction = null);
}
