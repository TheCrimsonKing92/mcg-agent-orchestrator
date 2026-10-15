using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

// Offline policies learn from earlier receipts; only retrospective labels may use later passes.
internal static class MergeTrainDropBacktest
{
    private static readonly string[] Features =
        ["prior-red-count", "revision-count", "recent-landing-overlap", "diff-size", "change-risk-tier"];

    internal static int Feature(string name, IReadOnlyList<MergeTrainReceipt> receipts,
        int decisionIndex, MergeTrainMemberBinding member)
    {
        var at = receipts[decisionIndex].CompletedAt;
        var earlier = receipts.Where(receipt => receipt.CompletedAt < at);
        return name switch
        {
            "prior-red-count" => earlier.Count(receipt => receipt.Outcome == MergeTrainGateOutcome.Failed &&
                receipt.Identity.Members.Any(prior => prior.GoalId == member.GoalId)),
            "revision-count" => earlier.SelectMany(receipt => receipt.Identity.Members)
                .Where(prior => prior.GoalId == member.GoalId).Select(prior => prior.CandidateRevision)
                .Append(member.CandidateRevision).Distinct(StringComparer.Ordinal).Count(),
            "recent-landing-overlap" => member.LandingPaths.Distinct(StringComparer.OrdinalIgnoreCase)
                .Intersect(earlier.Where(receipt => receipt.Outcome == MergeTrainGateOutcome.Passed &&
                        receipt.CompletedAt >= at.AddHours(-24))
                    .SelectMany(receipt => receipt.Identity.Members).SelectMany(prior => prior.LandingPaths),
                    StringComparer.OrdinalIgnoreCase).Count(),
            "diff-size" => member.LandingPaths.Count,
            "change-risk-tier" => (int)member.ChangeRiskTier,
            _ => throw new ArgumentException($"Unknown feature: {name}", nameof(name))
        };
    }

    internal static IReadOnlyList<string> Run(IReadOnlyList<MergeTrainReceipt> receipts, int trainingCount)
    {
        if (trainingCount <= 0) throw new ArgumentOutOfRangeException(nameof(trainingCount));
        var split = Math.Min(trainingCount, receipts.Count);
        var horizon = receipts.Count == 0 ? DateTimeOffset.MinValue : receipts[^1].CompletedAt.AddHours(-24);
        var training = new List<(int Index, MergeTrainMemberBinding Culprit)>();
        var evaluation = new List<(int Index, MergeTrainMemberBinding Culprit)>();
        var pairs = 0;
        var recent = 0;
        var noCulprit = 0;
        var ambiguous = 0;
        for (var i = 0; i < receipts.Count; i++)
        {
            var receipt = receipts[i];
            if (receipt.Outcome != MergeTrainGateOutcome.Failed) continue;
            var isEvaluation = i >= split;
            if (receipt.Identity.Members.Count < 3) { if (isEvaluation) pairs++; continue; }
            if (receipt.CompletedAt > horizon) { if (isEvaluation) recent++; continue; }
            var suspects = receipt.Identity.Members.Where(member => !receipts.Any(later =>
                later.CompletedAt > receipt.CompletedAt && later.Outcome == MergeTrainGateOutcome.Passed &&
                later.Identity.Members.Any(passed => passed.GoalId == member.GoalId &&
                    passed.CandidateRevision == member.CandidateRevision))).ToArray();
            if (suspects.Length == 0) { if (isEvaluation) noCulprit++; continue; }
            if (suspects.Length > 1) { if (isEvaluation) ambiguous++; continue; }
            (isEvaluation ? evaluation : training).Add((i, suspects[0]));
        }

        MergeTrainMemberBinding Pick(int index, string feature) => receipts[index].Identity.Members
            .Select((member, order) => (Member: member, Order: order, Value: Feature(feature, receipts, index, member)))
            .OrderByDescending(item => item.Value).ThenByDescending(item => item.Order).First().Member;
        var featureHits = Features.Select(feature => (Name: feature,
            Hits: training.Count(point => Pick(point.Index, feature) == point.Culprit))).ToArray();
        var chosen = featureHits.OrderByDescending(feature => feature.Hits).First();
        var passedMinutes = receipts.Take(split).Where(receipt => receipt.Outcome == MergeTrainGateOutcome.Passed)
            .Select(receipt => receipt.GateElapsedMilliseconds / 60000.0).Order().ToArray();
        var median = passedMinutes.Length == 0 ? 0 :
            (passedMinutes[(passedMinutes.Length - 1) / 2] + passedMinutes[passedMinutes.Length / 2]) / 2;

        (int Hits, int Landings, double Minutes) Cost(Func<int, MergeTrainMemberBinding> pick)
        {
            var hits = 0;
            var landings = 0;
            var minutes = 0.0;
            foreach (var point in evaluation)
            {
                var receipt = receipts[point.Index];
                var redMinutes = receipt.GateElapsedMilliseconds / 60000.0;
                if (pick(point.Index) == point.Culprit)
                {
                    hits++;
                    landings += receipt.Identity.Members.Count - 1;
                    minutes += redMinutes + median;
                }
                else minutes += 2 * redMinutes;
            }
            return (hits, landings, minutes);
        }
        var newest = Cost(index => receipts[index].Identity.Members[^1]);
        var risk = Cost(index => Pick(index, chosen.Name));
        string PerLanding((int Hits, int Landings, double Minutes) cost) => cost.Landings == 0 ? "none" :
            (cost.Minutes / cost.Landings).ToString("F1", CultureInfo.InvariantCulture);
        var verdict = evaluation.Count < 10 || passedMinutes.Length == 0 ? "inconclusive" :
            risk.Hits * 2 >= newest.Hits * 3 && risk.Hits > newest.Hits && risk.Landings > 0 &&
            (newest.Landings == 0 || risk.Minutes / risk.Landings <= 0.9 * newest.Minutes / newest.Landings)
                ? "confirmed" : "refuted";
        var lines = new List<string>
        {
            $"receipts: total={receipts.Count} training={split} evaluation={receipts.Count - split}",
            $"decision points: training={training.Count} evaluation={evaluation.Count}; evaluation excluded: pairs={pairs} recent={recent} no-culprit={noCulprit} ambiguous={ambiguous}",
            $"chosen feature: {chosen.Name} training hits={chosen.Hits}/{training.Count}",
            $"drop-newest: hits={newest.Hits}/{evaluation.Count} gate-minutes per landing={PerLanding(newest)}",
            $"risk-ordered: hits={risk.Hits}/{evaluation.Count} gate-minutes per landing={PerLanding(risk)}"
        };
        lines.AddRange(Features.Select(feature =>
            $"feature {feature}: evaluation hits={evaluation.Count(point => Pick(point.Index, feature) == point.Culprit)}/{evaluation.Count}"));
        lines.Add("bar: risk hits >= 1.5x newest hits and gate-minutes per landing <= 0.9x newest, with at least 10 evaluation decision points");
        lines.Add($"verdict: {verdict}");
        return lines;
    }
}
