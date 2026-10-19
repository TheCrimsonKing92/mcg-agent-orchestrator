using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: every filesystem artifact and SQLite database belongs to an isolated fixture root.
public sealed class LandedMergeTrainEvidenceRetentionTests
{
    private const string SupersededId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string[] SourceNames = ["z.trx", "a.trx"];
    private const string RegistryNote = "TRX copies of train receipts that cannot back a landing are reclaimed by MergeTrainEvidenceRetention 14 days after the receipt completes, and those of finalized landings by LandedMergeTrainEvidenceRetention 3 days after, once every member goal is Completed, Cancelled or Superseded; unlanded passing trains, prepared landings and gate-verdict.json are kept.";

    [Fact]
    public void OldFinalizedTrainReclaimsCopiesAndKeepsVerdictRowsAndDatabaseBytes()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var directory = TrainDirectory(receipt);
        var nested = Write(Path.Combine(directory, "nested", "old.trx"), "nested evidence");
        var notes = Write(Path.Combine(directory, "notes.txt"), "notes");
        var lookalike = Write(Path.Combine(directory, "other.trx.tmp"), "not a TRX");
        var lengths = Lengths(receipt);
        var verdict = File.ReadAllBytes(Path.Combine(directory, "gate-verdict.json"));
        var database = File.ReadAllBytes(StorePath(fixture));

        var decisions = Sweep(fixture, TerminalGoals(fixture));

        AssertDeleted(receipt, decisions, lengths);
        Assert.True(Directory.Exists(directory));
        Assert.Equal(verdict, File.ReadAllBytes(Path.Combine(directory, "gate-verdict.json")));
        Assert.Equal("nested evidence", File.ReadAllText(nested));
        Assert.Equal("notes", File.ReadAllText(notes));
        Assert.Equal("not a TRX", File.ReadAllText(lookalike));
        Assert.DoesNotContain(decisions, decision => decision.Path == nested || decision.Path == notes || decision.Path == lookalike);
        var store = MergeTrainAcceptanceStore.OpenReadOnly(StorePath(fixture));
        Assert.Equal(receipt.ReceiptId, store.TryReadReceipt(receipt.Identity.Value)!.ReceiptId);
        Assert.Contains(receipt.Identity.Value, store.ReadFinalizedLandingTrainIds());
        Assert.Equal(database, File.ReadAllBytes(StorePath(fixture)));
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("none")]
    public void PreparedAndUnlandedTrainsKeepEveryCopy(string landing)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture, landing: landing);

        AssertKept(receipt, Sweep(fixture, TerminalGoals(fixture)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-262800000, false)] // Three days plus one hour puts CompletedAt one hour in the future.
    public void ReceiptAgeRequiresStrictlyMoreThanThreeDays(long millisecondsPastBoundary, bool reclaim)
    {
        using var fixture = new RetentionReclaimFixture();
        var completedAt = RetentionReclaimFixture.Now.AddDays(-3).AddMilliseconds(-millisecondsPastBoundary);
        var receipt = Save(fixture, completedAt: completedAt);
        var lengths = Lengths(receipt);
        Assert.Equal(completedAt, MergeTrainAcceptanceStore.OpenReadOnly(StorePath(fixture))
            .TryReadReceipt(receipt.Identity.Value)!.CompletedAt);

        var decisions = Sweep(fixture, TerminalGoals(fixture));

        if (reclaim) AssertDeleted(receipt, decisions, lengths);
        else AssertKept(receipt, decisions);
    }

    [Theory]
    [InlineData(GoalStatus.Verified)]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.Failed)]
    public void NonReclaimableMemberKeepsEveryCopy(GoalStatus status)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var goals = TerminalGoals(fixture);
        goals[1] = goals[1] with { Status = status };

        AssertKept(receipt, Sweep(fixture, goals));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("stand-in")]
    [InlineData("duplicate")]
    [InlineData("stand-in-collision")]
    [InlineData("prefix-only")]
    public void EveryMemberRequiresExactlyOneRealFullIdMatch(string kind)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var goals = TerminalGoals(fixture).ToList();
        if (kind == "absent") goals.RemoveAt(1);
        else if (kind == "stand-in") goals[1] = goals[1] with { IsStoreStandIn = true };
        else if (kind == "prefix-only") goals[1] = goals[1] with { GoalId = SupersededId[..8] };
        else goals.Add(goals[1] with { GoalId = SupersededId.ToUpperInvariant(), IsStoreStandIn = kind == "stand-in-collision" });

        AssertKept(receipt, Sweep(fixture, goals.ToArray()));
    }

    [Fact]
    public void CaseInsensitiveFullIdsAndCancelledMembersCanBeReclaimed()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var lengths = Lengths(receipt);
        var goals = TerminalGoals(fixture);
        goals[1] = goals[1] with { GoalId = SupersededId.ToUpperInvariant(), Status = GoalStatus.Cancelled };

        AssertDeleted(receipt, Sweep(fixture, goals), lengths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoRealGoalsAddsNoDecision(bool empty)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var goals = empty ? [] : TerminalGoals(fixture).Select(goal => goal with { IsStoreStandIn = true }).ToArray();

        AssertKept(receipt, Sweep(fixture, goals));
    }

    [Theory]
    [InlineData(MergeTrainGateOutcome.Failed, false)]
    [InlineData(MergeTrainGateOutcome.Passed, false)]
    [InlineData(MergeTrainGateOutcome.Failed, true)]
    public void NonLandingReceiptsStayForTheOtherRetentionPass(MergeTrainGateOutcome outcome, bool validForLanding)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture, landing: "none", outcome: outcome, validForLanding: validForLanding,
            completedAt: RetentionReclaimFixture.Now.AddDays(-15));

        AssertKept(receipt, Sweep(fixture, TerminalGoals(fixture)));
    }

    [Fact]
    public void DirectoryWithoutReceiptAddsNoDecision()
    {
        using var fixture = new RetentionReclaimFixture();
        _ = new MergeTrainAcceptanceStore(StorePath(fixture));
        var path = Write(Path.Combine(fixture.OrchestratorDirectory, "merge-train-evidence", "unrecorded", "old.trx"), "evidence");
        var verdict = Write(Path.Combine(Path.GetDirectoryName(path)!, "gate-verdict.json"), "{}");

        Assert.Empty(Sweep(fixture, TerminalGoals(fixture)));
        Assert.Equal("evidence", File.ReadAllText(path));
        Assert.Equal("{}", File.ReadAllText(verdict));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingRootOrStoreAddsNoDecision(bool missingRoot)
    {
        using var fixture = new RetentionReclaimFixture();
        string? path = null;
        if (missingRoot) _ = new MergeTrainAcceptanceStore(StorePath(fixture));
        else path = Write(Path.Combine(fixture.OrchestratorDirectory, "merge-train-evidence", "train", "old.trx"), "evidence");

        Assert.Empty(Sweep(fixture, TerminalGoals(fixture)));
        if (path is not null) Assert.Equal("evidence", File.ReadAllText(path));
    }

    [Fact]
    public void InvalidStoreRetainsEvidenceWithOneTypedFailure()
    {
        using var fixture = new RetentionReclaimFixture();
        var root = Path.Combine(fixture.OrchestratorDirectory, "merge-train-evidence");
        var path = Write(Path.Combine(root, "train", "old.trx"), "evidence");
        Write(StorePath(fixture), "not a SQLite database");

        var decision = Assert.Single(Sweep(fixture, TerminalGoals(fixture)));

        AssertDecision(decision, root, EvidenceRetentionAction.RetainedUndecidable,
            EvidenceOwnerResolution.Unrecorded, "landed-merge-train-store-unavailable");
        Assert.Equal(0, decision.BytesAttempted);
        Assert.NotNull(decision.FailureExceptionType);
        Assert.Equal("evidence", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{", "JsonException")]
    [InlineData("{\"members\":[]}", "ArgumentException")]
    public void MalformedReceiptRetainsEvidenceAndAllowsLaterSweepFamilies(string payload, string exceptionType)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var events = Write(Path.Combine(fixture.OrchestratorDirectory, "goal-events",
            RetentionReclaimFixture.CompletedId + ".jsonl"), "{\"cursor\":1}");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = StorePath(fixture), Pooling = false }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE merge_train_receipts SET payload_json=$payload WHERE train_id=$id;";
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$id", receipt.Identity.Value);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var failure = Record.Exception(() => MergeTrainAcceptanceStore.OpenReadOnly(StorePath(fixture))
            .ReadReceiptsSince(DateTimeOffset.MinValue));
        Assert.NotNull(failure);
        Assert.Equal(exceptionType, failure.GetType().Name);

        var decision = Assert.Single(Sweep(fixture, TerminalGoals(fixture)));

        AssertDecision(decision, Path.Combine(fixture.OrchestratorDirectory, "merge-train-evidence"),
            EvidenceRetentionAction.RetainedUndecidable, EvidenceOwnerResolution.Unrecorded,
            "landed-merge-train-store-unavailable");
        Assert.Equal(exceptionType, decision.FailureExceptionType);
        Assert.Equal(0, decision.BytesAttempted);
        Assert.All(receipt.GateTestResultPaths, path => Assert.True(File.Exists(path)));

        var result = fixture.Run(null, TerminalGoals(fixture));

        Assert.False(result.Failed);
        Assert.Contains(result.Decisions, item => item == decision);
        Assert.Contains(result.Decisions, item => item.Family == EvidenceArtifactFamily.GoalEvents
            && item.Path == events && item.Action == EvidenceRetentionAction.Preserved
            && item.Reason == "replay-contract-requires-raw-jsonl");
        Assert.All(receipt.GateTestResultPaths, path => Assert.True(File.Exists(path)));
        Assert.True(File.Exists(Path.Combine(TrainDirectory(receipt), "gate-verdict.json")));
        Assert.Equal("{\"cursor\":1}", File.ReadAllText(events));
    }

    [Fact]
    public void DuplicateRecomputedIdentitiesStillReclaimFinalizedEvidence()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var lengths = Lengths(receipt);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = StorePath(fixture), Pooling = false }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // Distinct legacy row keys may deserialize to the same current identity.
            command.CommandText = """
                INSERT INTO merge_train_receipts(train_id, receipt_id, payload_json)
                SELECT 'legacy-train-id', 'legacy-receipt-id', payload_json
                FROM merge_train_receipts WHERE train_id=$id;
                """;
            command.Parameters.AddWithValue("$id", receipt.Identity.Value);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var receipts = MergeTrainAcceptanceStore.OpenReadOnly(StorePath(fixture))
            .ReadReceiptsSince(DateTimeOffset.MinValue);
        Assert.Equal(2, receipts.Count);
        Assert.Equal(receipt.Identity.Value, Assert.Single(receipts.Select(item => item.Identity.Value).Distinct()));

        AssertDeleted(receipt, Sweep(fixture, TerminalGoals(fixture)), lengths);
        Assert.True(File.Exists(Path.Combine(TrainDirectory(receipt), "gate-verdict.json")));
    }

    [Fact]
    public void ReadOnlyStoreReturnsOnlyFinalizedTrainIdsWithOrdinalMembership()
    {
        using var fixture = new RetentionReclaimFixture();
        var first = Save(fixture, variant: "first");
        var second = Save(fixture, variant: "second");
        var prepared = Save(fixture, landing: "prepared", variant: "prepared");
        var unlanded = Save(fixture, landing: "none", variant: "unlanded");
        var database = File.ReadAllBytes(StorePath(fixture));

        var ids = MergeTrainAcceptanceStore.OpenReadOnly(StorePath(fixture)).ReadFinalizedLandingTrainIds();

        Assert.Equal(new[] { first.Identity.Value, second.Identity.Value }.Order(StringComparer.Ordinal), ids.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(prepared.Identity.Value, ids);
        Assert.DoesNotContain(unlanded.Identity.Value, ids);
        Assert.False(ids.Contains(first.Identity.Value.ToUpperInvariant()));
        Assert.Equal(database, File.ReadAllBytes(StorePath(fixture)));
    }

    [Fact]
    public void ReadOnlyStoreWithoutLandingRowsReturnsEmptySet()
    {
        using var fixture = new RetentionReclaimFixture();
        _ = new MergeTrainAcceptanceStore(StorePath(fixture));

        Assert.Empty(MergeTrainAcceptanceStore.OpenReadOnly(StorePath(fixture)).ReadFinalizedLandingTrainIds());
    }

    [Fact]
    public void LockedCopyIsDeferredThenDeletedAfterHandleCloses()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var path = receipt.GateTestResultPaths[0];
        var lengths = Lengths(receipt);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var decisions = Sweep(fixture, TerminalGoals(fixture));
            Assert.Equal(2, decisions.Count);
            var locked = Assert.Single(decisions, decision => decision.Path == path);
            AssertDecision(locked, path, EvidenceRetentionAction.DeferredLocked,
                EvidenceOwnerResolution.UniqueTerminal, "merge-train-trx-locked");
            Assert.Equal(lengths[path], locked.BytesAttempted);
            Assert.NotNull(locked.FailureExceptionType);
            Assert.True(File.Exists(path));
            var deleted = Assert.Single(decisions, decision => decision.Path != path);
            AssertDecision(deleted, deleted.Path, EvidenceRetentionAction.Deleted,
                EvidenceOwnerResolution.UniqueTerminal, "merge-train-landed-trx-past-age", lengths[deleted.Path]);
            Assert.False(File.Exists(deleted.Path));
        }

        var retried = Assert.Single(Sweep(fixture, TerminalGoals(fixture)));

        AssertDecision(retried, path, EvidenceRetentionAction.Deleted,
            EvidenceOwnerResolution.UniqueTerminal, "merge-train-landed-trx-past-age", lengths[path]);
        Assert.Equal(lengths[path], retried.BytesAttempted);
        Assert.Null(retried.FailureExceptionType);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TrainsAndFilesAreReclaimedInOrdinalNameOrder()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipts = new[] { Save(fixture, variant: "z"), Save(fixture, variant: "a") };
        var expected = receipts.OrderBy(receipt => receipt.Identity.Value, StringComparer.Ordinal)
            .SelectMany(receipt => receipt.GateTestResultPaths.OrderBy(Path.GetFileName, StringComparer.Ordinal)).ToArray();

        var decisions = Sweep(fixture, TerminalGoals(fixture));

        Assert.Equal(4, decisions.Count);
        Assert.Equal(expected, decisions.Select(decision => decision.Path).ToArray());
        Assert.All(decisions, decision => Assert.Equal(EvidenceRetentionAction.Deleted, decision.Action));
        Assert.All(expected, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public void DailySweepReclaimsBeforeRedTrainPassAndKeepsBaselineEqual()
    {
        using var baseline = new RetentionReclaimFixture();
        using var withEvidence = new RetentionReclaimFixture();
        var goals = TerminalGoals(baseline);
        var receipt = Save(withEvidence);
        var lengths = Lengths(receipt);

        var before = baseline.Run(null, goals);
        var after = withEvidence.Run(null, goals);

        Assert.False(before.Failed);
        Assert.False(after.Failed);
        Assert.Equal(before.WorkerArtifactsDeleted, after.WorkerArtifactsDeleted);
        Assert.Equal(before.WorkerLogsCompressed, after.WorkerLogsCompressed);
        Assert.Equal(before.SuccessfulTrxReceiptsWritten, after.SuccessfulTrxReceiptsWritten);
        Assert.Equal(before.AcceptanceArtifactsDeleted, after.AcceptanceArtifactsDeleted);
        Assert.Equal(before.PromptArtifactsDeleted, after.PromptArtifactsDeleted);
        Assert.Equal(before.GoalJournalsArchived, after.GoalJournalsArchived);
        Assert.Equal(OtherDecisions(before, baseline), OtherDecisions(after, withEvidence));
        Assert.DoesNotContain(before.Decisions, decision => decision.Family == EvidenceArtifactFamily.MergeTrainEvidence);
        AssertDeleted(receipt, after.Decisions.Where(decision => decision.Family == EvidenceArtifactFamily.MergeTrainEvidence).ToArray(), lengths);
        Assert.DoesNotContain(after.Decisions, decision => decision.Reason == "merge-train-landing-evidence");
        Assert.True(File.Exists(Path.Combine(TrainDirectory(receipt), "gate-verdict.json")));
    }

    [Fact]
    public void RegistryDeclaresFinalizedRetentionWithExistingKindAndAge()
    {
        var entry = OrchestratorArtifactRegistry.Classify("merge-train-evidence");

        Assert.NotNull(entry);
        Assert.Equal(RegistryNote, entry.Note);
        Assert.Equal(OrchestratorArtifactRegistry.RetentionKind.ReclaimAfterGoalTerminalPlusDays, entry.Retention);
        Assert.Equal(StorageRetentionMaintenance.AcceptanceArtifactMaxAge, entry.RetentionAge);
    }

    private static StorageRetentionGoal[] TerminalGoals(RetentionReclaimFixture fixture) =>
        [fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed), fixture.Goal(SupersededId, GoalStatus.Superseded)];

    private static string StorePath(RetentionReclaimFixture fixture) =>
        Path.Combine(fixture.OrchestratorDirectory, "merge-train-acceptance.db");

    private static string TrainDirectory(MergeTrainReceipt receipt) => Path.GetDirectoryName(receipt.GateTestResultPaths[0])!;

    private static Dictionary<string, long> Lengths(MergeTrainReceipt receipt) =>
        receipt.GateTestResultPaths.ToDictionary(path => path, path => new FileInfo(path).Length);

    private static List<EvidenceRetentionDecision> Sweep(RetentionReclaimFixture fixture, params StorageRetentionGoal[] goals)
    {
        var decisions = new List<EvidenceRetentionDecision>();
        LandedMergeTrainEvidenceRetention.Sweep(fixture.OrchestratorDirectory, goals, RetentionReclaimFixture.Now, decisions);
        return decisions;
    }

    private static MergeTrainReceipt Save(RetentionReclaimFixture fixture, string landing = "finalized",
        DateTimeOffset? completedAt = null, MergeTrainGateOutcome outcome = MergeTrainGateOutcome.Passed,
        bool validForLanding = true, string variant = "default")
    {
        var sources = SourceNames.Select(name => Write(
            Path.Combine(fixture.ExecutionDirectory, "sources", variant, name), PassingTrx(name)))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(sources));
        var identity = MergeTrainIdentity.Create(
            [Binding(RetentionReclaimFixture.CompletedId), Binding(SupersededId)],
            new string('f', 40), new string('1', 40), "manifest-" + variant);
        var store = new MergeTrainAcceptanceStore(StorePath(fixture));
        var receipt = store.SaveGateReceipt(new MergeTrainReceipt(
            "landed-retention-" + variant, identity, outcome, completedAt ?? RetentionReclaimFixture.Now.AddDays(-4),
            1, [], outcome == MergeTrainGateOutcome.Passed || validForLanding ? 0 : 1, sources, validForLanding));
        Assert.Equal(2, receipt.GateTestResultPaths.Count);
        Assert.All(receipt.GateTestResultPaths, path => Assert.True(File.Exists(path)));
        if (landing != "none")
        {
            Assert.True(receipt.HasAuthoritativeLandingEvidence);
            store.PrepareLanding(receipt, new string('d', 40), null);
            if (landing == "finalized") store.CompleteLandingEffects(receipt.Identity.Value, receipt.ReceiptId);
        }
        Assert.Equal(landing == "finalized", store.ReadFinalizedLandingTrainIds().Contains(receipt.Identity.Value));
        return receipt;
    }

    private static MergeTrainMemberBinding Binding(string goalId) => new(
        new GoalId(goalId), new string('a', 40), new string('b', 40), ["src/Example.cs"], [],
        ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected", new string('c', 40));

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

    private static void AssertDeleted(MergeTrainReceipt receipt, IReadOnlyList<EvidenceRetentionDecision> decisions,
        Dictionary<string, long> lengths)
    {
        var paths = receipt.GateTestResultPaths.OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
        Assert.Equal(2, decisions.Count);
        Assert.Equal(paths, decisions.Select(decision => decision.Path).ToArray());
        foreach (var decision in decisions)
        {
            Assert.True(lengths[decision.Path] > 0);
            AssertDecision(decision, decision.Path, EvidenceRetentionAction.Deleted,
                EvidenceOwnerResolution.UniqueTerminal, "merge-train-landed-trx-past-age", lengths[decision.Path]);
            Assert.Equal(lengths[decision.Path], decision.BytesAttempted);
            Assert.Null(decision.FailureExceptionType);
            Assert.False(File.Exists(decision.Path));
        }
    }

    private static void AssertKept(MergeTrainReceipt receipt, IReadOnlyList<EvidenceRetentionDecision> decisions)
    {
        Assert.Empty(decisions);
        Assert.All(receipt.GateTestResultPaths, path => Assert.True(File.Exists(path)));
        Assert.True(File.Exists(Path.Combine(TrainDirectory(receipt), "gate-verdict.json")));
    }

    private static void AssertDecision(EvidenceRetentionDecision decision, string path,
        EvidenceRetentionAction action, EvidenceOwnerResolution owner, string reason, long reclaimed = 0)
    {
        Assert.Equal(EvidenceArtifactFamily.MergeTrainEvidence, decision.Family);
        Assert.Equal(path, decision.Path);
        Assert.Null(decision.GoalId);
        Assert.Equal(action, decision.Action);
        Assert.Equal(owner, decision.OwnerResolution);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(reclaimed, decision.BytesReclaimed);
    }

    private static (EvidenceArtifactFamily, EvidenceRetentionAction, string, string)[] OtherDecisions(
        StorageRetentionResult result, RetentionReclaimFixture fixture) => result.Decisions
        .Where(decision => decision.Family != EvidenceArtifactFamily.MergeTrainEvidence)
        .Select(decision => (decision.Family, decision.Action, Path.GetRelativePath(fixture.ExecutionDirectory, decision.Path), decision.Reason))
        .ToArray();
}
