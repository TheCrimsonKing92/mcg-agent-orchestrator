using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DeveloperDeferredNoChangeClassificationTests
{
    private static readonly string Candidate = new('b', 40);

    [Xunit.Fact]
    public void MarkerMismatchWithCompleteWorkerOutputFailsClosed()
    {
        var task = RetriedDeveloper();
        var marker = new DeferredNoChangeOutcome(Candidate, ["AlphaTests"],
            "NO_CHANGE: the candidate has the fix.").FormatMarker();
        var retained = Output("AlphaTests");
        var verification = new TaskVerificationRecord(
            "test.exe", "C:\\repo", 0, retained, marker, DateTimeOffset.UtcNow,
            WorkerResultPresent: true, HasCommittedChanges: false,
            FullStandardOutput: Output("BetaTests"));

        var outcome = DispatchFailureClassifier.Classify(task, verification,
            workerResultPresent: true, hasCommittedChanges: false);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.Contains("marker disagrees", outcome.EvidenceSummary, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MatchingCompleteWorkerOutputKeepsTypedOutcome()
    {
        var task = RetriedDeveloper();
        var marker = new DeferredNoChangeOutcome(Candidate, ["AlphaTests"],
            "NO_CHANGE: the candidate has the fix.").FormatMarker();
        var verification = new TaskVerificationRecord(
            "test.exe", "C:\\repo", 0, Output("AlphaTests"), marker,
            DateTimeOffset.UtcNow, WorkerResultPresent: true, HasCommittedChanges: false);

        var outcome = DispatchFailureClassifier.Classify(task, verification,
            workerResultPresent: true, hasCommittedChanges: false);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
    }

    private static TaskSpec RetriedDeveloper()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Retry candidate", AgentRole.Developer);
        var goal = kernel.CreateGoal("Deferred no-change classification", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RetryTask(goal.Id, task.Id, "Review candidate again.");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli", "codex exec", "C:\\repo", DateTimeOffset.UtcNow,
            BaseCommit: Candidate));
        return task;
    }

    private static string Output(string testClass) =>
        "NO_CHANGE: the candidate has the fix.\nWORKER_RESULT:\nfiles: none\n" +
        "commands: none\ntests: deferred - " + testClass +
        "\ncommit: none\nblockers: none\nassigned_scope_complete: true\n" +
        "model_fit: test/model - adequate - fixture\nskills: none\n" +
        "confidence: high\nEND_WORKER_RESULT";
}
