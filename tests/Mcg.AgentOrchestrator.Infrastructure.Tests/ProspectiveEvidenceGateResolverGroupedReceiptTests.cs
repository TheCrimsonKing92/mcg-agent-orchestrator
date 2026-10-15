using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns all database, custody and source TRX paths.
public sealed class ProspectiveEvidenceGateResolverGroupedReceiptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupedReceiptResolvesAllIncidentShapesWithRerunProvenance(bool cohort)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(GroupedGateTrxSamples.Selectors);
        var receipt = Save(fixture, GroupedGateTrxSamples.WriteIncidentAndRerun(fixture.Root), cohort);

        var result = fixture.Resolve();

        Assert.Equal([wait.Id], result.ResolvedRequestIds);
        Assert.Empty(result.UnresolvedWaits);
        Assert.True(wait.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Worker, wait.AuthoritativeAnswer!.Origin);
        Assert.StartsWith($"Satisfied by {receipt.Kind} receipt {receipt.ReceiptId} for candidate {fixture.CandidateSha}; TRX ",
            wait.Answer, StringComparison.Ordinal);
        Assert.All(receipt.TrxPaths, path => Assert.Contains(path, wait.Answer, StringComparison.Ordinal));
        Assert.Contains("CompletionBuildEvidenceLifecycleTests: 1 passed, 0 failed, 1 superseded by rerun",
            wait.Answer, StringComparison.Ordinal);
        Assert.Empty(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id));
    }

    [Fact]
    public void FailedReceiptCannotResolveEvenWhenEveryNamedClassPassed()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(GroupedGateTrxSamples.Selectors);
        var path = GroupedGateTrxSamples.Write(fixture.Root, "passed.trx",
            GroupedGateTrxSamples.Lifecycle with { Outcome = "Passed" },
            GroupedGateTrxSamples.Namespaced, GroupedGateTrxSamples.MethodOnly);
        Save(fixture, [path], passed: false);

        AssertPending(fixture, wait, fixture.Resolve());
    }

    [Fact]
    public void MissingClassIsReportedFromNewestGroupedReceiptWithoutSoloAttempt()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(GroupedGateTrxSamples.Selectors);
        var path = GroupedGateTrxSamples.Write(fixture.Root, "missing.trx",
            GroupedGateTrxSamples.Lifecycle with { Outcome = "Passed" }, GroupedGateTrxSamples.Namespaced);
        Save(fixture, [path]);

        var result = fixture.Resolve();

        AssertPending(fixture, wait, result);
        Assert.Equal(["ParallelExecutionPlannerTests"], Assert.Single(result.UnresolvedWaits).MissingOrFailedTestNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateMismatchLeavesWaitPending(bool cohort)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(GroupedGateTrxSamples.Selectors);
        Save(fixture, GroupedGateTrxSamples.WriteIncidentAndRerun(fixture.Root), cohort,
            candidate: new string('b', 40));

        AssertPending(fixture, wait, fixture.Resolve());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureNeedsIdenticalPassedRowInAnotherFile(bool sameFilePass)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([GroupedGateTrxSamples.Selectors[0]]);
        var rows = sameFilePass
            ? new[] { GroupedGateTrxSamples.Lifecycle, GroupedGateTrxSamples.Lifecycle with { Outcome = "Passed" } }
            : new[] { GroupedGateTrxSamples.Lifecycle };
        Save(fixture, [GroupedGateTrxSamples.Write(fixture.Root, "failed.trx", rows)]);

        AssertPending(fixture, wait, fixture.Resolve());
    }

    [Fact]
    public void SeparatePassingReceiptsCannotPoolClassCoverage()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([GroupedGateTrxSamples.Selectors[1], GroupedGateTrxSamples.Selectors[2]]);
        Save(fixture, [GroupedGateTrxSamples.Write(fixture.Root, "first.trx", GroupedGateTrxSamples.Namespaced)],
            completedAt: DateTimeOffset.UnixEpoch);
        Save(fixture, [GroupedGateTrxSamples.Write(fixture.Root, "second.trx", GroupedGateTrxSamples.MethodOnly)],
            tree: '2', completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));

        var result = fixture.Resolve();

        AssertPending(fixture, wait, result);
        Assert.Equal([GroupedGateTrxSamples.Selectors[1]], Assert.Single(result.UnresolvedWaits).MissingOrFailedTestNames);
    }

    [Fact]
    public void SoloMissingListStaysAuthoritativeWhenGroupedReceiptDoesNotResolve()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([GroupedGateTrxSamples.Selectors[1], GroupedGateTrxSamples.Selectors[2]]);
        fixture.WriteAttempt([(GroupedGateTrxSamples.Selectors[1] + ".Check", "Passed")]);
        Save(fixture, [GroupedGateTrxSamples.Write(fixture.Root, "grouped.trx", GroupedGateTrxSamples.MethodOnly)]);

        var result = fixture.Resolve();

        AssertPending(fixture, wait, result);
        Assert.Equal([GroupedGateTrxSamples.Selectors[2]], Assert.Single(result.UnresolvedWaits).MissingOrFailedTestNames);
    }

    [Fact]
    public void PassedSoloAnswerKeepsItsExistingTextDespiteGroupedEvidence()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait([GroupedGateTrxSamples.Selectors[1]]);
        var (attempt, path, _) = fixture.WriteAttempt([(GroupedGateTrxSamples.Selectors[1] + ".Check", "Passed")]);
        Save(fixture, [GroupedGateTrxSamples.Write(fixture.Root, "grouped.trx", GroupedGateTrxSamples.Namespaced)]);

        var result = fixture.Resolve();

        Assert.Equal([wait.Id], result.ResolvedRequestIds);
        Assert.Equal($"Satisfied by gate attempt {attempt}; TRX {path}; 1 named tests passed.", wait.Answer);
    }

    [Fact]
    public void ReaderOrdersStoresGloballyAndBindsCandidateIgnoringCase()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var paths = GroupedGateTrxSamples.WriteIncidentAndRerun(fixture.Root);
        var train = Save(fixture, paths);
        var cohort = Save(fixture, paths, cohort: true);
        var newer = Save(fixture, paths, cohort: true, tree: '2', completedAt: DateTimeOffset.UnixEpoch.AddMinutes(1));

        var receipts = GroupedGateReceiptTrxEvidence.ReadPassedReceipts(
            fixture.OrchestratorDirectory, fixture.Goal.Id, fixture.CandidateSha.ToUpperInvariant());

        Assert.Equal([newer.ReceiptId, train.ReceiptId, cohort.ReceiptId], receipts.Select(receipt => receipt.ReceiptId));
    }

    [Fact]
    public void AbsentStoresStayAbsentAndCorruptStoreFailsClosed()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var wait = fixture.CreateWait(GroupedGateTrxSamples.Selectors);

        AssertPending(fixture, wait, fixture.Resolve());
        Assert.False(Directory.Exists(fixture.OrchestratorDirectory));
        Directory.CreateDirectory(fixture.OrchestratorDirectory);
        File.WriteAllText(Path.Combine(fixture.OrchestratorDirectory, "merge-train-acceptance.db"), "invalid sqlite");
        AssertPending(fixture, wait, fixture.Resolve());
        Assert.False(File.Exists(Path.Combine(fixture.OrchestratorDirectory, "cohort-acceptance.db")));
    }

    private static void AssertPending(ProspectiveEvidenceGateFixture fixture, HumanInputRequest wait,
        ProspectiveEvidenceGateResolution result)
    {
        Assert.False(wait.IsCompleted);
        Assert.Null(wait.Answer);
        Assert.Empty(result.ResolvedRequestIds);
        Assert.Equal(wait.Id, Assert.Single(result.UnresolvedWaits).RequestId);
        Assert.Null(Assert.Single(result.UnresolvedWaits).SubmissionFailure);
        Assert.Equal(wait.Id, Assert.Single(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id)).Id);
    }

    private static GroupedGateReceiptTrxEvidence.Receipt Save(ProspectiveEvidenceGateFixture fixture,
        IReadOnlyList<string> paths, bool cohort = false, bool passed = true, string? candidate = null,
        char tree = '1', DateTimeOffset? completedAt = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var completed = completedAt ?? DateTimeOffset.UnixEpoch;
        var revision = candidate ?? fixture.CandidateSha;
        var otherGoal = GoalId.New();
        var sortedPaths = paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (cohort)
        {
            var identity = AcceptanceCohortIdentity.Create(
                [CohortMember(fixture.Goal.Id, revision), CohortMember(otherGoal, new string('c', 40))],
                new string('f', 40), new string(tree, 40), "manifest-v1");
            var store = new CohortAcceptanceStore(Path.Combine(fixture.OrchestratorDirectory, "cohort-acceptance.db"));
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt(id, identity,
                passed ? AcceptanceCohortGateOutcome.Passed : AcceptanceCohortGateOutcome.Failed,
                completed, 1, [], passed ? 0 : 1, sortedPaths, ValidForLanding: passed));
            Assert.Equal(passed, receipt.HasAuthoritativeLandingEvidence);
            return new("cohort", receipt.ReceiptId, receipt.CompletedAt, receipt.GateTestResultPaths);
        }
        else
        {
            var identity = MergeTrainIdentity.Create(
                [TrainMember(fixture.Goal.Id, revision), TrainMember(otherGoal, new string('c', 40))],
                new string('f', 40), new string(tree, 40), "manifest-v1");
            var store = new MergeTrainAcceptanceStore(Path.Combine(fixture.OrchestratorDirectory, "merge-train-acceptance.db"));
            var receipt = store.SaveGateReceipt(new MergeTrainReceipt(id, identity,
                passed ? MergeTrainGateOutcome.Passed : MergeTrainGateOutcome.Failed,
                completed, 1, [], passed ? 0 : 1, sortedPaths, ValidForLanding: passed));
            Assert.Equal(passed, receipt.HasAuthoritativeLandingEvidence);
            return new("merge-train", receipt.ReceiptId, receipt.CompletedAt, receipt.GateTestResultPaths);
        }
    }

    private static AcceptanceCohortMemberBinding CohortMember(GoalId goal, string candidate) =>
        new(goal, candidate, candidate, ["src/Example.cs"], ["resource:" + goal.Value],
            ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");

    private static MergeTrainMemberBinding TrainMember(GoalId goal, string candidate) =>
        new(goal, candidate, candidate, ["src/Example.cs"], [], ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected", candidate);
}
