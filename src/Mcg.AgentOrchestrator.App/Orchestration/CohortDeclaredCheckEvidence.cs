using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class CohortDeclaredCheckEvidence
{
    internal static IReadOnlyList<AcceptanceCohortCheckEvidence>? Resolve(AcceptanceVerificationResult result)
    {
        var required = result.RequiredCrosswalkChecks;
        var checks = (result.Checks ?? []).Where(check => !check.Advisory).ToArray();
        if (required.Count == 0 || required.Distinct(StringComparer.Ordinal).Count() != required.Count ||
            string.IsNullOrWhiteSpace(result.AttemptId) || result.RequiredTestCheckNames.Count == 0 ||
            required.Any(name => !result.RequiredTestCheckNames.Contains(name, StringComparer.Ordinal)) ||
            result.RequiredTestCheckNames.Any(name => checks.Count(check => check.Name == name) != 1)) return null;
        var sources = new List<AcceptanceCohortCheckEvidence>();
        foreach (var name in required)
        {
            var matches = checks.Where(check => check.Name == name).ToArray();
            if (matches.Length != 1 || matches[0].CohortEvidence is not { } evidence) return null;
            var check = matches[0];
            if (evidence.Format != AcceptanceCohortCheckEvidence.NormalizedFormat || evidence.CheckName != name ||
                evidence.Project != (check.TestProjectPath ?? "") || evidence.AttemptId != result.AttemptId ||
                evidence.AttemptId != check.TestResultAttemptId || evidence.RunOrdinal != check.TestResultRunOrdinal ||
                evidence.ExitCode != check.ExitCode || evidence.ExecutedTestCount != check.ExecutedTestCount ||
                !SamePaths(evidence.Artifacts.Select(a => a.Path), check.NormalizedTestResultPaths ?? [])) return null;
            if (check.WithinAttemptRerun is not null) return null;
            sources.Add(evidence with { SupplementalArtifacts = (check.TestResultPaths ?? [])
                .Select(path => CohortTestResultEvidence.Capture(path, AcceptanceCohortCheckEvidence.TrxFormat)).ToArray() });
        }
        if (checks.Any(check => (check.CrosswalkDiagnostics is not null || check.CohortEvidence is not null) &&
                !required.Contains(check.Name, StringComparer.Ordinal))) return null;
        foreach (var check in checks.Where(check => !required.Contains(check.Name, StringComparer.Ordinal) &&
                     check.TestResultPaths is { Count: > 0 }))
        {
            // The partition cache has already proved applicability for explicit carry. Keep
            // its source attempt, just as structural coverage does, rather than rebinding it.
            if (string.IsNullOrWhiteSpace(check.TestResultAttemptId) || check.TestResultRunOrdinal < 0 ||
                (check.TestResultAttemptId != result.AttemptId && !check.TestResultIsExplicitCrossAttemptReuse)) return null;
            var paths = GroupedReceiptTestResultPaths.Resolve(result with { Checks = [check] },
                AcceptanceCohortVerificationClassifier.NormalizeCohortTestResultPaths(check.TestResultPaths)).Paths;
            if (!AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(paths)) return null;
            sources.Add(new(check.Name, AcceptanceCohortCheckEvidence.TrxFormat, check.TestProjectPath ?? "",
                check.TestResultAttemptId, check.TestResultRunOrdinal, check.ExitCode,
                CommonDirectory(paths), [], CohortTestResultEvidence.CountExecutedTrx(paths),
                paths.Select(path => CohortTestResultEvidence.Capture(path, AcceptanceCohortCheckEvidence.TrxFormat)).ToArray())
            {
                SupplementalArtifacts = (check.NormalizedTestResultPaths ?? [])
                    .Select(path => CohortTestResultEvidence.Capture(path, AcceptanceCohortCheckEvidence.NormalizedFormat)).ToArray()
            });
        }
        // The aggregate includes advisory artifacts too. Exclude only artifacts owned
        // exclusively by advisory checks; missing required or unexplained paths still fail.
        var requiredPaths = new HashSet<string>(AcceptanceVerificationResult.CollectTestResultPaths(checks)
            .Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var advisoryOnlyPaths = new HashSet<string>(AcceptanceVerificationResult.CollectTestResultPaths(
                (result.Checks ?? []).Where(check => check.Advisory)).Select(Path.GetFullPath)
            .Where(path => !requiredPaths.Contains(path)), StringComparer.OrdinalIgnoreCase);
        var aggregate = AcceptanceCohortVerificationClassifier.NormalizeCohortTestResultPaths(result.TestResultPaths)
            .Where(path => !advisoryOnlyPaths.Contains(path)).ToArray();
        var resolved = GroupedReceiptTestResultPaths.Resolve(result with { Checks = checks }, aggregate).Paths;
        if (!SamePaths(sources.SelectMany(s => s.Artifacts).Select(a => a.Path), resolved)) return null;
        if (result.RequiredTestCheckNames.Any(name => !Covered(name, new HashSet<string>(StringComparer.Ordinal)))) return null;
        return sources;

        bool Covered(string name, HashSet<string> visiting)
        {
            if (!visiting.Add(name)) return false;
            if (sources.Any(source => source.CheckName == name)) return true;
            var matches = checks.Where(check => check.Name == name).ToArray();
            return matches.Length == 1 && matches[0].CoveredBy is { Count: > 0 } covering &&
                covering.All(cover => Covered(cover, new HashSet<string>(visiting, StringComparer.Ordinal)));
        }
    }

    private static bool SamePaths(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(right.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static string CommonDirectory(IReadOnlyList<string> paths)
    {
        var root = Path.GetDirectoryName(paths[0])!;
        while (paths.Any(path => !Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            root = Path.GetDirectoryName(root) ?? throw new InvalidDataException("TRX evidence has no common custody directory.");
        return root;
    }
}
