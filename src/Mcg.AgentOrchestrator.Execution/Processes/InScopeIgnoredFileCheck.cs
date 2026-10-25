using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Positive evidence only: a literal scope file, reported ignored by git, written since dispatch.
internal static class InScopeIgnoredFileCheck
{
    internal static InScopeIgnoredFileCheckResult Evaluate(string worktreePath, Goal goal, TaskSpec task) =>
        task.LastDispatch is not { } dispatch ? new(false, string.Empty) :
        Evaluate(worktreePath, [goal.Objective, task.Description], dispatch.DispatchedAt);

    internal static InScopeIgnoredFileCheckResult Evaluate(
        string worktreePath,
        IReadOnlyList<string> scopeTexts,
        DateTimeOffset dispatchedAt,
        Func<string, string[], GitCli.GitResult>? runGit = null,
        Func<string, DateTimeOffset?>? readLastWriteUtc = null)
    {
        runGit ??= RunGit;
        readLastWriteUtc ??= path => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        if (!Directory.Exists(worktreePath)) return new(false, string.Empty);
        var paths = scopeTexts.SelectMany(ExtractPaths)
            .Where(path => !Directory.Exists(Path.Combine(worktreePath, path)))
            .ToHashSet(StringComparer.Ordinal);
        if (paths.Count == 0) return new(false, string.Empty);

        var listed = runGit(worktreePath,
            ["ls-files", "--others", "--ignored", "--exclude-standard", "-z", "--",
                .. paths.Order(StringComparer.Ordinal).Select(path => ":(literal)" + path)]);
        if (!Completed(listed)) return Unavailable("ls-files", listed.ExitCode);

        var recent = listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(paths.Contains)
            .Distinct(StringComparer.Ordinal)
            .Where(path => WrittenSinceDispatch(Path.Combine(worktreePath, path), dispatchedAt, readLastWriteUtc))
            .Order(StringComparer.Ordinal).ToArray();
        if (recent.Length == 0) return new(false, string.Empty);

        var ignored = runGit(worktreePath, ["check-ignore", "-v", "-z", "--", .. recent]);
        if (!Completed(ignored)) return Unavailable("check-ignore", ignored.ExitCode);
        var fields = ignored.Output.Split('\0');
        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index + 3 < fields.Length; index += 4)
            rules[fields[index + 3]] = $"{fields[index]}:{fields[index + 1]}:{fields[index + 2]}";

        var entries = recent.Take(20).Select(path =>
            $"{OneLine(path)} ({OneLine(rules.GetValueOrDefault(path, "rule-unavailable"))})");
        var remainder = recent.Length > 20 ? $"; +{recent.Length - 20} more" : string.Empty;
        return new(true,
            "Candidate snapshot left out in-scope files that ignore rules exclude: " +
            string.Join("; ", entries) + remainder +
            ". Change the ignore rule if it is in scope, otherwise report it; the orchestrator does not force-add." +
            Environment.NewLine + DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.InScopeFileIgnored));
    }

    private static GitCli.GitResult RunGit(string worktreePath, string[] args)
    {
        if (args[0] != "check-ignore") return GitCli.Run(worktreePath, args);
        // check-ignore accepts -z only with --stdin; the injectable request carries paths after --.
        return GitCli.RunWithStandardInput(worktreePath, GitCli.DefaultTimeoutMilliseconds,
            string.Join('\0', args.Skip(4)) + '\0', "check-ignore", "-v", "-z", "--stdin");
    }

    private static bool Completed(GitCli.GitResult result) =>
        result.Succeeded && result.ProcessStarted && !result.DrainTimedOut;

    private static InScopeIgnoredFileCheckResult Unavailable(string operation, int exitCode) =>
        new(false, $"In-scope ignored-file check unavailable: operation={operation}; exit_code={exitCode}.");

    private static bool WrittenSinceDispatch(string path, DateTimeOffset dispatchedAt,
        Func<string, DateTimeOffset?> readLastWriteUtc)
    {
        try { return readLastWriteUtc(path) is { } written && written.ToUniversalTime() >= dispatchedAt.ToUniversalTime(); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string OneLine(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n");

    private static IEnumerable<string> ExtractPaths(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var scopeHeading = Array.FindIndex(lines, line => line.StartsWith('#') &&
            line.TrimStart('#').Trim().Equals("Scope", StringComparison.OrdinalIgnoreCase));
        var selected = scopeHeading < 0 ? lines.AsEnumerable() : lines.Skip(scopeHeading + 1)
            .TakeWhile(line => !line.StartsWith('#') && !line.StartsWith("Out of scope", StringComparison.Ordinal));
        foreach (Match match in Regex.Matches(string.Join('\n', selected), @"`([^`]*)`|([^\s`]+)"))
        {
            var path = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
                .Trim('`').TrimEnd('.', ',', ';', ':', ')');
            if (!path.Contains('/') || path.StartsWith('/') || path.Contains(':') ||
                path.IndexOfAny(['*', '?', '[', '\\']) >= 0 || path.Split('/').Contains("..")) continue;
            path = string.Join('/', path.Split('/').Where(segment => segment != "."));
            var name = path[(path.LastIndexOf('/') + 1)..];
            var dot = name.LastIndexOf('.');
            if (dot <= 0 || dot == name.Length - 1 || GitCli.IsOrchestratorInternalArtifactPath(path)) continue;
            yield return path;
        }
    }
}
