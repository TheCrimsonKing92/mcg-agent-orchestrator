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

    internal static bool IsCompilerDiagnostic(string line) => DiagnosticPattern().IsMatch(line.Trim());

    internal static WorkerBuildErrors? ReadNewest(string artifactsPath)
    {
        try
        {
            var newest = GetArtifactsRoots(artifactsPath)
                .Select(path => Path.Combine(path, "worker-build-logs"))
                .Where(Directory.Exists)
                .SelectMany(path => Directory.EnumerateDirectories(path))
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
                    if (IsCompilerDiagnostic(diagnostic) && seen.Add(diagnostic))
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

    private static IReadOnlyList<string> GetArtifactsRoots(string artifactsPath)
    {
        var roots = new List<string> { artifactsPath };
        try
        {
            var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactsPath));
            var parent = Path.GetDirectoryName(canonicalPath);
            if (parent is null || !Directory.Exists(parent))
                return roots;
            var prefix = Path.GetFileName(canonicalPath) + "-build-";
            var siblings = Directory.EnumerateDirectories(parent, prefix + "*")
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length &&
                        name.Skip(prefix.Length).All(char.IsAsciiDigit);
                }).ToArray();
            roots.AddRange(siblings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Canonical logs remain usable when sibling discovery is unavailable.
        }
        return roots;
    }
}
