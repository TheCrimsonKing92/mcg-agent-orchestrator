using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DispatchWorktreeCommitterTests
{
    [Xunit.Fact]
    public void DirtyPathsCommitUsesExactSubjectAndArgumentOrder()
    {
        var calls = new List<string[]>();
        var committer = new DispatchWorktreeCommitter(
            runGit: (_, arguments) =>
            {
                calls.Add(arguments);
                return arguments[0] switch
                {
                    "add" => Success(),
                    "diff" when arguments.Contains("--name-only", StringComparer.Ordinal) =>
                        Success("src/Feature.cs\n"),
                    "diff" => new GitCli.GitResult(1, string.Empty, string.Empty),
                    "commit" => Success(),
                    _ => throw new InvalidOperationException($"Unexpected git command: {string.Join(' ', arguments)}")
                };
            },
            directoryExists: _ => throw new InvalidOperationException("commit must not inspect the filesystem"),
            fileExists: _ => throw new InvalidOperationException("commit must not inspect the filesystem"));
        var task = new TaskSpec(TaskId.New(), "Developer task.", AgentRole.Developer);
        var goal = new Goal(new GoalId("0a09cd85d2974c208a8117d15f3cb2f8"), "Readable commit subjects", [task]);
        var message = DispatchWorktreeCommitter.BuildOrchestratorCommitMessage(
            goal,
            task,
            "dispatch-1",
            "Committed implementation.",
            string.Empty,
            ["src/Feature.cs"]);

        var result = committer.TryCommitWorktreeEdits(
            "memory-worktree",
            message,
            ["src/Feature.cs"]);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Equal("Developer(0a09cd85): Readable commit subjects", message.Subject);
        Xunit.Assert.Collection(
            calls,
            arguments => Xunit.Assert.Equal(new[] { "add", "-A", "--", "src/Feature.cs" }, arguments),
            arguments => Xunit.Assert.Equal(new[] { "diff", "--cached", "--name-only" }, arguments),
            arguments => Xunit.Assert.Equal(
                new[] { "diff", "--cached", "--ignore-all-space", "--quiet", "--exit-code", "--" },
                arguments),
            arguments => Xunit.Assert.Equal(
                new[] { "commit", "-m", "Developer(0a09cd85): Readable commit subjects", "-m", message.Body },
                arguments));
    }

    [Xunit.Fact]
    public void DirtyInspectionUsesInjectedGitAndFilesystemSeams()
    {
        var goalId = GoalId.New();
        var branch = GoalWorktreeLayout.BranchName(goalId);
        var calls = new List<string[]>();
        var committer = new DispatchWorktreeCommitter(
            runGit: (_, arguments) =>
            {
                calls.Add(arguments);
                return string.Join(' ', arguments) switch
                {
                    "branch --show-current" => Success(branch),
                    "rev-parse HEAD" => Success("abc1234567890abc1234567890abc1234567890abc"),
                    "status --short --untracked-files=all" => Success(" M src/Feature.cs\n"),
                    var command when command.StartsWith("log --format=%H --since=", StringComparison.Ordinal) =>
                        Success(),
                    var command when command.StartsWith("log --name-only --format= --since=", StringComparison.Ordinal) =>
                        Success(),
                    var command => throw new InvalidOperationException($"Unexpected git command: {command}")
                };
            },
            directoryExists: path => path == "memory-worktree",
            fileExists: path => path == Path.Combine("memory-worktree", ".git"));

        var inspection = committer.InspectGoalWorktree(
            "memory-worktree",
            goalId,
            DateTimeOffset.Parse("2026-08-22T12:00:00Z"));

        Xunit.Assert.True(inspection.IsAvailable, inspection.GitReceipt);
        Xunit.Assert.Equal("abc1234567890abc1234567890abc1234567890abc", inspection.Evidence.Head);
        Xunit.Assert.False(inspection.Evidence.IsClean);
        Xunit.Assert.Equal("dirty", inspection.Evidence.WorktreeStatus);
        Xunit.Assert.Equal("M src/Feature.cs", inspection.Evidence.StatusShort);
        Xunit.Assert.Equal(new[] { "src/Feature.cs" }, inspection.Evidence.DirtyPaths);
        Xunit.Assert.Equal(5, calls.Count);
    }

    [Xunit.Fact]
    public void CommitFailurePreservesExactDiagnostic()
    {
        var committer = new DispatchWorktreeCommitter(
            runGit: (_, arguments) => arguments[0] switch
            {
                "add" => Success(),
                "diff" when arguments.Contains("--name-only", StringComparer.Ordinal) =>
                    Success("src/Feature.cs\n"),
                "diff" => new GitCli.GitResult(1, string.Empty, string.Empty),
                "commit" => new GitCli.GitResult(42, string.Empty, "blocked residual commit"),
                _ => throw new InvalidOperationException($"Unexpected git command: {string.Join(' ', arguments)}")
            },
            directoryExists: _ => throw new InvalidOperationException("commit must not inspect the filesystem"),
            fileExists: _ => throw new InvalidOperationException("commit must not inspect the filesystem"));

        var result = committer.TryCommitWorktreeEdits(
            "memory-worktree",
            "Developer task.: Committed implementation.",
            ["src/Feature.cs"]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Equal(
            "Orchestrator commit-on-behalf git command failed. operation=commit; " +
            "command=git commit -m \"Developer task.: Committed implementation.\"; " +
            "exit_code=42; error=blocked residual commit.",
            result.Diagnostic);
    }

    [Xunit.Fact]
    public void FailedAddFallsBackToUnstagedPathsOnly()
    {
        string[] dirtyPaths = ["src/Widget.cs", "src/Removed.cs", "src/New.cs", "src/Old.cs",
            "docs/a.md", "src/Edited.cs", "src/Added.cs"];
        var calls = new List<string[]>();
        string? pathspecFile = null;
        var committer = new DispatchWorktreeCommitter(runGit: (_, arguments) =>
        {
            calls.Add(arguments);
            if (arguments[0] == "add" && arguments[2] != "--")
            {
                pathspecFile = arguments[2]["--pathspec-from-file=".Length..];
                Xunit.Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetDirectoryName(pathspecFile));
                Xunit.Assert.Equal(
                    System.Text.Encoding.UTF8.GetBytes("src/Added.cs\0src/Edited.cs\0src/Widget.cs\0"),
                    File.ReadAllBytes(pathspecFile));
                return Success();
            }

            return arguments[0] switch
            {
                "add" => new GitCli.GitResult(128, "", "fatal: pathspec 'src/Removed.cs' did not match any files"),
                "status" => Success("UU src/Widget.cs\0D  src/Removed.cs\0R  src/New.cs\0src/Old.cs\0" +
                    "M  docs/a.md\0 M src/Edited.cs\0?? src/Added.cs\0"),
                "diff" when arguments.Contains("--name-only", StringComparer.Ordinal) => Success("src/Widget.cs\n"),
                "diff" => new GitCli.GitResult(1, "", ""),
                "commit" => Success(),
                _ => throw new InvalidOperationException($"Unexpected git command: {string.Join(' ', arguments)}")
            };
        });

        var result = committer.TryCommitWorktreeEdits("memory-worktree", "Resolved integration", dirtyPaths);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.NotNull(pathspecFile);
        Xunit.Assert.False(File.Exists(pathspecFile));
        Xunit.Assert.Collection(calls,
            args => Xunit.Assert.Equal(new[] { "add", "-A", "--" }.Concat(dirtyPaths), args),
            args => Xunit.Assert.Equal(new[] { "status", "--porcelain=v1", "-z", "--untracked-files=all" }, args),
            args => Xunit.Assert.Equal(new[] { "add", "-A", $"--pathspec-from-file={pathspecFile}", "--pathspec-file-nul" }, args),
            args => Xunit.Assert.Equal(new[] { "diff", "--cached", "--name-only" }, args),
            args => Xunit.Assert.Equal(new[] { "diff", "--cached", "--ignore-all-space", "--quiet", "--exit-code", "--" }, args),
            args => Xunit.Assert.Equal(new[] { "commit", "-m", "Resolved integration" }, args));
    }

    [Xunit.Fact]
    public void FallbackWithEveryPathAlreadyStagedCommitsWithoutSecondAdd()
    {
        var calls = new List<string[]>();
        var committer = new DispatchWorktreeCommitter(runGit: (_, arguments) =>
        {
            calls.Add(arguments);
            return arguments[0] switch
            {
                // Succeeded checks only ExitCode; an unstarted process must still enter fallback.
                "add" => new GitCli.GitResult(0, "", "not started", ProcessStarted: false),
                "status" => Success("D  removed.txt\0R  new.txt\0old.txt\0M  staged.txt\0"),
                "diff" when arguments.Contains("--name-only", StringComparer.Ordinal) => Success("removed.txt\n"),
                "diff" => new GitCli.GitResult(1, "", ""),
                "commit" => Success(),
                _ => throw new InvalidOperationException($"Unexpected git command: {string.Join(' ', arguments)}")
            };
        });

        var result = committer.TryCommitWorktreeEdits("memory-worktree", "Staged edits",
            ["removed.txt", "new.txt", "old.txt", "staged.txt"]);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Collection(calls,
            args => Xunit.Assert.Equal(new[] { "add", "-A", "--", "removed.txt", "new.txt", "old.txt", "staged.txt" }, args),
            args => Xunit.Assert.Equal(new[] { "status", "--porcelain=v1", "-z", "--untracked-files=all" }, args),
            args => Xunit.Assert.Equal(new[] { "diff", "--cached", "--name-only" }, args),
            args => Xunit.Assert.Equal(new[] { "diff", "--cached", "--ignore-all-space", "--quiet", "--exit-code", "--" }, args),
            args => Xunit.Assert.Equal(new[] { "commit", "-m", "Staged edits" }, args));
    }

    [Xunit.Fact]
    public void FallbackAddFailureReturnsAddDiagnosticWithoutCommit()
    {
        var calls = new List<string[]>();
        string? pathspecFile = null;
        var committer = new DispatchWorktreeCommitter(runGit: (_, arguments) =>
        {
            calls.Add(arguments);
            if (arguments[0] == "status") return Success(" M edited.txt\0");
            Xunit.Assert.Equal("add", arguments[0]);
            if (arguments[2] == "--") return new GitCli.GitResult(128, "", "initial add refused");
            pathspecFile = arguments[2]["--pathspec-from-file=".Length..];
            Xunit.Assert.Equal("edited.txt\0", File.ReadAllText(pathspecFile));
            return new GitCli.GitResult(1, "", "error: fallback refused");
        });

        var result = committer.TryCommitWorktreeEdits("memory-worktree", "Edits", ["edited.txt"]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.StartsWith("Orchestrator commit-on-behalf git command failed. operation=add;", result.Diagnostic);
        Xunit.Assert.Contains("--pathspec-from-file", result.Diagnostic);
        Xunit.Assert.Contains("error: fallback refused", result.Diagnostic);
        Xunit.Assert.Equal(new[] { "add", "status", "add" }, calls.Select(args => args[0]));
        Xunit.Assert.NotNull(pathspecFile);
        Xunit.Assert.False(File.Exists(pathspecFile));
    }

    [Xunit.Theory]
    [Xunit.InlineData(1, true, false)]
    [Xunit.InlineData(0, false, false)]
    [Xunit.InlineData(0, true, true)]
    public void FallbackStatusFailurePreventsStagingAndCommit(int exitCode, bool started, bool drainTimedOut)
    {
        var calls = new List<string>();
        var committer = new DispatchWorktreeCommitter(runGit: (_, args) =>
        {
            calls.Add(args[0]);
            return args[0] switch
            {
                "add" => new GitCli.GitResult(128, "", "initial add refused"),
                "status" => new GitCli.GitResult(exitCode, " M edited.txt\0", "status unavailable", drainTimedOut, started),
                _ => throw new InvalidOperationException("No git command is allowed after unavailable status")
            };
        });

        var result = committer.TryCommitWorktreeEdits("memory-worktree", "Edits", ["edited.txt"]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.StartsWith("Orchestrator commit-on-behalf git command failed. operation=status;", result.Diagnostic);
        Xunit.Assert.Contains("status unavailable", result.Diagnostic);
        Xunit.Assert.Equal(new[] { "add", "status" }, calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData(" M edited.txt")]
    [Xunit.InlineData("R  renamed.txt\0")]
    [Xunit.InlineData("C  copied.txt\0\0")]
    public void FallbackRejectsIncompleteStatus(string status)
    {
        var calls = new List<string>();
        var committer = new DispatchWorktreeCommitter(runGit: (_, args) =>
        {
            calls.Add(args[0]);
            return args[0] switch
            {
                "add" => new GitCli.GitResult(128, "", "initial add refused"),
                "status" => Success(status),
                _ => throw new InvalidOperationException("No git command is allowed after malformed status")
            };
        });

        var result = committer.TryCommitWorktreeEdits("memory-worktree", "Edits", ["edited.txt"]);

        Xunit.Assert.False(result.Succeeded);
        Xunit.Assert.Contains("Incomplete", result.Diagnostic);
        Xunit.Assert.Equal(new[] { "add", "status" }, calls);
    }

    [Xunit.Fact]
    public void FallbackPreservesNamesAndFiltersDirtyDestinationsAcrossStatusCodes()
    {
        string[] unmergedCodes = ["DD", "AU", "UD", "UA", "DU", "AA", "UU"];
        string[] dirtyPaths = [.. unmergedCodes.Select(code => code + ".txt"),
            " renamed\nfile.txt ", "copy.txt", "source.txt", "copy-source.txt", "untracked.txt"];
        var status = string.Concat(unmergedCodes.Select(code => $"{code} {code}.txt\0")) +
            "RM  renamed\nfile.txt \0source.txt\0 C copy.txt\0copy-source.txt\0" +
            "?? untracked.txt\0 M outside-dirty-set.txt\0?? untracked.txt\0";
        string? pathspecFile = null;
        var committer = new DispatchWorktreeCommitter(runGit: (_, args) =>
        {
            if (args[0] == "add" && args[2] != "--")
            {
                pathspecFile = args[2]["--pathspec-from-file=".Length..];
                Xunit.Assert.Equal(" renamed\nfile.txt \0AA.txt\0AU.txt\0DD.txt\0DU.txt\0UA.txt\0UD.txt\0UU.txt\0copy.txt\0untracked.txt\0",
                    File.ReadAllText(pathspecFile));
                return Success();
            }

            return args[0] switch
            {
                "add" => new GitCli.GitResult(128, "", "initial add refused"),
                "status" => Success(status),
                "diff" when args.Contains("--name-only", StringComparer.Ordinal) => Success("AA.txt\n"),
                "diff" => new GitCli.GitResult(1, "", ""),
                "commit" => Success(),
                _ => throw new InvalidOperationException($"Unexpected git command: {string.Join(' ', args)}")
            };
        });

        var result = committer.TryCommitWorktreeEdits("memory-worktree", "Edits", dirtyPaths);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.NotNull(pathspecFile);
        Xunit.Assert.False(File.Exists(pathspecFile));
    }

    private static GitCli.GitResult Success(string output = "") => new(0, output, string.Empty);
}
