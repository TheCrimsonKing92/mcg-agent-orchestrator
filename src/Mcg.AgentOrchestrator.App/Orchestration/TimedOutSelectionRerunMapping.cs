using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class TimedOutSelectionRerunMapping
{
    internal static bool TryMapTimedOutSelections(ConductorDriver.TimedOutRoundReceipt[] sources,
        FailedGoalTimedOutSelectionRerun rerun, Func<FindingEvidenceSelection, string> format,
        out FindingEvidenceSelection[] selections)
    {
        var mapped = new List<FindingEvidenceSelection>();
        foreach (var slug in rerun.Selections)
        {
            var matches = new List<FindingEvidenceSelection>();
            foreach (var source in sources)
            foreach (var target in source.Evidence.Coverage?.TargetToChecks ?? [])
            {
                if (!target.CheckNames.Any(name => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') == slug))
                    continue;
                matches.AddRange(source.Receipt.Request.Selections.Where(selection =>
                    string.Equals(format(selection), target.Target, StringComparison.OrdinalIgnoreCase)));
            }
            var exact = matches.DistinctBy(format).ToArray();
            if (exact.Length == 0) { selections = []; return false; }
            mapped.AddRange(exact);
        }
        selections = mapped.DistinctBy(format)
            .OrderBy(format, StringComparer.Ordinal).ToArray();
        return selections.Length > 0;
    }

    internal static IReadOnlyList<AcceptanceCheckResult> AllRoundChecks(FocusedEvidenceRunResult evidence) =>
        evidence.Checks.Concat((evidence.Arms ?? []).SelectMany(arm => arm.Checks)).ToArray();

    internal static string? CheckClassification(AcceptanceCheckResult check) =>
        check.FailureClassification;
}
