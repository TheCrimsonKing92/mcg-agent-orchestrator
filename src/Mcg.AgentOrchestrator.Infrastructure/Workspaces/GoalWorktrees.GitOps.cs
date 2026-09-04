using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class GoalWorktrees
{
    public static bool HasChangesAgainstMain(string executionDirectory, GoalId goalId)
    {
        var worktree = TryResolve(executionDirectory, goalId);
        if (worktree is null)
        {
            return false;
        }

        var baseBranch = GetCurrentBranchName(executionDirectory) ?? "main";
        var result = GitCli.Run(worktree, "diff", "--name-only", $"{baseBranch}...HEAD");
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output);
    }

    /// <summary>
    /// True when the goal worktree has no uncommitted changes (git status --porcelain is empty).
    /// </summary>
    public static bool IsWorktreeClean(string executionDirectory, GoalId goalId)
    {
        var worktree = TryResolve(executionDirectory, goalId);
        if (worktree is null)
        {
            return false;
        }

        return !GitCli.IsWorktreeDirty(worktree);
    }

    public static string? TryGetBranchDiff(string executionDirectory, GoalId goalId)
    {
        var worktreePath = TryResolve(executionDirectory, goalId);
        if (worktreePath is null)
        {
            return null;
        }

        var statResult = GitCli.Run(worktreePath, "diff", "--stat", "main...HEAD");
        var patchResult = GitCli.Run(worktreePath, "diff", "main...HEAD");

        if (statResult.ExitCode != 0 && patchResult.ExitCode != 0)
        {
            return null;
        }

        var stat = statResult.Output.Trim();
        var patch = patchResult.Output.Trim();

        if (string.IsNullOrWhiteSpace(stat) && string.IsNullOrWhiteSpace(patch))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(stat) ? patch : $"{stat}{Environment.NewLine}---{Environment.NewLine}{patch}";
    }

    public static bool IsBranchMergedIntoCurrent(string executionDirectory, GoalId goalId)
    {
        RequireGitWorkTree(executionDirectory);

        var branch = BranchName(goalId);
        return !BranchExists(executionDirectory, branch) ||
            IsBranchAncestorOfHead(executionDirectory, branch, GitCli.DefaultTimeoutMilliseconds);
    }

    public static bool HasBranch(string executionDirectory, GoalId goalId)
    {
        RequireGitWorkTree(executionDirectory);
        return BranchExists(executionDirectory, BranchName(goalId));
    }

    private static bool IsBranchAncestorOfHead(string executionDirectory, string branch, int timeoutMilliseconds) =>
        GitCli.Run(executionDirectory, timeoutMilliseconds, "merge-base", "--is-ancestor", branch, "HEAD").ExitCode == 0;

    public static GoalWorktreeMergeResult? TryFastForwardMerge(
        string executionDirectory,
        GoalId goalId,
        Func<string?>? mutationBlocker = null)
    {
        var branch = BranchName(goalId);
        if (!BranchExists(executionDirectory, branch))
        {
            return null;
        }

        var changedFiles = ResolveChangedFilesAgainstHead(executionDirectory, goalId);
        if (!changedFiles.Succeeded)
        {
            return new GoalWorktreeMergeResult(
                false,
                branch,
                $"Fast-forward blocked: changed-file determination failed: {changedFiles.FailureReason}",
                null);
        }

        var blockReason = mutationBlocker?.Invoke();
        if (!string.IsNullOrWhiteSpace(blockReason))
        {
            return new GoalWorktreeMergeResult(
                false,
                branch,
                $"Fast-forward blocked before merge: {blockReason}",
                null,
                changedFiles.Files);
        }

        var merge = GitCli.Run(executionDirectory, "merge", "--ff-only", branch);
        if (merge.ExitCode == 0)
        {
            return new GoalWorktreeMergeResult(
                true,
                branch,
                $"Fast-forwarded to {branch}.",
                null,
                changedFiles.Files);
        }

        return new GoalWorktreeMergeResult(
            false,
            branch,
            $"Branch {branch} cannot fast-forward; merge it explicitly after review.",
            $"git merge {branch}",
            changedFiles.Files);
    }

    public static GoalWorktreeChangedFilesResult ResolveChangedFilesAgainstHead(
        string executionDirectory,
        GoalId goalId) =>
        ResolveChangedFilesAgainstHead(
            executionDirectory,
            goalId,
            static (workingDirectory, args) => GitCli.Run(workingDirectory, args));

    internal static GoalWorktreeChangedFilesResult ResolveChangedFilesAgainstHead(
        string executionDirectory,
        GoalId goalId,
        Func<string, string[], GitCli.GitResult> gitRunner)
    {
        ArgumentNullException.ThrowIfNull(gitRunner);
        RequireGitWorkTree(executionDirectory);
        var branch = BranchName(goalId);
        if (!BranchExists(executionDirectory, branch))
        {
            return new GoalWorktreeChangedFilesResult(
                false,
                [],
                $"goal branch '{branch}' does not exist");
        }

        var diff = gitRunner(executionDirectory, ["diff", "--name-only", $"HEAD...{branch}"]);
        if (diff.DrainTimedOut)
        {
            return new GoalWorktreeChangedFilesResult(
                false,
                [],
                "git diff output drain timed out");
        }

        if (!diff.Succeeded)
        {
            return new GoalWorktreeChangedFilesResult(
                false,
                [],
                string.IsNullOrWhiteSpace(diff.Error)
                    ? $"git diff exited {diff.ExitCode}"
                    : diff.Error.Trim());
        }

        var files = diff.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new GoalWorktreeChangedFilesResult(true, files, null);
    }

    public static GoalWorktreeRebaseResult TryRebaseOntoMain(string executionDirectory, GoalId goalId)
    {
        RequireGitWorkTree(executionDirectory);

        var branch = BranchName(goalId);
        var baseBranch = GetCurrentBranchName(executionDirectory) ?? "main";
        if (!BranchExists(executionDirectory, branch))
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.MissingBranch,
                branch,
                $"Goal branch {branch} is missing.",
                [],
                "goal-recovery");
        }

        var worktreePath = TryResolve(executionDirectory, goalId);
        if (worktreePath is null)
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.MissingWorktree,
                branch,
                $"Goal branch {branch} has no registered worktree.",
                [],
                $"workspace create {Prefix(goalId)}");
        }

        if (GitCli.IsWorktreeDirty(worktreePath))
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.DirtyWorktree,
                branch,
                $"Goal worktree for {branch} has uncommitted changes; commit or discard them before rebase recovery.",
                [],
                $"goal-recovery {Prefix(goalId)}");
        }

        if (GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", "HEAD", branch).ExitCode == 0)
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                branch,
                $"Branch {branch} can already fast-forward into main; no rebase needed.",
                [],
                $"acceptance {Prefix(goalId)}");
        }

        // Use the "merge" backend (a real per-commit 3-way merge), NOT "--apply" (the legacy am/patch
        // backend). --apply matches on patch CONTEXT, so it spuriously conflicts when main changed lines
        // NEAR the goal's changes in the same file — even non-overlapping — a base-skew false-conflict
        // that forces an escalation + manual re-dispatch. --merge only conflicts on actually-overlapping
        // hunks. (--no-stat + the RunGitDirect fallback keep the Windows stat-path workaround intact.)
        var rebase = GitCli.Run(worktreePath, "rebase", "--merge", "--no-stat", baseBranch);
        if (IsRebaseStatPathFailure(rebase))
        {
            rebase = RunGitDirect(worktreePath, "rebase", "--merge", "--no-stat", baseBranch);
        }
        if (rebase.ExitCode == 0)
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Rebased,
                branch,
                $"Rebased {branch} onto {baseBranch}; acceptance can now fast-forward after review.",
                [],
                $"acceptance {Prefix(goalId)}");
        }

        var conflictFiles = GetConflictFiles(worktreePath);
        _ = GitCli.Run(worktreePath, "rebase", "--abort");
        if (conflictFiles.Length > 0)
        {
            return new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Conflict,
                branch,
                $"Rebase of {branch} onto {baseBranch} found conflicts; branch was restored to its pre-rebase state.",
                conflictFiles,
                $"Create an operator task to resolve conflicts in order: {string.Join(", ", conflictFiles)}");
        }

        // Report the EXIT CODE and both streams, not just whichever one happened to be non-empty. git writes
        // its "Rebasing (n/m)" progress to stderr, so the old form produced messages like
        // "Rebase of goal/X onto main failed: Rebasing (1/6)" - a step, not a fact. That happened nine times
        // on 2026-08-01 and told the operator nothing: no exit code, no error text, and an EMPTY conflict list
        // (so it had not conflicted - it was unable to complete). Diagnosing it took correlating tick logs by
        // hand. Whatever the cause, the record has to carry enough to tell "conflicted" from "could not run".
        // Collapse CR/LF before splicing into a single-line record. git separates "Rebasing (n/m)" from what
        // follows with a BARE CARRIAGE RETURN, so the fatal text landed after a \r and every downstream
        // single-line renderer swallowed it - the operator saw "...failed: Rebasing (1/3)" and not
        // "Committer identity unknown ... fatal: unable to auto-detect email address". The full text was in
        // the record all along; only the rendering lost it, which is the worst way to lose evidence because
        // the record looks complete.
        var stdErr = CollapseControlWhitespace(rebase.Error);
        var stdOut = CollapseControlWhitespace(rebase.Output);
        var streams = string.Join(
            "; ",
            new[]
            {
                string.IsNullOrWhiteSpace(stdErr) ? null : $"stderr={stdErr}",
                string.IsNullOrWhiteSpace(stdOut) ? null : $"stdout={stdOut}",
            }.Where(part => part is not null));
        var detail = string.IsNullOrWhiteSpace(streams)
            ? $"exit={rebase.ExitCode}; no output captured"
            : $"exit={rebase.ExitCode}; {streams}";
        return new GoalWorktreeRebaseResult(
            GoalWorktreeRebaseStatus.Failed,
            branch,
            $"Rebase of {branch} onto {baseBranch} failed with no conflicting paths, so it could not complete rather than conflicting ({detail}); branch was restored to its pre-rebase state.",
            [],
            $"goal-recovery {Prefix(goalId)}");
    }

    private static string CollapseControlWhitespace(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(
                " | ",
                value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool BranchExists(string executionDirectory, string branch)
    {
        return BranchExists(executionDirectory, branch, GitCli.DefaultTimeoutMilliseconds);
    }

    private static bool BranchExists(string executionDirectory, string branch, int gitTimeoutMilliseconds)
    {
        return GitCli.Run(executionDirectory, gitTimeoutMilliseconds, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;
    }


    private static string? GetCurrentBranchName(string executionDirectory)
    {
        var result = GitCli.Run(executionDirectory, "branch", "--show-current");
        var branch = result.Output.Trim();
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(branch) ? branch : null;
    }

    private static string[] GetConflictFiles(string worktreePath)
    {
        var result = GitCli.Run(worktreePath, "diff", "--name-only", "--diff-filter=U");
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return [];
        }

        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsRegisteredWorktree(string executionDirectory, string path)
    {
        return IsRegisteredWorktree(executionDirectory, path, GitCli.DefaultTimeoutMilliseconds);
    }

    private static bool IsRegisteredWorktree(string executionDirectory, string path, int gitTimeoutMilliseconds)
    {
        return RegisteredWorktreePaths(executionDirectory, gitTimeoutMilliseconds).Contains(NormalizePath(path));
    }


    private static bool IsRebaseStatPathFailure(GitCli.GitResult result)
    {
        if (result.ExitCode == 0)
        {
            return false;
        }

        var message = $"{result.Output}{Environment.NewLine}{result.Error}";
        return message.Contains("failed to stat", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("...", StringComparison.Ordinal);
    }

    private static GitCli.GitResult RunGitDirect(string workingDirectory, params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("core.longpaths=true");
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new GitCli.GitResult(1, string.Empty, "failed to start git process");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(GitCli.DefaultTimeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new GitCli.GitResult(-1, string.Empty, $"git {string.Join(' ', args)} timed out after {GitCli.DefaultTimeoutMilliseconds}ms");
            }

            Task.WaitAll([outputTask, errorTask], 5_000);
            var output = outputTask.Status == TaskStatus.RanToCompletion ? outputTask.Result : string.Empty;
            var error = errorTask.Status == TaskStatus.RanToCompletion ? errorTask.Result : string.Empty;
            return new GitCli.GitResult(process.ExitCode, output, error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new GitCli.GitResult(1, string.Empty, ex.Message);
        }
    }

    private static HashSet<string> RegisteredWorktreePaths(string executionDirectory)
    {
        return RegisteredWorktreePaths(executionDirectory, GitCli.DefaultTimeoutMilliseconds);
    }

    private static HashSet<string> RegisteredWorktreePaths(string executionDirectory, int gitTimeoutMilliseconds)
    {
        var result = GitCli.Run(executionDirectory, gitTimeoutMilliseconds, "worktree", "list", "--porcelain");
        if (result.ExitCode != 0)
        {
            return [];
        }

        return result.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => NormalizePath(line["worktree ".Length..]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when <paramref name="executionDirectory"/> is inside a git work tree.
    /// Lets callers decide whether deterministic worktree chorekeeping is possible before
    /// attempting it (e.g. dispatch auto-create falls back to the execution directory otherwise).
    /// </summary>
    public static bool IsGitWorkTree(string executionDirectory)
    {
        return IsGitWorkTree(executionDirectory, GitCli.DefaultTimeoutMilliseconds);
    }

    private static bool IsGitWorkTree(string executionDirectory, int gitTimeoutMilliseconds)
    {
        var result = GitCli.Run(executionDirectory, gitTimeoutMilliseconds, "rev-parse", "--is-inside-work-tree");
        return result.ExitCode == 0 && result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireGitWorkTree(string executionDirectory)
    {
        RequireGitWorkTree(executionDirectory, GitCli.DefaultTimeoutMilliseconds);
    }

    private static void RequireGitWorkTree(string executionDirectory, int gitTimeoutMilliseconds)
    {
        // Carry the probe evidence into the diagnostic. A bare "not a work tree" message cannot
        // distinguish a genuinely non-git directory from a probe that exited 0 with empty output,
        // timed out, or never started - and those have different causes and different fixes.
        var result = GitCli.Run(executionDirectory, gitTimeoutMilliseconds, "rev-parse", "--is-inside-work-tree");
        if (result.ExitCode == 0 && result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var stdout = result.Output ?? string.Empty;
        var shownStdout = stdout.Trim();
        if (shownStdout.Length > 60)
        {
            shownStdout = shownStdout[..60];
        }

        var stderr = (result.Error ?? string.Empty).Trim();
        if (stderr.Length > 200)
        {
            stderr = stderr[..200];
        }

        throw new InvalidOperationException(
            $"Goal workspaces require '{executionDirectory}' to be inside a git work tree. " +
            $"probe='git rev-parse --is-inside-work-tree'; exit={result.ExitCode}; " +
            $"processStarted={result.ProcessStarted}; drainTimedOut={result.DrainTimedOut}; " +
            $"stdoutBytes={stdout.Length}; stdout='{shownStdout}'; stderr='{stderr}'; " +
            $"directoryExists={Directory.Exists(executionDirectory)}");
    }

}
