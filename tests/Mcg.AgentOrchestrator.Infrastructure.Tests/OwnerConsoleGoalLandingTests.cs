using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: local snapshots and an explicit display time zone.
public sealed class OwnerConsoleGoalLandingTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 34, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(111, "111th")]
    public void OrdinalsAreOneBasedEnglish(int position, string expected) => Assert.Equal(expected, OwnerConsoleGoalLanding.Ordinal(position));

    [Theory]
    [InlineData(GoalStatus.Completed, true, 2, "landed 15:34")]
    [InlineData(GoalStatus.Verifying, true, 2, "held: waiting for your approval")]
    [InlineData(GoalStatus.Verifying, false, 2, "gate running since 15:34")]
    [InlineData(GoalStatus.Verified, false, 2, "waiting for the gate (2nd)")]
    [InlineData(GoalStatus.AcceptanceFailed, false, null, "last gate failed 15:34: SampleTests.Fails")]
    public void CurrentStateTakesPrecedenceOverFailure(GoalStatus status, bool held, int? position, string expected)
    {
        var goal = Goal(status, held, true);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test", TimeSpan.FromHours(3), "Test", "Test");
        Assert.Equal(expected, OwnerConsoleGoalLanding.Line(goal, position, zone));
    }

    [Fact]
    public void NoLandingFactsOmitsLine() => Assert.Null(OwnerConsoleGoalLanding.Line(Goal(GoalStatus.Active, false, false), null, TimeZoneInfo.Utc));

    [Fact]
    public void InvalidQueuePositionFailsLoudly() => Assert.Throws<ArgumentOutOfRangeException>(() => OwnerConsoleGoalLanding.Ordinal(0));

    private static Goal Goal(GoalStatus status, bool held, bool failed)
    {
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111", "Landing", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(goal => goal with
        {
            Status = status, TerminatedAt = status == GoalStatus.Completed ? At : null,
            CurrentHold = held ? new("hold", "owner-review-hold", "waiting_for_approval", At) : null,
            LatestAcceptanceFailure = failed ? new(At, ["SampleTests.Fails"]) : null,
            Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "entered Verifying", At)]
        }).ToArray() });
        return harness.Kernel.Goals.Single();
    }
}
