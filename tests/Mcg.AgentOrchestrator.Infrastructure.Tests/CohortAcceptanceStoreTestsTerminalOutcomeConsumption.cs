using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CohortAcceptanceStoreTestsTerminalOutcomeConsumption : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttributedFailedReceipts_AreOrderedAndConsumedOnceAcrossReconstruction(bool sameCompletionTime)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cohort-consumption-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "cohort.db");
            var store = new CohortAcceptanceStore(databasePath);
            var completed = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
            var interaction = Save(store, "z-interaction", AcceptanceCohortGateOutcome.Failed, completed);
            interaction = Attribute(store, interaction, AcceptanceCohortAttributionOutcome.InteractionOnly);
            var blamed = Save(store, "a-blamed", AcceptanceCohortGateOutcome.Failed,
                sameCompletionTime ? completed : completed.AddMinutes(1));
            blamed = Attribute(store, blamed, AcceptanceCohortAttributionOutcome.FirstMemberFailed);
            _ = Save(store, "unattributed", AcceptanceCohortGateOutcome.Failed, completed.AddMinutes(2));
            var greenTrx = WritePassingTrx(root, "green.trx");
            _ = Save(store, "passed", AcceptanceCohortGateOutcome.Passed, completed.AddMinutes(3), [greenTrx]);
            _ = Save(store, "infrastructure", AcceptanceCohortGateOutcome.InfrastructureFailure, completed.AddMinutes(4));

            Assert.Equal(sameCompletionTime ? new[] { blamed.ReceiptId, interaction.ReceiptId }
                    : new[] { interaction.ReceiptId, blamed.ReceiptId },
                store.ReadUnconsumedTerminalFailedReceipts().Select(receipt => receipt.ReceiptId));
            var keys = store.ReadInteractionOnlyMemberKeys();
            Assert.Equal(interaction.Identity.Members.Select(Key).Order(StringComparer.Ordinal), keys.Order(StringComparer.Ordinal));
            Assert.All(blamed.Identity.Members, member => Assert.DoesNotContain(Key(member), keys));
            Assert.All(store.ReadPartitionReceipts(interaction.Identity.Value),
                partition => Assert.Equal(AcceptanceCohortGateOutcome.Passed, partition.Outcome));
            Assert.True(store.TryRecordOutcomeConsumption(interaction.Identity.Value, interaction.ReceiptId));
            Assert.False(new CohortAcceptanceStore(databasePath)
                .TryRecordOutcomeConsumption(interaction.Identity.Value, interaction.ReceiptId));
            Assert.Equal(blamed.ReceiptId, Assert.Single(store.ReadUnconsumedTerminalFailedReceipts()).ReceiptId);
            Assert.True(store.TryRecordOutcomeConsumption(blamed.Identity.Value, blamed.ReceiptId));
            var rebuilt = new CohortAcceptanceStore(databasePath);
            Assert.Empty(rebuilt.ReadUnconsumedTerminalFailedReceipts());
            Assert.Equal(keys.Order(StringComparer.Ordinal), rebuilt.ReadInteractionOnlyMemberKeys().Order(StringComparer.Ordinal));
        }
        finally { DeleteDirectory(root); }
    }

    private static string Key(AcceptanceCohortMemberBinding member) => $"{member.GoalId.Value}:{member.CandidateRevision}";

    private static AcceptanceCohortReceipt Save(CohortAcceptanceStore store, string receiptId,
        AcceptanceCohortGateOutcome outcome, DateTimeOffset completed, IReadOnlyList<string>? paths = null)
    {
        var identity = AcceptanceCohortIdentity.Create(
        [
            Bind(GoalId.New(), new string('b', 40), "src/A.cs", "resource:a"),
            Bind(GoalId.New(), new string('c', 40), "tests/B.cs", "resource:b")
        ], new string('a', 40), new string('d', 40), "manifest-v1");
        return store.SaveGateReceipt(new AcceptanceCohortReceipt(receiptId, identity, outcome, completed,
            0, outcome == AcceptanceCohortGateOutcome.Failed ? ["red"] : [],
            outcome == AcceptanceCohortGateOutcome.Passed ? 0 : 2, paths ?? [],
            ValidForLanding: outcome == AcceptanceCohortGateOutcome.Passed,
            InfrastructureReasonCode: outcome == AcceptanceCohortGateOutcome.InfrastructureFailure ? "test-infrastructure" : null));
    }

    private static AcceptanceCohortReceipt Attribute(CohortAcceptanceStore store,
        AcceptanceCohortReceipt receipt, AcceptanceCohortAttributionOutcome attribution)
    {
        var partitions = receipt.Identity.Members.Select((member, ordinal) => new AcceptanceCohortPartitionReceipt(
            $"{receipt.ReceiptId}-{ordinal}", member.GoalId, ordinal, member.CandidateRevision,
            receipt.Identity.ObservedMainRevision, new string('e', 40), receipt.Identity.ManifestIdentity,
            attribution == AcceptanceCohortAttributionOutcome.FirstMemberFailed && ordinal == 0
                ? AcceptanceCohortGateOutcome.Failed : AcceptanceCohortGateOutcome.Passed, 0, [])).ToArray();
        return store.SaveAttribution(receipt.Identity.Value, attribution, partitions,
            $"pair-{receipt.ReceiptId}", innocentGoalId: null);
    }
}
