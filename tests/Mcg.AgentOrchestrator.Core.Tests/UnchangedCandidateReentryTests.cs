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

    [Fact]
    public void DeveloperRepairOnSameCandidateDoesNotReopenInvalidatedTester()
    {
        var (kernel, goal, developer, tester, identity) = PriorTesterRejection();

        kernel.RetryTask(goal.Id, developer.Id, "Address the Tester finding", RetryCause.NewTestFinding);
        Assert.Null(tester.LatestRoleInputRetryAt);
        Assert.Null(AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot())
            .GetGoal(goal.Id).FindTask(tester.Id).LatestRoleInputRetryAt);

        kernel.RecordTaskDispatch(goal.Id, developer.Id,
            new TaskDispatchRecord("worker", "command", "C:\\repo", DateTimeOffset.UtcNow,
                CandidateIdentity: identity));
        kernel.RecordTaskVerification(goal.Id, developer.Id,
            new TaskVerificationRecord("command", "C:\\repo", 0, "ok", "", DateTimeOffset.UtcNow,
                WorkerResultPresent: true, CandidateIdentity: identity));

        Assert.Equal(AgentRole.Tester, UnchangedCandidateRule.Evaluate(goal, tester, identity)?.Role);
    }

    [Fact]
    public void DirectTesterRetryAfterInheritedResetReopensSameCandidate()
    {
        var (kernel, goal, developer, tester, identity) = PriorTesterRejection();
        kernel.RetryTask(goal.Id, developer.Id, "Address the Tester finding", RetryCause.NewTestFinding);

        kernel.RetryTask(goal.Id, tester.Id, "Read new Tester guidance", RetryCause.CriterionEvidenceOwnerMismatch);

        Assert.NotNull(tester.LatestRoleInputRetryAt);
        Assert.Null(UnchangedCandidateRule.Evaluate(goal, tester, identity));
    }

    [Fact]
    public void InheritedResetDoesNotEraseEarlierDirectTesterInput()
    {
        var (kernel, goal, developer, tester, identity) = PriorTesterRejection();
        kernel.RetryTask(goal.Id, tester.Id, "Read new Tester guidance", RetryCause.CriterionEvidenceOwnerMismatch);
        var addressedAt = tester.LatestRoleInputRetryAt;
        kernel.RecordTaskDispatch(goal.Id, tester.Id,
            new TaskDispatchRecord("worker", "command", "C:\\repo", DateTimeOffset.UtcNow,
                CandidateIdentity: identity));

        kernel.RetryTask(goal.Id, developer.Id, "Address the Tester finding", RetryCause.NewTestFinding);

        Assert.Null(tester.LastDispatch);
        Assert.Equal(addressedAt, tester.LatestRoleInputRetryAt);
        Assert.Null(UnchangedCandidateRule.Evaluate(goal, tester, identity));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Developer, TaskSpec Tester,
        CandidateIdentity Identity) PriorTesterRejection()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement the change", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Test the change", AgentRole.Tester);
        var goal = kernel.CreateGoal("Repair a Tester finding", [developer, tester]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var identity = new CandidateIdentity("patch", "base", "manifest");
        foreach (var task in new[] { developer, tester })
        {
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("worker", "command", "C:\\repo", DateTimeOffset.UtcNow.AddMinutes(-2),
                    CandidateIdentity: identity));
            kernel.RecordTaskVerification(goal.Id, task.Id,
                new TaskVerificationRecord("command", "C:\\repo",
                    task == tester ? 1 : 0, task == tester ? "finding" : "ok", "",
                    DateTimeOffset.UtcNow.AddMinutes(-1), WorkerResultPresent: true,
                    CandidateIdentity: identity));
        }
        return (kernel, goal, developer, tester, identity);
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
