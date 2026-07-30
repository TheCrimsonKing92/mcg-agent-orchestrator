namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class OrchestratorGeneratedArtifactIgnore
{
    private static readonly object ExcludeFileGate = new();

    internal static void EnsureIgnored(string workingDirectory, params string[] patterns)
    {
        var repository = GitCli.Run(workingDirectory, "rev-parse", "--is-inside-work-tree");
        if (!repository.Succeeded ||
            !string.Equals(repository.Output.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var excludeResult = GitCli.Run(workingDirectory, "rev-parse", "--git-path", "info/exclude");
        if (!excludeResult.Succeeded || string.IsNullOrWhiteSpace(excludeResult.Output))
        {
            throw new InvalidOperationException(
                $"Could not resolve the local Git exclude file for orchestrator-generated artifacts: {excludeResult.Error}");
        }

        var rawPath = excludeResult.Output.Trim();
        var excludePath = Path.IsPathRooted(rawPath)
            ? Path.GetFullPath(rawPath)
            : Path.GetFullPath(Path.Combine(workingDirectory, rawPath));

        lock (ExcludeFileGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);
            var existingLines = File.Exists(excludePath)
                ? File.ReadAllLines(excludePath)
                : [];
            var missing = patterns
                .Where(pattern => !existingLines.Contains(pattern, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (missing.Length == 0)
            {
                return;
            }

            var existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : string.Empty;
            var prefix = existing.Length > 0 && !existing.EndsWith('\n') ? Environment.NewLine : string.Empty;
            File.AppendAllText(
                excludePath,
                prefix + string.Join(Environment.NewLine, missing) + Environment.NewLine);
        }
    }
}
