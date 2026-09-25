using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WithinAttemptRerunApparatusClassifierTests
{
    private const string PartitionName = "infrastructure tests: Process spawning";

    [Fact]
    public void CoveredAggregateInheritsPartitionApparatusAndHonorsRegateBound()
    {
        var partition = Partition();
        var aggregate = new AcceptanceCheckResult("infrastructure tests", false, 1, "aggregate failed",
            CoveredBy: [PartitionName]);
        var summary = new AcceptanceVerificationSummary(false, [partition, aggregate]);

        var regate = Assert.IsType<ApparatusRedDisposition.Regate>(
            WithinAttemptRerunApparatusClassifier.Classify(summary, 0, 2));
        Assert.Equal(AcceptanceWithinAttemptRerunEvidence.Reason, regate.EvidenceKind);
        Assert.Equal(1, regate.RegateOrdinal);
        Assert.Equal([PartitionName], regate.TestIdentities);
        Assert.IsType<ApparatusRedDisposition.BoundExhausted>(
            WithinAttemptRerunApparatusClassifier.Classify(summary, 2, 2));
    }

    [Fact]
    public void IncompleteOrMixedEvidenceKeepsExistingClassification()
    {
        var partition = Partition();
        var aggregate = new AcceptanceCheckResult("infrastructure tests", false, 1, "aggregate failed",
            CoveredBy: [PartitionName]);
        AcceptanceCheckResult[][] scenarios =
        [
            [partition with { WithinAttemptRerun = null }],
            [partition with { FailingTestIdentities = ["Failing.Test"] }],
            [partition with { WithinAttemptRerun = partition.WithinAttemptRerun! with { ExecutedTestCount = 0 } }],
            [partition, aggregate with { CoveredBy = [] }],
            [partition, aggregate with { CoveredBy = [PartitionName, "other partition"] }],
            [partition, aggregate with { FailingTestIdentities = ["Failing.Test"] }]
        ];
        foreach (var checks in scenarios)
        {
            Assert.Null(WithinAttemptRerunApparatusClassifier.Classify(
                new AcceptanceVerificationSummary(false, checks), 0, 2));
        }
    }

    internal static AcceptanceCheckResult Partition() =>
        new(PartitionName, false, 0, "first run crashed",
            FailureClassification: AcceptanceFailureClassifications.SharedGateApparatusInvalidated,
            CompletionDecision: new AcceptanceShardCompletionDecision(
                false, AcceptanceShardCompletionPredicates.MissingTrx, false, 0, 351, 0, "missing"))
        {
            WithinAttemptRerun = new AcceptanceWithinAttemptRerunEvidence(
                "Process spawning", AcceptanceShardCompletionPredicates.MissingTrx,
                "attempt:partition:0", "attempt:partition:1", 351, ["rerun.trx"])
        };
}
