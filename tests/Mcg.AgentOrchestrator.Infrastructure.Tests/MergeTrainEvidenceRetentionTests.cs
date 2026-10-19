using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class MergeTrainEvidenceRetentionTests
{
    private const string CancelledId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string[] SourceNames = ["z.trx", "a.trx"];
    private const string RegistryNote = "TRX copies of train receipts that cannot back a landing are reclaimed by MergeTrainEvidenceRetention 14 days after the receipt completes once every member goal is Completed, Cancelled or Superseded; landing-evidence trains and gate-verdict.json are kept.";

    [Fact]
    public void OldRedTrainReclaimsOrderedTrxCopiesAndKeepsReceiptVerdictAndOtherFiles()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var directory = TrainDirectory(receipt);
        var nested = Write(Path.Combine(directory, "nested", "old.trx"), "nested evidence");
        var notes = Write(Path.Combine(directory, "notes.txt"), "notes");
        var trxLookalike = Write(Path.Combine(directory, "other.trx.tmp"), "not a TRX");
        var lengths = receipt.GateTestResultPaths.ToDictionary(path => path, path => new FileInfo(path).Length);

        var decisions = Sweep(fixture, TerminalGoals(fixture));

        AssertDeleted(receipt, decisions, lengths);
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(Path.Combine(directory, "gate-verdict.json")));
        Assert.True(File.Exists(nested));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(trxLookalike));
        Assert.DoesNotContain(decisions, decision => decision.Path == nested || decision.Path == notes || decision.Path == trxLookalike);
        Assert.NotNull(MergeTrainAcceptanceStore.OpenReadOnly(StorePath(fixture)).TryReadReceipt(receipt.Identity.Value));
    }

    [Theory]
    [InlineData(MergeTrainGateOutcome.Passed, true, false)]
    [InlineData(MergeTrainGateOutcome.Passed, false, true)]
    [InlineData(MergeTrainGateOutcome.InfrastructureFailure, false, true)]
    [InlineData(MergeTrainGateOutcome.Invalidated, false, true)]
    public void LandingFieldsDecideWhetherOldReceiptCanBeReclaimed(
        MergeTrainGateOutcome outcome, bool validForLanding, bool reclaim)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture, outcome: outcome, validForLanding: validForLanding);
        var lengths = receipt.GateTestResultPaths.ToDictionary(path => path, path => new FileInfo(path).Length);

        var decisions = Sweep(fixture, TerminalGoals(fixture).Select(goal => goal with { Status = GoalStatus.Completed }).ToArray());

        if (reclaim) AssertDeleted(receipt, decisions, lengths);
        else AssertKept(receipt, decisions, EvidenceRetentionAction.Preserved,
            EvidenceOwnerResolution.Unrecorded, "merge-train-landing-evidence");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1213200000, false)] // CompletedAt is one hour in the future.
    public void AgeComesOnlyFromReceiptAndIncludesExactBoundary(long millisecondsPastBoundary, bool reclaim)
    {
        using var fixture = new RetentionReclaimFixture();
        var completedAt = RetentionReclaimFixture.Now.AddDays(-14).AddMilliseconds(-millisecondsPastBoundary);
        var receipt = Save(fixture, completedAt: completedAt);
        var lengths = receipt.GateTestResultPaths.ToDictionary(path => path, path => new FileInfo(path).Length);
        Assert.Equal(completedAt, MergeTrainAcceptanceStore.OpenReadOnly(StorePath(fixture))
            .TryReadReceipt(receipt.Identity.Value)!.CompletedAt);

        var decisions = Sweep(fixture, TerminalGoals(fixture).Select(goal => goal with { Status = GoalStatus.Completed }).ToArray());

        if (reclaim) AssertDeleted(receipt, decisions, lengths);
        else AssertKept(receipt, decisions, EvidenceRetentionAction.Preserved,
            EvidenceOwnerResolution.Unrecorded, "merge-train-within-age");
    }

    [Theory]
    [InlineData(GoalStatus.Verified)]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.Failed)]
    public void NonReclaimableMemberStatusPreservesWholeDirectory(GoalStatus status)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var goals = TerminalGoals(fixture);
        goals[1] = goals[1] with { Status = status };

        AssertKept(receipt, Sweep(fixture, goals), EvidenceRetentionAction.Preserved,
            EvidenceOwnerResolution.NonTerminal, "merge-train-member-not-terminal");
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("stand-in")]
    [InlineData("duplicate")]
    [InlineData("stand-in-collision")]
    [InlineData("prefix-only")]
    public void UnresolvedMemberTakesPrecedenceOverNonTerminalMember(string kind)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var goals = TerminalGoals(fixture).ToList();
        goals[0] = goals[0] with { Status = GoalStatus.Active };
        if (kind == "absent") goals.RemoveAt(1);
        else if (kind == "stand-in") goals[1] = goals[1] with { IsStoreStandIn = true };
        else if (kind == "prefix-only") goals[1] = goals[1] with { GoalId = CancelledId[..8] };
        else goals.Add(goals[1] with { GoalId = CancelledId.ToUpperInvariant(), IsStoreStandIn = kind == "stand-in-collision" });

        AssertKept(receipt, Sweep(fixture, goals.ToArray()), EvidenceRetentionAction.RetainedUndecidable,
            EvidenceOwnerResolution.Unmatched, "merge-train-member-unresolved");
    }

    [Fact]
    public void FullMemberIdsMatchIgnoringCaseAndSupersededIsReclaimable()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var goals = TerminalGoals(fixture);
        goals[1] = goals[1] with { GoalId = CancelledId.ToUpperInvariant(), Status = GoalStatus.Superseded };

        var decisions = Sweep(fixture, goals);

        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, decision => Assert.Equal(EvidenceRetentionAction.Deleted, decision.Action));
        Assert.All(receipt.GateTestResultPaths, path => Assert.False(File.Exists(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoRealGoalsAddsNoDecision(bool empty)
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var goals = empty ? [] : TerminalGoals(fixture).Select(goal => goal with { IsStoreStandIn = true }).ToArray();

        Assert.Empty(Sweep(fixture, goals));
        Assert.All(receipt.GateTestResultPaths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void UnrecordedDirectoryIsUndecidableAndVerdictOnlyDirectoryAddsNoDecision()
    {
        using var fixture = new RetentionReclaimFixture();
        _ = new MergeTrainAcceptanceStore(StorePath(fixture));
        var root = Path.Combine(fixture.OrchestratorDirectory, "merge-train-evidence");
        var path = Write(Path.Combine(root, "unrecorded", "old.trx"), "unrecorded TRX");
        var verdict = Write(Path.Combine(root, "verdict-only", "gate-verdict.json"), "{}");

        var decision = Assert.Single(Sweep(fixture, TerminalGoals(fixture)));

        AssertDecision(decision, Path.GetDirectoryName(path)!, EvidenceRetentionAction.RetainedUndecidable,
            EvidenceOwnerResolution.Unrecorded, "merge-train-receipt-not-recorded");
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(verdict));
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
        if (path is not null) Assert.True(File.Exists(path));
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
            EvidenceOwnerResolution.Unrecorded, "merge-train-store-unavailable");
        Assert.NotNull(decision.FailureExceptionType);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void LockedCopyIsDeferredAndRetriedAfterHandleCloses()
    {
        using var fixture = new RetentionReclaimFixture();
        var receipt = Save(fixture);
        var path = receipt.GateTestResultPaths[0];
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var decisions = Sweep(fixture, TerminalGoals(fixture));
            Assert.Equal(2, decisions.Count);
            var locked = Assert.Single(decisions, decision => decision.Path == path);
            AssertDecision(locked, path, EvidenceRetentionAction.DeferredLocked,
                EvidenceOwnerResolution.UniqueTerminal, "merge-train-trx-locked");
            Assert.NotNull(locked.FailureExceptionType);
            Assert.True(File.Exists(path));
            Assert.Equal(EvidenceRetentionAction.Deleted, Assert.Single(decisions, decision => decision.Path != path).Action);
        }

        var retried = Assert.Single(Sweep(fixture, TerminalGoals(fixture)));

        AssertDecision(retried, path, EvidenceRetentionAction.Deleted,
            EvidenceOwnerResolution.UniqueTerminal, "merge-train-red-trx-past-age", retried.BytesAttempted);
        Assert.True(retried.BytesReclaimed > 0);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DailySweepAddsOnlyMergeTrainDecisionsAndLeavesExistingCountersEqual()
    {
        using var baseline = new RetentionReclaimFixture();
        using var withEvidence = new RetentionReclaimFixture();
        var goals = TerminalGoals(baseline);
        var receipt = Save(withEvidence);
        var lengths = receipt.GateTestResultPaths.ToDictionary(path => path, path => new FileInfo(path).Length);

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
        Assert.True(File.Exists(Path.Combine(TrainDirectory(receipt), "gate-verdict.json")));
    }

    [Fact]
    public void RegistryDeclaresReceiptBasedRetentionAndKeepsWriterIdentity()
    {
        var entry = OrchestratorArtifactRegistry.Classify("merge-train-evidence");

        Assert.NotNull(entry);
        Assert.Equal(OrchestratorArtifactRegistry.RetentionKind.ReclaimAfterGoalTerminalPlusDays, entry.Retention);
        Assert.Equal(StorageRetentionMaintenance.AcceptanceArtifactMaxAge, entry.RetentionAge);
        Assert.Equal(RegistryNote, entry.Note);
        Assert.EndsWith("Execution/Persistence/MergeTrainAcceptanceStore.cs", entry.WriterPath);
    }

    private static StorageRetentionGoal[] TerminalGoals(RetentionReclaimFixture fixture) =>
        [fixture.Goal(RetentionReclaimFixture.CompletedId, GoalStatus.Completed), fixture.Goal(CancelledId, GoalStatus.Cancelled)];

    private static string StorePath(RetentionReclaimFixture fixture) =>
        Path.Combine(fixture.OrchestratorDirectory, "merge-train-acceptance.db");

    private static string TrainDirectory(MergeTrainReceipt receipt) => Path.GetDirectoryName(receipt.GateTestResultPaths[0])!;

    private static List<EvidenceRetentionDecision> Sweep(RetentionReclaimFixture fixture, params StorageRetentionGoal[] goals)
    {
        var decisions = new List<EvidenceRetentionDecision>();
        MergeTrainEvidenceRetention.Sweep(fixture.OrchestratorDirectory, goals, RetentionReclaimFixture.Now, decisions);
        return decisions;
    }

    private static MergeTrainReceipt Save(RetentionReclaimFixture fixture,
        MergeTrainGateOutcome outcome = MergeTrainGateOutcome.Failed, bool validForLanding = false,
        DateTimeOffset? completedAt = null)
    {
        var sources = SourceNames.Select(name => Write(Path.Combine(fixture.ExecutionDirectory, name), PassingTrx(name)))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(sources));
        var identity = MergeTrainIdentity.Create(
            [Binding(RetentionReclaimFixture.CompletedId), Binding(CancelledId)], new string('f', 40), new string('1', 40), "manifest-v1");
        return new MergeTrainAcceptanceStore(StorePath(fixture)).SaveGateReceipt(new MergeTrainReceipt(
            "retention-receipt", identity, outcome, completedAt ?? RetentionReclaimFixture.Now.AddDays(-15),
            1, [], outcome == MergeTrainGateOutcome.Passed || validForLanding ? 0 : 1, sources, validForLanding));
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
        Assert.Equal(paths, decisions.Select(decision => decision.Path).ToArray());
        foreach (var decision in decisions)
        {
            AssertDecision(decision, decision.Path, EvidenceRetentionAction.Deleted,
                EvidenceOwnerResolution.UniqueTerminal, "merge-train-red-trx-past-age", lengths[decision.Path]);
            Assert.Equal(lengths[decision.Path], decision.BytesAttempted);
            Assert.Null(decision.FailureExceptionType);
            Assert.False(File.Exists(decision.Path));
        }
    }

    private static void AssertKept(MergeTrainReceipt receipt, IReadOnlyList<EvidenceRetentionDecision> decisions,
        EvidenceRetentionAction action, EvidenceOwnerResolution owner, string reason)
    {
        var decision = Assert.Single(decisions);
        AssertDecision(decision, TrainDirectory(receipt), action, owner, reason);
        Assert.Equal(0, decision.BytesAttempted);
        Assert.Null(decision.FailureExceptionType);
        Assert.All(receipt.GateTestResultPaths, path => Assert.True(File.Exists(path)));
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
