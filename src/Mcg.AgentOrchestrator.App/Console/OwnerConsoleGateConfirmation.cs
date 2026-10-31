using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A summary of an existing confirmation attempt, not a confirmation policy decision.
internal sealed record OwnerConsoleGateConfirmation(string SourceName, int TargetCount, TimeSpan Elapsed,
    bool Running, OwnerConsoleGateTrxOutcomes? Results, bool SameCandidateRerun = false)
{
    private static readonly Regex FilterNames = new(@"FullyQualifiedName[~=](?<name>[^|&()]+)",
        RegexOptions.CultureInvariant);
    internal bool Confirmed => !Running && Results is { Available: true, FailureCount: > 0 };
    internal bool NotReproduced => !Running && SameCandidateRerun && Results?.AllPassed == true;
    internal string Line => Running
        ? $"confirming: re-running {TargetCount} failed test(s), {(int)Elapsed.TotalSeconds}s"
        : Confirmed ? "confirmed: failed again (real failure)"
        : NotReproduced ? "not reproduced: passed on re-run (flake)"
        : Results is { Available: true, TotalCount: > 0 }
            ? "confirmation result inconclusive" : "confirmation result unavailable";

    internal static IReadOnlyList<OwnerConsoleGateLane> Attach(IReadOnlyList<OwnerConsoleGateLane> rows,
        OwnerConsoleGateFailureNames? reader = null)
    {
        var failed = rows.Where(row => !row.IsAttribution &&
            !row.Name.EndsWith(" [trusted main baseline]", StringComparison.Ordinal) &&
            row.State == OwnerConsoleGateLaneState.Done && row.Outcome == "failed").ToArray();
        var links = new Dictionary<string, OwnerConsoleGateLane>(StringComparer.Ordinal);
        foreach (var rerun in rows.Where(row => row.IsAttribution &&
            row.EvidenceKind != "baseline" && !row.Name.EndsWith(" [trusted main baseline]", StringComparison.Ordinal) &&
            row.EvidenceKind != "member-reproduction" && string.IsNullOrWhiteSpace(row.ReproductionMemberId) &&
            row.State is OwnerConsoleGateLaneState.Running or OwnerConsoleGateLaneState.Done))
        {
            var targets = Targets(rerun);
            var matches = failed.Where(lane => targets.Length > 0 && targets.All(target =>
                (reader?.ReadOutcomes(lane).FailureTestNames ?? lane.FailingTestNames ?? []).Any(name =>
                    name.Contains(target, StringComparison.Ordinal) ||
                    target.EndsWith("." + name, StringComparison.Ordinal)))).ToArray();
            var owner = matches.Length == 1 ? matches[0] : matches.Length == 0 && failed.Length == 1 ? failed[0] : null;
            if (owner is null) continue;
            // Rows already carry the policy's current attempt. Prefer active work; otherwise the last completed row.
            if (links.TryGetValue(owner.Name, out var prior) && prior.State == OwnerConsoleGateLaneState.Running &&
                rerun.State != OwnerConsoleGateLaneState.Running) continue;
            links[owner.Name] = rerun;
        }
        return rows.Select(lane =>
        {
            if (!links.TryGetValue(lane.Name, out var rerun) || lane.Confirmation is not null && reader is null) return lane;
            var count = Targets(rerun).Length;
            if (count == 0) count = lane.FailingTestNames?.Count ?? 0;
            var results = rerun.State == OwnerConsoleGateLaneState.Done ? reader?.ReadConfirmation(rerun, lane) : null;
            return lane with { Confirmation = new(rerun.Name, count, rerun.Elapsed,
                rerun.State == OwnerConsoleGateLaneState.Running,
                results, OwnerConsoleGateCandidateRerunEvidence.Covers(lane, rerun, reader?.ReadOutcomes(lane), results)) };
        }).ToArray();
    }

    internal static string Header(IReadOnlyList<OwnerConsoleGateLane> failed)
    {
        var confirmations = failed.Select(row => row.Confirmation).OfType<OwnerConsoleGateConfirmation>().ToArray();
        if (confirmations.Any(item => item.Running))
            return $"FAILING, confirming {confirmations.Where(item => item.Running).Sum(item => item.TargetCount)} failure(s)";
        if (confirmations.Any(item => item.Confirmed))
            return $"FAILED, {confirmations.Where(item => item.Confirmed).Sum(item => item.Results!.FailureCount)} confirmed failure(s)";
        return failed.Count > 0 && failed.All(row => row.Confirmation?.NotReproduced == true)
            ? "FLAKE, re-run passed" : "FAILING";
    }

    private static string[] Targets(OwnerConsoleGateLane row) => FilterNames.Matches(row.Target ?? row.Name)
        .Select(match => match.Groups["name"].Value.Trim()).ToArray();
}
