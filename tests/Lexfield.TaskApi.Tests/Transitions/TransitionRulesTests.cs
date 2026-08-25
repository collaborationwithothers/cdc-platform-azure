using Lexfield.Contracts;
using Lexfield.TaskApi.Transitions;

namespace Lexfield.TaskApi.Tests.Transitions;

public sealed class TransitionRulesTests
{
    [Fact]
    public void StateMachineAcceptsExactlyTheLegalTransitions()
    {
        HashSet<(TaskState From, TaskState To)> legal =
        [
            (TaskState.Created, TaskState.Assigned),
            (TaskState.Assigned, TaskState.InProgress),
            (TaskState.InProgress, TaskState.Submitted),
            (TaskState.Submitted, TaskState.QA),
            (TaskState.QA, TaskState.Completed),
            (TaskState.Completed, TaskState.Delivered),
            (TaskState.QA, TaskState.InProgress)
        ];

        foreach (var from in Enum.GetValues<TaskState>())
            foreach (var to in Enum.GetValues<TaskState>())
                Assert.Equal(legal.Contains((from, to)), TransitionRules.IsLegal(from, to));
    }

    [Fact]
    public void AggregateIdCombinesTenantAndLocalTaskId()
    {
        Assert.Equal("tenant-a-4711", TransitionRules.AggregateId("tenant-a", 4711));
    }
}
