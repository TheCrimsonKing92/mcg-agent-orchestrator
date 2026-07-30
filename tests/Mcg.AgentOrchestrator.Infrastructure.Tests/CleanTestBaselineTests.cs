using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

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
            var goal = new Goal(GoalId.New(), "Baseline journal", []);
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
            File.AppendAllText(path, "{malformed" + Environment.NewLine);

            var journal = GoalOperationJournal.ReadAll(root)[goal.Id];

            var acceptance = Assert.Single(journal.Entries.Where(entry => entry.AcceptanceOutcome == "failed"));
            Assert.Equal(["core tests"], acceptance.FailedCheckNames);
            var baseline = Assert.Single(journal.LatestByOperation.Where(entry =>
                entry.Operation == "conductor:clean-baseline"));
            Assert.Equal("main-a", baseline.MainHeadSha);
            Assert.Equal(GoalOperationStatus.Completed, baseline.Status);
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
