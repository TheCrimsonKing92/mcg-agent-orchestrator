using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsTerminalSweepAlreadyGoneCleanup : CliCommandTestBase
{
    [Xunit.Fact]
    public void Run_AlreadyGoneDeferredCleanup_RecordsCompletionOnce()
    {
        var root = CreateAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Complete work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Landed workspace already gone", [task]);
            cleanupGoalId = goal.Id;
            kernel.ActivateGoal(goal.Id, []);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UnixEpoch));
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
            goal = kernel.GetGoal(goal.Id);
            var mainSha = RunGitOutput(root, "rev-parse", "HEAD").Trim();
            GoalOperationJournal.RecordLandingIntent(
                root, goal, GoalWorktrees.BranchName(goal.Id), "main", mainSha, "conductor");
            GoalOperationJournal.Completed(root, goal, "conductor:land", "Landing completed.");
            GoalOperationJournal.Begin(root, goal, "conductor:cleanup",
                "Deferred goal cleanup scheduled for terminal sweep.");
            GoalOperationJournal.Failed(root, goal, "conductor:cleanup",
                "Workspace cleanup deferred to terminal sweep.");

            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Null(GoalWorktrees.TryResolve(root, goal.Id));
            Assert.Equal(string.Empty, RunGitOutput(
                root, "branch", "--list", GoalWorktrees.BranchName(goal.Id)).Trim());
            var before = GoalOperationJournal.Read(root, goal.Id);
            Assert.True(GoalOperationJournal.HasDurableLandingIntent(before));
            Assert.False(GoalOperationJournal.HasCompletedCleanupEvidence(before));
            Assert.Equal(GoalOperationStatus.Failed,
                before.LatestByOperation.Single(entry => entry.Operation == "conductor:cleanup").Status);

            RunSweep(kernel, root, goal.Id);

            var afterFirst = GoalOperationJournal.Read(root, goal.Id);
            var completion = Assert.Single(afterFirst.Entries.Where(entry =>
                entry.Operation == "conductor:cleanup" && entry.Status == GoalOperationStatus.Completed));
            Assert.Contains("already gone", completion.Detail, StringComparison.Ordinal);
            Assert.True(GoalOperationJournal.HasCompletedCleanupEvidence(afterFirst));

            RunSweep(kernel, root, goal.Id);

            var afterSecond = GoalOperationJournal.Read(root, goal.Id);
            Assert.Single(afterSecond.Entries.Where(entry =>
                entry.Operation == "conductor:cleanup" && entry.Status == GoalOperationStatus.Completed));
            Assert.Equal(afterFirst.Entries, afterSecond.Entries);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }

    private static void RunSweep(AgentOrchestratorKernel kernel, string root, GoalId goalId) =>
        TerminalGoalSweep.Run(
            kernel, root, onlyGoalId: goalId,
            gitRunner: static (directory, args) => GitCli.Run(directory, args.ToArray()),
            cleanupHooks: new GoalWorktreeCleanupHooks
            {
                BuildStorageRoot = new DotnetBuildStorageRoot(Path.Combine(root, ".orchestrator", "test-dotnet"))
            });
}
