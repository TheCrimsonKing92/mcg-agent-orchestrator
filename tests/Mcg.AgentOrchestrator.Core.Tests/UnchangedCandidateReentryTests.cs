using Mcg.AgentOrchestrator.Core;

public sealed class UnchangedCandidateReentryTests
{
    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void SameCandidateWithoutNewInputHoldsRole(AgentRole role)
    {
        var (kernel, goal, task, identity) = PriorVerdict(role);
        kernel.RetryTask(goal.Id, task.Id, "Repeat the same inputs", RetryCause.UnchangedContextRepeat);

        Assert.Equal(role, UnchangedCandidateRule.Evaluate(goal, task, identity)?.Role);
    }

    [Theory]
    [InlineData(AgentRole.Tester, RetryCause.CriterionEvidenceOwnerMismatch)]
    [InlineData(AgentRole.Reviewer, RetryCause.ContractClarification)]
    [InlineData(AgentRole.Tester, RetryCause.EnvironmentApparatusFailure)]
    [InlineData(AgentRole.Reviewer, RetryCause.Unknown)]
    public void NewRetryInputReopensSameCandidate(AgentRole role, RetryCause cause)
    {
        var (kernel, goal, task, identity) = PriorVerdict(role);
        kernel.RetryTask(goal.Id, task.Id, "Read new retry guidance", cause);

        Assert.Null(UnchangedCandidateRule.Evaluate(goal, task, identity));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, CandidateIdentity Identity) PriorVerdict(AgentRole role)
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Review the candidate", role);
        var goal = kernel.CreateGoal("Review the candidate", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var identity = new CandidateIdentity("patch", "base", "manifest");
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("worker", "command", "C:\\repo", DateTimeOffset.UtcNow.AddMinutes(-2),
                CandidateIdentity: identity));
        kernel.RecordTaskVerification(goal.Id, task.Id,
            new TaskVerificationRecord("command", "C:\\repo", 0, "ok", "", DateTimeOffset.UtcNow.AddMinutes(-1),
                WorkerResultPresent: true, CandidateIdentity: identity));
        return (kernel, goal, task, identity);
    }
}
