using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsAwaitingVerification
{
    [Xunit.Fact(DisplayName = "ConductorDriver_AwaitingVerification_names_missing_task_evidence")]
    public void ConductorDriverAwaitingVerificationNamesMissingTaskEvidence()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");

        var result = MakeDriver(getFacts: _ => GoalLifecycleFacts.None)
            .AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.AwaitingVerification, held.State);
        Assert.Contains(task.Id.Value[..8], held.Reason, StringComparison.Ordinal);
        Assert.Contains(nameof(VerificationGateStatus.MissingVerification), held.Reason, StringComparison.Ordinal);
        Assert.Contains("no verification evidence", held.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auto-reconcile", held.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_AwaitingVerification_bounds_details_before_reason_sanitization")]
    public void ConductorDriverAwaitingVerificationBoundsDetailsBeforeReasonSanitization()
    {
        var kernel = new AgentOrchestratorKernel();
        var tasks = new[]
        {
            new TaskSpec(TaskId.New(), "First blocked task", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Second blocked task", AgentRole.Tester),
            new TaskSpec(TaskId.New(), "Third blocked task", AgentRole.Researcher),
            new TaskSpec(TaskId.New(), "Fourth blocked task", AgentRole.Reviewer)
        };
        var goal = kernel.CreateGoal("Bound verification hold details", tasks);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        foreach (var task in tasks)
        {
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                new TaskVerificationRecord(
                    $"dotnet test {new string('x', 600)}",
                    "C:\\work",
                    1,
                    "",
                    "failed",
                    DateTimeOffset.UtcNow));
        }

        var result = MakeDriver(getFacts: _ => GoalLifecycleFacts.None)
            .AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.True(held.Reason.Length <= 512, $"Hold reason was {held.Reason.Length} characters: {held.Reason}");
        var sanitizedReason = ConductorBatchLoop.SanitizeReason(held.Reason);
        foreach (var task in tasks.Take(3))
        {
            Assert.Contains(task.Id.Value[..8], held.Reason, StringComparison.Ordinal);
            Assert.Contains(task.Id.Value[..8], sanitizedReason, StringComparison.Ordinal);
        }

        Assert.Contains("plus 1 more unsatisfied task gate", held.Reason, StringComparison.Ordinal);
    }
}
