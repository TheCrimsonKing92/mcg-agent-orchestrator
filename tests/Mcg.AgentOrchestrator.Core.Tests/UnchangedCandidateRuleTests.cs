using Mcg.AgentOrchestrator.Core;

public sealed class UnchangedCandidateRuleTests
{
    [Fact]
    public void SameCandidateWithNoNewInputReturnsTypedHoldForPriorVerdict()
    {
        var (kernel, goal, task, original) = PriorVerdict();
        kernel.RetryTask(goal.Id, task.Id, "retry without new guidance", RetryCause.UnchangedContextRepeat);

        var reason = UnchangedCandidateRule.Evaluate(goal, task, original);

        Assert.NotNull(reason);
        Assert.Equal(AgentRole.Developer, reason.Role);
        Assert.Equal(task.Id, reason.PriorVerdictTaskId);
        Assert.Equal(original, reason.CandidateIdentity);
        Assert.Contains("verdict=passed", reason.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedIdentityOrMissingIdentityAllowsDispatch()
    {
        var (kernel, goal, task, _) = PriorVerdict();
        kernel.RetryTask(goal.Id, task.Id, "retry without new guidance", RetryCause.UnchangedContextRepeat);

        Assert.Null(UnchangedCandidateRule.Evaluate(goal, task, new CandidateIdentity("changed", "base", "manifest")));
        Assert.Null(UnchangedCandidateRule.Evaluate(goal, task, null));
    }

    [Fact]
    public void AcceptedRetryGuidanceReopensSameCandidate()
    {
        var (kernel, goal, task, identity) = PriorVerdict();
        kernel.RetryTask(goal.Id, task.Id, "retry without new guidance", RetryCause.UnchangedContextRepeat);
        var afterVerdict = task.VerificationHistory.Last().CompletedAt.AddMinutes(1);
        task.RecordAcceptedRetryFeedback("Read the new finding", afterVerdict);

        Assert.Null(UnchangedCandidateRule.Evaluate(goal, task, identity));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, CandidateIdentity Identity) PriorVerdict()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Do a change");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.Single(item => item.RequiredRole == AgentRole.Developer);
        var identity = new CandidateIdentity("patch", "base", "manifest");
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("worker", "command", "C:\\repo", DateTimeOffset.UtcNow,
                CandidateIdentity: identity));
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("command", "C:\\repo", 0, "ok", "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, CandidateIdentity: identity));
        return (kernel, goal, task, identity);
    }
}
