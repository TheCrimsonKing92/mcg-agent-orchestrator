using Mcg.AgentOrchestrator.Core;
using static ConductorDriverTests;

// Parallel-safe: isolated in-memory kernels and fixed fixture clocks; no processes or files.
public sealed class TesterInconclusiveSubscriptionRoundReplayTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly CandidateIdentity Candidate = new("patch", "base", "manifest");
    private static readonly string[] ReceiptIds =
    [
        "finding-evidence-31e62a69", "finding-evidence-45affd81", "finding-evidence-b0783048",
        "finding-evidence-d65827f9", "finding-evidence-ee3b78fa"
    ];
    private const string DirectoryPath = @"C:\repo";
    private const string Command = "codex verify";
    private const string Contract = """
        WORKER_RESULT:
        files: none
        commands: inspected packaged receipts
        tests: inconclusive - receipt d65827f9 verified 150 passing results; lifecycle receipt b0783048 has zero results and exit code 8
        commit: none
        blockers: none
        model_fit: fixture
        skills: none
        confidence: high
        END_WORKER_RESULT
        """;

    [Fact]
    public void RepeatedInputs_EscalateAfterSecondAndThirdRecordedRounds()
    {
        var fixture = new Fixture();
        fixture.RecordRound(1);
        fixture.Retry();
        fixture.RecordRound(2);
        AssertUnchangedEscalation(fixture);

        fixture.Retry();
        fixture.RecordRound(3);
        AssertUnchangedEscalation(fixture);
    }

    [Fact]
    public void PreviousRound_UsesOwnDispatchWhileLiveClassificationStaysIncomplete()
    {
        var fixture = TwoRounds();
        var round1 = fixture.Tester.VerificationHistory[^2];

        Assert.Equal(DispatchOutcomeKind.VerificationIncomplete,
            DispatchFailureClassifier.Classify(fixture.Tester, round1).Kind);
        Assert.Equal(DispatchOutcomeKind.VerificationInconclusive,
            DispatchFailureClassifier.ClassifyStoredRound(fixture.Tester, round1).Kind);
        Assert.NotNull(fixture.Read()!.Previous);
    }

    [Fact]
    public void MismatchedStoredAttempt_CannotBecomePreviousRound()
    {
        var fixture = TwoRounds();
        Assert.True(fixture.Read()!.InputsUnchanged);
        // The recording API rejects mismatched attempts. Corrupt the retained snapshot instead.
        var snapshot = fixture.Kernel.ExportSnapshot();
        var goal = Assert.Single(snapshot.Goals);
        var task = goal.Tasks.Single(item => item.Id == fixture.Tester.Id.Value);
        var history = task.VerificationHistory!.ToArray();
        history[^2] = history[^2] with { DispatchAttemptAt = Start.AddSeconds(1) };
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goal with
            {
                Tasks = goal.Tasks.Select(item => item.Id == task.Id
                    ? item with { VerificationHistory = history } : item).ToArray()
            }]
        });
        var restoredGoal = restored.GetGoal(fixture.Goal.Id);
        var tester = restored.GetTask(fixture.Goal.Id, fixture.Tester.Id);

        Assert.Equal(DispatchOutcomeKind.VerificationIncomplete,
            DispatchFailureClassifier.ClassifyStoredRound(tester, tester.VerificationHistory[^2]).Kind);
        var pair = Assert.IsType<FailedGoalInconclusiveRoundPair>(
            TesterInconclusiveRoundInputsReader.Read(restoredGoal, tester));
        Assert.Null(pair.Previous);
        Assert.False(pair.InputsUnchanged);
        AssertTransient(Facts(pair));
    }

    [Fact]
    public void ChangedCandidate_KeepsTransientRetry()
    {
        var fixture = new Fixture();
        fixture.RecordRound(1);
        fixture.Retry();
        var changed = new CandidateIdentity("changed", "base", "manifest");
        fixture.Kernel.ConfigureCandidateIdentityResolver(_ => changed);
        fixture.RecordRound(2);

        var pair = fixture.Read()!;
        Assert.NotNull(pair.Previous);
        Assert.Equal(Candidate.Canonical, pair.Previous.CandidateIdentity);
        Assert.Equal(changed.Canonical, pair.Current.CandidateIdentity);
        Assert.False(pair.InputsUnchanged);
        AssertTransient(Facts(pair));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewReceipt_KeepsTransientRetryRegardlessOfOutcome(bool passed)
    {
        var fixture = new Fixture();
        fixture.RecordRound(1);
        fixture.Retry();
        const string sixth = "finding-evidence-sixth";
        fixture.RecordReceipt(sixth, passed);
        fixture.RecordRound(2);

        var pair = fixture.Read()!;
        Assert.NotNull(pair.Previous);
        Assert.Equal(ReceiptIds.Order(StringComparer.Ordinal), pair.Previous.ReceiptIds);
        Assert.Equal(ReceiptIds.Append(sixth).Order(StringComparer.Ordinal), pair.Current.ReceiptIds);
        Assert.False(pair.InputsUnchanged);
        AssertTransient(Facts(pair));
    }

    [Fact]
    public void ProviderInterruption_DoesNotCountAsInconclusiveRound()
    {
        var fixture = new Fixture();
        fixture.RecordRound(1);
        fixture.Retry();
        fixture.RecordRound(2, providerInterruption: true);
        // Provider interruption schedules a retry and clears the live pointer, retaining the round in history.
        Assert.Null(fixture.Tester.LastVerification);
        var record = fixture.Tester.VerificationHistory[^1];

        Assert.True(ProviderFailureSignals.IsProviderInterruptionFailure(record));
        var outcome = DispatchFailureClassifier.Classify(fixture.Tester, record);
        Assert.Equal(DispatchOutcomeKind.ProviderInterruption, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
        var pair = fixture.Read();
        Assert.Null(pair);
        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts(pair, outcome.Kind));
        Assert.NotEqual("verification-inconclusive-unchanged-inputs", decision.DiscriminatingEvidence);
        AssertTransient(Facts(pair));
    }

    private static Fixture TwoRounds()
    {
        var fixture = new Fixture();
        fixture.RecordRound(1);
        fixture.Retry();
        fixture.RecordRound(2);
        return fixture;
    }

    private static void AssertUnchangedEscalation(Fixture fixture)
    {
        var pair = Assert.IsType<FailedGoalInconclusiveRoundPair>(fixture.Read());
        Assert.NotNull(pair.Previous);
        Assert.Equal(pair.Previous, pair.Current);
        Assert.True(pair.InputsUnchanged);
        Assert.Equal(Candidate.Canonical, pair.Current.CandidateIdentity);
        Assert.Equal(ReceiptIds.Order(StringComparer.Ordinal), pair.Current.ReceiptIds);
        var decision = FailedGoalRecoveryPolicy.Evaluate(Facts(pair));
        Assert.Equal(FailedGoalRecoveryAction.Escalate, decision.Action);
        Assert.Equal("verification-inconclusive-unchanged-inputs", decision.DiscriminatingEvidence);
    }

    private static void AssertTransient(FailedGoalRecoveryFacts facts)
    {
        var decision = FailedGoalRecoveryPolicy.Evaluate(facts);
        Assert.Equal(FailedGoalRecoveryAction.RetryTransient, decision.Action);
        Assert.Equal("verification-inconclusive", decision.DiscriminatingEvidence);
    }

    private static FailedGoalRecoveryFacts Facts(FailedGoalInconclusiveRoundPair? rounds,
        DispatchOutcomeKind kind = DispatchOutcomeKind.VerificationInconclusive) =>
        new(new GoalId("goal-inconclusive"), GoalLifecycleState.Failed,
            automaticAcceptanceRetryCount: 0, maxCriterionRetries: 2, maxTransientAttempts: 24,
            contextVersion: "context-v1", tasks: [new FailedGoalRecoveryTaskFacts(
                new TaskId("tester-inconclusive"), AgentRole.Tester, WorkTaskStatus.Failed,
                HasLiveProcess: false, IsExitedWithoutAppliedCompletion: false, AttemptIdentity: "round",
                OutcomeKind: kind,
                RecoveryRecommendation: RecoveryRecommendation.AutoRetry, OutcomeClass: TaskOutcomeClass.UnknownEra,
                EvidenceSummary: "Inspected packaged receipts; verification inconclusive",
                StaleRecoveryDisposition: FailedGoalStaleRecoveryDisposition.None,
                StaleRecoveryDiagnostic: null, EmptyOutputRetryCount: 1, CriterionRetryCount: 0,
                AutomaticRetryCause: RetryCause.EnvironmentApparatusFailure, ProviderFailureKind: null,
                ExitCode: 0, Command: Command, RetryBackoff: TimeSpan.FromSeconds(7), InconclusiveRounds: rounds)],
            terminalEscalationReason: "terminal failure");

    private static TaskDispatchRecord DispatchRecord(DateTimeOffset at) =>
        new("codex-cli", Command, DirectoryPath, at, WorkerProviderKind: ProviderKind.OpenAICodexCli);

    private static TaskVerificationRecord Evidence(DateTimeOffset at, bool providerInterruption) =>
        new(Command, DirectoryPath, providerInterruption ? 1 : 0,
            providerInterruption ? "{\"type\":\"turn.failed\",\"error\":{\"message\":\"fixture interrupted\"}}" : Contract,
            "", at.AddMinutes(2), WorkerResultPresent: !providerInterruption,
            DispatchStartedAt: at.AddSeconds(2), EvidenceSource: VerificationEvidenceSource.Dispatch,
            DispatchAttemptAt: at);

    private sealed class Fixture
    {
        private readonly FixtureClock _clock = new();
        private readonly ReviewFinding _finding = EvidenceFindingWithRequest("Inspect packaged receipts") with
        {
            Severity = FindingSeverity.Advisory
        };
        private readonly TaskSpec _owner = new(TaskId.New(), "Supply evidence", AgentRole.Developer);
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal TaskSpec Tester { get; } = new(TaskId.New(), "Inspect receipts", AgentRole.Tester);

        internal Fixture()
        {
            Kernel = new(_clock);
            Kernel.ConfigureCandidateIdentityResolver(_ => Candidate);
            Goal = Kernel.CreateGoal("Stop unchanged inconclusive retries", [_owner, Tester]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            Kernel.RecordTaskDispatch(Goal.Id, _owner.Id,
                new("fixture", "evidence", DirectoryPath, _clock.UtcNow));
            _clock.UtcNow = Start.AddMinutes(-9);
            Kernel.RecordTaskVerification(Goal.Id, _owner.Id,
                new("evidence", DirectoryPath, 0, "ok", "", _clock.UtcNow, MergedReviewFindings: [_finding]));
            Assert.Equal(WorkTaskStatus.Completed, _owner.Status);
            foreach (var id in ReceiptIds)
                RecordReceipt(id, passed: true);
        }

        internal void RecordReceipt(string id, bool passed) =>
            Kernel.RecordFindingEvidenceOutcome(Goal.Id, _owner.Id, _finding.StableId, new(passed, id),
                new(id, "candidate-sha", _finding.EvidenceRequest!, true, passed, "fixture evidence"));

        internal void RecordRound(int number, bool providerInterruption = false)
        {
            var at = Start.AddMinutes((number - 1) * 5);
            _clock.UtcNow = at;
            Kernel.RecordTaskDispatch(Goal.Id, Tester.Id, DispatchRecord(at));
            _clock.UtcNow = at.AddMinutes(2);
            Kernel.RecordDispatchExecutionResult(Goal.Id, Tester.Id, Evidence(at, providerInterruption));
            Assert.Equal(number, Tester.DispatchHistory.Count);
            Assert.Equal(number, Tester.VerificationHistory.Count);
            if (!providerInterruption)
                Assert.Equal(WorkTaskStatus.Failed, Tester.Status);
        }

        internal void Retry()
        {
            _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
            Kernel.RetryTask(Goal.Id, Tester.Id, "Retry inconclusive fixture round",
                RetryCause.EnvironmentApparatusFailure, invalidateDownstream: false);
        }

        internal FailedGoalInconclusiveRoundPair? Read() => TesterInconclusiveRoundInputsReader.Read(Goal, Tester);
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Start.AddMinutes(-10);
    }
}
