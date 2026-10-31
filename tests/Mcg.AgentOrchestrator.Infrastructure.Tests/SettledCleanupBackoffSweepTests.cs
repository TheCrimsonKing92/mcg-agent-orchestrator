using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: real git, cleanup state, clock and journals are confined to each case's root.
public sealed class SettledCleanupBackoffSweepTests : CliCommandTestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void StandingDisposition_NoArtifacts_ClearsBackoff(bool landed, bool cancelled) =>
        Check(landed: landed, cancelled: cancelled, expectCleared: true);

    [Fact]
    public void StandingDisposition_NoBackoff_ProducesNoResult() => Check(withBackoff: false);

    [Fact]
    public void ExistingFolder_KeepsBackoffInHelperAndSweep() => Check(withFolder: true);

    [Fact]
    public void CancelledWithBranch_KeepsBackoff() => Check(cancelled: true, withBranch: true);

    private void Check(
        bool landed = false, bool cancelled = false, bool withBackoff = true,
        bool withFolder = false, bool withBranch = false, bool expectCleared = false)
    {
        var root = CreateAcceptanceRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var created = kernel.CreateGoal("Settled goal", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(created.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(created.Id, created.Tasks[0].Id, WorkTaskStatus.Completed, "done");
            kernel = WithGoalStatus(kernel, created.Id, cancelled ? GoalStatus.Cancelled : GoalStatus.Completed);
            var goal = kernel.GetGoal(created.Id);
            var path = GoalWorktrees.WorktreePath(root, goal.Id);
            var main = RunGitOutput(root, "rev-parse", "main").Trim();
            if (withBranch)
                RunGit(root, "branch", GoalWorktrees.BranchName(goal.Id), main);
            if (withFolder)
                Directory.CreateDirectory(path);
            else
                Assert.False(Directory.Exists(path));
            GoalOperationJournal.RecordTerminalDisposition(root, goal, landed
                ? new GoalTerminalDisposition(GoalTerminalDispositionKind.Landed, "landed", GoalTerminalDispositionSource.MergeEvidence, main)
                : new GoalTerminalDisposition(GoalTerminalDispositionKind.Retired, "retired"));
            var hooks = CreateIsolatedCleanupContext(workspace, new GoalWorktreeCleanupHooks
            {
                CleanupUtcNow = () => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                ResetSandboxAcl = (_, _) => { },
                FindLockHoldersForCleanup = _ => [],
                ProcessCommandLineSnapshot = _ => new ProcessCommandLineSnapshot(new Dictionary<int, string>())
            }).Hooks;
            const string reason = "remove:goal-mark-landed-deferred";
            GoalWorktreeCleanupBackoff? before = null;
            if (withBackoff)
            {
                before = GoalWorktrees.RecordGoalCleanupNeeded(root, goal.Id, reason, hooks);
                Assert.NotNull(before);
                Assert.Equal(reason, before.Reason);
                if (withFolder)
                {
                    Assert.Null(GoalWorktrees.ClearSettledGoalCleanupBackoff(root, goal.Id, hooks));
                    Assert.Equal(before, GoalWorktrees.TryGetCleanupBackoff(root, goal.Id, hooks));
                }
            }
            else
            {
                Assert.Null(GoalWorktrees.ClearSettledGoalCleanupBackoff(root, goal.Id, hooks));
            }

            var result = TerminalGoalSweep.Run(kernel, root, "main", cleanupHooks: hooks);

            if (expectCleared)
            {
                var item = Assert.Single(result.Goals);
                Assert.Equal(goal.Id, item.GoalId);
                var repair = Assert.Single(item.Repairs);
                Assert.Equal("settled-cleanup-backoff-cleared", repair.Kind);
                Assert.Contains($"disposition={(landed ? "landed-merge-evidence" : "retired")}", repair.Evidence, StringComparison.Ordinal);
                Assert.Contains($"reason={reason}", repair.Evidence, StringComparison.Ordinal);
                Assert.Contains($"skip_until_utc={before!.SkipUntilUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}", repair.Evidence, StringComparison.Ordinal);
                Assert.Equal("excluded", repair.Command);
                Assert.Null(GoalWorktrees.TryGetCleanupBackoff(root, goal.Id, hooks));
                Assert.Null(GoalWorktrees.ClearSettledGoalCleanupBackoff(root, goal.Id, hooks));
            }
            else
            {
                Assert.Empty(result.Goals);
                Assert.Equal(before, GoalWorktrees.TryGetCleanupBackoff(root, goal.Id, hooks));
            }
            if (withBranch)
                Assert.Equal(main, RunGitOutput(root, "rev-parse", GoalWorktrees.BranchName(goal.Id)).Trim());
            if (withFolder)
                Assert.True(Directory.Exists(path));
        }
        finally { CleanupAcceptanceRepository(root, null); }
    }
}
