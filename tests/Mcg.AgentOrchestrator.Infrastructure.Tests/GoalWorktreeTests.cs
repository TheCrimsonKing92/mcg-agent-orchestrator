using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalWorktreeTests
{
    private static AgentDefinition EchoDeveloper() => new(
        new AgentId("echo-developer"),
        "Echo Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local"));

    private static WorkerProfileCatalog EchoProfiles() => new(
    [
        new WorkerProfile("local", "git add -A; if ((git status --short).Length -gt 0) { git commit -m Lifecycle-work }; Write-Output {subscriptionModelName}")
    ]);

    [Xunit.Fact(DisplayName = "GoalWorktrees_creates_and_resolves_worktree_per_goal")]
    public void GoalWorktreesCreatesAndResolvesWorktreePerGoal()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();

            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is null);

            var path = GoalWorktrees.Ensure(repo, goalId);

            Assert.True(File.Exists(Path.Combine(path, ".git")));
            Assert.True(File.Exists(Path.Combine(path, "seed.txt")));
            Assert.Equal(path, GoalWorktrees.TryResolve(repo, goalId));
            Assert.Equal(path, GoalWorktrees.Ensure(repo, goalId));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_fast_forwards_goal_branch_on_merge")]
    public void GoalWorktreesFastForwardsGoalBranchOnMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            File.WriteAllText(Path.Combine(path, "feature.txt"), "goal work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal work");

            var merge = GoalWorktrees.TryFastForwardMerge(repo, goalId);

            Assert.True(merge is not null);
            Assert.True(merge!.FastForwarded);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            var removeResult = GoalWorktrees.Remove(repo, goalId);
            Assert.Equal("Removed workspace and merged branch " + GoalWorktrees.BranchName(goalId) + ".", removeResult.Message);
            Assert.True(removeResult.IsComplete);
            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is null);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_resumes_after_unregistered_worktree_leaves_directory")]
    public void GoalWorktreesRemoveResumesAfterUnregisteredWorktreeLeavesDirectory()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);
            File.WriteAllText(Path.Combine(path, "leftover.log"), "held by prior test process");

            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            Assert.True(Directory.Exists(path));
            Assert.True(GoalWorktrees.TryResolve(repo, goalId) is null);
            Assert.True(BranchExists(repo, branch));

            var removeResult = GoalWorktrees.Remove(repo, goalId);
            Assert.Equal("Removed workspace and merged branch " + branch + ".", removeResult.Message);
            Assert.True(removeResult.IsComplete);

            Assert.False(Directory.Exists(path));
            Assert.False(BranchExists(repo, branch));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reports_leftover_path_and_resumes_when_lock_released")]
    public void GoalWorktreesRemoveReportsLeftoverPathAndResumesWhenLockReleased()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);

            // Deregister the worktree manually to isolate directory-deletion behavior.
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var lockedFile = Path.Combine(path, "leftover.log");
            File.WriteAllText(lockedFile, "held open");

            // Hold the file open exclusively so Directory.Delete fails.
            GoalWorktreeRemoveResult partial;
            using (var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                partial = GoalWorktrees.Remove(repo, goalId);
            }

            Assert.False(partial.IsComplete);
            Assert.Equal(path, partial.LeftoverPath);
            Assert.Equal("workspace remove", partial.ResumeCommand);
            Assert.True(partial.Message.Contains("could not be removed", StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(path));

            // Lock released; resume call deletes the directory and cleans up the branch.
            var final = GoalWorktrees.Remove(repo, goalId);
            Assert.True(final.IsComplete);
            Assert.False(Directory.Exists(path));
            Assert.False(BranchExists(repo, branch));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_partial_result_identifies_branch_state")]
    public void GoalWorktreesRemovePartialResultIdentifiesBranchState()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);

            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var lockedFile = Path.Combine(path, "hold.txt");
            File.WriteAllText(lockedFile, "lock");

            GoalWorktreeRemoveResult partial;
            using (var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                partial = GoalWorktrees.Remove(repo, goalId);
            }

            Assert.False(partial.IsComplete);
            Assert.True(partial.Message.Contains(branch, StringComparison.Ordinal));
            Assert.Equal(path, partial.LeftoverPath);
            Assert.True(partial.LockHolders.Count >= 0); // collection always initialized
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_ParseWmicListOutput_extracts_pid_and_command_line")]
    public void GoalWorktreesParseWmicListOutputExtractsPidAndCommandLine()
    {
        const string wmicOutput = """

            CommandLine=dotnet test MyProject.dll
            ProcessId=1234

            CommandLine=VBCSCompiler.exe -pipename:xyz
            ProcessId=5678

            CommandLine=
            ProcessId=9999

            """;

        var result = GoalWorktrees.ParseWmicListOutput(wmicOutput);

        Assert.Equal(2, result.Count);
        Assert.Equal("dotnet test MyProject.dll", result[1234]);
        Assert.Equal("VBCSCompiler.exe -pipename:xyz", result[5678]);
        Assert.False(result.ContainsKey(9999));
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_suggests_manual_merge_when_branches_diverge")]
    public void GoalWorktreesSuggestsManualMergeWhenBranchesDiverge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            File.WriteAllText(Path.Combine(path, "feature.txt"), "goal work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal work");

            File.WriteAllText(Path.Combine(repo, "main.txt"), "main work");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main work");

            var merge = GoalWorktrees.TryFastForwardMerge(repo, goalId);

            Assert.True(merge is not null);
            Assert.False(merge!.FastForwarded);
            Assert.Equal($"git merge {GoalWorktrees.BranchName(goalId)}", merge.SuggestedCommand);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_merge_returns_null_without_goal_branch")]
    public void GoalWorktreesMergeReturnsNullWithoutGoalBranch()
    {
        var repo = CreateSeededRepository();
        try
        {
            Assert.True(GoalWorktrees.TryFastForwardMerge(repo, GoalId.New()) is null);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_command_creates_and_removes_goal_worktree")]
    public void CliWorkspaceCommandCreatesAndRemovesGoalWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Workspace goal", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            CliCommandDispatcher.ExecuteCommand(["workspace", "create"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

            var path = GoalWorktrees.TryResolve(repo, goal.Id);
            Assert.True(path is not null);
            Assert.Equal(path, workspace.ResolveExecutionDirectory(goal.Id));

            CliCommandDispatcher.ExecuteCommand(["workspace", "remove"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.Equal(workspace.ExecutionDirectory, workspace.ResolveExecutionDirectory(goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_can_target_non_current_goal")]
    public void CliWorkspaceRemoveCanTargetNonCurrentGoal()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var olderGoal = kernel.CreateGoal("Older workspace goal", [new TaskSpec(TaskId.New(), "Do older work", AgentRole.Developer)]);
            var latestGoal = kernel.CreateGoal("Latest workspace goal", [new TaskSpec(TaskId.New(), "Do latest work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = latestGoal;
            var olderPath = GoalWorktrees.Ensure(repo, olderGoal.Id);
            var latestPath = GoalWorktrees.Ensure(repo, latestGoal.Id);
            var olderGoalPrefix = olderGoal.Id.Value[..8];

            CliCommandDispatcher.ExecuteCommand(["workspace", "remove", olderGoalPrefix], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);

            Assert.True(GoalWorktrees.TryResolve(repo, olderGoal.Id) is null);
            Assert.False(Directory.Exists(olderPath));
            Assert.Equal(latestPath, GoalWorktrees.TryResolve(repo, latestGoal.Id));
            Assert.True(Directory.Exists(latestPath));
            Assert.Equal(olderGoal.Id, currentGoal!.Id);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_blocks_merge_when_verification_fails")]
    public void CliAcceptanceBlocksMergeWhenVerificationFails()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance gate test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Completed, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "change.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var fakeVerifier = new GoalAcceptanceVerifier(
                (_, _, _) => Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "Test run failed\nFailed: 2")));
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                CliCommandHandlers.Execute(["acceptance"], context);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var output = writer.ToString();
            Assert.True(output.Contains("failed (exit 1)", StringComparison.Ordinal));
            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.True(output.Contains("Test run failed", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "change.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_merges_after_passing_verification")]
    public void CliAcceptanceMergesAfterPassingVerification()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance gate test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Completed, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var fakeVerifier = new GoalAcceptanceVerifier(
                (_, _, _) => Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "")));
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                CliCommandHandlers.Execute(["acceptance"], context);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var output = writer.ToString();
            Assert.True(output.Contains("passed (exit 0)", StringComparison.Ordinal));
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_skip_verify_bypasses_verification_and_merges")]
    public void CliAcceptanceSkipVerifyBypassesVerificationAndMerges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance gate test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Completed, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "skip.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var verifierCalled = false;
            var fakeVerifier = new GoalAcceptanceVerifier((_, _, _) =>
            {
                verifierCalled = true;
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "should not run"));
            });
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            Console.SetOut(writer);
            try
            {
                CliCommandHandlers.Execute(["acceptance", "--skip-verify"], context);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var output = writer.ToString();
            Assert.False(verifierCalled);
            Assert.True(output.Contains("skipped (--skip-verify)", StringComparison.Ordinal));
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "skip.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_runs_accepts_and_removes_workspace")]
    public void CliLifecycleSimpleGoalRunsAcceptsAndRemovesWorkspace()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = EchoProfiles();
            var fakeVerifier = new GoalAcceptanceVerifier(
                (_, _, _) => Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "")));
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["lifecycle-simple-goal", "Ship a small echo change", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.True(output.Contains($"Lifecycle goal: {goal.Id.Value}", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage run-goal:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage acceptance:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage workspace remove:", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_requires_confirm_batch_start")]
    public void CliLifecycleSimpleGoalRequiresConfirmBatchStart()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = EchoProfiles();
        var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null);

        var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
            ["lifecycle-simple-goal", "Do work"],
            context));

        Xunit.Assert.Contains("--confirm-batch-start", ex.Message);
        Xunit.Assert.Empty(kernel.Goals);
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_requires_large_paid_prompt_confirm")]
    public void CliLifecycleSimpleGoalRequiresLargePaidPromptConfirm()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = EchoProfiles();
        var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null);

        var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
            ["lifecycle-simple-goal", "Do work", "--confirm-batch-start"],
            context));

        Xunit.Assert.Contains("--confirm-large-paid-subscription-start", ex.Message);
        Xunit.Assert.Empty(kernel.Goals);
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_fails")]
    public void CliLifecycleSimpleGoalKeepsWorkspaceWhenAcceptanceFails()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = EchoProfiles();
            var fakeVerifier = new GoalAcceptanceVerifier(
                (_, _, _) => Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "Focused tests failed")));
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() =>
            {
                var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
                    ["lifecycle-simple-goal", "Run but fail acceptance", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                    context));
                Xunit.Assert.Contains("acceptance", ex.Message);
            });

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.True(output.Contains("Next: acceptance", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static string CreateSeededRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-worktree-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "Worktree Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        return root;
    }

    private static bool BranchExists(string workingDirectory, string branch)
    {
        return RunGitExitCode(workingDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}") == 0;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var exitCode = RunGitExitCode(workingDirectory, arguments, out var error);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }
    }

    private static string CaptureConsole(Action action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return writer.ToString();
    }

    private static int RunGitExitCode(string workingDirectory, params string[] arguments)
    {
        return RunGitExitCode(workingDirectory, arguments, out _);
    }

    private static int RunGitExitCode(string workingDirectory, string[] arguments, out string error)
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
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        return process.ExitCode;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; temp directories are pruned by the OS.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
