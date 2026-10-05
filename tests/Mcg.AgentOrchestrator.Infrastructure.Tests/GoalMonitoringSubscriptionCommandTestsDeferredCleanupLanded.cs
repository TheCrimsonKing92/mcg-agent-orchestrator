using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalMonitoringSubscriptionCommandTestsDeferredCleanupLanded
{
    [Xunit.Fact]
    public void ReadLifecycleFacts_CompletedWithCleanupDeferral_DisplaysLanded()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Complete work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Landed with cleanup deferral", [task]);
            kernel.ActivateGoal(goal.Id, []);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UnixEpoch));
            kernel.CompleteGoal(goal.Id, "Acceptance completed.");
            GoalOperationJournal.Completed(root, goal, "conductor:land", "Landing completed.");
            GoalOperationJournal.Begin(root, goal, "conductor:cleanup",
                "Deferred goal cleanup scheduled for terminal sweep.");
            GoalOperationJournal.Failed(root, goal, "conductor:cleanup",
                "Workspace cleanup deferred to terminal sweep.");

            var facts = GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(workspace, goal);

            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(facts.IsBlocked);
            Assert.True(facts.IsMerged);
            Assert.False(facts.IsRecorded);
            Assert.False(facts.IsCleanedUp);
            var state = GoalLifecycle.ResolveState(goal, facts);
            Assert.Equal("Landed", ApplicationGoalStatusText.Resolve(goal.Status, state));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
