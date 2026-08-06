using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("ProcessSpawning")]
public sealed class GitCliTests
{
    [Xunit.Fact(DisplayName = "GitCli_Run_succeeds_for_valid_git_command")]
    public void GitCliRunSucceedsForValidGitCommand()
    {
        var repo = CreateSeededRepository();
        try
        {
            var result = GitCli.Run(repo, "rev-parse", "HEAD");

            Assert.True(result.Succeeded);
            Assert.Equal(0, result.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(result.Output));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GitCli_Run_returns_nonzero_exit_code_for_failing_command")]
    public void GitCliRunReturnsNonzeroExitCodeForFailingCommand()
    {
        var repo = CreateSeededRepository();
        try
        {
            var result = GitCli.Run(repo, "rev-parse", "--verify", "refs/heads/branch-that-does-not-exist");

            Assert.False(result.Succeeded);
            Assert.True(result.ExitCode != 0);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GitCli_Run_captures_stdout_output")]
    public void GitCliRunCapturesStdoutOutput()
    {
        var repo = CreateSeededRepository();
        try
        {
            var result = GitCli.Run(repo, "log", "--oneline", "-1");

            Assert.True(result.Succeeded);
            Assert.True(result.Output.Contains("Seed", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GitCli_Run_with_explicit_timeout_succeeds_for_fast_command")]
    public void GitCliRunWithExplicitTimeoutSucceedsForFastCommand()
    {
        var repo = CreateSeededRepository();
        try
        {
            var result = GitCli.Run(repo, 5_000, "rev-parse", "HEAD");

            Assert.True(result.Succeeded);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GitCli_IsWorktreeDirty_returns_false_for_clean_worktree")]
    public void GitCliIsWorktreeDirtyReturnsFalseForCleanWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            Assert.False(GitCli.IsWorktreeDirty(repo));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GitCli_IsWorktreeDirty_returns_true_when_untracked_file_present")]
    public void GitCliIsWorktreeDirtyReturnsTrueWhenUntrackedFilePresent()
    {
        var repo = CreateSeededRepository();
        try
        {
            File.WriteAllText(Path.Combine(repo, "untracked.txt"), "new content");

            Assert.True(GitCli.IsWorktreeDirty(repo));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GitCli_IsWorktreeDirty_ignores_orchestrator_owned_context_artifacts")]
    public void GitCliIsWorktreeDirtyIgnoresOrchestratorOwnedContextArtifacts()
    {
        var repo = CreateSeededRepository();
        try
        {
            File.WriteAllText(Path.Combine(repo, ".orchestrator-handoff.md"), "durable handoff");
            var contextDirectory = Path.Combine(repo, ".orchestrator-context", "goal");
            Directory.CreateDirectory(contextDirectory);
            File.WriteAllText(Path.Combine(contextDirectory, "planner-plan.md"), "durable plan");

            Assert.False(GitCli.IsWorktreeDirty(repo));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GitCli_IsWorktreeDirty_ignores_exact_legacy_PowerShell_module_cache")]
    public void GitCliIsWorktreeDirtyIgnoresExactLegacyPowerShellModuleCache()
    {
        var repo = CreateSeededRepository();
        try
        {
            var cachePath = Path.Combine(repo, "Microsoft", "Windows", "PowerShell", "ModuleAnalysisCache");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, "cache");

            var inspection = GitCli.InspectWorktreeStatus(repo);

            Assert.True(inspection.Succeeded);
            Assert.False(inspection.IsDirty);
            Assert.Empty(inspection.CommitWorthyPaths);
            Assert.False(GitCli.IsWorktreeDirty(repo));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GitCli_IsWorktreeDirty_does_not_hide_PowerShell_cache_siblings")]
    public void GitCliIsWorktreeDirtyDoesNotHidePowerShellCacheSiblings()
    {
        var repo = CreateSeededRepository();
        try
        {
            var siblingPath = Path.Combine(repo, "Microsoft", "Windows", "PowerShell", "ModuleAnalysisCache.source");
            Directory.CreateDirectory(Path.GetDirectoryName(siblingPath)!);
            File.WriteAllText(siblingPath, "source");

            var inspection = GitCli.InspectWorktreeStatus(repo);

            Assert.True(inspection.Succeeded);
            Assert.True(inspection.IsDirty);
            Assert.Equal(["Microsoft/Windows/PowerShell/ModuleAnalysisCache.source"], inspection.CommitWorthyPaths);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GitCli_IsWorktreeDirty_returns_true_when_staged_change_present")]
    public void GitCliIsWorktreeDirtyReturnsTrueWhenStagedChangePresent()
    {
        var repo = CreateSeededRepository();
        try
        {
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "modified content");
            RunGit(repo, "add", "-A");

            Assert.True(GitCli.IsWorktreeDirty(repo));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GitCli_IsWorktreeDirty_returns_false_after_changes_are_committed")]
    public void GitCliIsWorktreeDirtyReturnsFalseAfterChangesAreCommitted()
    {
        var repo = CreateSeededRepository();
        try
        {
            File.WriteAllText(Path.Combine(repo, "new.txt"), "content");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Add new.txt");

            Assert.False(GitCli.IsWorktreeDirty(repo));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GitCli_Run_returns_sentinel_result_for_nonexistent_working_directory")]
    public void GitCliRunReturnsSentinelResultForNonexistentWorkingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));

        var result = GitCli.Run(path, "status");

        Assert.False(result.Succeeded);
        Assert.True(result.ExitCode != 0);
    }

    private static string CreateSeededRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-gitcli-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "GitCli Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        return root;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
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
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
