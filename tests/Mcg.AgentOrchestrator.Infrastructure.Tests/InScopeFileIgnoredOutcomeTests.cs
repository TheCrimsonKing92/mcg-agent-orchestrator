using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class InScopeFileIgnoredOutcomeTests
{
    [Xunit.Fact]
    public void Classify_IgnoredScopeMarker_RetriesAsNewSourceFinding()
    {
        var task = VerifiedTask(DispatchFailureDiagnosticMarker.InScopeFileIgnored);
        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification!);

        Assert.Equal("in-scope-file-ignored", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        Assert.Equal(TaskOutcomeClass.RealFailure, outcome.OutcomeClass);
        Assert.Equal(RetryCause.NewSourceFinding, AutomaticWorkerRetryCause.Resolve(task));
        Assert.Equal(TaskOutcomeClass.RealFailure,
            TaskOutcomeClassifier.Classify(WorkTaskStatus.Failed, "in-scope-file-ignored").Class);
    }

    [Xunit.Fact]
    public void Classify_BuildCheckMarker_KeepsOperatorRecommendation()
    {
        var task = VerifiedTask(DispatchFailureDiagnosticMarker.WorkerBuildCheckFailed);
        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification!);

        Assert.Equal("worker-build-check-failed", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    }

    private static TaskSpec VerifiedTask(string marker)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Classify ignored scope file.",
            [new TaskSpec(TaskId.New(), "Implement delimiter validation.", AgentRole.Developer)]);
        var agent = new AgentDefinition(new AgentId("developer"), "Developer", AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("codex-cli", "codex exec prompt", "C:\\repo", dispatchedAt));
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "codex exec prompt", "C:\\repo", 1, "Work complete.",
            "Candidate snapshot left out in-scope files that ignore rules exclude: " +
            "cmd/schemadrift/delimiter_flag_test.go (.gitignore:1:schemadrift)." + Environment.NewLine +
            DispatchFailureDiagnosticMarker.Format(marker), dispatchedAt.AddMinutes(1),
            HasCommittedChanges: true, DispatchStartedAt: dispatchedAt));
        return task;
    }
}
