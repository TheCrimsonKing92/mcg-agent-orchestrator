using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A recorded result establishes execution only when that check ran tests.
internal sealed record FocusedEvidenceNotRunPlan(IReadOnlyList<FocusedEvidenceNotRunPlan.NotRunCheck> Checks,
    IReadOnlyList<string>? Skipped = null)
{
    internal const string ApparatusStoppedAfterFailure = "apparatus-stopped-after-failure";
    internal const string NoResultRecorded = "no-result-recorded";
    internal const string ExecutionNotDemonstrated = "execution-not-demonstrated";
    internal sealed record NotRunCheck(string CheckName, string Reason, IReadOnlyList<FindingEvidenceSelection> Selections,
        IReadOnlyList<FindingEvidenceSelection>? UndemonstratedSelections = null);

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
            var crosswalk = CrosswalkFocusedEvidence.IsEvidenceCheck(check);
            if (!crosswalk && recorded.Length > 0 && recorded.All(result => result.ExecutedTestCount is > 0))
            {
                stoppedAfterFailure |= !continueAfterFailure && recorded.Any(result => !result.Passed);
                continue;
            }
            var selections = coverage.TargetToChecks
                .Where(target => target.CheckNames.Contains(check.Name, StringComparer.Ordinal))
                .Select(target => crosswalk
                    ? CrosswalkFocusedEvidence.SplitTarget(check, target.Target)
                    : target.Target.Split(':', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .Select(parts => new FindingEvidenceSelection(parts[0], parts[1])).ToArray();
            var undemonstrated = crosswalk
                ? CrosswalkFocusedEvidence.UndemonstratedSelections(check, recorded, selections) : null;
            if (crosswalk && recorded.Length > 0 && recorded.All(result => result.ExecutedTestCount is > 0) &&
                undemonstrated!.Count == 0)
            {
                stoppedAfterFailure |= !continueAfterFailure && recorded.Any(result => !result.Passed);
                continue;
            }
            var reason = recorded.Length > 0 ? ExecutionNotDemonstrated
                : stoppedAfterFailure ? ApparatusStoppedAfterFailure : NoResultRecorded;
            missing.Add(new(check.Name, $"not-run:{reason}", crosswalk ? undemonstrated! : selections,
                GoPackageFocusedEvidence.IsEvidenceCheck(check)
                    ? GoPackageFocusedEvidence.UndemonstratedSelections(check, recorded, selections) : undemonstrated));
            stoppedAfterFailure |= !continueAfterFailure && recorded.Any(result => !result.Passed);
        }
        return new(missing, results.SelectMany(result => result.SkippedTestIdentities ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    internal string AppendSummary(string summary)
    {
        var lines = Checks.SelectMany(check => new[] { $"{check.CheckName} {check.Reason}" }
                .Concat((check.UndemonstratedSelections ?? []).Select(selection =>
                    $"undemonstrated {selection.TestProject}:{selection.TestClass} {check.Reason}")))
            .Concat((Skipped ?? []).Select(identity => $"skipped: {identity}")).ToArray();
        return lines.Length == 0 ? summary : summary + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
