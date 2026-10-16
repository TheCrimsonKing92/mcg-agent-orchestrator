using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A recorded result establishes execution only when that check ran tests.
internal sealed record FocusedEvidenceNotRunPlan(IReadOnlyList<FocusedEvidenceNotRunPlan.NotRunCheck> Checks)
{
    internal const string ApparatusStoppedAfterFailure = "apparatus-stopped-after-failure";
    internal const string NoResultRecorded = "no-result-recorded";
    internal const string ExecutionNotDemonstrated = "execution-not-demonstrated";
    internal sealed record NotRunCheck(string CheckName, string Reason, IReadOnlyList<FindingEvidenceSelection> Selections);

    internal IReadOnlyList<FindingEvidenceSelection> Selections => Checks.SelectMany(check => check.Selections)
        .DistinctBy(selection => $"{selection.TestProject}:{selection.TestClass}", StringComparer.Ordinal).ToArray();

    internal static FocusedEvidenceNotRunPlan Compare(
        IReadOnlyList<AcceptanceManifestCheck> planned,
        IReadOnlyList<AcceptanceCheckResult> results,
        FocusedEvidenceCoverage coverage,
        bool continueAfterFailure)
    {
        var missing = new List<NotRunCheck>();
        var stoppedAfterFailure = false;
        foreach (var check in planned)
        {
            var recorded = results.Where(result => result.Name == check.Name).ToArray();
            if (recorded.Length > 0 && recorded.All(result => result.ExecutedTestCount is > 0))
            {
                stoppedAfterFailure |= !continueAfterFailure && recorded.Any(result => !result.Passed);
                continue;
            }
            var selections = coverage.TargetToChecks
                .Where(target => target.CheckNames.Contains(check.Name, StringComparer.Ordinal))
                .Select(target => target.Target.Split(':', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .Select(parts => new FindingEvidenceSelection(parts[0], parts[1])).ToArray();
            var reason = recorded.Length > 0 ? ExecutionNotDemonstrated
                : stoppedAfterFailure ? ApparatusStoppedAfterFailure : NoResultRecorded;
            missing.Add(new(check.Name, $"not-run:{reason}", selections));
            stoppedAfterFailure |= !continueAfterFailure && recorded.Any(result => !result.Passed);
        }
        return new(missing);
    }

    internal string AppendSummary(string summary) => Checks.Count == 0 ? summary :
        summary + Environment.NewLine + string.Join(Environment.NewLine, Checks.Select(check => $"{check.CheckName} {check.Reason}"));
}
