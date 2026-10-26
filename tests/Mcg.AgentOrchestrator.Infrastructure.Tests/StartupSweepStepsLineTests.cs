using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: formatter inputs and each sweep's repository, kernel and stores are private.
public sealed class StartupSweepStepsLineTests : CliCommandTestBase
{
    [Fact]
    public void Format_AllFieldsAppearInContractOrder()
    {
        var id = new GoalId("12345678abcdef");
        var result = new TerminalGoalSweepResult([
            new(id, "12345678", [new("completed-branch-landed-retired", "proof", "retired")], [])])
        {
            OwnedRootDurationMs = 1, GitIndexDurationMs = 2, EvidenceDurationMs = 3,
            EphemeralDurationMs = 4, AttentionDurationMs = 5, MergeEvidenceDurationMs = 6,
            GoalsDurationMs = 7, GitSpawnCount = 8, GoalsSweptCount = 9, TerminalGoalsSweptCount = 10,
            GoalDurations = [(id, 11)]
        };

        Assert.Equal("STARTUP_SWEEP_STEPS total_ms=12 owned_root_ms=1 git_index_ms=2 evidence_ms=3 ephemeral_ms=4 attention_ms=5 merge_evidence_ms=6 goals_ms=7 git_spawns=8 goals_swept=9 terminal_goals_swept=10 landed_branches_retired=1 slowest=12345678:11",
            StartupSweepStepsLine.Format(result, 12));
    }

    [Fact]
    public void Format_SevenDurations_SelectsFiveByDescendingTimeThenOrdinalPrefix()
    {
        var result = new TerminalGoalSweepResult([])
        {
            GoalDurations = [
                (new("bbbbbbbb1"), 40), (new("aaaaaaaa1"), 40), (new("eeeeeeee1"), 10),
                (new("dddddddd1"), 20), (new("cccccccc1"), 30), (new("ffffffff1"), 5), (new("gggggggg1"), 1)]
        };

        Assert.Equal("STARTUP_SWEEP_STEPS total_ms=0 owned_root_ms=0 git_index_ms=0 evidence_ms=0 ephemeral_ms=0 attention_ms=0 merge_evidence_ms=0 goals_ms=0 git_spawns=0 goals_swept=0 terminal_goals_swept=0 landed_branches_retired=0 slowest=aaaaaaaa:40,bbbbbbbb:40,cccccccc:30,dddddddd:20,eeeeeeee:10",
            StartupSweepStepsLine.Format(result, 0));
    }

    [Fact]
    public void Format_NoSweptGoals_UsesNone()
    {
        Assert.Equal("STARTUP_SWEEP_STEPS total_ms=0 owned_root_ms=0 git_index_ms=0 evidence_ms=0 ephemeral_ms=0 attention_ms=0 merge_evidence_ms=0 goals_ms=0 git_spawns=0 goals_swept=0 terminal_goals_swept=0 landed_branches_retired=0 slowest=none",
            StartupSweepStepsLine.Format(new TerminalGoalSweepResult([]), 0));
    }

    [Fact]
    public void Format_RetirementCount_UsesOnlyExactRepairKind()
    {
        var result = new TerminalGoalSweepResult([
            new(new("12345678"), "12345678", [
                new("completed-branch-landed-retired", "proof", "retired"),
                new("completed-branch-superseded", "equivalent", "retired"),
                new("completed-branch-landed-retired-extra", "other", "other")],
                [new("completed-branch-landed-retired", "blocked", "retry")]),
            new(new("23456789"), "23456789", [new("completed-branch-landed-retired", "proof", "retired")], [])]);

        Assert.Equal("STARTUP_SWEEP_STEPS total_ms=0 owned_root_ms=0 git_index_ms=0 evidence_ms=0 ephemeral_ms=0 attention_ms=0 merge_evidence_ms=0 goals_ms=0 git_spawns=0 goals_swept=0 terminal_goals_swept=0 landed_branches_retired=2 slowest=none",
            StartupSweepStepsLine.Format(result, 0));
    }

    [Fact]
    public void GoalDurationsHasOneEntryPerSweptGoal()
    {
        var root = CreateAcceptanceRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var ids = Enumerable.Range(0, 3).Select(index => kernel.CreateGoal($"Timing {index}",
                [new TaskSpec(TaskId.New(), "Original task", AgentRole.Developer)]).Id).ToArray();
            foreach (var id in ids)
                kernel = WithGoalStatus(kernel, id, GoalStatus.Completed);
            GoalOperationJournal.RecordLandingIntent(root, kernel.GetGoal(ids[0]), GoalWorktrees.BranchName(ids[0]),
                "main", RunGitOutput(root, "rev-parse", "HEAD").Trim(), "test");
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal.Id == ids[0].Value
                    ? goal with { Tasks = goal.Tasks.Select(task => task with { Description = "Changed task graph" }).ToArray() }
                    : goal).ToArray()
            });

            var result = TerminalGoalSweep.Run(kernel, root, "main",
                cleanupHooks: CreateIsolatedCleanupContext(OrchestratorWorkspace.ForDirectory(root)).Hooks);

            Assert.Contains(result.Goals, goal => goal.GoalId == ids[0] && goal.Blockers.Any(blocker => blocker.Kind == "landing-graph-changed"));
            Assert.Equal(ids.OrderBy(id => id.Value), result.ExplicitlySweptGoalIds.OrderBy(id => id.Value));
            Assert.Equal(result.ExplicitlySweptGoalIds.OrderBy(id => id.Value), result.GoalDurations.Select(item => item.GoalId).OrderBy(id => id.Value));
            Assert.Equal(3, result.GoalDurations.Select(item => item.GoalId).Distinct().Count());
            Assert.Equal(3, result.TerminalGoalsSweptCount);
        }
        finally { CleanupAcceptanceRepository(root, null); }
    }
}
