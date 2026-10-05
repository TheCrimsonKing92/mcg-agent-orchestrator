using Mcg.AgentOrchestrator.Core;
using Xunit;

public sealed class FailureClusterTouchAttributionTests
{
    [Fact]
    public void OneTouchBelongsToTheLatestClusterOnItsGoal()
    {
        var at = FailureClusterTestData.Since;
        var rows = FailureClusterReport.Build([
            new("TaskFailed", "Dispatch failed: exit code 1: codex exec", "goal", "task", at),
            new("TaskRetried", FailureClusterTestData.Green, "goal", "task", at.AddMinutes(10))
        ], [], [new("goal", null, at.AddMinutes(20))], at, at.AddDays(1));

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Sum(r => r.OperatorTouches));
        Assert.Equal(1, Assert.Single(rows, r => r.EventKind == "TaskRetried").OperatorTouches);
        Assert.Equal(0, Assert.Single(rows, r => r.EventKind == "TaskFailed").OperatorTouches);
    }

    [Fact]
    public void EqualFirstRootsUseTheOrdinalKeyRegardlessOfInputOrder()
    {
        var at = FailureClusterTestData.Since;
        FailureClusterGoalEvent[] events = [
            new("TaskFailed", "Dispatch failed: exit code 1: codex exec", "goal", "task", at),
            new("TaskRetried", FailureClusterTestData.Green, "goal", "task", at)
        ];
        foreach (var input in new[] { events, events.Reverse().ToArray() })
        {
            var rows = FailureClusterReport.Build(input, [], [new("goal", null, at)], at, at.AddDays(1));
            var owner = rows.OrderBy(r => r.Key, StringComparer.Ordinal).First();
            Assert.Equal(1, owner.OperatorTouches);
            Assert.Equal(1, rows.Sum(r => r.OperatorTouches));
        }
    }
}
