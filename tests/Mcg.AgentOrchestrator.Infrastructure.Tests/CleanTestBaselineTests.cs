using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CleanTestBaselineTests
{
    [Xunit.Fact]
    public void ResolveGreenMainAttributesCandidateFailureAsIntroduced()
    {
        var current = GoalId.New();
        var prior = GoalId.New();
        var journals = Journals(
            (prior, Entry(prior, "main-a", "passed")));

        var receipt = CleanTestBaseline.Resolve(journals, current, " MAIN-A ", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt, ["core tests"], journals, current, "main-a"));

        Assert.Equal(CleanBaselineAttestation.AttestedGreen, receipt.Attestation);
        Assert.Equal(AcceptanceFailureOrigin.Introduced, attribution.Origin);
    }

    [Xunit.Fact]
    public void ResolveTwoGoalsSharingFailureAttributesAsInherited()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        var journals = Journals(
            (first, Entry(first, "main-a", "failed", ["infrastructure tests"])),
            (second, Entry(second, "MAIN-A", "failed", ["infrastructure tests", "other"])));

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt, ["infrastructure tests"], journals, current, "main-a"));

        Assert.Equal(CleanBaselineAttestation.AttestedRed, receipt.Attestation);
        Assert.Equal(["infrastructure tests"], receipt.SharedFailingChecks);
        Assert.Equal(AcceptanceFailureOrigin.Inherited, attribution.Origin);
        Assert.True(
            attribution.Evidence.Contains(first.Value[..8], StringComparison.OrdinalIgnoreCase) ||
            attribution.Evidence.Contains(second.Value[..8], StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact]
    public void ResolveSharedFailureTakesPrecedenceOverPassingCandidateEvidence()
    {
        var current = GoalId.New();
        var passing = GoalId.New();
        var firstFailure = GoalId.New();
        var secondFailure = GoalId.New();
        var journals = Journals(
            (passing, Entry(passing, "main-a", "passed")),
            (firstFailure, Entry(firstFailure, "main-a", "failed", ["core tests"])),
            (secondFailure, Entry(secondFailure, "main-a", "failed", ["core tests"])));

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);
        var attribution = Assert.Single(CleanTestBaseline.Attribute(
            receipt, ["core tests"], journals, current, "main-a"));

        Assert.Equal(CleanBaselineAttestation.AttestedRed, receipt.Attestation);
        Assert.Equal(["core tests"], receipt.SharedFailingChecks);
        Assert.Equal(AcceptanceFailureOrigin.Inherited, attribution.Origin);
    }

    [Xunit.Fact]
    public void ResolveSingleGoalOrDifferentMainRemainsUnattested()
    {
        var current = GoalId.New();
        var first = GoalId.New();
        var second = GoalId.New();
        var journals = Journals(
            (first, Entry(first, "main-a", "failed", ["core tests"])),
            (second, Entry(second, "main-b", "failed", ["core tests"])));

        var receipt = CleanTestBaseline.Resolve(journals, current, "main-a", null);

        Assert.Equal(CleanBaselineAttestation.Unattested, receipt.Attestation);
        Assert.Equal(
            AcceptanceFailureOrigin.Unattributed,
            Assert.Single(CleanTestBaseline.Attribute(receipt, ["core tests"], journals, current, "main-a")).Origin);
    }

    [Xunit.Fact]
    public void AcceptanceFailureJournalPersistsFailedChecksAndIgnoresMalformedLines()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-clean-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var goal = TestGoal("Baseline journal");
            GoalOperationJournal.AcceptanceFailed(
                root,
                goal,
                "conductor:acceptance",
                "branch-a",
                "main-a",
                failedCheckNames: ["core tests"]);
            GoalOperationJournal.Begin(
                root,
                goal,
                "conductor:clean-baseline",
                "resolving",
                "main-a");
            GoalOperationJournal.Completed(
                root,
                goal,
                "conductor:clean-baseline",
                "attestation=unattested",
                "main-a");
            var path = Path.Combine(root, ".orchestrator", "goal-operations", $"{goal.Id.Value}.jsonl");
            SharedJsonlFile.AppendLine(path, "{malformed");

            var journal = GoalOperationJournal.ReadAll(root)[goal.Id];

            var acceptance = Assert.Single(journal.Entries.Where(entry => entry.AcceptanceOutcome == "failed"));
            Assert.Equal(["core tests"], acceptance.FailedCheckNames);
            var baseline = Assert.Single(journal.LatestByOperation.Where(entry =>
                entry.Operation == "conductor:clean-baseline"));
            Assert.Equal("main-a", baseline.MainHeadSha);
            Assert.Equal(GoalOperationStatus.Completed, baseline.Status);

            var evidence = Assert.Single(GoalOperationJournal.ReadAcceptanceEvidenceForMain(root, " MAIN-A "));
            Assert.Equal(goal.Id, evidence.GoalId);
            Assert.Equal("failed", evidence.AcceptanceOutcome);
            Assert.Equal(["core tests"], evidence.FailedCheckNames);
            Assert.Empty(GoalOperationJournal.ReadAcceptanceEvidenceForMain(root, "main-b"));
            Assert.Empty(GoalOperationJournal.ReadAcceptanceEvidenceForMain(root, "   "));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ReconcileAttentionResolvesRedBaselineItemAfterMainMoves()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-clean-baseline-attention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = CollaborationItemStore.ForDirectory(Path.Combine(root, ".orchestrator"));
            var goal = TestGoal("Baseline attention");
            var red = new CleanTestBaselineReceipt(
                "main-a",
                null,
                CleanBaselineAttestation.AttestedRed,
                goal.Id.Value,
                DateTimeOffset.UtcNow,
                ["core tests"],
                "shared failure");
            var green = new CleanTestBaselineReceipt(
                "main-b",
                null,
                CleanBaselineAttestation.AttestedGreen,
                goal.Id.Value,
                DateTimeOffset.UtcNow,
                [],
                "passing receipt");

            ConductorDriver.ReconcileCleanBaselineAttention(store, goal, "main-a", red);
            ConductorDriver.ReconcileCleanBaselineAttention(store, goal, "main-b", green);

            var item = Assert.Single(await store.ListAsync());
            Assert.Equal("clean-baseline-red:main-a", item.CorrelationKey);
            Assert.Equal(CollaborationItemStatus.Resolved, item.Status);
            Assert.Contains("main-b", item.Resolution, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IReadOnlyDictionary<GoalId, GoalOperationJournalSummary> Journals(
        params (GoalId GoalId, GoalOperationJournalEntry Entry)[] values) =>
        values
            .GroupBy(value => value.GoalId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var entries = group.Select(value => value.Entry).ToArray();
                    return new GoalOperationJournalSummary("journal", entries, entries, []);
                });

    private static Goal TestGoal(string objective) =>
        new(
            GoalId.New(),
            objective,
            [new TaskSpec(TaskId.New(), "Exercise baseline behavior", AgentRole.Developer)]);

    private static GoalOperationJournalEntry Entry(
        GoalId goalId,
        string mainSha,
        string outcome,
        IReadOnlyList<string>? failedChecks = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            goalId,
            "conductor:acceptance",
            outcome == "passed" ? GoalOperationStatus.Completed : GoalOperationStatus.Failed,
            DateTimeOffset.UtcNow,
            "receipt",
            MainHeadSha: mainSha,
            AcceptanceOutcome: outcome,
            FailedCheckNames: failedChecks);
}
