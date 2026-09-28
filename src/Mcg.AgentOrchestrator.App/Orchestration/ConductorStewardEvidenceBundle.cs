using System.Text;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorStewardEvidenceBundle
{
    internal const string Heading = "## Precomputed evidence bundle";
    internal const string LessonsHeading = "## Operator lessons";
    private const int MaxReasonLines = 30;
    private const int MaxReasonChars = 4000;
    private const int MaxReasonLineChars = 400;
    private const int MaxPaths = 20;
    private const int MaxPathLineChars = 600;
    private static readonly Regex Reason = new(
        "reason:|rejection|rejected|assert|expected|actual|fail|error|exception",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RepositoryPath = new(
        @"(?<![\w/\\])(?<path>(?:(?:[A-Za-z0-9_.-]+[/\\])+[A-Za-z0-9_*?.-]+|[A-Za-z0-9_.-]+\.(?:cs|md|json|props|targets|slnx?|cmd|ps1|ya?ml))(?:::\w+|#L\d+|:\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string Build(ConductorStewardTrigger trigger, string worktree,
        IConductorStewardTrackedFileLister files, Func<string, string, bool> pathExists,
        ConductorLessonSelection? lessons = null)
    {
        var bundle = new StringBuilder(Heading).AppendLine()
            .Append("Candidate commit: ").AppendLine(trigger.CandidateSha)
            .AppendLine("Failure and rejection lines:");
        var reasonChars = 0;
        var reasonLines = 0;
        var truncated = false;
        foreach (var segment in Regex.Split(trigger.Evidence, @"\r?\n| \| "))
        {
            var line = segment.Trim();
            if (line.Length == 0 || !Reason.IsMatch(line)) continue;
            if (reasonLines == MaxReasonLines || reasonChars == MaxReasonChars)
            {
                truncated = true;
                break;
            }
            var allowed = Math.Min(MaxReasonLineChars, Math.Min(line.Length, MaxReasonChars - reasonChars));
            bundle.AppendLine(line[..allowed]);
            reasonChars += allowed;
            reasonLines++;
            if (allowed < line.Length) truncated = true;
        }
        if (reasonLines == 0) bundle.AppendLine("none found");
        if (truncated) bundle.AppendLine("[failure lines truncated]");

        bundle.AppendLine("Cited repository paths:");
        var paths = RepositoryPath.Matches(string.Join("\n", trigger.Evidence, trigger.WorkerResult,
                string.Join("\n", trigger.EvidenceReferences)))
            .Select(match => NormalizePath(match.Groups["path"].Value))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxPaths + 1).ToArray();
        if (paths.Length == 0) bundle.AppendLine("none found");
        foreach (var path in paths.Take(MaxPaths))
        {
            var exists = false;
            try { exists = pathExists(worktree, path); }
            catch (Exception ex)
            {
                bundle.AppendLine(Limit($"{path}: existence check failed: {ex.GetType().Name}", MaxPathLineChars));
                continue;
            }
            if (exists)
            {
                bundle.AppendLine(Limit($"{path}: exists", MaxPathLineChars));
                continue;
            }
            var stem = Path.GetFileNameWithoutExtension(path).Trim('*', '?');
            if (stem.Length == 0)
            {
                bundle.AppendLine(Limit($"{path}: missing; no usable stem", MaxPathLineChars));
                continue;
            }
            try
            {
                var matches = files.MatchingFiles(worktree, stem).Take(20);
                bundle.AppendLine(Limit($"{path}: missing; tracked files sharing stem '{stem}': " +
                    string.Join(", ", matches), MaxPathLineChars));
            }
            catch (Exception ex)
            {
                bundle.AppendLine(Limit($"{path}: missing; tracked-file lookup failed: {ex.GetType().Name}",
                    MaxPathLineChars));
            }
        }
        if (paths.Length > MaxPaths) bundle.AppendLine("[additional paths truncated]");
        bundle.AppendLine().AppendLine(LessonsHeading).AppendLine(ConductorLessonSelector.Render(lessons));
        return bundle.ToString().TrimEnd();
    }

    internal static bool PathExists(string worktree, string path)
    {
        var root = Path.GetFullPath(worktree);
        var fullPath = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!Path.IsPathFullyQualified(fullPath) ||
            !fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;
        return File.Exists(fullPath) || Directory.Exists(fullPath);
    }

    private static string NormalizePath(string citation) =>
        Regex.Split(citation, @"::|#L\d+|:\d+")[0].Replace('\\', '/').TrimEnd('.', ',', ';', ')');

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..(maximum - 15)] + "... [truncated]";
}
