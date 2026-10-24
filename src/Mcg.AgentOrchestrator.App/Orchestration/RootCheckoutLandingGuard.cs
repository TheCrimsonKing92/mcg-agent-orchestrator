using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// This guard only observes the user's checkout. Publication and checkout updates belong to the caller.
internal static class RootCheckoutLandingGuard
{
    internal enum Outcome { OtherBranch, Hold, FastForward }
    internal sealed record Inspection(Outcome Outcome, string? Reason = null);
    internal sealed record TrackedChanges(bool Succeeded, IReadOnlyList<string> Paths, string? Failure = null);
    private const string Remediation = "commit, stash or revert these changes yourself; the landing retries automatically";

    internal static Inspection Inspect(
        Func<string, string[], GitCli.GitResult> runGit, string root, string branch,
        string baseline, string landed)
    {
        if (!IsOnBranch(runGit, root, branch))
            return new(Outcome.OtherBranch);

        var changes = ReadTrackedChanges(runGit, root);
        if (!changes.Succeeded)
            return Hold(root, branch, $"could not be inspected: {changes.Failure}");

        var additions = runGit(root, ["diff", "--name-only", "--no-renames", "--diff-filter=A", "-z", baseline, landed]);
        if (!Complete(additions))
            return Hold(root, branch, $"could not be inspected: {Failure("diff", additions)}");
        if (!TryReadPaths(additions.Output, out var addedPaths))
            return Hold(root, branch, "could not be inspected: malformed git diff output");

        var paths = new SortedSet<string>(changes.Paths, StringComparer.Ordinal);
        foreach (var path in addedPaths)
        {
            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                paths.Add(path);
        }

        if (paths.Count == 0)
            return new(Outcome.FastForward);
        var displayed = string.Join(", ", paths.Take(10).Select(Escape));
        var remainder = paths.Count > 10 ? $" (and {paths.Count - 10} more)" : string.Empty;
        return Hold(root, branch, $"has changes that would mix with the landing: {displayed}{remainder}");
    }

    internal static bool IsOnBranch(
        Func<string, string[], GitCli.GitResult> runGit, string root, string branch) =>
        string.Equals(ReadSymbolicHead(runGit, root), $"refs/heads/{branch}", StringComparison.Ordinal);

    internal static string? ReadSymbolicHead(Func<string, string[], GitCli.GitResult> runGit, string root)
    {
        var result = runGit(root, ["symbolic-ref", "-q", "HEAD"]);
        return Complete(result) && result.Output.Trim().StartsWith("refs/heads/", StringComparison.Ordinal)
            ? result.Output.Trim() : null;
    }

    internal static TrackedChanges ReadTrackedChanges(Func<string, string[], GitCli.GitResult> runGit, string root)
    {
        var result = runGit(root, ["status", "--porcelain=v1", "-z", "--untracked-files=no"]);
        if (!Complete(result))
            return new(false, [], Failure("status", result));
        if (!TryReadPaths(result.Output, out var records, status: true))
            return new(false, [], "malformed git status output");
        return new(true, records.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    internal static string Escape(string value)
    {
        var escaped = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsControl(character)) escaped.Append($"\\u{(int)character:x4}");
            else if (character == '\\') escaped.Append("\\\\");
            else escaped.Append(character);
        }
        return escaped.ToString();
    }

    private static Inspection Hold(string root, string branch, string detail) =>
        new(Outcome.Hold, $"root checkout {Escape(root)} on {Escape(branch)} {detail}; {Remediation}");

    private static bool Complete(GitCli.GitResult result) =>
        result.Succeeded && result.ProcessStarted && !result.DrainTimedOut;

    private static string Failure(string command, GitCli.GitResult result) =>
        $"git {command} failed exit={result.ExitCode} drainTimedOut={result.DrainTimedOut} processStarted={result.ProcessStarted}";

    private static bool TryReadPaths(string output, out string[] paths, bool status = false)
    {
        paths = [];
        if (output.Length == 0) return true;
        if (output[^1] != '\0') return false;
        var records = output[..^1].Split('\0');
        var parsed = new List<string>();
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            var renamed = false;
            if (status)
            {
                if (record.Length < 4 || record[2] != ' ' ||
                    !" MADRCUT".Contains(record[0]) || !" MADRCUT".Contains(record[1]) ||
                    (record[0] == ' ' && record[1] == ' ')) return false;
                renamed = record[0] is 'R' or 'C' || record[1] is 'R' or 'C';
                record = record[3..];
            }
            if (!ValidPath(record)) return false;
            parsed.Add(record);
            // Porcelain -z writes the destination first, followed by the original path.
            if (renamed)
            {
                if (++index >= records.Length || !ValidPath(records[index])) return false;
                parsed.Add(records[index]);
            }
        }
        paths = parsed.ToArray();
        return true;
    }

    private static bool ValidPath(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) &&
        !path.Split('/').Any(segment => segment is "" or "." or "..");
}
