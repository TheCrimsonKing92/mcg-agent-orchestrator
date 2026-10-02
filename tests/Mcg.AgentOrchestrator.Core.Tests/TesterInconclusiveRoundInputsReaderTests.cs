using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// In-memory kernel, injected clock, and fixed records only; parallel-safe.
public sealed class TesterInconclusiveRoundInputsReaderTests
{
    private static readonly CandidateIdentity Candidate = new("patch", "base", "manifest");
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Output = "WORKER_RESULT:\nfiles: none\ntests: inconclusive - same receipts\nblockers: none\nEND_WORKER_RESULT";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DispatchSnapshots_SurviveTwoRoundVerificationAndJsonRestore(bool manualVerification)
    {
        var fixture = new Fixture();
        fixture.Evidence.RecordVerification(EvidenceRecord("r-b", "r-a", "r-a"));
        fixture.Dispatch();
        var firstDispatch = Assert.IsType<FailedGoalInconclusiveRoundInputs>(fixture.Tester.LastDispatch!.InconclusiveRoundInputs);
        Assert.Equal(Candidate.Canonical, firstDispatch.CandidateIdentity);
        Assert.Equal(new[] { "r-a", "r-b" }, firstDispatch.ReceiptIds);
        fixture.Complete(manualVerification);
        fixture.Retry();
        fixture.Dispatch();
        fixture.Complete(manualVerification);

        var json = JsonSerializer.Serialize(fixture.Kernel.ExportSnapshot());
        var restored = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(json)!);
        var goal = restored.GetGoal(fixture.Goal.Id);
        var task = goal.FindTask(fixture.Tester.Id);
        var pair = Assert.IsType<FailedGoalInconclusiveRoundPair>(TesterInconclusiveRoundInputsReader.Read(goal, task));

        Assert.True(pair.InputsUnchanged);
        Assert.Equal(firstDispatch, pair.Previous);
        Assert.Equal(firstDispatch, pair.Current);
        Assert.Equal(firstDispatch, task.LastDispatch!.InconclusiveRoundInputs);
        Assert.Equal(firstDispatch, task.LastVerification!.InconclusiveRoundInputs);
        Assert.Equal(2, task.VerificationHistory.Count);
    }

    [Fact]
    public void ReceiptAppendedToOlderRecord_ChangesOnlyTheNextDispatchInputs()
    {
        var fixture = new Fixture();
        var evidence = EvidenceRecord("r-a");
        fixture.Evidence.RecordVerification(evidence);
        fixture.Dispatch();
        fixture.Complete();
        // Same verification identity, enriched in place after round one, not a new round.
        fixture.Evidence.RestoreVerificationHistory(evidence with
        {
            FindingEvidenceReceipts = [.. evidence.FindingEvidenceReceipts!, Receipt("r-b")]
        });
        Assert.Single(fixture.Evidence.VerificationHistory);
        fixture.Retry();
        fixture.Dispatch();
        fixture.Complete();

        var pair = Assert.IsType<FailedGoalInconclusiveRoundPair>(
            TesterInconclusiveRoundInputsReader.Read(fixture.Goal, fixture.Tester));
        Assert.NotNull(pair.Previous);
        Assert.Equal(new[] { "r-a" }, pair.Previous.ReceiptIds);
        Assert.Equal(new[] { "r-a", "r-b" }, pair.Current.ReceiptIds);
        Assert.False(pair.InputsUnchanged);
    }

    [Fact]
    public void ChangedCandidate_ProducesChangedPair()
    {
        var fixture = new Fixture();
        fixture.Dispatch();
        fixture.Complete();
        fixture.Kernel.ConfigureCandidateIdentityResolver(_ => new CandidateIdentity("new", "base", "manifest"));
        fixture.Retry();
        fixture.Dispatch();
        fixture.Complete();

        var pair = Assert.IsType<FailedGoalInconclusiveRoundPair>(
            TesterInconclusiveRoundInputsReader.Read(fixture.Goal, fixture.Tester));
        Assert.False(pair.InputsUnchanged);
        Assert.Equal(Candidate.Canonical, pair.Previous!.CandidateIdentity);
        Assert.Equal("candidate:v1:new:base:manifest", pair.Current.CandidateIdentity);
    }

    [Fact]
    public void InterveningNonInconclusiveRound_ClearsPrevious()
    {
        var fixture = new Fixture();
        fixture.Tester.RecordVerification(Round(1));
        fixture.Tester.RecordVerification(Round(2) with { StandardOutput = "passed", AuthoritativeStandardOutput = "passed", WorkerResultPresent = false });
        fixture.Tester.RecordVerification(Round(3));

        var pair = Assert.IsType<FailedGoalInconclusiveRoundPair>(
            TesterInconclusiveRoundInputsReader.Read(fixture.Goal, fixture.Tester));
        Assert.Null(pair.Previous);
        Assert.False(pair.InputsUnchanged);
    }

    [Fact]
    public void HistoryOverCap_RetainsInterveningOutcomeAndClearsPrevious()
    {
        var fixture = new Fixture();
        for (var round = 1; round <= TaskSpec.VerificationHistoryLimit + 5; round++)
            fixture.Tester.RecordVerification(Round(round));
        var interruption = Round(30) with
        {
            StandardOutput = "interrupted", AuthoritativeStandardOutput = "interrupted",
            WorkerResultPresent = false, InconclusiveRoundInputs = null
        };
        fixture.Tester.RecordVerification(interruption);
        fixture.Tester.RecordVerification(Round(31));

        Assert.Same(interruption, fixture.Tester.VerificationHistory[^2]);
        var pair = Assert.IsType<FailedGoalInconclusiveRoundPair>(
            TesterInconclusiveRoundInputsReader.Read(fixture.Goal, fixture.Tester));
        Assert.Null(pair.Previous);
    }

    [Fact]
    public void SameDispatchRecordedTwice_DoesNotCountAsTwoRounds()
    {
        var fixture = new Fixture();
        fixture.Tester.RecordVerification(Round(1));
        fixture.Tester.RecordVerification(Round(1) with { CompletedAt = Start.AddMinutes(2) });

        var pair = Assert.IsType<FailedGoalInconclusiveRoundPair>(
            TesterInconclusiveRoundInputsReader.Read(fixture.Goal, fixture.Tester));
        Assert.Null(pair.Previous);
    }

    [Fact]
    public void FirstRound_HasCurrentInputsAndNoPrevious()
    {
        var fixture = new Fixture();
        fixture.Tester.RecordVerification(Round(1));

        var pair = Assert.IsType<FailedGoalInconclusiveRoundPair>(
            TesterInconclusiveRoundInputsReader.Read(fixture.Goal, fixture.Tester));
        Assert.Null(pair.Previous);
        Assert.Equal(Candidate.Canonical, pair.Current.CandidateIdentity);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("dispatch-start")]
    [InlineData("snapshot")]
    [InlineData("mismatch")]
    public void MissingOrConflictingCurrentSnapshot_ReturnsNoPair(string missing)
    {
        var fixture = new Fixture();
        fixture.Tester.RecordVerification(Round(1));
        var round = Round(2);
        round = missing switch
        {
            "identity" => round with { CandidateIdentity = null },
            "dispatch-start" => round with { DispatchStartedAt = null },
            "snapshot" => round with { InconclusiveRoundInputs = null },
            _ => round with { InconclusiveRoundInputs = new("candidate:v1:other:base:manifest", []) }
        };
        fixture.Tester.RecordVerification(round);

        Assert.Null(TesterInconclusiveRoundInputsReader.Read(fixture.Goal, fixture.Tester));
    }

    [Fact]
    public void MissingPriorSnapshot_IsTreatedAsFirstRound()
    {
        var fixture = new Fixture();
        fixture.Tester.RecordVerification(Round(1) with { InconclusiveRoundInputs = null });
        fixture.Tester.RecordVerification(Round(2));

        var pair = Assert.IsType<FailedGoalInconclusiveRoundPair>(
            TesterInconclusiveRoundInputsReader.Read(fixture.Goal, fixture.Tester));
        Assert.Null(pair.Previous);
    }

    [Fact]
    public void LatestNonInconclusiveOrNonTester_ReturnsNoPair()
    {
        var fixture = new Fixture();
        fixture.Tester.RecordVerification(Round(1));
        fixture.Tester.RecordVerification(Round(2) with { StandardOutput = "passed", AuthoritativeStandardOutput = "passed", WorkerResultPresent = false });
        fixture.Evidence.RecordVerification(Round(1));

        Assert.Null(TesterInconclusiveRoundInputsReader.Read(fixture.Goal, fixture.Tester));
        Assert.Null(TesterInconclusiveRoundInputsReader.Read(fixture.Goal, fixture.Evidence));
        Assert.Null(TesterInconclusiveRoundInputsReader.Capture(fixture.Goal, fixture.Evidence, Candidate));
        Assert.Null(TesterInconclusiveRoundInputsReader.Capture(fixture.Goal, fixture.Tester, null));
    }

    private static TaskVerificationRecord Round(int number) =>
        new("verify", "worktree", 0, Output, "", Start.AddMinutes(number),
            WorkerResultPresent: true, DispatchStartedAt: Start.AddMinutes(number).AddSeconds(-1),
            CandidateIdentity: Candidate, InconclusiveRoundInputs: new(Candidate.Canonical, []));

    private static FindingEvidenceReceipt Receipt(string id) =>
        new(id, "candidate-sha", new FindingEvidenceRequest(
            [new FindingEvidenceSelection("Core.Tests", "FailedGoalRecoveryPolicyTests")]), true, true, "passed");

    private static TaskVerificationRecord EvidenceRecord(params string[] ids) =>
        new("evidence", "worktree", 0, "passed", "", Start.AddMinutes(-1),
            FindingEvidenceReceipts: ids.Select(Receipt).ToArray());

    private sealed class Fixture
    {
        public FakeClock Clock { get; } = new();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Tester { get; } = new(TaskId.New(), "Verify candidate", AgentRole.Tester);
        public TaskSpec Evidence { get; } = new(TaskId.New(), "Supply evidence", AgentRole.Developer);

        public Fixture()
        {
            Kernel = new AgentOrchestratorKernel(Clock);
            Goal = Kernel.CreateGoal("Compare inconclusive inputs", [Tester, Evidence]);
            Kernel.ConfigureCandidateIdentityResolver(_ => Candidate);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
        }

        public void Dispatch()
        {
            Kernel.RecordTaskDispatch(Goal.Id, Tester.Id, new TaskDispatchRecord("worker", "verify", "worktree", Clock.UtcNow));
            Clock.Advance();
        }

        public void Complete(bool manualVerification = false)
        {
            var started = Clock.UtcNow;
            Clock.Advance();
            var verification = new TaskVerificationRecord("verify", "worktree", 0, Output, "", Clock.UtcNow,
                WorkerResultPresent: true, DispatchStartedAt: started);
            if (manualVerification)
                Kernel.RecordTaskVerification(Goal.Id, Tester.Id, verification);
            else
                Kernel.RecordDispatchExecutionResult(Goal.Id, Tester.Id, verification);
            Assert.Equal(WorkTaskStatus.Failed, Tester.Status);
        }

        public void Retry()
        {
            Clock.Advance();
            Kernel.RetryTask(Goal.Id, Tester.Id, "Retry inconclusive verification",
                RetryCause.EnvironmentApparatusFailure, invalidateDownstream: false);
            Clock.Advance();
        }
    }
}
