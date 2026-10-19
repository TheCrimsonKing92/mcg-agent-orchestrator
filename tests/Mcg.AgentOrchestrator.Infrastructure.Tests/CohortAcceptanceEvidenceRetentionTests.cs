using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns its temporary filesystem and SQLite database; time is injected.
public sealed class CohortAcceptanceEvidenceRetentionTests
{
    private const string SecondId = "44444444444444444444444444444444";
    private const string RegistryNote = "TRX copies of cohort receipts that cannot back a landing are reclaimed by CohortAcceptanceEvidenceRetention 14 days after the receipt completes once every member goal is Completed, Cancelled or Superseded; landing-evidence receipts, judge-panel trigger receipts and gate-verdict.json are kept.";

    [Fact]
    public void Sweep_OldFailedReceipt_DeletesOnlyCustodyTrxAndKeepsStoreBytes()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var lengths = Lengths(receipt);
        var store = new CohortAcceptanceStore(StorePath(fixture));
        var databaseBytes = File.ReadAllBytes(StorePath(fixture));

        var decisions = Sweep(fixture, TerminalGoals(fixture));

        AssertDeleted(receipt, decisions, lengths);
        Assert.Equal(databaseBytes, File.ReadAllBytes(StorePath(fixture)));
        Assert.True(Directory.Exists(CustodyDirectory(receipt)));
        Assert.True(Directory.Exists(CohortDirectory(receipt)));
        Assert.True(File.Exists(Path.Combine(CustodyDirectory(receipt), "gate-verdict.json")));
        Assert.NotNull(store.TryReadReceipt(receipt.Identity.Value));
    }

    [Fact]
    public void Sweep_LandingEvidence_PreservesEveryTrx()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture, AcceptanceCohortGateOutcome.Passed);
        Assert.True(receipt.HasAuthoritativeLandingEvidence);

        AssertKept(receipt, Sweep(fixture, TerminalGoals(fixture, GoalStatus.Completed)), EvidenceRetentionAction.Preserved,
            EvidenceOwnerResolution.Unrecorded, "cohort-landing-evidence");
    }

    [Fact]
    public void Sweep_OldInfrastructureFailure_ReclaimsTrx()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture, AcceptanceCohortGateOutcome.InfrastructureFailure);
        var lengths = Lengths(receipt);

        AssertDeleted(receipt, Sweep(fixture, TerminalGoals(fixture, GoalStatus.Completed)), lengths);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1213200000, false)] // Fourteen days in milliseconds plus one future hour.
    public void Sweep_ReceiptAgeBoundary_UsesCompletionTime(int pastBoundaryMs, bool delete)
    {
        using var fixture = new RetentionReclaimFixture();
        var completedAt = RetentionReclaimFixture.Now.AddDays(-14).AddMilliseconds(-pastBoundaryMs);
        var receipt = Save(fixture, completedAt: completedAt);
        Assert.Equal(completedAt, receipt.CompletedAt);
        var lengths = Lengths(receipt);

        var decisions = Sweep(fixture, TerminalGoals(fixture, GoalStatus.Completed));

        if (delete) AssertDeleted(receipt, decisions, lengths);
        else AssertKept(receipt, decisions, EvidenceRetentionAction.Preserved,
            EvidenceOwnerResolution.Unrecorded, "cohort-within-age");
    }

    [Theory]
    [InlineData(AcceptanceCohortAttributionOutcome.BothMembersFailed, null, true)]
    [InlineData(AcceptanceCohortAttributionOutcome.InteractionOnly, "main-suspect", true)]
    [InlineData(AcceptanceCohortAttributionOutcome.InteractionOnly, null, false)]
    [InlineData(AcceptanceCohortAttributionOutcome.InteractionOnly, "MAIN-SUSPECT", false)]
    public void Sweep_Attribution_PreservesOnlyJudgePanelTriggers(
        AcceptanceCohortAttributionOutcome attribution, string? source, bool keep)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var partitions = receipt.Identity.Members.Select((member, ordinal) =>
            new AcceptanceCohortPartitionReceipt($"partition-{ordinal}", member.GoalId, ordinal,
                member.CandidateRevision, receipt.Identity.ObservedMainRevision, new string('e', 40),
                receipt.Identity.ManifestIdentity, AcceptanceCohortGateOutcome.Passed, 1, [])).ToArray();
        receipt = new CohortAcceptanceStore(StorePath(fixture)).SaveAttribution(receipt.Identity.Value,
            attribution, partitions, "retention-pair", null, attributionSource: source);
        Assert.Equal(attribution, receipt.Attribution);
        var lengths = Lengths(receipt);

        var decisions = Sweep(fixture, TerminalGoals(fixture, GoalStatus.Completed));

        if (keep) AssertKept(receipt, decisions, EvidenceRetentionAction.Preserved,
            EvidenceOwnerResolution.Unrecorded, "cohort-judge-panel-trigger");
        else AssertDeleted(receipt, decisions, lengths);
    }

    [Theory]
    [InlineData(GoalStatus.Verified)]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.Failed)]
    public void Sweep_NonterminalMember_PreservesTrx(GoalStatus status)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);

        AssertKept(receipt, Sweep(fixture,
            fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed), fixture.Goal(SecondId, status)),
            EvidenceRetentionAction.Preserved, EvidenceOwnerResolution.NonTerminal, "cohort-member-not-terminal");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stand-in")]
    [InlineData("ambiguous")]
    [InlineData("zero-members")]
    [InlineData("unresolved-before-nonterminal")]
    public void Sweep_UnresolvedMember_RetainsUndecidable(string scenario)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var first = fixture.Goal(RetentionReclaimFixture.CompletedId,
            scenario == "unresolved-before-nonterminal" ? GoalStatus.Active : GoalStatus.Completed);
        var second = fixture.Goal(SecondId, GoalStatus.Completed);
        StorageRetentionGoal[] goals = scenario switch
        {
            "stand-in" => [first, second with { IsStoreStandIn = true }],
            "ambiguous" => [first, second, second],
            "zero-members" => [first, second],
            _ => [first]
        };
        if (scenario == "zero-members") Execute(fixture, "DELETE FROM cohort_members");

        AssertKept(receipt, Sweep(fixture, goals), EvidenceRetentionAction.RetainedUndecidable,
            EvidenceOwnerResolution.Unmatched, "cohort-member-unresolved");
    }

    [Fact]
    public void Sweep_SupersededMember_ReclaimsTrx()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var lengths = Lengths(receipt);

        AssertDeleted(receipt, Sweep(fixture,
            fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Superseded),
            fixture.Goal(SecondId, GoalStatus.Completed)), lengths);
    }

    [Fact]
    public void Sweep_OnlyStandIns_AddsNoDecision()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);

        Assert.Empty(Sweep(fixture, TerminalGoals(fixture).Select(goal => goal with { IsStoreStandIn = true }).ToArray()));
        Assert.All(receipt.GateTestResultPaths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void Sweep_UnrecordedDirectory_RetainsOnlyDirectChildTrxWithDecision()
    {
        using var fixture = new RetentionReclaimFixture();
        _ = new CohortAcceptanceStore(StorePath(fixture));
        var root = StorePath(fixture) + ".artifacts";
        var cohort = Path.Combine(root, "orphan");
        var trx = Write(Path.Combine(cohort, "custody", "old.TRX"), "orphan");
        var ignored = Write(Path.Combine(root, "ignored", "custody", "nested", "old.trx"), "nested");
        var topLevel = Write(Path.Combine(root, "top.trx"), "top");

        var decision = Assert.Single(Sweep(fixture, TerminalGoals(fixture)));

        AssertDecision(decision, cohort, EvidenceRetentionAction.RetainedUndecidable,
            EvidenceOwnerResolution.Unrecorded, "cohort-receipt-not-recorded");
        Assert.All(new[] { trx, ignored, topLevel }, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void Sweep_UnlistedNestedAndOutsideFiles_LeavesAllControlsUntouched()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var custody = CustodyDirectory(receipt);
        var controls = new[]
        {
            Write(Path.Combine(custody, "extra.trx"), "unlisted"),
            Write(Path.Combine(custody, "notes.txt"), "notes"),
            Write(Path.Combine(custody, "nested", "old.trx"), "nested"),
            Write(Path.Combine(fixture.ExecutionDirectory, "outside.trx"), "outside"),
            Write(Path.Combine(CohortDirectory(receipt), "top.trx"), "top")
        };
        var verdict = Path.Combine(custody, "gate-verdict.json");
        SetPaths(fixture, receipt, receipt.GateTestResultPaths.Concat(controls.Skip(1)).Append(verdict).Reverse().ToArray());
        var lengths = Lengths(receipt);

        var decisions = Sweep(fixture, TerminalGoals(fixture));

        AssertDeleted(receipt, decisions, lengths);
        Assert.All(controls.Append(verdict), path =>
        {
            Assert.True(File.Exists(path));
            Assert.DoesNotContain(decisions, decision => decision.Path == path);
        });
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void Sweep_EmptyOrNullPathList_AddsNoDecision(string json)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture, pathCount: 0);
        Execute(fixture, "UPDATE cohort_receipts SET gate_test_result_paths_json=$value", json);

        Assert.Empty(Sweep(fixture, TerminalGoals(fixture)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sweep_MissingInputs_DoesNotCreateDatabase(bool createRoot)
    {
        using var fixture = new RetentionReclaimFixture();
        if (createRoot) Write(Path.Combine(StorePath(fixture) + ".artifacts", "orphan", "custody", "old.trx"), "keep");

        Assert.Empty(Sweep(fixture, TerminalGoals(fixture)));
        Assert.False(File.Exists(StorePath(fixture)));
    }

    [Fact]
    public void Sweep_MissingArtifactsRoot_LeavesExistingStoreUntouched()
    {
        using var fixture = new RetentionReclaimFixture();
        _ = new CohortAcceptanceStore(StorePath(fixture));
        var bytes = File.ReadAllBytes(StorePath(fixture));
        Assert.False(Directory.Exists(StorePath(fixture) + ".artifacts"));

        Assert.Empty(Sweep(fixture, TerminalGoals(fixture)));
        Assert.Equal(bytes, File.ReadAllBytes(StorePath(fixture)));
    }

    [Fact]
    public void Sweep_CorruptDatabase_RetainsEverythingWithFailureType()
    {
        using var fixture = new RetentionReclaimFixture();
        Write(StorePath(fixture), "not a SQLite database");
        var path = Write(Path.Combine(StorePath(fixture) + ".artifacts", "orphan", "custody", "old.trx"), "keep");

        AssertStoreUnavailable(fixture, Sweep(fixture, TerminalGoals(fixture)));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("timestamp")]
    [InlineData("missing-members-table")]
    public void Sweep_ReadFailureAfterEligibleReceipt_DeletesNothing(string failure)
    {
        using var fixture = new RetentionReclaimFixture();
        var eligible = Save(fixture);
        var malformed = Save(fixture, variant: 'e');
        if (failure == "missing-members-table") Execute(fixture, "DROP TABLE cohort_members");
        else Execute(fixture, failure == "json"
            ? "UPDATE cohort_receipts SET gate_test_result_paths_json=$value WHERE cohort_id=$cohort"
            : "UPDATE cohort_receipts SET completed_at=$value WHERE cohort_id=$cohort",
            "malformed", malformed.Identity.Value);

        AssertStoreUnavailable(fixture, Sweep(fixture, TerminalGoals(fixture)));
        Assert.All(eligible.GateTestResultPaths.Concat(malformed.GateTestResultPaths), path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void Sweep_LockedTrx_DefersThenReclaimsAfterHandleCloses()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture, pathCount: 1);
        var path = Assert.Single(receipt.GateTestResultPaths);
        var lengths = Lengths(receipt);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var decision = Assert.Single(Sweep(fixture, TerminalGoals(fixture)));
            AssertDecision(decision, path, EvidenceRetentionAction.DeferredLocked,
                EvidenceOwnerResolution.UniqueTerminal, "cohort-trx-locked");
            Assert.Equal(lengths[path], decision.BytesAttempted);
            Assert.NotNull(decision.FailureExceptionType);
            Assert.True(File.Exists(path));
        }

        AssertDeleted(receipt, Sweep(fixture, TerminalGoals(fixture)), lengths);
    }

    [Fact]
    public void Run_CohortEvidence_AddsOnlyCohortDecisionsWithEqualCounters()
    {
        using var baseline = new RetentionReclaimFixture();
        using var seeded = new RetentionReclaimFixture();
        var goals = TerminalGoals(baseline);
        var receipt = Save(seeded);
        var lengths = Lengths(receipt);

        var before = baseline.Run(null, goals);
        var after = seeded.Run(null, goals);

        Assert.False(before.Failed);
        Assert.False(after.Failed);
        Assert.Equal(before.WorkerArtifactsDeleted, after.WorkerArtifactsDeleted);
        Assert.Equal(before.WorkerLogsCompressed, after.WorkerLogsCompressed);
        Assert.Equal(before.SuccessfulTrxReceiptsWritten, after.SuccessfulTrxReceiptsWritten);
        Assert.Equal(before.AcceptanceArtifactsDeleted, after.AcceptanceArtifactsDeleted);
        Assert.Equal(before.PromptArtifactsDeleted, after.PromptArtifactsDeleted);
        Assert.Equal(before.GoalJournalsArchived, after.GoalJournalsArchived);
        Assert.Equal(OtherDecisions(before, baseline), OtherDecisions(after, seeded));
        Assert.DoesNotContain(before.Decisions, decision => decision.Family == EvidenceArtifactFamily.CohortAcceptanceEvidence);
        AssertDeleted(receipt, after.Decisions.Where(decision => decision.Family == EvidenceArtifactFamily.CohortAcceptanceEvidence).ToArray(), lengths);
        Assert.True(File.Exists(Path.Combine(CustodyDirectory(receipt), "gate-verdict.json")));
    }

    [Fact]
    public void Classify_CohortArtifacts_DeclaresRetentionAndOriginalWriter()
    {
        var entry = OrchestratorArtifactRegistry.Classify("cohort-acceptance.db.artifacts");

        Assert.NotNull(entry);
        Assert.Equal(OrchestratorArtifactRegistry.RetentionKind.ReclaimAfterGoalTerminalPlusDays, entry.Retention);
        Assert.Equal(StorageRetentionMaintenance.AcceptanceArtifactMaxAge, entry.RetentionAge);
        Assert.Equal(RegistryNote, entry.Note);
        Assert.Equal(".artifacts", entry.WriterLiteral);
        Assert.EndsWith("Execution/Persistence/CohortAcceptanceStore.cs", entry.WriterPath);
    }

    private static StorageRetentionGoal[] TerminalGoals(RetentionReclaimFixture fixture, GoalStatus secondStatus = GoalStatus.Cancelled) =>
        [fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed), fixture.Goal(SecondId, secondStatus)];

    private static string StorePath(RetentionReclaimFixture fixture) => Path.Combine(fixture.OrchestratorDirectory, "cohort-acceptance.db");
    private static string CustodyDirectory(AcceptanceCohortReceipt receipt) => Path.GetDirectoryName(receipt.GateTestResultPaths[0])!;
    private static string CohortDirectory(AcceptanceCohortReceipt receipt) => Path.GetDirectoryName(CustodyDirectory(receipt))!;
    private static Dictionary<string, long> Lengths(AcceptanceCohortReceipt receipt) =>
        receipt.GateTestResultPaths.ToDictionary(path => path, path => new FileInfo(path).Length);

    private static List<EvidenceRetentionDecision> Sweep(RetentionReclaimFixture fixture, params StorageRetentionGoal[] goals)
    {
        var decisions = new List<EvidenceRetentionDecision>();
        CohortAcceptanceEvidenceRetention.Sweep(fixture.OrchestratorDirectory, goals, RetentionReclaimFixture.Now, decisions);
        return decisions;
    }

    private static AcceptanceCohortReceipt Save(RetentionReclaimFixture fixture,
        AcceptanceCohortGateOutcome outcome = AcceptanceCohortGateOutcome.Failed,
        DateTimeOffset? completedAt = null, int pathCount = 2, char variant = 'd')
    {
        var paths = Enumerable.Range(0, pathCount).Select(index =>
            Write(Path.Combine(fixture.ExecutionDirectory, "source", variant.ToString(), $"result-{index}.trx"),
                PassingTrx($"test-{index}"))).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length > 0) Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(paths));
        var identity = AcceptanceCohortIdentity.Create(
            [Binding(RetentionReclaimFixture.CompletedId), Binding(SecondId)],
            new string('a', 40), new string(variant, 40), "manifest-v1");
        return new CohortAcceptanceStore(StorePath(fixture)).SaveGateReceipt(new AcceptanceCohortReceipt(
            $"receipt-{variant}", identity, outcome, completedAt ?? RetentionReclaimFixture.Now.AddDays(-15), 1, [],
            outcome == AcceptanceCohortGateOutcome.Passed ? 0 : 1, paths,
            ValidForLanding: outcome == AcceptanceCohortGateOutcome.Passed,
            InfrastructureReasonCode: outcome == AcceptanceCohortGateOutcome.InfrastructureFailure ? "test-infrastructure" : null));
    }

    private static AcceptanceCohortMemberBinding Binding(string goalId) => new(
        new GoalId(goalId), new string('b', 40), new string('c', 40), ["src/Example.cs"], ["resource:example"],
        ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");

    private static string PassingTrx(string name) => $$"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results><UnitTestResult testName="{{name}}" outcome="Passed" /></Results>
          <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
        </TestRun>
        """;

    private static string Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void Execute(RetentionReclaimFixture fixture, string sql, string? value = null, string? cohortId = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StorePath(fixture), Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue("$cohort", (object?)cohortId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void SetPaths(RetentionReclaimFixture fixture, AcceptanceCohortReceipt receipt, string[] paths) =>
        Execute(fixture, "UPDATE cohort_receipts SET gate_test_result_paths_json=$value WHERE cohort_id=$cohort",
            JsonSerializer.Serialize(paths), receipt.Identity.Value);

    private static void AssertDeleted(AcceptanceCohortReceipt receipt, IReadOnlyList<EvidenceRetentionDecision> decisions,
        Dictionary<string, long> lengths)
    {
        Assert.NotEmpty(receipt.GateTestResultPaths);
        Assert.Equal(receipt.GateTestResultPaths.Order(StringComparer.Ordinal).ToArray(), decisions.Select(decision => decision.Path).ToArray());
        foreach (var decision in decisions)
        {
            AssertDecision(decision, decision.Path, EvidenceRetentionAction.Deleted,
                EvidenceOwnerResolution.UniqueTerminal, "cohort-red-trx-past-age", lengths[decision.Path]);
            Assert.Equal(lengths[decision.Path], decision.BytesAttempted);
            Assert.Null(decision.FailureExceptionType);
            Assert.False(File.Exists(decision.Path));
        }
    }

    private static void AssertKept(AcceptanceCohortReceipt receipt, IReadOnlyList<EvidenceRetentionDecision> decisions,
        EvidenceRetentionAction action, EvidenceOwnerResolution owner, string reason)
    {
        var decision = Assert.Single(decisions);
        AssertDecision(decision, CohortDirectory(receipt), action, owner, reason);
        Assert.Equal(0, decision.BytesAttempted);
        Assert.Null(decision.FailureExceptionType);
        Assert.All(receipt.GateTestResultPaths, path => Assert.True(File.Exists(path)));
    }

    private static void AssertStoreUnavailable(RetentionReclaimFixture fixture, IReadOnlyList<EvidenceRetentionDecision> decisions)
    {
        var decision = Assert.Single(decisions);
        AssertDecision(decision, StorePath(fixture) + ".artifacts", EvidenceRetentionAction.RetainedUndecidable,
            EvidenceOwnerResolution.Unrecorded, "cohort-store-unavailable");
        Assert.NotNull(decision.FailureExceptionType);
    }

    private static void AssertDecision(EvidenceRetentionDecision decision, string path,
        EvidenceRetentionAction action, EvidenceOwnerResolution owner, string reason, long reclaimed = 0)
    {
        Assert.Equal(EvidenceArtifactFamily.CohortAcceptanceEvidence, decision.Family);
        Assert.Equal(path, decision.Path);
        Assert.Null(decision.GoalId);
        Assert.Equal(action, decision.Action);
        Assert.Equal(owner, decision.OwnerResolution);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(reclaimed, decision.BytesReclaimed);
    }

    private static (EvidenceArtifactFamily, EvidenceRetentionAction, string, string)[] OtherDecisions(
        StorageRetentionResult result, RetentionReclaimFixture fixture) => result.Decisions
        .Where(decision => decision.Family != EvidenceArtifactFamily.CohortAcceptanceEvidence)
        .Select(decision => (decision.Family, decision.Action, Path.GetRelativePath(fixture.ExecutionDirectory, decision.Path), decision.Reason))
        .ToArray();
}
