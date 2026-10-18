using System.Text.RegularExpressions;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Resolves a focused selection against the same resource owners as the gate's lanes.
internal sealed class FocusedEvidenceRemoteEligibility
{
    internal sealed record Decision(bool Eligible, string? Reason, IReadOnlyList<string> ExcludedLanes);

    private readonly IReadOnlyList<AcceptanceTestLane> _lanes;
    private readonly IReadOnlyList<AcceptanceTestClassDescriptor> _classes;
    private readonly IReadOnlyList<string> _machineLocalKeys;
    private readonly bool _failed;

    private FocusedEvidenceRemoteEligibility(IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, IReadOnlyList<string> machineLocalKeys, bool failed = false)
    {
        _lanes = lanes;
        _classes = classes;
        _machineLocalKeys = machineLocalKeys;
        _failed = failed;
    }

    internal static FocusedEvidenceRemoteEligibility Create(string worktreePath, IReadOnlyList<string> machineLocalKeys)
    {
        try
        {
            var lanes = AcceptanceGateEngineSettings.Load(worktreePath).InfrastructureTestLanes;
            if (!lanes.Any(lane => lane.ExclusiveResourceKeys.Count > 0 || lane.RequiresBuildSystemChange))
                return new([], [], machineLocalKeys);
            var classes = AcceptanceTestClassSourceScanner.Scan(worktreePath);
            return new(AcceptanceLaneMembership.ResolveOwnedCollections(lanes, classes), classes, machineLocalKeys);
        }
        catch (Exception) { return new([], [], machineLocalKeys, failed: true); }
    }

    internal Decision Decide(Check check)
    {
        if (_failed) return new(false, "lane-resolution-failed", []);
        var terms = PositiveTerms(check.Arguments).ToArray();
        if (terms.Length == 0) return new(false, "unresolved-selection", []);
        var reason = RemoteLaneResourceEligibility.ExclusionReason(check.ExclusiveResourceKeys,
            check.RequiresBuildSystemChange, _machineLocalKeys);
        try
        {
            var selected = new HashSet<string>(terms.Select(term => term.Value), StringComparer.Ordinal);
            foreach (var entry in _classes)
            {
                if (terms.Any(term => (term.Contains
                        ? entry.FullName.Contains(term.Value, StringComparison.OrdinalIgnoreCase)
                        : entry.FullName.Equals(term.Value, StringComparison.OrdinalIgnoreCase)) ||
                    term.Value.StartsWith(entry.FullName + ".", StringComparison.OrdinalIgnoreCase)))
                    selected.Add(entry.FullName);
            }
            var selectedLanes = selected.SelectMany(name => AcceptanceLaneMembership.LanesIncluding(_lanes, name)).ToHashSet();
            var excluded = new List<string>();
            foreach (var lane in _lanes)
            {
                if (!selectedLanes.Contains(lane)) continue;
                var laneReason = RemoteLaneResourceEligibility.ExclusionReason(lane.ExclusiveResourceKeys,
                    lane.RequiresBuildSystemChange, _machineLocalKeys);
                if (laneReason is null) continue;
                reason ??= laneReason;
                if (!excluded.Contains(lane.Name, StringComparer.Ordinal)) excluded.Add(lane.Name);
            }
            return new(reason is null, reason, excluded);
        }
        catch (Exception) { return new(false, "lane-resolution-failed", []); }
    }

    private static IEnumerable<(string Value, bool Contains)> PositiveTerms(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            string filter;
            if (arguments[index] == "--filter" && index + 1 < arguments.Count) filter = arguments[++index];
            else if (arguments[index].StartsWith("--filter=", StringComparison.Ordinal)) filter = arguments[index][9..];
            else continue;
            // Unioning positive terms, including intersections, conservatively selects a superset.
            foreach (var term in filter.Replace("(", "").Replace(")", "").Split(['|', '&']))
            {
                var match = Regex.Match(term.Trim(), @"^(?<property>\w+)\s*(?<operator>!=|!~|=|~)\s*(?<value>.+)$",
                    RegexOptions.CultureInvariant);
                if (!match.Success || !match.Groups["property"].Value.Equals("FullyQualifiedName", StringComparison.OrdinalIgnoreCase))
                    continue;
                var op = match.Groups["operator"].Value;
                var value = match.Groups["value"].Value.Trim();
                if (op is "=" or "~" && value.Length > 0) yield return (value, op == "~");
            }
        }
    }
}
