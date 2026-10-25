using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns an in-memory kernel and snapshot.
public sealed class GoalLandingBlockerTerminalClearTests
{
    private static readonly GoalLandingBlocker Blocker = new("untyped/BackgroundAttempt",
        "Acceptance cohort gate owns this member", "untyped/BackgroundAttempt:",
        new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(-5)), "unmapped", "unmapped");

    [Theory]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Superseded)]
    public void TerminalTransition_RecordedBlocker_ClearsAndOmitsProperty(GoalStatus status)
    {
        var snapshot = SeededSnapshot(GoalStatus.Verified);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []));
        var goal = kernel.GetGoal(new GoalId(snapshot.Id));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.All(goal.Tasks, task => Assert.Equal(WorkTaskStatus.Completed, task.Status));
        Assert.Equal(Blocker, goal.LandingBlocker);

        switch (status)
        {
            case GoalStatus.Completed:
                kernel.CompleteGoalFromMergeEvidence(goal.Id, new string('a', 40), "landed");
                break;
            case GoalStatus.Cancelled:
                kernel.CancelGoal(goal.Id, "stop");
                break;
            case GoalStatus.Superseded:
                kernel.SupersedeGoal(goal.Id, "replaced");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        Assert.Equal(status, goal.Status);
        Assert.Null(goal.LandingBlocker);
        Assert.DoesNotContain("LandingBlocker", JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id)),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.Cancelled)]
    [InlineData(GoalStatus.Superseded)]
    public void FromSnapshot_TerminalBlocker_NormalizesIdempotently(GoalStatus status)
    {
        var snapshot = SeededSnapshot(status);
        Assert.Equal(Blocker, snapshot.LandingBlocker);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []));
        var id = new GoalId(snapshot.Id);
        Assert.Equal(status, kernel.GetGoal(id).Status);
        Assert.Null(kernel.GetGoal(id).LandingBlocker);
        Assert.DoesNotContain("LandingBlocker", JsonSerializer.Serialize(kernel.ExportGoalSnapshot(id)),
            StringComparison.OrdinalIgnoreCase);

        var reloaded = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        Assert.Equal(status, reloaded.GetGoal(id).Status);
        Assert.Null(reloaded.GetGoal(id).LandingBlocker);
        Assert.DoesNotContain("LandingBlocker", JsonSerializer.Serialize(reloaded.ExportGoalSnapshot(id)),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(GoalStatus.Verified)]
    [InlineData(GoalStatus.Verifying)]
    public void FromSnapshot_LiveBlocker_PreservesRecordAndSince(GoalStatus status)
    {
        var snapshot = SeededSnapshot(status);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []));
        var goal = kernel.GetGoal(new GoalId(snapshot.Id));
        Assert.Equal(status, goal.Status);
        Assert.Equal(Blocker, goal.LandingBlocker);
        Assert.Equal(Blocker.Since, goal.LandingBlocker!.Since);
    }

    private static GoalSnapshot SeededSnapshot(GoalStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("terminal blocker lifecycle",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        return snapshot with
        {
            Status = status,
            Tasks = snapshot.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray(),
            LandingBlocker = Blocker
        };
    }
}
