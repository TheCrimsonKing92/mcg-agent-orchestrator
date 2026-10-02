using Mcg.AgentOrchestrator.Core;
using static DeveloperResumedDeferredNoChangeQualifierTests;

public sealed class DeveloperResumedDeferredNoChangeClassificationTests
{
    [Xunit.Fact]
    public void QualifiedResumedCompletionUsesDeferredNoChangeRule()
    {
        var (_, task) = Scenario();
        var marker = new DeferredNoChangeOutcome(Candidate, ["ClassA"], Rationale).FormatMarker();

        var outcome = Classify(task, marker);

        Xunit.Assert.Equal(DispatchOutcomeKind.VerifiedSuccess, outcome.Kind);
        Xunit.Assert.Equal("deferred-no-change-round", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    [Xunit.Theory]
    [Xunit.InlineData(null)]
    [Xunit.InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void MarkerWithoutEarlierCommittingDispatchFailsClosed(string? resultCommit)
    {
        var (goal, task) = Scenario(resultCommit: resultCommit);
        Xunit.Assert.False(Qualify(goal, task, out _));
        var marker = new DeferredNoChangeOutcome(Candidate, ["ClassA"], Rationale).FormatMarker();

        var outcome = Classify(task, marker);

        Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Xunit.Assert.NotEqual("deferred-no-change-round", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("wrong-candidate")]
    [Xunit.InlineData("wrong-classes")]
    [Xunit.InlineData("wrong-rationale")]
    public void EarlierCommitDoesNotBypassCandidateBoundMarker(string invalid)
    {
        var (_, task) = Scenario();
        var marker = invalid == "missing" ? string.Empty : new DeferredNoChangeOutcome(
            invalid == "wrong-candidate" ? new string('c', 40) : Candidate,
            invalid == "wrong-classes" ? ["ClassB"] : ["ClassA"],
            invalid == "wrong-rationale" ? "NO_CHANGE: a different rationale." : Rationale).FormatMarker();

        var outcome = Classify(task, marker);

        Xunit.Assert.NotEqual("deferred-no-change-round", TaskOutcomeClassifier.TryExtractRule(outcome.ClassifierReceipt));
        if (invalid != "missing") Xunit.Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
    }

    private static DispatchOutcome Classify(TaskSpec task, string marker) =>
        DispatchFailureClassifier.Classify(task, new TaskVerificationRecord(
            "test.exe", "C:\\repo", 0, Output, marker, task.LastDispatch!.DispatchedAt,
            WorkerResultPresent: true, HasCommittedChanges: false),
            workerResultPresent: true, hasCommittedChanges: false);
}
