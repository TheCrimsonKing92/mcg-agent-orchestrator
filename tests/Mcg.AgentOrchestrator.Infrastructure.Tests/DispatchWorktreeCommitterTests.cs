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
        var subject = DispatchWorktreeCommitter.BuildOrchestratorCommitSubject(
            task,
            "Committed implementation.",
            string.Empty);

        var result = committer.TryCommitWorktreeEdits(
            "memory-worktree",
            subject,
            ["src/Feature.cs"]);

        Xunit.Assert.True(result.Succeeded, result.Diagnostic);
        Xunit.Assert.Equal("Developer task.: Committed implementation.", subject);
        Xunit.Assert.Collection(
            calls,
            arguments => Xunit.Assert.Equal(new[] { "add", "-A", "--", "src/Feature.cs" }, arguments),
            arguments => Xunit.Assert.Equal(new[] { "diff", "--cached", "--name-only" }, arguments),
            arguments => Xunit.Assert.Equal(
                new[] { "diff", "--cached", "--ignore-all-space", "--quiet", "--exit-code", "--" },
                arguments),
            arguments => Xunit.Assert.Equal(
                new[] { "commit", "-m", "Developer task.: Committed implementation." },
                arguments));
    }

    [Xunit.Fact]
    public void DirtyInspectionUsesInjectedGitAndFilesystemSeams()
    {
        var goalId = GoalId.New();
        var branch = GoalWorktrees.BranchName(goalId);
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

    private static GitCli.GitResult Success(string output = "") => new(0, output, string.Empty);
}
