using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleGateReproduction(string MemberId, string Result, string? Reason,
    string? TreeRevision, string? BuildOutputRoot)
{
    internal string Line => $"reproduction {MemberId[..Math.Min(8, MemberId.Length)]}: {Result}" +
        (Reason is null ? "" : $" ({Reason})");

    internal static IReadOnlyList<OwnerConsoleGateLane> Attach(OwnerConsoleGateProgress gate,
        IReadOnlyList<OwnerConductEvent> events)
    {
        var reproductions = events.Where(item => item.Timestamp >= gate.StartedAt &&
            gate.Members.Any(member => item.GoalId?.StartsWith(member.Id, StringComparison.Ordinal) == true))
            .Select(Read).OfType<MergeTrainReproductionOutcome>().ToArray();
        return gate.Lanes.Select(lane =>
        {
            if (!string.IsNullOrWhiteSpace(lane.ReproductionMemberId))
                return lane with { ReproductionResult = reproductions.LastOrDefault(item =>
                    item.MemberId == lane.ReproductionMemberId && item.Provenance?.TreeRevision == lane.TreeRevision)?.Result };
            if (lane.IsAttribution || lane.Outcome != "failed") return lane;
            var matches = reproductions.Where(item => item.SelectedTests.Any(test =>
                (lane.FailingTestNames ?? []).Any(name => test.Split('(')[0].EndsWith("." + name, StringComparison.Ordinal) || test.Split('(')[0] == name)))
                .GroupBy(item => item.MemberId, StringComparer.Ordinal).Select(group => group.Last())
                .Select(item => new OwnerConsoleGateReproduction(item.MemberId, item.Result, item.Reason,
                    item.Provenance?.TreeRevision, item.Provenance?.BuildOutputRoots.FirstOrDefault())).ToArray();
            return lane with { Reproductions = matches };
        }).ToArray();
    }

    private static MergeTrainReproductionOutcome? Read(OwnerConductEvent item)
    {
        const string marker = " evidence=";
        var start = item.Detail.IndexOf(marker, StringComparison.Ordinal);
        if (item.EventKind != "train-reproduction" || start < 0) return null;
        try { return JsonSerializer.Deserialize<MergeTrainReproductionOutcome>(item.Detail[(start + marker.Length)..]); }
        catch (JsonException) { return null; }
    }
}
