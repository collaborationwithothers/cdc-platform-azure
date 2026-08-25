using Lexfield.Contracts;

namespace Lexfield.TaskApi.Transitions;

public static class TransitionRules
{
    private static readonly HashSet<(TaskState From, TaskState To)> Legal =
    [
        (TaskState.Created, TaskState.Assigned),
        (TaskState.Assigned, TaskState.InProgress),
        (TaskState.InProgress, TaskState.Submitted),
        (TaskState.Submitted, TaskState.QA),
        (TaskState.QA, TaskState.Completed),
        (TaskState.Completed, TaskState.Delivered),
        (TaskState.QA, TaskState.InProgress)
    ];

    public static bool IsLegal(TaskState from, TaskState to) => Legal.Contains((from, to));

    public static string AggregateId(string tenantId, int taskId) => $"{tenantId}-{taskId}";
}
