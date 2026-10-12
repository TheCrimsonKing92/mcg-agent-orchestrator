using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Result presence, rather than the requested set, establishes which checks executed.
internal sealed record FocusedEvidenceNotRunPlan(IReadOnlyList<FocusedEvidenceNotRunPlan.NotRunCheck> Checks)
{
    internal const string ApparatusStoppedAfterFailure = "apparatus-stopped-after-failure";
    internal const string NoResultRecorded = "no-result-recorded";
    internal sealed record NotRunCheck(string CheckName, string Reason, IReadOnlyList<FindingEvidenceSelection> Selections);

    internal IReadOnlyList<FindingEvidenceSelection> Selections => Checks.SelectMany(check => check.Selections)
        .DistinctBy(selection => $"{selection.TestProject}:{selection.TestClass}", StringComparer.Ordinal).ToArray();

    internal static FocusedEvidenceNotRunPlan Compare(
        IReadOnlyList<AcceptanceManifestCheck> planned,
        IReadOnlyList<AcceptanceCheckResult> results,
        FocusedEvidenceCoverage coverage,
        bool continueAfterFailure)
    {
        var completed = results.Select(result => result.Name).ToHashSet(StringComparer.Ordinal);
        var missing = new List<NotRunCheck>();
        var stoppedAfterFailure = false;
        foreach (var check in planned)
        {
            if (completed.Contains(check.Name))
            {
                stoppedAfterFailure |= !continueAfterFailure && results.Any(result => result.Name == check.Name && !result.Passed);
                continue;
            }
            var selections = coverage.TargetToChecks
                .Where(target => target.CheckNames.Contains(check.Name, StringComparer.Ordinal))
                .Select(target => target.Target.Split(':', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .Select(parts => new FindingEvidenceSelection(parts[0], parts[1])).ToArray();
            missing.Add(new(check.Name, $"not-run:{(stoppedAfterFailure ? ApparatusStoppedAfterFailure : NoResultRecorded)}", selections));
        }
        return new(missing);
    }

    internal string AppendSummary(string summary) => Checks.Count == 0 ? summary :
        summary + Environment.NewLine + string.Join(Environment.NewLine, Checks.Select(check => $"{check.CheckName} {check.Reason}"));
}
