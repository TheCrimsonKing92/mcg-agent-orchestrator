using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every case owns a unique temporary root and SQLite databases;
// stores close their connections before the fixture removes that root.
public sealed class SourceSizeLandingPreflightExecutorTests
{
    [Xunit.Fact]
    public void Inline_ViolatingAuthority_ReturnsFailureWithResolvedHeads()
    {
        using var fixture = new Fixture(2);
        var resolvedPaths = new List<string>();

        var summary = SourceSizeLandingPreflightExecutor.RunInlineLandingSourceSizePreflight(
            fixture.Worktree, fixture.Root, path =>
            {
                resolvedPaths.Add(path);
                return path == fixture.Worktree ? "branch-head" : "main-head";
            });

        AssertFailureSummary(summary);
        Assert.Equal(new[] { fixture.Worktree, fixture.Root }, resolvedPaths);
    }

    [Xunit.Fact]
    public void Parallel_ViolatingAuthority_PreservesCandidateAndHeads()
    {
        using var fixture = new Fixture(2);
        var candidate = Candidate();

        var result = SourceSizeLandingPreflightExecutor.RunParallelLandingSourceSizePreflight(
            candidate, fixture.Worktree);

        Assert.NotNull(result);
        Assert.Same(candidate, result.Candidate);
        Assert.Null(result.EarlyResult);
        Assert.Null(result.Exception);
        AssertFailureSummary(result.Acceptance);
    }

    [Xunit.Fact]
    public void Cohort_ViolatingAuthority_PersistsFailedReceiptWithSuppliedTimestamp()
    {
        using var fixture = new Fixture(2);
        var identity = CohortIdentity();
        var store = new CohortAcceptanceStore(Path.Combine(fixture.Root, "cohort.db"));
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var receipt = SourceSizeLandingPreflightExecutor.RunAcceptanceCohortSourceSizePreflight(
            fixture.Worktree, identity, store, BoundDetail, timestamp);

        Assert.NotNull(receipt);
        var persisted = store.TryReadReceipt(identity.Value);
        Assert.NotNull(persisted);
        foreach (var actual in new[] { receipt, persisted })
        {
            Assert.StartsWith("cohort-receipt-v2-", actual.ReceiptId, StringComparison.Ordinal);
            Assert.Equal(identity.Value, actual.Identity.Value);
            Assert.Equal(AcceptanceCohortGateOutcome.Failed, actual.Outcome);
            Assert.False(actual.ValidForLanding);
            Assert.Equal(1, actual.GateExitCode);
            Assert.Equal(0, actual.GateElapsedMilliseconds);
            Assert.Empty(actual.GateTestResultPaths);
            Assert.Equal(timestamp, actual.CompletedAt);
            AssertReceiptFailures(actual.FailedChecks);
        }
        Assert.Equal(receipt.ReceiptId, persisted.ReceiptId);
    }

    [Xunit.Fact]
    public void MergeTrain_ViolatingAuthority_PersistsFailedReceipt()
    {
        using var fixture = new Fixture(2);
        var identity = TrainIdentity();
        var store = new MergeTrainAcceptanceStore(Path.Combine(fixture.Root, "train.db"));

        var receipt = SourceSizeLandingPreflightExecutor.RunMergeTrainSourceSizePreflight(
            fixture.Worktree, identity, store, BoundDetail);

        Assert.NotNull(receipt);
        var persisted = store.TryReadReceipt(identity.Value);
        Assert.NotNull(persisted);
        foreach (var actual in new[] { receipt, persisted })
        {
            Assert.StartsWith("merge-train-receipt-", actual.ReceiptId, StringComparison.Ordinal);
            Assert.Equal(identity.Value, actual.Identity.Value);
            Assert.Equal(MergeTrainGateOutcome.Failed, actual.Outcome);
            Assert.False(actual.ValidForLanding);
            Assert.Equal(1, actual.GateExitCode);
            Assert.Equal(0, actual.GateElapsedMilliseconds);
            Assert.Empty(actual.GateTestResultPaths);
            AssertReceiptFailures(actual.FailedChecks);
        }
        Assert.Equal(receipt.ReceiptId, persisted.ReceiptId);
        Assert.Equal(receipt.CompletedAt, persisted.CompletedAt);
    }

    [Xunit.Fact]
    public void AllPaths_CompliantAuthority_ReturnNullWithoutSavingReceipts()
    {
        using var fixture = new Fixture(3);
        var cohort = CohortIdentity();
        var train = TrainIdentity();
        var cohortStore = new CohortAcceptanceStore(Path.Combine(fixture.Root, "cohort.db"));
        var trainStore = new MergeTrainAcceptanceStore(Path.Combine(fixture.Root, "train.db"));

        Assert.Null(SourceSizeLandingPreflightExecutor.RunParallelLandingSourceSizePreflight(
            Candidate(), fixture.Worktree));
        Assert.Null(SourceSizeLandingPreflightExecutor.RunInlineLandingSourceSizePreflight(
            fixture.Worktree, fixture.Root, UnexpectedHeadResolution));
        Assert.Null(SourceSizeLandingPreflightExecutor.RunAcceptanceCohortSourceSizePreflight(
            fixture.Worktree, cohort, cohortStore, UnexpectedDetailBound, DateTimeOffset.UnixEpoch));
        Assert.Null(SourceSizeLandingPreflightExecutor.RunMergeTrainSourceSizePreflight(
            fixture.Worktree, train, trainStore, UnexpectedDetailBound));
        Assert.Null(cohortStore.TryReadReceipt(cohort.Value));
        Assert.Null(trainStore.TryReadReceipt(train.Value));
    }

    [Xunit.Fact]
    public void Inline_MissingInputs_ReturnsNullWithoutResolvingHeads()
    {
        using var fixture = new Fixture(2);
        var missing = Path.Combine(fixture.Root, "missing");
        Assert.False(Directory.Exists(missing));

        Assert.Null(SourceSizeLandingPreflightExecutor.RunInlineLandingSourceSizePreflight(
            missing, null, UnexpectedHeadResolution));
        Assert.Null(SourceSizeLandingPreflightExecutor.RunInlineLandingSourceSizePreflight(
            fixture.Worktree, null, UnexpectedHeadResolution));
        Assert.Null(SourceSizeLandingPreflightExecutor.RunInlineLandingSourceSizePreflight(
            missing, fixture.Root, UnexpectedHeadResolution));
    }

    private static void AssertFailureSummary(AcceptanceVerificationSummary? summary)
    {
        Assert.NotNull(summary);
        Assert.False(summary.Passed);
        var check = Assert.Single(summary.UnmetCriteria);
        Assert.Equal(SourceSizeRatchetPreflight.CheckName, check.Name);
        Assert.False(check.Passed);
        Assert.Equal(1, check.ExitCode);
        Assert.Equal(new[] { SourceSizeRatchetPreflight.CheckName }, summary.FailedChecks);
        Assert.Equal("branch-head", summary.BranchHeadSha);
        Assert.Equal("main-head", summary.MainHeadSha);
        Assert.Contains("guarded.cs has 3 lines", summary.FailureDetail, StringComparison.Ordinal);
        Assert.Contains("recorded ceiling of 2", summary.FailureDetail, StringComparison.Ordinal);
    }

    private static void AssertReceiptFailures(IReadOnlyList<string> failures)
    {
        Assert.Equal(2, failures.Count);
        Assert.Equal(SourceSizeRatchetPreflight.CheckName, failures[0]);
        Assert.StartsWith("bounded:", failures[1], StringComparison.Ordinal);
        Assert.Contains("guarded.cs has 3 lines", failures[1], StringComparison.Ordinal);
        Assert.Contains("recorded ceiling of 2", failures[1], StringComparison.Ordinal);
    }

    private static string BoundDetail(string detail) => $"bounded:{detail}";
    private static string? UnexpectedHeadResolution(string path) =>
        throw new InvalidOperationException($"Unexpected head resolution: {path}");
    private static string UnexpectedDetailBound(string detail) =>
        throw new InvalidOperationException($"Unexpected detail bound: {detail}");

    private static ConductorParallelAcceptanceCandidate Candidate() => new(
        new AgentOrchestratorKernel().CreateGoal("Source-size preflight"),
        0, [], [], [], [], "branch-head", "main-head");

    private static AcceptanceCohortIdentity CohortIdentity() => AcceptanceCohortIdentity.Create(
        [CohortMember('a'), CohortMember('b')], new string('c', 40), new string('d', 40), "manifest");

    private static AcceptanceCohortMemberBinding CohortMember(char id) => new(
        new GoalId(new string(id, 32)), new string(id, 40), new string(id, 40),
        [$"src/{id}.cs"], [$"member:{id}"], ChangeRiskTier.Behavior,
        ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(),
        GateReadyMergeReason.NoConflictsDetected.ToString());

    private static MergeTrainIdentity TrainIdentity() => MergeTrainIdentity.Create(
        [TrainMember('a'), TrainMember('b')], new string('c', 40), new string('d', 40), "manifest");

    private static MergeTrainMemberBinding TrainMember(char id) => new(
        new GoalId(new string(id, 32)), new string(id, 40), new string(id, 40),
        [$"src/{id}.cs"], [$"member:{id}"], ChangeRiskTier.Behavior,
        ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(),
        GateReadyMergeReason.NoConflictsDetected.ToString(), new string(id, 40));

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(
            Path.GetTempPath(), nameof(SourceSizeLandingPreflightExecutorTests), Guid.NewGuid().ToString("N"));
        internal string Worktree => Path.Combine(Root, "worktree");

        internal Fixture(int ceiling)
        {
            var authority = Path.Combine(Worktree,
                SourceSizeRatchet.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(authority)!);
            File.WriteAllText(authority, $"new SourceSizeCeiling(\"guarded.cs\", {ceiling})");
            File.WriteAllLines(Path.Combine(Worktree, "guarded.cs"), ["one", "two", "three"]);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
