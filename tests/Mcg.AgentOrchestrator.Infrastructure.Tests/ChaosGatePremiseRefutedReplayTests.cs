using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ChaosGatePremiseRefutedReplayTests : ChaosGateTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer, true)]
    [Xunit.InlineData(AgentRole.Developer, false)]
    [Xunit.InlineData(AgentRole.Tester, false)]
    public void Refuted412ea10cPremiseWaitsWithoutAutomaticRetry(AgentRole role, bool hasCommit)
    {
        // Reconstruct the documented round shape; the historical stdout is not packaged.
        var root = CreateSeededRepo();
        var output = """
            WORKER_RESULT:
            files: {files}
            commands: inspect repository
            tests: {tests}
            commit: {commit}
            blockers: premise-invalid - repository already satisfies the hypothesis; seed.txt is evidence
            assigned_scope_complete: false
            model_fit: OpenAI/test - adequate - premise inspection - found refuting evidence
            skills: none
            confidence: high
            END_WORKER_RESULT
            """;
        var clock = new ReplayClock { UtcNow = DispatchedAt };
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Inspect the disputed premise", role);
        var goal = kernel.CreateGoal("Replay the refuted 412ea10c premise", [task]);
        var agent = new AgentDefinition(new AgentId("worker"), "worker", role,
            new ModelProfile("OpenAI", "test", ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        var baseCommit = ReadGit(worktree, ["rev-parse", "HEAD"]);
        if (hasCommit)
            CommitSourceFile(worktree, "src/Feature.cs", "// existing behavior refutes the premise");
        Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
        Assert.Equal(hasCommit ? 1 : 0, int.Parse(ReadGit(worktree, ["rev-list", "--count", $"{baseCommit}..HEAD"])));
        var logRoot = Path.Combine(root, "logs");
        Directory.CreateDirectory(logRoot);
        var stdoutPath = Path.Combine(logRoot, "developer.out.log");
        var stderrPath = Path.Combine(logRoot, "developer.err.log");
        var exitPath = Path.Combine(logRoot, "developer.exit.txt");
        File.WriteAllText(stdoutPath, output
            .Replace("{files}", hasCommit ? "src/Feature.cs" : "none", StringComparison.Ordinal)
            .Replace("{commit}", hasCommit ? ReadGit(worktree, ["rev-parse", "HEAD"]) : "none", StringComparison.Ordinal)
            .Replace("{tests}", role == AgentRole.Tester ? "not-run - premise refuted before verification" : "deferred - ApiTests",
                StringComparison.Ordinal));
        File.WriteAllText(stderrPath, string.Empty);
        File.WriteAllText(exitPath, "0");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", worktree,
            clock.UtcNow, ProviderName: "OpenAI", ModelName: "test", TaskComplexity: TaskComplexity.Simple,
            BaseCommit: baseCommit));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt", worktree,
            stdoutPath, stderrPath, exitPath, clock.UtcNow, null, null));
        clock.UtcNow = CommittedAt.AddMinutes(1);

        new BackgroundDispatchRunner(clock: clock, isStillRunning: _ => false).RefreshLatestProcess(kernel, goal.Id, task.Id);

        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification!);
        Assert.Equal(0, task.LastVerification!.ExitCode);
        Assert.Equal(0, task.LastVerification.ObservedRootExitCode);
        Assert.DoesNotContain("required-file-change-evidence-missing", task.LastVerification.StandardError, StringComparison.Ordinal);
        Assert.Equal(TaskOutcomeClass.Finding, outcome.OutcomeClass);
        Assert.Equal("premise-refuted", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Null(AutomaticWorkerRetryCause.Resolve(task));
        Assert.False(task.LastVerification!.AssignedScopeComplete);
        Assert.Equal(hasCommit, task.LastVerification.HasCommittedChanges);
        Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
        Assert.Equal(hasCommit ? 1 : 0, int.Parse(ReadGit(worktree, ["rev-list", "--count", $"{baseCommit}..HEAD"])));
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.StartsWith($"{role} reported premise-invalid:", request.Question, StringComparison.Ordinal);
        Assert.Contains("seed.txt", request.Question, StringComparison.Ordinal);
        Assert.Contains("Clarify, supersede, or abandon", request.Question, StringComparison.Ordinal);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, task.LastVerification);
        Assert.Same(request, Assert.Single(kernel.GetPendingHumanInput(goal.Id)));

        var retries = 0;
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithCause: (goalId, taskId, message, _, cause) =>
            {
                retries++;
                return kernel.RetryTask(goalId, taskId, message, retryCause: cause);
            });
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(0, retries);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
        Assert.Empty(task.CriterionRetryFeedback);
        Assert.Equal(0, task.CriterionRetryCount);
        Assert.DoesNotContain(goal.Timeline, evt => evt.Kind is ProgressKind.TaskRetried or ProgressKind.TaskFailed);

        var classification = TaskOutcomeClassifier.FromTimeline(goal.Timeline, task.Id, task.Status);
        Assert.Equal(TaskOutcomeClass.Finding, classification.Class);
        var scorecard = Assert.Single(ModelOutcomeScorecard.Build(kernel.Goals));
        Assert.Equal(1, scorecard.Findings);
        Assert.Equal(0, scorecard.Failed);
        Assert.Equal(0, scorecard.Completed);
        Assert.Equal(0, scorecard.RealFailures);
        var duration = Assert.Single(TaskDurationReport.BuildByRoleAndComplexity(kernel.Goals));
        Assert.Equal(1, duration.FindingAttemptCount);
        Assert.Equal(0, duration.FailedAttemptCount);
        Assert.Equal(0, duration.RealFailureAttemptCount);
        Assert.Null(duration.MedianLegitimateRuntime);
        Assert.Equal(TimeSpan.Zero, duration.MedianFailureInterventionOverhead);
    }

    private sealed class ReplayClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
