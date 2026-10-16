using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory receipts and caller-owned temporary stores only.
public sealed class MergeTrainDropBacktestTests
{
    [Fact]
    public void Run_LargerCulprit_ConfirmsHitAndCostImprovement()
    {
        Assert.Equal(new[]
        {
            "receipts: total=25 training=4 evaluation=21",
            "decision points: training=1 evaluation=10; evaluation excluded: pairs=0 recent=0 no-culprit=0 ambiguous=0",
            "chosen feature: diff-size training hits=1/1",
            "drop-newest: hits=3/10 gate-minutes per landing=62.7",
            "risk-ordered: hits=10/10 gate-minutes per landing=16.0",
            "feature prior-red-count: evaluation hits=3/10",
            "feature revision-count: evaluation hits=3/10",
            "feature recent-landing-overlap: evaluation hits=3/10",
            "feature diff-size: evaluation hits=10/10",
            "feature change-risk-tier: evaluation hits=3/10",
            "bar: risk hits >= 1.5x newest hits and gate-minutes per landing <= 0.9x newest, with at least 10 evaluation decision points",
            "verdict: confirmed"
        }, MergeTrainDropBacktest.Run(Fixture.Confirmed(), 4));
    }

    [Fact]
    public void Run_LargerInnocent_RefutesRiskPolicy()
    {
        var report = MergeTrainDropBacktest.Run(Fixture.Confirmed(refuted: true), 4);
        Assert.Contains("drop-newest: hits=10/10 gate-minutes per landing=16.0", report);
        Assert.Contains("risk-ordered: hits=0/10 gate-minutes per landing=none", report);
        Assert.Equal("verdict: refuted", report[^1]);
    }

    [Fact]
    public void Run_NineEvaluationDecisions_IsInconclusive()
    {
        var report = MergeTrainDropBacktest.Run(Fixture.Confirmed(pairs: 9), 4);
        Assert.Contains("risk-ordered: hits=9/9 gate-minutes per landing=16.0", report);
        Assert.Equal("verdict: inconclusive", report[^1]);
    }

    [Fact]
    public void Run_Exclusions_CountsPairsExoneratedAndAmbiguous()
    {
        var receipts = Fixture.Confirmed();
        var trailing = receipts[^1];
        receipts.RemoveAt(receipts.Count - 1);
        var allPass = new[] { Fixture.Member(200), Fixture.Member(201), Fixture.Member(202) };
        var unclear = new[] { Fixture.Member(203), Fixture.Member(204), Fixture.Member(205) };
        Fixture.Add(receipts, MergeTrainGateOutcome.Failed, 20, allPass);
        Fixture.Add(receipts, MergeTrainGateOutcome.Passed, 12, allPass);
        Fixture.Add(receipts, MergeTrainGateOutcome.Failed, 20, unclear);
        Fixture.Add(receipts, MergeTrainGateOutcome.Passed, 12, [unclear[2], Fixture.Member(206)]);
        Fixture.Add(receipts, MergeTrainGateOutcome.Failed, 20, [Fixture.Member(207), Fixture.Member(208)]);
        receipts.Add(trailing);
        var report = MergeTrainDropBacktest.Run(receipts, 4);
        Assert.Equal("decision points: training=1 evaluation=10; evaluation excluded: pairs=1 recent=0 no-culprit=1 ambiguous=1", report[1]);
        Assert.Equal("verdict: confirmed", report[^1]);
    }

    [Fact]
    public void Feature_TimeCut_ExcludesFutureAndSameTimeReds()
    {
        var receipts = Fixture.Confirmed();
        var member = receipts[4].Identity.Members[0];
        var earlier = receipts[4] with { CompletedAt = receipts[4].CompletedAt.AddHours(-1) };
        receipts.Add(earlier);
        receipts.Add(receipts[4] with { CompletedAt = receipts[4].CompletedAt.AddHours(1) });
        receipts.Add(receipts[4]);
        Assert.Equal(1, MergeTrainDropBacktest.Feature("prior-red-count", receipts, 4, member));
        Assert.Throws<ArgumentException>(() => MergeTrainDropBacktest.Feature("unknown", receipts, 4, member));
    }

    [Fact]
    public void Feature_EarlierHistory_UsesRevisionsDistinctOverlapAndRisk()
    {
        var member = Fixture.Member(300, 3) with { LandingPaths = ["src/A.cs", "src/a.cs", "src/B.cs"] };
        var prior = member with { CandidateRevision = new string('b', 40), LandingPaths = ["SRC/A.CS"] };
        var at = Fixture.Start.AddDays(2);
        var receipts = new List<MergeTrainReceipt>();
        Fixture.Add(receipts, MergeTrainGateOutcome.Passed, 10, [prior, Fixture.Member(301)]);
        receipts[0] = receipts[0] with { CompletedAt = at.AddHours(-24) };
        Fixture.Add(receipts, MergeTrainGateOutcome.Failed, 20, [member, Fixture.Member(302), Fixture.Member(303)]);
        receipts[1] = receipts[1] with { CompletedAt = at };
        Assert.Equal(2, MergeTrainDropBacktest.Feature("revision-count", receipts, 1, member));
        Assert.Equal(1, MergeTrainDropBacktest.Feature("recent-landing-overlap", receipts, 1, member));
        Assert.Equal(3, MergeTrainDropBacktest.Feature("diff-size", receipts, 1, member));
        Assert.Equal(1, MergeTrainDropBacktest.Feature("change-risk-tier", receipts, 1, member));
        receipts[0] = receipts[0] with { CompletedAt = at.AddHours(-24).AddTicks(-1) };
        Assert.Equal(0, MergeTrainDropBacktest.Feature("recent-landing-overlap", receipts, 1, member));
    }

    [Fact]
    public void Run_EmptyOrUntrainedHistory_IsInconclusive()
    {
        var empty = MergeTrainDropBacktest.Run([], 100);
        Assert.Contains("chosen feature: prior-red-count training hits=0/0", empty);
        Assert.Equal("verdict: inconclusive", empty[^1]);
        Assert.Equal("verdict: inconclusive", MergeTrainDropBacktest.Run(Fixture.Confirmed(), 100)[^1]);
        var noPasses = Fixture.Confirmed().Where(receipt => receipt.Outcome != MergeTrainGateOutcome.Passed).ToArray();
        Assert.Equal("verdict: inconclusive", MergeTrainDropBacktest.Run(noPasses, 1)[^1]);
    }

    internal static class Fixture
    {
        internal static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        internal static MergeTrainMemberBinding Member(int id, int paths = 1) => new(
            new GoalId(id.ToString("x32")), id.ToString("x40"), id.ToString("x40"),
            Enumerable.Range(0, paths).Select(path => $"src/Goal{id}/File{path}.cs").ToArray(), [],
            ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected", id.ToString("x40"));

        internal static List<MergeTrainReceipt> Confirmed(bool refuted = false, int pairs = 10)
        {
            var receipts = new List<MergeTrainReceipt>();
            Add(receipts, MergeTrainGateOutcome.Passed, 10, [Member(1), Member(2)]);
            var train = new[] { Member(3, 3), Member(4), Member(5) };
            Add(receipts, MergeTrainGateOutcome.Failed, 20, train);
            Add(receipts, MergeTrainGateOutcome.Passed, 12, [train[1], train[2]]);
            Add(receipts, MergeTrainGateOutcome.Passed, 14, [Member(6), Member(7)]);
            for (var i = 0; i < pairs; i++)
            {
                var culprit = Member(10 + i * 3, refuted ? 1 : 3);
                var innocent = Member(11 + i * 3, refuted ? 3 : 1);
                var other = Member(12 + i * 3);
                Add(receipts, MergeTrainGateOutcome.Failed, 20,
                    refuted || i < 3 ? [innocent, other, culprit] : [culprit, innocent, other]);
                Add(receipts, MergeTrainGateOutcome.Passed, 12, [innocent, other]);
            }
            var trailingAt = receipts[^1].CompletedAt.AddDays(2);
            Add(receipts, MergeTrainGateOutcome.Passed, 12, [Member(100), Member(101)]);
            receipts[^1] = receipts[^1] with { CompletedAt = trailingAt };
            return receipts;
        }

        internal static void Add(List<MergeTrainReceipt> receipts, MergeTrainGateOutcome outcome,
            int minutes, IReadOnlyList<MergeTrainMemberBinding> members)
        {
            var number = receipts.Count + 1;
            receipts.Add(new MergeTrainReceipt($"receipt-{number:D3}",
                MergeTrainIdentity.Create(members, new string('a', 40), number.ToString("x40"), "manifest"),
                outcome, Start.AddHours(receipts.Count * 2), minutes * 60000L,
                outcome == MergeTrainGateOutcome.Failed ? ["gate"] : [], outcome == MergeTrainGateOutcome.Passed ? 0 : 1, []));
        }

        internal static MergeTrainReceipt Save(MergeTrainAcceptanceStore store, string root, MergeTrainReceipt receipt)
        {
            if (receipt.Outcome == MergeTrainGateOutcome.Passed)
            {
                var trx = Path.Combine(root, receipt.ReceiptId + ".trx");
                File.WriteAllText(trx, """
                    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                      <Results><UnitTestResult testName="Fixture.Passes" outcome="Passed" /></Results>
                      <ResultSummary><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary>
                    </TestRun>
                    """);
                receipt = receipt with { GateTestResultPaths = [trx] };
            }
            return store.SaveGateReceipt(receipt);
        }
    }
}
