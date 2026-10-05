using Mcg.AgentOrchestrator.Core;

// Parallel-safe: isolated in-memory kernels and FakeClock; no external worker runs.
public sealed class WorkerSandboxRefusedRecordingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusalRecordingPreservesRetryBudgetAndPublishesEvidence(bool dispatchResult)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Escalate sandbox refusal",
            [new TaskSpec(TaskId.New(), "Implement scoped behavior", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = Assert.Single(goal.Tasks);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", @"C:\repo", clock.UtcNow, WorkerProviderKind: ProviderKind.OpenAICodexCli));
        var criterionRetryCount = task.CriterionRetryCount;
        var emptyOutputRetryCount = task.EmptyOutputRetryCount;
        var latestRetryAt = task.LatestRetryAt;
        var verification = new TaskVerificationRecord("codex exec", @"C:\repo", 1, string.Empty,
            @"WORKER_SANDBOX_REFUSED reason=shared-git-metadata-low-writable path=C:\repo-shared\objects",
            clock.UtcNow);

        if (dispatchResult)
        {
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
            Assert.Equal(WorkTaskStatus.Failed, task.Status);
            var failure = Assert.Single(goal.Timeline.Where(evt =>
                evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskFailed));
            Assert.Contains("rule=worker-sandbox-refused", failure.Message, StringComparison.Ordinal);
        }
        else
        {
            kernel.RecordTaskVerification(goal.Id, task.Id, verification);
        }

        Assert.NotNull(task.LastVerification);
        Assert.Equal(criterionRetryCount, task.CriterionRetryCount);
        Assert.Equal(emptyOutputRetryCount, task.EmptyOutputRetryCount);
        Assert.Equal(latestRetryAt, task.LatestRetryAt);
        Assert.DoesNotContain(goal.Timeline,
            evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried);
        var receipt = Assert.Single(goal.Timeline.Where(evt => evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote && evt.Message.Contains("CLASSIFIER ", StringComparison.Ordinal)));
        Assert.Contains("rule=worker-sandbox-refused", receipt.Message, StringComparison.Ordinal);
        Assert.Contains("outcome_class=environmental", receipt.Message, StringComparison.Ordinal);
        Assert.Contains("shared-git-metadata-low-writable", receipt.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\repo-shared\objects", receipt.Message, StringComparison.Ordinal);
        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }
}
