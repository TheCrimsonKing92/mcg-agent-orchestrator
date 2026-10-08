namespace Mcg.AgentOrchestrator.Infrastructure;

// Local-only hygiene shared by dispatch sandbox preparation and project conductor start-up.
internal static class LocalGitExclude
{
    internal static bool TryAppendEntries(
        string repositoryDirectory,
        IReadOnlyList<string> entries,
        Func<string, string, bool>? isEntryPresent = null)
    {
        try
        {
            var pathResult = GitCli.Run(repositoryDirectory, "rev-parse", "--git-path", "info/exclude");
            if (!pathResult.Succeeded || string.IsNullOrWhiteSpace(pathResult.Output))
                return false;

            var excludeRaw = pathResult.Output.Trim();
            var excludePath = Path.IsPathRooted(excludeRaw)
                ? excludeRaw
                : Path.GetFullPath(Path.Combine(repositoryDirectory, excludeRaw));
            Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);

            var existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : string.Empty;
            var existingEntries = existing.Split('\n')
                .Select(line => line.Trim()).ToHashSet(StringComparer.Ordinal);
            var missing = new List<string>();
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry) || entry.Contains('\n') || entry.Contains('\r'))
                    continue;
                var normalized = entry.Trim();
                if (isEntryPresent?.Invoke(existing, normalized) == true)
                    continue;
                if (existingEntries.Add(normalized))
                    missing.Add(normalized);
            }

            if (missing.Count > 0)
            {
                var prefix = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;
                File.AppendAllText(excludePath, prefix + string.Join('\n', missing) + "\n");
            }
            return true;
        }
        catch
        {
            // Best-effort: callers can continue when local git hygiene is unavailable.
            return false;
        }
    }
}
