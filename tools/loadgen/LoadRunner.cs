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
    /// <summary>Stamped on every generated event so nothing this tool writes reads as real.</summary>
    public const string SyntheticActor = "synthetic:loadgen";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient client;
    private readonly TimeProvider time;
    private readonly LoadOptions options;
    private readonly TextWriter stageZero;
    private readonly Dictionary<string, TaskProgress> tasks = [];

    public LoadRunner(HttpClient client, LoadOptions options, TimeProvider time, TextWriter stageZero)
    {
        this.client = client;
        this.options = options;
        this.time = time;
        this.stageZero = stageZero;
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
        using var response = await client.PostAsJsonAsync(
            $"/tenants/{tenantId}/tasks", body, Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new EventOutcome(false, (int)response.StatusCode, null, TaskState.Created);
        }

        var created = await response.Content.ReadFromJsonAsync<CreatedTask>(Json, cancellationToken);
        if (created is null)
        {
            return new EventOutcome(false, (int)response.StatusCode, null, TaskState.Created);
        }

        tasks[tenantId] = new TaskProgress(created.TaskId, created.Version, TaskState.Created);
        return new EventOutcome(true, (int)response.StatusCode, created.TaskId, TaskState.Created);
    }

    private async Task<EventOutcome> TransitionAsync(
        string tenantId, TaskProgress progress, CancellationToken cancellationToken)
    {
        var to = NextState(progress.State);
        var body = new
        {
            to,
            actor = SyntheticActor,
            expectedVersion = progress.Version,
            teamId = $"{tenantId}-team",
            assigneeId = SyntheticActor,
        };
        using var response = await client.PostAsJsonAsync(
            $"/tenants/{tenantId}/tasks/{progress.TaskId}/transitions", body, Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The local version is not advanced on failure, so the next attempt
            // sends the version the server still holds rather than compounding
            // one rejection into a permanently wedged task.
            return new EventOutcome(false, (int)response.StatusCode, progress.TaskId, to);
        }

        tasks[tenantId] = progress with { Version = progress.Version + 1, State = to };
        return new EventOutcome(true, (int)response.StatusCode, progress.TaskId, to);
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
        _ => throw new InvalidOperationException($"No generated edge leaves {current}."),
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
    }

    private sealed record TaskProgress(int TaskId, int Version, TaskState State);

    private sealed record CreatedTask(int TaskId, int Version);

    private sealed record EventOutcome(bool Succeeded, int Status, int? TaskId, TaskState To);
}
