using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class CohortAcceptanceStoreTestsAttributionSkip : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void NoIdentitySkip_PersistsInteractionOnlyAndSuppressionWithoutPartitionRows()
    {
        WithReceipt((store, receipt, databasePath) =>
        {
            var saved = store.SaveAttribution(receipt.Identity.Value, AcceptanceCohortAttributionOutcome.InteractionOnly,
                [], "skipped-pair", null, [], [], AcceptanceCohortAttributionSources.NoFailingTestIdentities,
                suppressPair: true, cohortFailingTests: []);
            var reloaded = new CohortAcceptanceStore(databasePath).TryReadReceipt(receipt.Identity.Value)!;
            Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, saved.Attribution);
            Assert.Equal(saved.Attribution, reloaded.Attribution);
            Assert.Equal(AcceptanceCohortAttributionSources.NoFailingTestIdentities, reloaded.AttributionSource);
            Assert.Empty(reloaded.AttributedMembers);
            Assert.Empty(store.ReadPartitionReceipts(receipt.Identity.Value));
            Assert.Contains("skipped-pair", new CohortAcceptanceStore(databasePath).ReadSuppressedPairs());
            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            connection.Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM cohort_partition_receipts WHERE cohort_id=$cohort";
            count.Parameters.AddWithValue("$cohort", receipt.Identity.Value);
            Assert.Equal(0L, count.ExecuteScalar());
        });
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("focused-pass", 0)]
    [InlineData("other-source", 0)]
    [InlineData("no-failing-test-identities", 1)]
    [InlineData("no-failing-test-identities", 3)]
    public void InvalidPartitionCounts_StillThrow(string? source, int count)
    {
        WithReceipt((store, receipt, _) =>
        {
            var member = receipt.Identity.Members[0];
            var partitions = Enumerable.Range(0, count).Select(index => new AcceptanceCohortPartitionReceipt(
                $"partition-{index}", member.GoalId, index, member.CandidateRevision, receipt.Identity.ObservedMainRevision,
                "tree", receipt.Identity.ManifestIdentity, AcceptanceCohortGateOutcome.Passed, 0, [])).ToArray();
            Assert.Throws<ArgumentException>(() => store.SaveAttribution(receipt.Identity.Value,
                AcceptanceCohortAttributionOutcome.InteractionOnly, partitions, "pair", null, attributionSource: source));
            Assert.Empty(store.ReadPartitionReceipts(receipt.Identity.Value));
            Assert.Empty(store.ReadSuppressedPairs());
        });
    }

    private static void WithReceipt(Action<CohortAcceptanceStore, AcceptanceCohortReceipt, string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "cohort.db");
            var store = new CohortAcceptanceStore(path);
            var identity = AcceptanceCohortIdentity.Create(
            [
                Bind(GoalId.New(), new string('b', 40), "src/A.cs", "resource:a"),
                Bind(GoalId.New(), new string('c', 40), "tests/B.cs", "resource:b")
            ], new string('a', 40), new string('d', 40), "manifest-v1");
            var receipt = store.SaveGateReceipt(new AcceptanceCohortReceipt("skip-receipt", identity,
                AcceptanceCohortGateOutcome.Failed, DateTimeOffset.UtcNow, 0,
                [AttributionLaneFixture.Process, AttributionLaneFixture.Remainder], 1, [], ValidForLanding: false));
            action(store, receipt, path);
        }
        finally { DeleteDirectory(root); }
    }
}
