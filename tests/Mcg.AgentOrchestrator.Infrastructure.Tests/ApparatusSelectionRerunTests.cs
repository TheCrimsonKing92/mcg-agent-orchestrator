using System.Collections.Immutable;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Pure recovery facts and check records; no I/O or shared mutable state. Parallel-safe.
public sealed class ApparatusSelectionRerunTests
{
    private const string Candidate = "candidate:v1:patch:base:manifest";
    private const string ApparatusSelection = "reviewer-focused-evidence-infrastructure-tests";
    private static readonly FailedGoalRoundCheck Apparatus =
        new("Reviewer focused evidence: Infrastructure Tests", "apparatus");

    [Fact]
    public void Evaluate_ApparatusOnly_RequestsSlugRerun()
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts([Apparatus]));

        Assert.Equal(FailedGoalRecoveryAction.RerunTimedOutSelections, decision.Action);
        Assert.Equal(3, decision.DiscriminatingRung);
        Assert.Equal(FailedGoalTimedOutSelectionRerunRule.ReasonSlug, decision.DiscriminatingEvidence);
        var rerun = Assert.IsType<FailedGoalTimedOutSelectionRerun>(decision.TimedOutRerun);
        Assert.Equal(Candidate, rerun.CandidateIdentity);
        Assert.Equal(new[] { ApparatusSelection }, rerun.Selections);
        Assert.Equal(FailedGoalTimedOutSelectionRerunRule.BuildKey(Candidate, [ApparatusSelection]), rerun.Key);
    }

    [Fact]
    public void Evaluate_RecordedApparatusKey_EscalatesUnchangedInputs()
    {
        var key = FailedGoalTimedOutSelectionRerunRule.BuildKey(Candidate, [ApparatusSelection]);

        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts([Apparatus], [key]));

        AssertUnchangedEscalation(decision);
    }

    [Fact]
    public void Evaluate_MixedTimeoutAndApparatus_RequestsOneSortedRerun()
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts([
            Apparatus,
            new("acceptance-check-timeout: a-timed-out elapsed=40m budget=40m", "timed-out"),
            Apparatus]));

        Assert.Equal(FailedGoalRecoveryAction.RerunTimedOutSelections, decision.Action);
        var rerun = Assert.IsType<FailedGoalTimedOutSelectionRerun>(decision.TimedOutRerun);
        Assert.Equal(Candidate, rerun.CandidateIdentity);
        Assert.Equal(new[] { "a-timed-out", ApparatusSelection }, rerun.Selections);
        Assert.Equal(FailedGoalTimedOutSelectionRerunRule.BuildKey(
            Candidate, ["a-timed-out", ApparatusSelection]), rerun.Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_FailingTrxWithOrWithoutApparatus_Escalates(bool includeApparatus)
    {
        var failure = new FailedGoalRoundCheck("real test failure", "failing-trx");
        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts(
            includeApparatus ? [Apparatus, failure] : [failure]));

        AssertUnchangedEscalation(decision);
    }

    [Theory]
    [InlineData(AcceptanceFailureClassifications.GateEnvironmentInterference)]
    [InlineData(AcceptanceFailureClassifications.InheritedBaselineApparatus)]
    [InlineData(AcceptanceFailureClassifications.FocusedSelectionApparatusFailure)]
    [InlineData(AcceptanceFailureClassifications.FocusedSelectionReceiptUnreadable)]
    [InlineData(AcceptanceFailureClassifications.SeededRepositoryProcessOutputApparatus)]
    [InlineData(AcceptanceFailureClassifications.SeededRepositoryApparatus)]
    [InlineData(AcceptanceFailureClassifications.AssemblyCleanupFailure)]
    [InlineData(AcceptanceFailureClassifications.BuildPhaseTimeout)]
    [InlineData(AcceptanceFailureClassifications.SharedGateApparatusInvalidated)]
    public void CheckClassification_EnvironmentalApparatus_ReturnsApparatus(string classification)
    {
        Assert.True(AcceptanceFailureClassifications.IsEnvironmentalApparatus(classification));
        var check = Check(classification);

        Assert.Equal("apparatus", TimedOutSelectionRerunMapping.CheckClassification(check));
    }

    [Theory]
    [InlineData("failing-trx")]
    [InlineData("timed-out")]
    [InlineData(AcceptanceFailureClassifications.StructuralCoverageFailed)]
    [InlineData(null)]
    public void CheckClassification_OtherOutcome_PreservesClassification(string? classification)
    {
        Assert.False(AcceptanceFailureClassifications.IsEnvironmentalApparatus(classification));

        Assert.Equal(classification, TimedOutSelectionRerunMapping.CheckClassification(Check(classification)));
    }

    [Fact]
    public void DescribeApparatus_Selections_NamesCauseAndAcceptanceOwner()
    {
        var description = TimedOutSelectionRerunMapping.DescribeApparatus(["a", "b"]);

        Assert.StartsWith("Apparatus:", description, StringComparison.Ordinal);
        Assert.Contains("a, b", description, StringComparison.Ordinal);
        Assert.Contains("failed as an apparatus outcome", description, StringComparison.Ordinal);
        Assert.Contains("not a verdict on the candidate", description, StringComparison.Ordinal);
        Assert.Contains("the acceptance gate should judge them", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "timed-out", true)]
    [InlineData(false, "apparatus", true)]
    [InlineData(false, AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, true)]
    [InlineData(false, AcceptanceFailureClassifications.BuildPhaseTimeout, true)]
    [InlineData(true, "timed-out", false)]
    [InlineData(true, "apparatus", false)]
    [InlineData(true, AcceptanceFailureClassifications.FocusedSelectionApparatusFailure, false)]
    [InlineData(false, "failing-trx", false)]
    [InlineData(false, AcceptanceFailureClassifications.StructuralCoverageFailed, false)]
    [InlineData(false, null, false)]
    public void IsUnusableRerunCheck_CheckOutcome_RequiresNonPassingTimeoutOrApparatus(
        bool passed, string? classification, bool expected)
    {
        Assert.Equal(expected, TimedOutSelectionRerunMapping.IsUnusableRerunCheck(Check(classification, passed)));
    }

    private static AcceptanceCheckResult Check(string? classification, bool passed = false) =>
        new("Reviewer focused evidence: Infrastructure Tests", passed, passed ? 0 : 1, null,
            FailureClassification: classification);

    private static void AssertUnchangedEscalation(FailedGoalRecoveryDecision decision)
    {
        Assert.Equal(FailedGoalRecoveryAction.Escalate, decision.Action);
        Assert.Equal("verification-inconclusive-unchanged-inputs", decision.DiscriminatingEvidence);
        Assert.Contains("stayed verification-inconclusive on unchanged inputs", decision.Reason,
            StringComparison.Ordinal);
        Assert.Null(decision.TimedOutRerun);
    }

    private static FailedGoalRecoveryFacts Facts(ImmutableArray<FailedGoalRoundCheck> checks,
        ImmutableArray<string> completedRerunKeys = default) =>
        new(new GoalId("goal-inconclusive"), GoalLifecycleState.Failed,
            automaticAcceptanceRetryCount: 0, maxCriterionRetries: 2, maxTransientAttempts: 24,
            contextVersion: "context-v1", tasks: [new FailedGoalRecoveryTaskFacts(
                new TaskId("tester-inconclusive"), AgentRole.Tester, WorkTaskStatus.Failed,
                HasLiveProcess: false, IsExitedWithoutAppliedCompletion: false, AttemptIdentity: "round",
                OutcomeKind: DispatchOutcomeKind.VerificationInconclusive,
                RecoveryRecommendation: RecoveryRecommendation.AutoRetry, OutcomeClass: TaskOutcomeClass.UnknownEra,
                EvidenceSummary: "same candidate receipts", StaleRecoveryDisposition: FailedGoalStaleRecoveryDisposition.None,
                StaleRecoveryDiagnostic: null, EmptyOutputRetryCount: 1, CriterionRetryCount: 0,
                AutomaticRetryCause: RetryCause.EnvironmentApparatusFailure, ProviderFailureKind: null,
                ExitCode: 0, Command: "verify", RetryBackoff: TimeSpan.Zero,
                InconclusiveRounds: new(new(Candidate, ["same-receipt"]), new(Candidate, ["same-receipt"])),
                TimedOutSelections: new(checks, completedRerunKeys))], terminalEscalationReason: "terminal failure");
}
