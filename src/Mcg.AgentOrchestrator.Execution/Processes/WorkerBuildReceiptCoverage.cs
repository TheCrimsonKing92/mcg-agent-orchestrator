namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerBuildReceiptCoverage
{
    internal static IReadOnlyList<string> FindMissingProjects(
        IReadOnlyList<string> requiredProjects,
        IReadOnlyList<string>? receiptProjects)
    {
        var covered = new HashSet<string>(
            (receiptProjects ?? []).Select(Normalize), StringComparer.OrdinalIgnoreCase);
        return requiredProjects.Where(project => !covered.Contains(Normalize(project))).ToArray();
    }

    private static string Normalize(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized;
    }
}
