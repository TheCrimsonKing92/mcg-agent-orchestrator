using Mcg.AgentOrchestrator.Core;

// Pure facts only; parallel-safe.
public sealed class FailedGoalRecoveryPolicyUnchangedInconclusiveInputsTests
{
    private const string Candidate = "candidate:v1:patch:base:manifest";
    private const string Summary = "Inspected the same candidate receipts; verification inconclusive";

    [Fact]
    public void EqualConsecutiveInputs_EscalatesWithCandidateReceiptsAndSummary()
    {
        var rounds = new FailedGoalInconclusiveRoundPair(
            new(Candidate, ["r-b", "r-a", "r-a"]), new(Candidate, ["r-a", "r-b"]));

        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts(rounds));

        Assert.Equal(FailedGoalRecoveryAction.Escalate, decision.Action);
        Assert.Equal(3, decision.DiscriminatingRung);
        Assert.Equal("verification-inconclusive-unchanged-inputs", decision.DiscriminatingEvidence);
        Assert.Contains(Candidate, decision.Reason, StringComparison.Ordinal);
        Assert.Contains("r-a, r-b", decision.Reason, StringComparison.Ordinal);
        Assert.Contains(Summary, decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualEmptyReceiptSets_Escalates()
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts(
            new(new(Candidate, []), new(Candidate, []))));

        Assert.Equal(FailedGoalRecoveryAction.Escalate, decision.Action);
        Assert.Equal("verification-inconclusive-unchanged-inputs", decision.DiscriminatingEvidence);
        Assert.Contains("receipt ids: none", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedCandidate_KeepsTransientRetry()
    {
        AssertTransient(Facts(new(new(Candidate, ["r-a"]),
            new("candidate:v1:changed:base:manifest", ["r-a"]))));
    }

    [Fact]
    public void AddedReceipt_KeepsTransientRetry()
    {
        AssertTransient(Facts(new(new(Candidate, ["r-a"]), new(Candidate, ["r-a", "r-b"]))));
    }

    [Fact]
    public void RemovedReceipt_KeepsTransientRetry()
    {
        AssertTransient(Facts(new(new(Candidate, ["r-a", "r-b"]), new(Candidate, ["r-a"]))));
    }

    [Fact]
    public void FirstInconclusiveRound_KeepsTransientRetry()
    {
        AssertTransient(Facts(new(null, new(Candidate, ["r-a"]))));
    }

    [Fact]
    public void LegacyHistoryWithoutInputs_KeepsTransientRetry()
    {
        AssertTransient(Facts(null));
    }

    [Fact]
    public void ExhaustedBudget_PrecedesUnchangedInputEscalation()
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts(
            new(new(Candidate, ["r-a"]), new(Candidate, ["r-a"])), retries: 25));

        Assert.Equal(FailedGoalRecoveryAction.Escalate, decision.Action);
        Assert.Equal("verification-inconclusive-budget-exhausted", decision.DiscriminatingEvidence);
    }

    [Fact]
    public void InputEqualityAndHashCode_IgnoreReceiptOrderAndDuplicates()
    {
        var first = new FailedGoalInconclusiveRoundInputs(Candidate, ["r-b", "r-a", "r-a"]);
        var same = new FailedGoalInconclusiveRoundInputs(Candidate, ["r-a", "r-b"]);

        Assert.Equal(first, same);
        Assert.Equal(first.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(first, new(Candidate, ["r-a"]));
    }

    private static void AssertTransient(FailedGoalRecoveryFacts facts)
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(facts);
        Assert.Equal(FailedGoalRecoveryAction.RetryTransient, decision.Action);
        Assert.Equal(RetryCause.EnvironmentApparatusFailure, decision.RetryCause);
        Assert.Equal("verification-inconclusive", decision.DiscriminatingEvidence);
    }

    private static FailedGoalRecoveryFacts Facts(FailedGoalInconclusiveRoundPair? rounds, int retries = 1) =>
        new(new GoalId("goal-inconclusive"), GoalLifecycleState.Failed,
            automaticAcceptanceRetryCount: 0, maxCriterionRetries: 2, maxTransientAttempts: 24,
            contextVersion: "context-v1", tasks: [new FailedGoalRecoveryTaskFacts(
                new TaskId("tester-inconclusive"), AgentRole.Tester, WorkTaskStatus.Failed,
                HasLiveProcess: false, IsExitedWithoutAppliedCompletion: false, AttemptIdentity: "round",
                OutcomeKind: DispatchOutcomeKind.VerificationInconclusive,
                RecoveryRecommendation: RecoveryRecommendation.AutoRetry, OutcomeClass: TaskOutcomeClass.UnknownEra,
                EvidenceSummary: Summary, StaleRecoveryDisposition: FailedGoalStaleRecoveryDisposition.None,
                StaleRecoveryDiagnostic: null, EmptyOutputRetryCount: retries, CriterionRetryCount: 0,
                AutomaticRetryCause: RetryCause.EnvironmentApparatusFailure, ProviderFailureKind: null,
                ExitCode: 0, Command: "verify", RetryBackoff: TimeSpan.FromSeconds(7), InconclusiveRounds: rounds)],
            terminalEscalationReason: "terminal failure");
}
