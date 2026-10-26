using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: immutable synthetic receipts and timeline events; no I/O.
public sealed class FindingRoundBatchScheduleTests
{
    private const string Candidate = "abc1234";
    private const string Round = "finding-round-fixture";
    private const string Basis = "execution-basis";
    private static readonly TaskId RequestingTask = new("requesting-task");

    [Fact]
    public void CompletedRedReceiptCoversSameAndSubsetSelections()
    {
        var union = Batch("union", "Infrastructure.Tests", "AlphaTests", "BetaTests");
        var subset = Batch("alpha", "Infrastructure.Tests", "FullyQualifiedName~AlphaTests");
        var outside = Batch("outside", "Infrastructure.Tests", "AlphaTests", "GammaTests");
        var otherProject = Batch("other", "Core.Tests", "GammaTests");
        var receipt = RedReceipt(union, subset);

        var plan = Plan([union, subset, outside, otherProject], [receipt]);

        Assert.Empty(plan.Passed);
        Assert.Equal(new[] { union, subset }, plan.RedCovered.Select(pair => pair.Batch));
        Assert.All(plan.RedCovered, pair => Assert.Same(receipt, pair.Receipt));
        Assert.Equal(new[] { outside, otherProject }, plan.Executable);
        Assert.Null(plan.Capped);
    }

    [Fact]
    public void GreenInconclusiveAndUnsettledReceiptsDoNotCover()
    {
        var union = Batch("union", "Infrastructure.Tests", "AlphaTests", "BetaTests");
        var subset = Batch("alpha", "Infrastructure.Tests", "AlphaTests");
        var red = RedReceipt(union, subset);
        var candidate = Assert.Single(red.Arms!);
        var green = red with
        {
            Passed = true,
            Arms = [candidate with { Disposition = FindingEvidenceArmDisposition.Green, Passed = true }]
        };
        FindingEvidenceReceipt[] variants =
        [
            green,
            red with { Accepted = false },
            red with { Arms = [] },
            red with { Arms = [candidate with { Accepted = false }] },
            red with { Arms = [candidate with { Disposition = FindingEvidenceArmDisposition.Inconclusive }] },
            red with { Arms = [candidate, candidate with { Arm = FindingEvidenceArm.Baseline,
                Disposition = FindingEvidenceArmDisposition.ApparatusFailure }] },
            red with { Arms = [candidate, candidate with { Arm = FindingEvidenceArm.Baseline,
                Disposition = FindingEvidenceArmDisposition.Inconclusive }] },
            red with { Arms = [candidate with { ExecutedTestCount = 0 }] },
            red with { Arms = [candidate with { FailingTestIdentities = [] }] },
            red with { Arms = [candidate with { Sha = "other-candidate" }] },
            red with { CandidateSha = "other-candidate" },
            red with { FindingRoundFingerprint = "other-round" },
            red with { ExecutionBasisIdentity = "other-basis" },
            red with { RequestDispositions = null },
            red with { RequestDispositions = red.RequestDispositions!.Where(item => item.FindingStableId != "alpha").ToArray() },
            red with { RequestDispositions = red.RequestDispositions!.Select(item => item with
                { Decision = FindingEvidenceRequestDecision.PendingExecution }).ToArray() },
            red with { Arms = [candidate with { NotRunSelections = [new("Infrastructure.Tests", "AlphaTests")] }] }
        ];
        foreach (var receipt in variants)
        {
            var plan = Plan([subset], [receipt]);
            Assert.Empty(plan.RedCovered);
            Assert.Empty(plan.Passed);
            Assert.Equal(new[] { subset }, plan.Executable);
        }
        var passed = Plan([subset], [green with { ReceiptId = ReceiptId(subset) }]);
        Assert.Equal(new[] { subset }, passed.Passed);
        Assert.Empty(passed.RedCovered);
        Assert.Empty(passed.Executable);
    }

    [Fact]
    public void FourthRunOfSameRequestIsCapped()
    {
        var batch = Batch("alpha", "Infrastructure.Tests", "AlphaTests");
        var second = Batch("second", "Core.Tests", "BetaTests");
        var receiptId = ReceiptId(batch);
        var message = $"receipt_id={receiptId}; finding_id=alpha;";
        var first = Event(message);
        ProgressEvent[] twoRuns =
        [
            first, Event(message),
            first with { TaskId = new TaskId("other-task") },
            Event("receipt_id=other-receipt; finding_id=alpha;"),
            Event($"receipt_id={receiptId}; finding_id=other-finding;"),
            Event(message) with { Kind = ProgressKind.FindingEvidenceRequestRecorded },
            Event($"receipt_id={receiptId}-suffix; finding_id=alpha;"),
            Event($"receipt_id={receiptId}; finding_id=alpha-suffix;")
        ];

        var before = Plan([batch, second], [], twoRuns);
        Assert.Equal(new[] { batch, second }, before.Executable);
        Assert.Null(before.Capped);
        var capped = Plan([batch, second], [], [.. twoRuns, Event(message)]).Capped;
        Assert.NotNull(capped);
        Assert.Same(batch, capped.Value.Batch);
        Assert.Equal(3, capped.Value.Count);
        Assert.Equal(receiptId, capped.Value.ReceiptId);

        // The first capped batch blocks dispatch even if an earlier batch has no runs.
        var laterCapped = Plan([second, batch], [], [.. twoRuns, Event(message)]).Capped;
        Assert.Same(batch, laterCapped!.Value.Batch);
    }

    [Fact]
    public void OpaqueCoverageRequiresOwnReceiptAndFirstQualifyingReceiptWins()
    {
        var opaque = Batch("opaque", "Infrastructure.Tests", "FullyQualifiedName~AlphaTests&Category!=Slow");
        var receipt = RedReceipt(opaque);
        Assert.Same(receipt, Assert.Single(Plan([opaque], [receipt]).RedCovered).Receipt);
        Assert.Equal(new[] { opaque }, Plan([opaque], [receipt with { ReceiptId = "another-receipt" }]).Executable);

        var alpha = Batch("alpha", "Infrastructure.Tests", "AlphaTests");
        var first = RedReceipt(alpha) with { ReceiptId = "first" };
        var second = first with { ReceiptId = "second" };
        Assert.Same(first, Assert.Single(Plan([alpha], [first, second]).RedCovered).Receipt);
        var unavailable = FindingRoundBatchSchedule.Plan([alpha], RequestingTask, [first], "unavailable", Round, Basis, []);
        Assert.Equal(new[] { alpha }, unavailable.Executable);
        Assert.Null(unavailable.Capped);
    }

    private static FindingRoundBatchPlan Plan(IReadOnlyList<FindingEvidenceBatch> batches,
        IReadOnlyList<FindingEvidenceReceipt> receipts, IReadOnlyList<ProgressEvent>? timeline = null) =>
        FindingRoundBatchSchedule.Plan(batches, RequestingTask, receipts, Candidate, Round, Basis, timeline ?? []);

    private static FindingEvidenceBatch Batch(string findingId, string project, params string[] classes)
    {
        var request = new FindingEvidenceRequest(classes.Select(name => new FindingEvidenceSelection(project, name)).ToArray());
        var identity = ConductorDriver.BuildFindingEvidenceIdentity(request);
        var group = new FindingEvidenceRequestGroup(identity, identity, request,
            [new(findingId, ReviewFindingState.Open, new("tests/fixture.cs", findingId), "Evidence needed.")]);
        return new(identity, identity, request, group.Findings, [group]);
    }

    private static FindingEvidenceReceipt RedReceipt(FindingEvidenceBatch batch, params FindingEvidenceBatch[] covered)
    {
        var dispositions = covered.Prepend(batch).SelectMany(item => item.Findings.Select(finding =>
            new FindingEvidenceRequestDisposition(finding.StableId, item.Identity, "executed-batched",
                Decision: FindingEvidenceRequestDecision.Executed, CandidateSha: Candidate,
                FindingRoundFingerprint: Round, ExecutionBasisIdentity: Basis))).ToArray();
        return new(ReceiptId(batch), Candidate, batch.TypedRequest, true, false, "Candidate RED",
            [new(FindingEvidenceArm.Candidate, Candidate, FindingEvidenceArmDisposition.Red, true, false, "RED",
                FailingTestIdentities: ["Fixture.AlphaTests.Fails"], ExecutedTestCount: 2)],
            dispositions, Round, Basis);
    }

    private static string ReceiptId(FindingEvidenceBatch batch) =>
        ConductorDriver.CreateFindingEvidenceReceiptId(Candidate, Round, batch.Identity);

    private static ProgressEvent Event(string message) =>
        new(new GoalId("goal"), RequestingTask, ProgressKind.FindingEvidenceRunRecorded, message, DateTimeOffset.UnixEpoch);
}
