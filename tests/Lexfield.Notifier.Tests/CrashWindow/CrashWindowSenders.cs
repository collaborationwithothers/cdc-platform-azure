using System.Collections.Concurrent;
using Lexfield.Contracts;
using Lexfield.Notifier;
using Microsoft.Data.SqlClient;

namespace Lexfield.Notifier.Tests.CrashWindow;

internal sealed class RecordingSender(bool crashAfterSend = false) : ISender
{
    private readonly ConcurrentQueue<SentCall> _calls = new();
    private readonly TaskCompletionSource<object?> _sent =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _lockReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _releaseLock =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _recorded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _callOrder = new();
    private Task? _lockTask;

    public int Count => _calls.Count;
    public Action? StopApplication { get; set; }
    public Func<Task<int>>? RowsAtSend { get; set; }
    public bool TrackRecord { get; set; }
    public Func<Task>? WaitBeforeStop { get; set; }
    public string? LockConnectionString { get; set; }
    public IReadOnlyCollection<SentCall> Calls => _calls.ToArray();
    public IReadOnlyCollection<string> CallOrder => _callOrder.ToArray();

    public async Task SendAsync(
        string tenantId,
        TransitionEvent taskEvent,
        CancellationToken cancellationToken = default)
    {
        if (RowsAtSend is { } rowsAtSend)
        {
            var rows = await rowsAtSend();
            _calls.Enqueue(new SentCall(
                tenantId, taskEvent.TaskId, taskEvent.Version,
                rows));
            _callOrder.Enqueue(rows == 0 ? "send" : "record-before-send");
        }
        else
        {
            _calls.Enqueue(new SentCall(tenantId, taskEvent.TaskId, taskEvent.Version, null));
            _callOrder.Enqueue("send");
        }

        if (crashAfterSend)
        {
            _lockTask = HoldSentNotificationLockAsync(LockConnectionString!);
            await _lockReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            _ = Task.Run(async () =>
            {
                if (WaitBeforeStop is { } waitBeforeStop)
                    await waitBeforeStop();
                StopApplication?.Invoke();
                await Task.Delay(3000);
                _releaseLock.TrySetResult(null);
            });
        }

        _sent.TrySetResult(null);
        if (TrackRecord) _ = ObserveRecordAsync();
    }

    public Task WaitForSendAsync() => _sent.Task.WaitAsync(TimeSpan.FromSeconds(15));
    public Task WaitForRecordAsync() => _recorded.Task.WaitAsync(TimeSpan.FromSeconds(15));
    public async Task ReleaseLockAsync()
    {
        _releaseLock.TrySetResult(null);
        if (_lockTask is not null) await _lockTask;
    }
    public Task WaitForCountAsync(int expected) => WaitForAsync(
        () => Task.FromResult(Count >= expected),
        $"Notifier sender did not receive {expected} call(s).");

    private static async Task WaitForAsync(
        Func<Task<bool>> condition,
        string timeoutMessage)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        throw new TimeoutException(timeoutMessage);
    }

    private async Task HoldSentNotificationLockAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM dbo.SentNotifications WITH (TABLOCKX, HOLDLOCK);";
        await command.ExecuteScalarAsync();
        _lockReady.TrySetResult(null);
        await _releaseLock.Task;
    }

    private async Task ObserveRecordAsync()
    {
        while (RowsAtSend is { } rowsAtSend && await rowsAtSend() == 0)
            await Task.Delay(50);

        _callOrder.Enqueue("record");
        _recorded.TrySetResult(null);
    }
}

internal sealed record SentCall(
    string TenantId,
    int TaskId,
    int Version,
    int? RowsAtSend);

internal static class TransitionEventFixture
{
    public static TransitionEvent Event(int version, int taskId = 4711) => new()
    {
        TaskId = taskId,
        From = version == 1 ? null : TaskState.Created,
        To = version == 1 ? TaskState.Created : TaskState.Assigned,
        Actor = "crash-window-test",
        At = DateTimeOffset.Parse("2026-08-31T12:00:00Z"),
        Version = version,
        TeamId = "team-conveyancing",
        AssigneeId = "user:1234"
    };
}
