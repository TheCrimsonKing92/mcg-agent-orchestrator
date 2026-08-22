using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class DispatchWorktreeCommitter
{
    private readonly Func<string, string[], GitCli.GitResult> _runGit;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Action? _beforeWorktreeInspection;
    private readonly ConcurrentDictionary<WorktreeInspectionCacheKey, GoalWorktreeInspectionResult> _worktreeInspectionCache = [];

    internal DispatchWorktreeCommitter(
        Func<string, string[], GitCli.GitResult>? runGit = null,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? fileExists = null,
        Action? beforeWorktreeInspection = null)
    {
        _runGit = runGit ?? ((workingDirectory, arguments) => GitCli.Run(workingDirectory, arguments));
        _directoryExists = directoryExists ?? Directory.Exists;
        _fileExists = fileExists ?? File.Exists;
        _beforeWorktreeInspection = beforeWorktreeInspection;
    }

    internal void BeginRefreshCycle() => _worktreeInspectionCache.Clear();

    // Commits the worker's uncommitted worktree edits from the orchestrator after verification guards
    // pass. The dirty path list is filtered from git status so generated/noise paths are not absorbed
    // into the recovery commit.
    internal CommitWorktreeEditsResult TryCommitWorktreeEdits(
        string workingDirectory,
        string subject,
        IReadOnlyList<string> dirtyPaths)
    {
        try
        {
            if (dirtyPaths.Count == 0)
            {
                return CommitWorktreeEditsResult.Failed("Orchestrator commit-on-behalf skipped: no commit-worthy dirty paths.");
            }

            var addArgs = new List<string>(dirtyPaths.Count + 3) { "add", "-A", "--" };
            addArgs.AddRange(dirtyPaths);
            var add = _runGit(workingDirectory, addArgs.ToArray());
            if (!add.Succeeded)
            {
                return CommitWorktreeEditsResult.FromGitFailure("add", addArgs, add);
            }

            var staged = _runGit(workingDirectory, ["diff", "--cached", "--name-only"]);
            if (staged.ExitCode != 0 || string.IsNullOrWhiteSpace(staged.Output))
            {
                // Nothing to commit (e.g. only the excluded sandbox scratch was dirty) — leave the
                // dispatch to fail/report rather than create an empty commit.
                return staged.ExitCode == 0
                    ? CommitWorktreeEditsResult.Failed("Orchestrator commit-on-behalf found no staged changes after git add.")
                    : CommitWorktreeEditsResult.FromGitFailure("diff", ["diff", "--cached", "--name-only"], staged);
            }

            var substantive = _runGit(
                workingDirectory,
                ["diff", "--cached", "--ignore-all-space", "--quiet", "--exit-code", "--"]);
            if (substantive.ExitCode == 0)
            {
                return CommitWorktreeEditsResult.Failed("Orchestrator commit-on-behalf found no substantive staged changes after ignoring whitespace.");
            }

            if (substantive.ExitCode != 1)
            {
                return CommitWorktreeEditsResult.FromGitFailure(
                    "diff",
                    ["diff", "--cached", "--ignore-all-space", "--quiet", "--exit-code", "--"],
                    substantive);
            }

            var commitArgs = new[] { "commit", "-m", subject };
            var commit = _runGit(workingDirectory, commitArgs);
            return commit.Succeeded
                ? CommitWorktreeEditsResult.Success
                : CommitWorktreeEditsResult.FromGitFailure("commit", commitArgs, commit);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return CommitWorktreeEditsResult.Failed(
                $"Orchestrator commit-on-behalf failed with {ex.GetType().Name}: {NormalizeDiagnosticText(ex.Message)}");
        }
    }

    internal static string BuildOrchestratorCommitSubject(TaskSpec task, string standardOutput, string standardError)
    {
        var title = NormalizeCommitSubjectPart(task.Description);
        var summary = ExtractWorkerSummary(standardOutput, standardError);
        var subject = string.IsNullOrWhiteSpace(summary)
            ? title
            : $"{title}: {summary}";
        return TruncateCommitSubject(subject);
    }

    private static string ExtractWorkerSummary(string standardOutput, string standardError)
    {
        if (WorkerResultParser.TryParseFields($"{standardOutput}\n{standardError}", out var fields, out _) &&
            fields.TryGetValue("summary", out var summary))
        {
            return NormalizeCommitSubjectPart(summary);
        }

        foreach (var line in $"{standardOutput}\n{standardError}".Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                WorkerResultParser.IsOpener(trimmed) ||
                WorkerResultParser.IsEndMarker(trimmed) ||
                trimmed.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            return NormalizeCommitSubjectPart(trimmed);
        }

        return string.Empty;
    }

    private static string NormalizeCommitSubjectPart(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private static string TruncateCommitSubject(string subject)
    {
        const int MaxSubjectLength = 72;
        subject = NormalizeCommitSubjectPart(subject);
        return subject.Length <= MaxSubjectLength
            ? subject
            : subject[..MaxSubjectLength].TrimEnd();
    }

    internal static string BuildCommitOnBehalfFailureDiagnostic(
        string gitFailureDiagnostic,
        GoalWorktreeDispatchEvidence worktreeEvidence)
    {
        return gitFailureDiagnostic + " " +
            "Developer/Tester dispatch exited 0 but left the worktree dirty. " +
            "Commit-on-behalf failure is retryable; worktree preserved. " +
            "operator_action=inspect the preserved worktree, resolve the named git failure, then rerun refresh-dispatch for this task; " +
            $"branch={worktreeEvidence.Branch}; head={worktreeEvidence.Head}; worktree={worktreeEvidence.WorktreeStatus}; " +
            $"commits_after_dispatch={worktreeEvidence.CommitsAfterDispatch}; status_short={worktreeEvidence.StatusShort}.";
    }

    internal GoalWorktreeInspectionResult InspectGoalWorktree(
        string workingDirectory,
        GoalId goalId,
        DateTimeOffset dispatchedAt,
        bool forceRefresh = false)
    {
        var key = new WorktreeInspectionCacheKey(workingDirectory, goalId, dispatchedAt);
        if (!forceRefresh && _worktreeInspectionCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        _beforeWorktreeInspection?.Invoke();
        var result = InspectGoalWorktreeCore(workingDirectory, goalId, dispatchedAt);
        _worktreeInspectionCache[key] = result;
        return result;
    }

    internal bool TryInspectGoalWorktree(
        string workingDirectory,
        GoalId goalId,
        DateTimeOffset dispatchedAt,
        out GoalWorktreeDispatchEvidence evidence,
        bool forceRefresh = false)
    {
        var inspection = InspectGoalWorktree(workingDirectory, goalId, dispatchedAt, forceRefresh);
        evidence = inspection.Evidence;
        return inspection.IsAvailable;
    }

    private GoalWorktreeInspectionResult InspectGoalWorktreeCore(
        string workingDirectory,
        GoalId goalId,
        DateTimeOffset dispatchedAt)
    {
        if (!_directoryExists(workingDirectory))
        {
            return GoalWorktreeInspectionResult.Unavailable("directory-missing", "git-not-run");
        }

        if (!_fileExists(Path.Combine(workingDirectory, ".git")))
        {
            return GoalWorktreeInspectionResult.Unavailable("git-metadata-missing", "git-not-run");
        }

        var branch = _runGit(workingDirectory, ["branch", "--show-current"]);
        var expectedBranch = GoalWorktrees.BranchName(goalId);
        if (branch.ExitCode != 0)
        {
            return GoalWorktreeInspectionResult.Unavailable(
                "branch-inspection-failed",
                BuildGitInspectionReceipt("branch", branch));
        }

        if (!string.Equals(branch.Output.Trim(), expectedBranch, StringComparison.Ordinal))
        {
            return GoalWorktreeInspectionResult.Unsafe(
                "branch-mismatch",
                $"expected={expectedBranch}; actual={NormalizeDiagnosticText(branch.Output)}");
        }

        var head = _runGit(workingDirectory, ["rev-parse", "--short", "HEAD"]);
        var status = _runGit(workingDirectory, ["status", "--short", "--untracked-files=all"]);
        var dispatch = _runGit(workingDirectory, ["log", "--format=%H", $"--since={dispatchedAt:O}"]);
        var changedPaths = _runGit(workingDirectory, ["log", "--name-only", "--format=", $"--since={dispatchedAt:O}"]);
        var failedGitOperation = new[]
        {
            (Name: "head", Result: head),
            (Name: "status", Result: status),
            (Name: "dispatch-log", Result: dispatch),
            (Name: "changed-paths", Result: changedPaths)
        }.FirstOrDefault(item => !item.Result.Succeeded || item.Result.DrainTimedOut);
        if (failedGitOperation.Name is not null)
        {
            return GoalWorktreeInspectionResult.Unavailable(
                "git-inspection-failed",
                BuildGitInspectionReceipt(failedGitOperation.Name, failedGitOperation.Result));
        }

        var commitsAfterDispatch = dispatch.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Length;
        var pathsChangedAfterDispatch = changedPaths.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var filteredStatusOutput = GitCli.FilterCommitWorthyStatus(status.Output);
        var evidence = new GoalWorktreeDispatchEvidence(
            branch.Output.Trim(),
            head.Output.Trim(),
            string.IsNullOrWhiteSpace(filteredStatusOutput),
            string.IsNullOrWhiteSpace(filteredStatusOutput) ? "clean" : "dirty",
            FormatStatusShort(new GitCli.GitResult(status.ExitCode, filteredStatusOutput, string.Empty)),
            commitsAfterDispatch,
            pathsChangedAfterDispatch,
            GitCli.ParseCommitWorthyStatusPaths(filteredStatusOutput));
        return GoalWorktreeInspectionResult.Available(evidence);
    }

    private static string BuildGitInspectionReceipt(string operation, GitCli.GitResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        var normalizedDetail = NormalizeDiagnosticText(detail);
        return $"operation={operation}; exit_code={result.ExitCode}; drain_timed_out={result.DrainTimedOut.ToString().ToLowerInvariant()}; " +
            $"detail={normalizedDetail[..Math.Min(normalizedDetail.Length, 256)]}";
    }

    private sealed record WorktreeInspectionCacheKey(
        string WorkingDirectory,
        GoalId GoalId,
        DateTimeOffset DispatchedAt);

    internal static string FormatChangedPaths(IReadOnlyList<string> changedPaths)
    {
        if (changedPaths.Count == 0)
        {
            return "none";
        }

        var entries = changedPaths.Take(8).ToArray();
        return string.Join(" | ", entries);
    }

    private static string FormatStatusShort(GitCli.GitResult status)
    {
        if (status.ExitCode != 0)
        {
            return "unavailable";
        }

        var entries = status.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .ToArray();
        return entries.Length == 0
            ? "clean"
            : string.Join(" | ", entries);
    }

    internal static string NormalizeDiagnosticText(string value)
    {
        var normalized = string.Join(
            " ",
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "none" : normalized;
    }
}

internal sealed record GoalWorktreeInspectionResult(
    bool IsAvailable,
    bool IsUnsafe,
    GoalWorktreeDispatchEvidence Evidence,
    string? UnavailableReason,
    string GitReceipt)
{
    public static GoalWorktreeInspectionResult Available(GoalWorktreeDispatchEvidence evidence) =>
        new(true, false, evidence, null, "git-inspection-succeeded");

    public static GoalWorktreeInspectionResult Unsafe(string reason, string gitReceipt) =>
        new(false, true, GoalWorktreeDispatchEvidence.Unknown, reason, gitReceipt);

    public static GoalWorktreeInspectionResult Unavailable(string reason, string gitReceipt) =>
        new(false, false, GoalWorktreeDispatchEvidence.Unknown, reason, gitReceipt);
}

internal sealed record GoalWorktreeDispatchEvidence(
    string Branch,
    string Head,
    bool IsClean,
    string WorktreeStatus,
    string StatusShort,
    int CommitsAfterDispatch,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<string> DirtyPaths)
{
    public bool HasCommitAfterDispatch => CommitsAfterDispatch > 0;
    public bool HasRelevantCommitAfterDispatch => ChangedPaths.Any(path => !GitCli.IsOrchestratorInternalArtifactPath(path));
    public string ChangedPathsSummary => DispatchWorktreeCommitter.FormatChangedPaths(ChangedPaths);

    public static GoalWorktreeDispatchEvidence Unknown { get; } = new("unknown", "unknown", false, "unknown", "unavailable", 0, [], []);
}

internal readonly record struct CommitWorktreeEditsResult(bool Succeeded, string Diagnostic)
{
    public static CommitWorktreeEditsResult Success { get; } = new(true, string.Empty);

    public static CommitWorktreeEditsResult Failed(string diagnostic) => new(false, diagnostic);

    public static CommitWorktreeEditsResult FromGitFailure(
        string operation,
        IReadOnlyList<string> arguments,
        GitCli.GitResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error)
            ? result.Output
            : result.Error;
        return Failed(
            "Orchestrator commit-on-behalf git command failed. " +
            $"operation={operation}; command=git {FormatGitArguments(arguments)}; exit_code={result.ExitCode}; " +
            $"error={DispatchWorktreeCommitter.NormalizeDiagnosticText(detail)}.");
    }

    private static string FormatGitArguments(IReadOnlyList<string> arguments)
    {
        return string.Join(
            ' ',
            arguments.Select(argument => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument));
    }
}
