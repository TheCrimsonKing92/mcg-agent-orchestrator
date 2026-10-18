using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class AcceptanceCohortWorkflowTestsNoIdentityAttributionSkip : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void NoIdentities_SkipsAllRerunsAndPreservesInteractionOnlyRouting()
    {
        using var scenario = new CohortLaneAttributionScenario(
            [AttributionLaneFixture.Process, AttributionLaneFixture.Remainder, "infrastructure tests"], noIdentities: true);
        var result = scenario.Run();
        var receipt = Assert.IsType<AcceptanceCohortReceipt>(result.Receipt);
        Assert.Equal<GoalId?>([null], scenario.Verifier.GoalIds);
        Assert.Equal(0, scenario.Verifier.FocusedCalls);
        Assert.Equal(0, scenario.LeaseRequests);
        Assert.Empty(scenario.Verifier.MemberChecks);
        Assert.Equal(AcceptanceCohortGateOutcome.Failed, receipt.Outcome);
        Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, receipt.Attribution);
        Assert.Equal(AcceptanceCohortAttributionSources.NoFailingTestIdentities, receipt.AttributionSource);
        Assert.Empty(receipt.AttributedMembers);
        Assert.Empty(scenario.Store.ReadPartitionReceipts(receipt.Identity.Value));
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(scenario.Workspace.OrchestratorDirectory, "cohort-acceptance.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cohort_partition_receipts WHERE cohort_id=$cohort";
            command.Parameters.AddWithValue("$cohort", receipt.Identity.Value);
            Assert.Equal(0L, command.ExecuteScalar());
        }
        Assert.All(result.MemberResults.Values, member => Assert.Contains("deterministic RED; attribution=InteractionOnly",
            Assert.IsType<ConductorAdvanceOutcome.Held>(member.Outcome).Reason));
        Assert.Contains(ConductorAcceptanceCohortSelector.PairFingerprint(scenario.Selection.Members[0], scenario.Selection.Members[1]), scenario.Store.ReadSuppressedPairs());
        Assert.Equal(receipt.Identity.Members.Select(member => $"{member.GoalId.Value}:{member.CandidateRevision}").Order(StringComparer.Ordinal),
            scenario.Store.ReadInteractionOnlyMemberKeys().Order(StringComparer.Ordinal));
        Assert.Equal(2, scenario.VerdictLines.Count);
        foreach (var goal in new[] { scenario.First, scenario.Second })
        {
            var line = Assert.Single(scenario.VerdictLines.Where(line => line.Contains($"goal={goal.Id.Value}", StringComparison.Ordinal)));
            Assert.Contains("action=skipped reason=not-attributed", line);
            Assert.Null(goal.LatestAcceptanceFailure);
        }
        var skipped = Assert.Single(scenario.Events.Where(record => record.EventKind == "cohort-attribution-skipped"));
        Assert.Equal($"ATTRIBUTION_SKIPPED cohort={receipt.Identity.Value[..18]} reason=no-failing-test-identities failed-checks=3", skipped.Detail);
        var events = scenario.Events;
        Assert.True(Array.FindIndex(events, record => record.EventKind == "cohort-attribution") < Array.IndexOf(events, skipped));
        Assert.Equal(receipt.AttributionSource, scenario.Store.TryReadReceipt(receipt.Identity.Value)!.AttributionSource);
    }

    [Fact]
    public void OneIdentity_RunsNonDispositiveFocusedPassThenBothNarrowedPartitions()
    {
        using var scenario = new CohortLaneAttributionScenario([AttributionLaneFixture.Remainder], selectableIdentity: true);
        var receipt = Assert.IsType<AcceptanceCohortReceipt>(scenario.Run().Receipt);
        Assert.Equal<GoalId?>([null, scenario.First.Id, scenario.Second.Id], scenario.Verifier.GoalIds);
        Assert.Equal(2, scenario.Verifier.FocusedCalls);
        Assert.Equal(4, scenario.LeaseRequests);
        Assert.Equal(2, scenario.Verifier.MemberChecks.Count);
        Assert.All(scenario.Verifier.MemberChecks, checks => Assert.Equal(new[] { AttributionLaneFixture.Remainder }, checks));
        var partitions = scenario.Store.ReadPartitionReceipts(receipt.Identity.Value);
        Assert.Equal(2, partitions.Count);
        Assert.All(partitions, partition => Assert.StartsWith("cohort-lane-scoped-partition-v1-", partition.ReceiptId));
        Assert.Null(receipt.AttributionSource);
        Assert.Equal(AcceptanceCohortAttributionOutcome.InteractionOnly, receipt.Attribution);
        Assert.DoesNotContain(scenario.Events, record => record.EventKind == "cohort-attribution-skipped");
    }
}
