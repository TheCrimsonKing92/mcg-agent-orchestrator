using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Attribution describes only declared test-source ownership, never a guessed cause.
internal static class MergeTrainRedAttribution
{
    internal static IReadOnlyList<AcceptanceTrxFailure> ReadFatalFailures(IReadOnlyList<string> paths) =>
        FatalFailures(paths.Select(AcceptanceTrxFailureReader.Read));

    private static IReadOnlyList<AcceptanceTrxFailure> FatalFailures(IEnumerable<AcceptanceTrxReadResult> results) =>
        results.Where(result => result.Status == AcceptanceTrxReadStatus.Readable)
            .SelectMany(result => result.Failures)
            .Where(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) &&
                ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is null)
            .ToArray();

    internal static MergeTrainMemberBinding? TryAttribute(
        MergeTrainReceipt receipt,
        string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members)
    {
        if (receipt.Outcome != MergeTrainGateOutcome.Failed) return null;
        var results = receipt.GateTestResultPaths.Select(AcceptanceTrxFailureReader.Read).ToArray();
        // Missing evidence may hide another owner's failure; never attribute a partial read.
        if (results.Any(result => result.Status != AcceptanceTrxReadStatus.Readable)) return null;
        var failures = FatalFailures(results);
        if (failures.Count == 0) return null;

        MergeTrainMemberBinding? attributed = null;
        var sourcesByClass = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var failure in failures)
        {
            var className = AcceptanceTestSourceResolver.ExtractClassName(failure.TestName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(className)) return null;
            if (!sourcesByClass.TryGetValue(className, out var sources))
            {
                sources = AcceptanceTestSourceResolver.ResolveSourcePaths(workspacePath, null, failure.TestName)
                    .Select(path => NormalizePath(workspacePath, path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                sourcesByClass.Add(className, sources);
            }
            if (sources.Count == 0) return null;
            var owners = members.Where(member => member.LandingPaths
                .Any(path => sources.Contains(NormalizePath(workspacePath, path)))).ToArray();
            if (owners.Length != 1 || (attributed is not null && attributed.GoalId != owners[0].GoalId))
                return null;
            attributed = owners[0];
        }
        return attributed;
    }

    private static string NormalizePath(string workspacePath, string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized))
            normalized = Path.GetRelativePath(workspacePath, normalized).Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
}
