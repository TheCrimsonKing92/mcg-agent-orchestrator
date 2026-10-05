using System.Collections.Immutable;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Pure facts and isolated in-memory snapshots; parallel-safe.
public sealed class FailedGoalTimedOutSelectionRerunRuleTests
{
    private const string Candidate = "candidate:v1:patch:base:manifest";
    private const string Selection = "reviewer-focused-evidence-infrastructure-tests-fullyqualifiedname-mtptestrunnerscripttests";
    private static readonly FailedGoalRoundCheck Timeout =
        new($"acceptance-check-timeout: {Selection} elapsed=40m budget=40m", "timed-out");

    [Fact]
    public void TimeoutOnlyUnchangedRound_RequestsExactlyTheTimedOutSelection()
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts([Timeout]));

        Assert.Equal(FailedGoalRecoveryAction.RerunTimedOutSelections, decision.Action);
        Assert.Equal(3, decision.DiscriminatingRung);
        Assert.Equal(FailedGoalTimedOutSelectionRerunRule.ReasonSlug, decision.DiscriminatingEvidence);
        var rerun = Assert.IsType<FailedGoalTimedOutSelectionRerun>(decision.TimedOutRerun);
        Assert.Equal(Candidate, rerun.CandidateIdentity);
        Assert.Equal(new[] { Selection }, rerun.Selections);
        Assert.Equal(FailedGoalTimedOutSelectionRerunRule.BuildKey(Candidate, [Selection]), rerun.Key);
    }

    [Fact]
    public void TimeoutAndRealFailure_PreserveTheExactExistingEscalation()
    {
        AssertExistingEscalation(Facts([Timeout, new("real test failure", "failing-trx")]));
    }

    [Fact]
    public void AlreadyRecordedCandidateAndSelection_PreserveTheExactExistingEscalation()
    {
        AssertExistingEscalation(Facts([Timeout],
            [FailedGoalTimedOutSelectionRerunRule.BuildKey(Candidate, [Selection])]));
    }

    [Theory]
    [InlineData("other-candidate", Selection)]
    [InlineData(Candidate, "other-selection")]
    public void MarkerForAnotherPair_DoesNotConsumeThisRerun(string candidate, string selection)
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts([Timeout],
            [FailedGoalTimedOutSelectionRerunRule.BuildKey(candidate, [selection])]));
        Assert.Equal(FailedGoalRecoveryAction.RerunTimedOutSelections, decision.Action);
        Assert.Equal(new[] { Selection }, decision.TimedOutRerun!.Selections);
    }

    [Fact]
    public void MultipleTimeouts_AreDeduplicatedAndSortedOrdinally()
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts([
            new("acceptance-check-timeout: z elapsed=40m budget=40m", "timed-out"),
            Timeout, Timeout,
            new("acceptance-check-timeout: A elapsed=40m budget=40m", "timed-out")]));
        Assert.Equal(new[] { "A", Selection, "z" }, decision.TimedOutRerun!.Selections);
        Assert.Equal(FailedGoalTimedOutSelectionRerunRule.BuildKey(Candidate, ["z", Selection, "A", "z"]),
            decision.TimedOutRerun.Key);
    }

    [Theory]
    [InlineData("unparseable timeout", "timed-out")]
    [InlineData("acceptance-check-timeout:  elapsed=40m budget=40m", "timed-out")]
    [InlineData("acceptance-check-timeout: selection elapsed=40m", "timed-out")]
    [InlineData("acceptance-check-timeout: selection elapsed=40m budget=40m", null)]
    [InlineData("acceptance-check-timeout: selection elapsed=40m budget=40m", "failing-trx")]
    public void MissingPositiveTimeoutEvidence_PreservesEscalation(string name, string? classification)
    {
        AssertExistingEscalation(Facts([new(name, classification)]));
    }

    [Fact]
    public void NoNonPassingChecks_PreservesEscalation() => AssertExistingEscalation(Facts([]));

    [Fact]
    public void ChangedInputs_KeepTheExistingTransientRetry()
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts([Timeout], unchanged: false));
        Assert.Equal(FailedGoalRecoveryAction.RetryTransient, decision.Action);
        Assert.Equal("verification-inconclusive", decision.DiscriminatingEvidence);
        Assert.Null(decision.TimedOutRerun);
    }

    [Fact]
    public void RequestMarker_SurvivesSnapshotBeforeAnyReceiptExists()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Verify", AgentRole.Tester);
        var goal = kernel.CreateGoal("Durable rerun marker", [task]);
        var key = FailedGoalTimedOutSelectionRerunRule.BuildKey(Candidate, [Selection]);
        kernel.RecordFindingEvidenceRequest(goal.Id, task.Id,
            $"{FailedGoalTimedOutSelectionRerunRule.ReasonSlug}; phase=request; key={key}; candidate={Candidate}; selections={Selection}");
        var restored = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
        var keys = FailedGoalTimedOutSelectionRerunRule.ReadRecordedKeys(restored.GetGoal(goal.Id));

        Assert.Equal(new[] { key }, keys);
        AssertExistingEscalation(Facts([Timeout], keys));
    }

    private static void AssertExistingEscalation(FailedGoalRecoveryFacts facts)
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(facts);
        var oldDecision = FailedGoalRecoveryPolicy.Evaluate(Facts([]));
        Assert.Equal(FailedGoalRecoveryAction.Escalate, decision.Action);
        Assert.Equal(3, decision.DiscriminatingRung);
        Assert.Equal("verification-inconclusive-unchanged-inputs", decision.DiscriminatingEvidence);
        Assert.Equal(oldDecision.Reason, decision.Reason);
        Assert.Null(decision.TimedOutRerun);
    }

    private static FailedGoalRecoveryFacts Facts(ImmutableArray<FailedGoalRoundCheck> checks,
        ImmutableArray<string> markers = default, bool unchanged = true) =>
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
                InconclusiveRounds: new(new(Candidate, ["old-receipt"]),
                    new(Candidate, unchanged ? ["old-receipt"] : ["new-receipt"])),
                TimedOutSelections: new(checks, markers))], terminalEscalationReason: "terminal failure");
}
