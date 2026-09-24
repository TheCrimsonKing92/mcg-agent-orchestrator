using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record WorkerBuildErrors(IReadOnlyList<string> Lines, int Total)
{
    internal string Format(int limit)
    {
        var lines = Lines.Take(limit).ToArray();
        var omitted = Total - lines.Length;
        return "worker build errors:" + Environment.NewLine +
            string.Join(Environment.NewLine, lines) +
            (omitted > 0 ? $"{Environment.NewLine}{omitted} additional error lines omitted." : string.Empty);
    }
}

internal static partial class WorkerBuildLogErrorReader
{
    [GeneratedRegex(@"^.+\(\d+,\d+\): error [A-Za-z]+\d+: .+$", RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticPattern();

    internal static WorkerBuildErrors? ReadNewest(string artifactsPath)
    {
        try
        {
            var root = Path.Combine(artifactsPath, "worker-build-logs");
            if (!Directory.Exists(root))
                return null;
            var newest = Directory.EnumerateDirectories(root)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .ThenByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
                return null;
            var logs = Directory.EnumerateFiles(newest, "*.log", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (logs.Length == 0)
                return null;
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var log in logs)
            {
                foreach (var line in File.ReadLines(log))
                {
                    var diagnostic = line.Trim();
                    if (DiagnosticPattern().IsMatch(diagnostic) && seen.Add(diagnostic))
                        errors.Add(diagnostic);
                }
            }
            return errors.Count == 0 ? null : new WorkerBuildErrors(errors.Take(50).ToArray(), errors.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
