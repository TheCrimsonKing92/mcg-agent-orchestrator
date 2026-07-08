using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class LandingExecutorTests
{
    [Xunit.Fact(DisplayName = "LandingExecutor_failed_count_excludes_auto_recovered_empty_output_flake")]
    public void LandingExecutorFailedCountExcludesAutoRecoveredEmptyOutputFlake()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer);
        var task = goal.Tasks.Single();

        FlakeThenPass(kernel, goal, task);

        Assert.Equal(0, LandingExecutor.CountFailedVerifications(goal));
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_failed_count_includes_recovered_real_failure")]
    public void LandingExecutorFailedCountIncludesRecoveredRealFailure()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer);
        var task = goal.Tasks.Single();

        FailThenPass(kernel, goal, task);

        Assert.Equal(1, LandingExecutor.CountFailedVerifications(goal));
    }

    [Xunit.Fact(DisplayName = "LandingDecision_escalates_when_two_genuine_failed_tasks_recovered")]
    public void LandingDecisionEscalatesWhenTwoGenuineFailedTasksRecovered()
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer, AgentRole.Tester);

        foreach (var task in goal.Tasks)
        {
            FailThenPass(kernel, goal, task);
        }

        var failedCount = LandingExecutor.CountFailedVerifications(goal);
        var decision = LandingDecisionEngine.Decide(new LandingInputs(
            RepositoryChangeClassifier.Classify(["README.md"]),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: failedCount));

        Assert.Equal(LandingDecisionEngine.RepeatedFailureThreshold, failedCount);
        if (decision is not LandingDecision.Escalate escalation)
        {
            throw new InvalidOperationException("Expected landing escalation.");
        }

        Assert.True(escalation.Reason.Contains("repeated failures", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "LandingExecutor_applies_landed_backlog_add_proposal_once")]
    public void LandingExecutorAppliesLandedBacklogAddProposalOnce()
    {
        var repo = CreateGitRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var (kernel, goal) = CreateVerifiedGoal(repo);
            var goalBranch = GoalWorktrees.BranchName(goal.Id);
            RunGit(repo, "checkout", "-b", goalBranch);
            Directory.CreateDirectory(Path.Combine(repo, ".orchestrator-proposals"));
            File.WriteAllText(Path.Combine(repo, ".orchestrator-proposals", "backlog-add-proposed-follow-up.md"), """
                ---
                kind: backlog-add
                title: Proposed follow-up
                ---
                Body from a landed proposal.
                """);
            RunGit(repo, "add", ".orchestrator-proposals/backlog-add-proposed-follow-up.md");
            RunGit(repo, "commit", "-m", "Add state-effect proposal");
            RunGit(repo, "checkout", "main");

            var result = LandingExecutor.Execute(kernel, goal, workspace);

            Assert.True(result.MainAdvanced);
            var items = new BacklogStore(workspace.BacklogStorePath).ListAsync(includeAll: true).GetAwaiter().GetResult();
            var item = Assert.Single(items);
            Assert.Equal("proposed-follow-up", item.Id);
            Assert.Equal("Proposed follow-up", item.Title);
            Assert.Equal(goal.Id.Value, item.SourceGoalId);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            Assert.Contains(journal.LatestByOperation, entry =>
                entry.Operation.StartsWith("conductor:state-effect:", StringComparison.Ordinal) &&
                entry.Status == GoalOperationStatus.Completed);
            Assert.Contains(kernel.GetTimeline(goal.Id), evt =>
                evt.Message.Contains("State-effect proposal applied", StringComparison.Ordinal));

            var reapplied = StateEffectProposalApplier.ApplyLandedProposals(
                kernel,
                goal,
                workspace,
                [".orchestrator-proposals/backlog-add-proposed-follow-up.md"]);

            Assert.Single(reapplied);
            Assert.False(reapplied[0].Applied);
            var afterReapply = new BacklogStore(workspace.BacklogStorePath).ListAsync(includeAll: true).GetAwaiter().GetResult();
            Assert.Single(afterReapply);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal(params AgentRole[] roles)
    {
        var kernel = new AgentOrchestratorKernel();
        var tasks = roles
            .Select(role => new TaskSpec(TaskId.New(), $"Run {role} task.", role))
            .ToArray();
        var goal = kernel.CreateGoal("Landing count test", tasks);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (kernel, goal);
    }

    private static void FlakeThenPass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "silent-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "silent-agent",
            "C:\\repo",
            0,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow));
        kernel.RetryTask(goal.Id, task.Id, "Auto-retry transient empty-output dispatch flake.");
        Pass(kernel, goal, task);
    }

    private static void FailThenPass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "failing-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "failing-agent",
            "C:\\repo",
            1,
            "attempted work",
            "test failed",
            DateTimeOffset.UtcNow));
        kernel.RetryTask(goal.Id, task.Id, "Fix real failure.");
        Pass(kernel, goal, task);
    }

    private static void Pass(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        Dispatch(kernel, goal, task, "passing-agent");
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, new TaskVerificationRecord(
            "passing-agent",
            "C:\\repo",
            0,
            "WORKER_RESULT: tests pass",
            string.Empty,
            DateTimeOffset.UtcNow));
    }

    private static void Dispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string command)
    {
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "test-worker",
            command,
            "C:\\repo",
            DateTimeOffset.UtcNow));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateVerifiedGoal(string repo)
    {
        var (kernel, goal) = CreateGoal(AgentRole.Developer);
        var task = goal.Tasks.Single();
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        return (kernel, goal);
    }

    private static string CreateGitRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-landing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.invalid");
        RunGit(root, "config", "user.name", "Tests");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial" + Environment.NewLine);
        RunGit(root, "add", "README.md");
        RunGit(root, "commit", "-m", "Initial");
        return root;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var result = GitCli.Run(workingDirectory, arguments);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.Error}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
