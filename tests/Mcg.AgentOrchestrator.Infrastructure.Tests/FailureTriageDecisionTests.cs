using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class FailureTriageDecisionTests : WorkerDispatchTestSupport
{
    [Xunit.Fact(DisplayName = "FailureTriagePlanner_uses_typed_preflight_outcome_for_permission_repair")]
    public void FailureTriagePlannerUsesTypedPreflightOutcomeForPermissionRepair()
    {
        var repo = CreateSeededDispatchRepository();
        try
        {
            var (kernel, goal, task, agents) = CreateActiveGoal("Typed preflight triage");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "codex preflight",
                repo,
                1,
                string.Empty,
                "sandbox preflight failed: Low Integrity token could not initialize the workspace",
                DateTimeOffset.UtcNow));

            var item = FailureTriagePlanner.Build(
                    kernel,
                    goal,
                    agents,
                    repo,
                    AutonomyPolicy.Observe)
                .Items
                .Single(item => item.TaskId == task.Id);

            Assert.Equal(FailureTriageCause.MissingWorkerPermissions, item.Cause);
            Assert.Equal("worker-profile-check", item.SuggestedCommand);
            Assert.Contains("sandbox-preflight-failure", item.Explanation, StringComparison.Ordinal);
        }
        finally
        {
            _ = GoalWorktrees.DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "FailureTriagePlanner_does_not_treat_quoted_permission_test_text_as_apparatus_failure")]
    public void FailureTriagePlannerDoesNotTreatQuotedPermissionTestTextAsApparatusFailure()
    {
        var repo = CreateSeededDispatchRepository();
        try
        {
            var (kernel, goal, task, agents) = CreateActiveGoal("Permission negative control");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "dotnet test --filter PermissionTests",
                repo,
                1,
                "PermissionTests failed: expected 'access denied' but assertion reported 'not writable'.",
                "1 test failed",
                DateTimeOffset.UtcNow));

            var item = FailureTriagePlanner.Build(
                    kernel,
                    goal,
                    agents,
                    repo,
                    AutonomyPolicy.Observe)
                .Items
                .Single(item => item.TaskId == task.Id);

            Assert.Equal(FailureTriageCause.FailedVerification, item.Cause);
            Assert.Equal(FailureTriageAction.RetryWithNote, item.Action);
            Assert.StartsWith("retry 1 ", item.SuggestedCommand, StringComparison.Ordinal);
            Assert.DoesNotContain("worker-profile-check", item.SuggestedCommand, StringComparison.Ordinal);
        }
        finally
        {
            _ = GoalWorktrees.DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalHealthEvaluator_uses_typed_task_status_instead_of_recovery_display_text")]
    public void GoalHealthEvaluatorUsesTypedTaskStatus()
    {
        var repo = CreateSeededDispatchRepository();
        try
        {
            var (kernel, goal, task, agents) = CreateActiveGoal("Typed health");
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "diagnostic text without outcome keywords");

            var health = GoalHealthEvaluator.Build(
                kernel,
                goal,
                agents,
                WorkerProfileCatalog.Default(),
                repo,
                AutonomyPolicy.Observe);

            Assert.Equal(GoalHealthDisposition.Blocked, health.Disposition);
            Assert.Equal(25, health.Score);
            Assert.Contains("task status is failed", Assert.Single(health.Reasons), StringComparison.Ordinal);
        }
        finally
        {
            _ = GoalWorktrees.DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalHealthEvaluator_does_not_reclassify_completed_task_from_non_success_classifier_detail")]
    public void GoalHealthEvaluatorDoesNotReclassifyCompletedTaskFromNonSuccessClassifierDetail()
    {
        var repo = CreateSeededDispatchRepository();
        try
        {
            var (kernel, goal, task, agents) = CreateActiveGoal("Completed health negative control");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "worker verification",
                repo,
                0,
                string.Join(Environment.NewLine,
                    "WORKER_RESULT:",
                    "files: none",
                    "commands: none",
                    "tests: fail - quoted historical result",
                    "commit: none",
                    "blockers: none",
                    "model_fit: OpenAI/gpt-5.6-sol - adequate - test",
                    "skills: none",
                    "confidence: high",
                    "END_WORKER_RESULT"),
                string.Empty,
                DateTimeOffset.UtcNow));
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Accepted by the owning workflow.");

            var health = GoalHealthEvaluator.Build(
                kernel,
                goal,
                agents,
                WorkerProfileCatalog.Default(),
                repo,
                AutonomyPolicy.Observe);

            Assert.NotEqual(GoalHealthDisposition.Blocked, health.Disposition);
            Assert.DoesNotContain(health.Reasons, reason => reason.Contains("failed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _ = GoalWorktrees.DeleteDirectory(repo);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, IReadOnlyList<AgentDefinition> Agents)
        CreateActiveGoal(string objective)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement the requested change", AgentRole.Developer);
        var goal = kernel.CreateGoal(objective, [task]);
        var agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(goal.Id, agents);
        return (kernel, goal, task, agents);
    }
}
