using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Reach is attribution evidence only when the complete failing set has one unique member.
internal static class MergeTrainReachAttribution
{
    internal static MergeTrainMemberBinding? TryAttribute(
        IReadOnlyList<string> resultPaths, string workspacePath,
        IReadOnlyList<MergeTrainMemberBinding> members, out IReadOnlyList<string> failingClasses)
    {
        failingClasses = [];
        var classes = ReadFailingClasses(resultPaths);
        if (classes is null) return null;
        try
        {
            var reaches = new List<IReadOnlySet<string>>();
            foreach (var member in members)
            {
                var sources = member.LandingPaths.Select(path => NormalizePath(workspacePath, path))
                    .Where(path => path.StartsWith("src/", StringComparison.OrdinalIgnoreCase) &&
                        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var reach = sources.Length == 0 ? new HashSet<string>(StringComparer.Ordinal)
                    : RepositoryTestReach.TryReadReachableTestClasses(workspacePath, sources);
                if (reach is null) return null;
                reaches.Add(reach);
            }

            MergeTrainMemberBinding? attributed = null;
            foreach (var className in classes)
            {
                var owners = members.Where((_, index) => reaches[index].Contains(className)).ToArray();
                if (owners.Length != 1 || (attributed is not null && attributed.GoalId != owners[0].GoalId))
                    return null;
                attributed = owners[0];
            }
            failingClasses = classes;
            return attributed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // Keep parity with the impact path: never authorize from partial or guarded evidence.
    internal static IReadOnlyList<string>? ReadFailingClasses(IReadOnlyList<string> resultPaths)
    {
        var results = resultPaths.Select(AcceptanceTrxFailureReader.Read).ToArray();
        if (results.Any(result => result.Status != AcceptanceTrxReadStatus.Readable)) return null;
        var failures = results.SelectMany(result => result.Failures)
            .Where(failure => AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome) &&
                ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace) is null).ToArray();
        if (failures.Length == 0 || failures.Any(MergeTrainRedAttribution.IsMessageSubjectGuard)) return null;
        var classes = failures.Select(failure => AcceptanceTestSourceResolver.ExtractClassName(failure.TestName ?? string.Empty))
            .Distinct(StringComparer.Ordinal).ToArray();
        return classes.Any(string.IsNullOrWhiteSpace) ? null : classes.Select(name => name!).Order(StringComparer.Ordinal).ToArray();
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
