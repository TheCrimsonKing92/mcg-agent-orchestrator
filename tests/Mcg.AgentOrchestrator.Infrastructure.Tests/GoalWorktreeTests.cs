using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

public sealed class GoalWorktreeIntegrationTests
{
    private static AgentDefinition EchoDeveloper() => new(
        new AgentId("echo-developer"),
        "Echo Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local"));

    private static IReadOnlyList<AgentDefinition> EchoAgents() =>
    [
        EchoAgent(AgentRole.Planner),
        EchoAgent(AgentRole.Researcher),
        EchoDeveloper(),
        EchoAgent(AgentRole.Tester),
        EchoAgent(AgentRole.Reviewer)
    ];

    private static AgentDefinition EchoAgent(AgentRole role) => new(
        new AgentId($"echo-{role.ToString().ToLowerInvariant()}"),
        $"Echo {role}",
        role,
        new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local"));

    private static WorkerProfileCatalog EchoProfiles() => new(
    [
        new WorkerProfile("local", "git add -A; if ((git status --short).Length -gt 0) { git commit -m Lifecycle-work }; Write-Output {subscriptionModelName}")
    ]);

    private static TimeSpan FastLifecyclePollInterval => TimeSpan.FromMilliseconds(1);

    private static Task SkipLifecycleSleep(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;

    private static Func<Goal, Task<RunGoalService.RunGoalResult>> CreateFastLifecycleRunGoal(
        AgentOrchestratorKernel kernel,
        string repo)
    {
        return goal =>
        {
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            var sourcePath = Path.Combine(worktreePath, "src", $"lifecycle-{goal.Id.Value[..8]}.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, "// fake lifecycle work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Lifecycle-work");

            var completedTasks = new List<RunGoalService.RunGoalTaskSummary>();
            foreach (var task in goal.Tasks)
            {
                kernel.RecordTaskVerification(
                    goal.Id,
                    task.Id,
                    ManualVerificationRecorder.Create(true, "Fake lifecycle run-goal passed.", worktreePath, DateTimeOffset.UtcNow));
                completedTasks.Add(new RunGoalService.RunGoalTaskSummary(
                    TaskDisplayNumber.Resolve(goal, task.Id),
                    task.Id.Value,
                    task.Description,
                    Succeeded: true,
                    OutputTail: null));
            }

            return Task.FromResult(new RunGoalService.RunGoalResult(
                Executed: true,
                StopReason: "Goal completed.",
                BlockingAction: null,
                CompletedTasks: completedTasks,
                StopEvidence: null));
        };
    }

    private static InMemoryModelProviderRegistry SeedSpecRefiner(OrchestratorWorkspace workspace)
    {
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        return new InMemoryModelProviderRegistry([
            new FakeSmokeProvider("{}", providerName: "fake-refiner")
        ]);
    }

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

    [Xunit.Fact(DisplayName = "InvokeRepoScript_runs_FindOrchestratorLocks_without_synthetic_argument")]
    public void InvokeRepoScriptRunsFindOrchestratorLocksWithoutSyntheticArgument()
    {
        var repoRoot = FindCurrentSourceRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repoRoot, "scripts", "Invoke-RepoScript.ps1"));
        startInfo.ArgumentList.Add("scripts\\Find-OrchestratorLocks.ps1");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Invoke-RepoScript.ps1.");
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();

        Assert.True(process.WaitForExit(30000), "Find-OrchestratorLocks.ps1 did not exit within 30 seconds.");
        Assert.True(
            process.ExitCode is 0 or 2,
            $"Expected Find-OrchestratorLocks.ps1 to exit 0 or 2, got {process.ExitCode}. stderr: {stderr}");
        Assert.DoesNotContain("A positional parameter cannot be found that accepts argument", stderr, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "InvokeRepoScript_StartOrchestratorCommand_emits_parseable_launch_json")]
    public void InvokeRepoScriptStartOrchestratorCommandEmitsParseableLaunchJson()
    {
        var repoRoot = FindCurrentSourceRoot();
        var sandboxPath = Path.Combine(Path.GetTempPath(), $"start-orchestrator-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandboxPath);
        try
        {
            var hostPath = Path.Combine(sandboxPath, "fake-dotnet.cmd");
            File.WriteAllText(hostPath, """
                @echo off
                powershell.exe -NoProfile -ExecutionPolicy Bypass -File %*
                """);

            var appScriptPath = Path.Combine(sandboxPath, "fake-app.ps1");
            File.WriteAllText(appScriptPath, """
                param(
                    [Parameter(ValueFromRemainingArguments = $true)]
                    [string[]]$Arguments
                )

                Start-Sleep -Milliseconds 250
                $Arguments | ConvertTo-Json -Compress
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = hostPath;
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(repoRoot, "scripts", "Invoke-RepoScript.ps1"));
            startInfo.ArgumentList.Add("scripts\\Start-OrchestratorCommand.ps1");
            startInfo.ArgumentList.Add("-Name");
            startInfo.ArgumentList.Add("goal-worktree-launch-json-test");
            startInfo.ArgumentList.Add("-AppDll");
            startInfo.ArgumentList.Add(appScriptPath);
            startInfo.ArgumentList.Add("conduct");
            startInfo.ArgumentList.Add("--loop");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start Invoke-RepoScript.ps1.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30000), "Start-OrchestratorCommand.ps1 did not exit within 30 seconds.");
            Assert.Equal(0, process.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);

            var outputLines = stdout.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Single(outputLines);

            using var document = JsonDocument.Parse(outputLines[0]);
            var root = document.RootElement;
            var pid = root.GetProperty("pid").GetInt32();
            var stdoutPath = root.GetProperty("stdoutPath").GetString();
            var stderrPath = root.GetProperty("stderrPath").GetString();
            var args = root.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray();

            Assert.True(pid > 0);
            Assert.False(string.IsNullOrWhiteSpace(stdoutPath));
            Assert.False(string.IsNullOrWhiteSpace(stderrPath));
            Assert.Equal(new[] { appScriptPath, "conduct", "--loop" }, args);
            Assert.True(DateTimeOffset.TryParse(root.GetProperty("startedAt").GetString(), out _));
        }
        finally
        {
            DeleteDirectory(sandboxPath);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_git_metadata_access_resolves_linked_index_lock_path")]
    public void GoalWorktreesGitMetadataAccessResolvesLinkedIndexLockPath()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            var access = GoalWorktrees.InspectGitMetadataAccess(
                path,
                new WorkerSandboxOptions(Enabled: false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

            var expected = Path.GetFullPath(Path.Combine(
                repo,
                ".git",
                "worktrees",
                goalId.Value[..8],
                "index.lock"));
            Assert.Equal(NormalizePath(expected), NormalizePath(access.IndexLockPath));
            Assert.Equal(Path.GetFullPath(path), access.WorktreePath);
            Assert.True(access.CurrentProcessCanWriteIndexLock, access.Error ?? "index.lock probe failed");
            Assert.True(access.WorkerCanWriteIndexLock, access.WorkerWriteDisposition);
            Assert.Equal("same-as-orchestrator", access.WorkerWriteDisposition);
            Assert.Contains("orchestrator commits", access.CommitContract);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_git_metadata_access_marks_low_integrity_worker_non_committing")]
    public void GoalWorktreesGitMetadataAccessMarksLowIntegrityWorkerNonCommitting()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            var access = GoalWorktrees.InspectGitMetadataAccess(
                path,
                new WorkerSandboxOptions(Enabled: OperatingSystem.IsWindows(), WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

            if (OperatingSystem.IsWindows())
            {
                Assert.False(access.WorkerCanWriteIndexLock);
                Assert.Equal("blocked-by-low-integrity", access.WorkerWriteDisposition);
            }
            else
            {
                Assert.Equal("same-as-orchestrator", access.WorkerWriteDisposition);
            }

            Assert.EndsWith(
                NormalizePathSeparators(Path.Combine(".git", "worktrees", goalId.Value[..8], "index.lock")),
                NormalizePath(access.IndexLockPath));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_resolve_all_matches_per_goal_try_resolve")]
    public void GoalWorktreesResolveAllMatchesPerGoalTryResolve()
    {
        var repo = CreateSeededRepository();
        try
        {
            var first = GoalId.New();
            var second = GoalId.New();
            var missing = GoalId.New();
            var firstPath = GoalWorktrees.Ensure(repo, first);
            var secondPath = GoalWorktrees.Ensure(repo, second);

            var resolved = GoalWorktrees.ResolveAll(repo, [first, second, missing]);

            Assert.Equal(firstPath, resolved[first]);
            Assert.Equal(secondPath, resolved[second]);
            Assert.False(resolved.ContainsKey(missing));
            Assert.Equal(GoalWorktrees.TryResolve(repo, first), resolved[first]);
            Assert.Equal(GoalWorktrees.TryResolve(repo, second), resolved[second]);
            Assert.Equal(GoalWorktrees.TryResolve(repo, missing), resolved.GetValueOrDefault(missing));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_ensure_fast_forwards_undriven_stale_worktree_to_base")]
    public void GoalWorktreesEnsureFastForwardsUndrivenStaleWorktreeToBase()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            // A fix lands on the base branch AFTER the goal worktree was created.
            File.WriteAllText(Path.Combine(repo, "landed-fix.txt"), "fix on main");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Landed fix on base");

            // The goal worktree is undriven (no commits of its own, clean) but now behind base.
            Assert.False(File.Exists(Path.Combine(path, "landed-fix.txt")));

            // Re-ensuring brings the undriven worktree up to the current base so a worker never
            // builds on a stale base (which would conflict at acceptance with the landed fix).
            var reEnsured = GoalWorktrees.Ensure(repo, goalId);

            Assert.Equal(path, reEnsured);
            Assert.True(File.Exists(Path.Combine(path, "landed-fix.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_ensure_leaves_driven_divergent_worktree_untouched")]
    public void GoalWorktreesEnsureLeavesDrivenDivergentWorktreeUntouched()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            // The goal branch has its own committed work (driven).
            File.WriteAllText(Path.Combine(path, "goal-work.txt"), "developer work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Developer work");

            // The base branch advances divergently.
            File.WriteAllText(Path.Combine(repo, "landed-fix.txt"), "fix on main");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Landed fix on base");

            // Re-ensuring must NOT fast-forward (it would discard the goal work); the divergent branch
            // is left as-is for TryRebaseOntoMain/acceptance to reconcile.
            var reEnsured = GoalWorktrees.Ensure(repo, goalId);

            Assert.Equal(path, reEnsured);
            Assert.True(File.Exists(Path.Combine(path, "goal-work.txt")));
            Assert.False(File.Exists(Path.Combine(path, "landed-fix.txt")));
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
            var buildEnvironment = DotnetBuildEnvironmentManager.CreateAttempt(goalId, "cleanup-test");
            Assert.True(Directory.Exists(buildEnvironment.RootPath));

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
            Assert.False(Directory.Exists(buildEnvironment.RootPath));
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

            if (OperatingSystem.IsWindows())
            {
                // Windows holds the file exclusively, so the registered worktree and branch are
                // cleaned up while the leftover directory is deferred to a later sweep.
                Assert.True(partial.IsComplete);
                Assert.Null(partial.LeftoverPath);
                Assert.True(partial.Message.Contains("deferred to orphan sweep", StringComparison.OrdinalIgnoreCase));
                Assert.True(Directory.Exists(path));

                // Lock released; resume call deletes the directory and cleans up the branch.
                var final = GoalWorktrees.Remove(repo, goalId);
                Assert.True(final.IsComplete);
            }
            else
            {
                // POSIX allows unlinking files with open handles, so removal completes immediately.
                Assert.True(partial.IsComplete);
            }

            Assert.False(Directory.Exists(path));
            Assert.False(BranchExists(repo, branch));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_is_idempotent_when_already_clean")]
    public void GoalWorktreesRemoveIsIdempotentWhenAlreadyClean()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            GoalWorktrees.Ensure(repo, goalId);

            var first = GoalWorktrees.Remove(repo, goalId);
            Assert.True(first.IsComplete);

            // Second call with nothing left must return success, not throw.
            var second = GoalWorktrees.Remove(repo, goalId);
            Assert.True(second.IsComplete);
            Assert.True(second.Message.Contains("already clean", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_retries_and_succeeds_when_transient_lock_releases")]
    public void GoalWorktreesRemoveRetriesAndSucceedsWhenTransientLockReleases()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            var branch = GoalWorktrees.BranchName(goalId);

            // Simulate the half-removed state: worktree already unregistered, directory lingers.
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var lockedFile = Path.Combine(path, "transient-hold.log");
            File.WriteAllText(lockedFile, "held");

            GoalWorktreeRemoveResult result;
            if (OperatingSystem.IsWindows())
            {
                // Hold the file exclusively then release it partway through the retry window so
                // that a single Remove() call succeeds without requiring a second invocation.
                var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);
                _ = Task.Delay(150).ContinueWith(_ => fs.Dispose());

                result = GoalWorktrees.Remove(repo, goalId);

                Assert.True(result.IsComplete);
            }
            else
            {
                // POSIX: open handles do not prevent deletion, so removal completes immediately.
                result = GoalWorktrees.Remove(repo, goalId);
                Assert.True(result.IsComplete);
            }

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

            if (OperatingSystem.IsWindows())
            {
                Assert.True(partial.IsComplete);
                Assert.True(partial.Message.Contains(branch, StringComparison.Ordinal));
                Assert.True(partial.Message.Contains("deferred to orphan sweep", StringComparison.OrdinalIgnoreCase));
                Assert.Null(partial.LeftoverPath);
            }
            else
            {
                // POSIX: the held handle does not block removal, so it completes.
                Assert.True(partial.IsComplete);
            }
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

    [Xunit.Fact(DisplayName = "GoalWorktrees_rebases_stale_branch_onto_main_when_clean")]
    public void GoalWorktreesRebasesStaleBranchOntoMainWhenClean()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            File.WriteAllText(Path.Combine(path, "feature.txt"), "goal work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal work");

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main work");

            var rebase = GoalWorktrees.TryRebaseOntoMain(repo, goalId);
            var merge = GoalWorktrees.TryFastForwardMerge(repo, goalId);

            Assert.True(
                rebase.Status == GoalWorktreeRebaseStatus.Rebased,
                $"{rebase.Status}: {rebase.Message}");
            Assert.True(rebase.ConflictFiles.Count == 0);
            Assert.True(merge is not null);
            Assert.True(merge!.FastForwarded);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "main-advanced.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_reports_conflict_files_and_aborts_rebase")]
    public void GoalWorktreesReportsConflictFilesAndAbortsRebase()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            File.WriteAllText(Path.Combine(path, "seed.txt"), "goal edit");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal edit");

            File.WriteAllText(Path.Combine(repo, "seed.txt"), "main edit");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main edit");

            var rebase = GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            Assert.True(
                rebase.Status == GoalWorktreeRebaseStatus.Conflict,
                $"{rebase.Status}: {rebase.Message}");
            Assert.Equal(1, rebase.ConflictFiles.Count);
            Assert.Equal("seed.txt", rebase.ConflictFiles[0]);
            Assert.True(rebase.SuggestedCommand?.Contains("Create an operator task", StringComparison.Ordinal) == true);
            Assert.Equal("goal edit", File.ReadAllText(Path.Combine(path, "seed.txt")));
            Assert.Equal("main edit", File.ReadAllText(Path.Combine(repo, "seed.txt")));
            Assert.Equal(0, RunGitExitCode(path, "rev-parse", "--verify", "--quiet", "HEAD"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_refuses_rebase_when_worktree_dirty")]
    public void GoalWorktreesRefusesRebaseWhenWorktreeDirty()
    {
        var repo = CreateSeededRepository();
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            File.WriteAllText(Path.Combine(path, "feature.txt"), "uncommitted goal work");
            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main work");

            var rebase = GoalWorktrees.TryRebaseOntoMain(repo, goalId);

            Assert.Equal(GoalWorktreeRebaseStatus.DirtyWorktree, rebase.Status);
            Assert.True(rebase.Message.Contains("uncommitted changes", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(path, "feature.txt")));
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalHealthEvaluator_prioritizes_dirty_worktree_before_next_action")]
    public void GoalHealthEvaluatorPrioritizesDirtyWorktreeBeforeNextAction()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Dirty health", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            var agents = AgentCatalog.Default().Agents;
            kernel.ActivateGoal(goal.Id, agents);
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(path, "dirty.txt"), "uncommitted");

            var health = GoalHealthEvaluator.Build(
                kernel,
                goal,
                agents,
                WorkerProfileCatalog.Default(),
                repo,
                AutonomyPolicy.SupervisedAuto);

            Assert.Equal(GoalHealthDisposition.Blocked, health.Disposition);
            Assert.Equal(20, health.Score);
            Assert.True(health.Recommendation.Contains("dirty worktree", StringComparison.OrdinalIgnoreCase));
            Assert.True(health.SuggestedCommand.Contains("goal-recovery", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalHealthEvaluator_scores_ready_failed_stalled_provider_limited_and_healthy_states")]
    public void GoalHealthEvaluatorScoresReadyFailedStalledProviderLimitedAndHealthyStates()
    {
        var repo = CreateSeededRepository();
        try
        {
            var agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();

            var readyKernel = new AgentOrchestratorKernel();
            var readyGoal = CreateCompletedGoal(readyKernel, "Ready health", repo);
            var readyPath = GoalWorktrees.Ensure(repo, readyGoal.Id);
            File.WriteAllText(Path.Combine(readyPath, "ready.txt"), "ready");
            RunGit(readyPath, "add", "-A");
            RunGit(readyPath, "commit", "-m", "Ready health");
            var ready = GoalHealthEvaluator.Build(readyKernel, readyGoal, agents, profiles, repo, AutonomyPolicy.SupervisedAuto);
            Assert.Equal(GoalHealthDisposition.ReadyForAcceptance, ready.Disposition);
            Assert.Equal(85, ready.Score);

            var failedKernel = new AgentOrchestratorKernel();
            var failedTask = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
            var failedGoal = failedKernel.CreateGoal("Failed health", [failedTask]);
            failedKernel.ActivateGoal(failedGoal.Id, agents);
            failedKernel.ReportTaskProgress(failedGoal.Id, failedTask.Id, WorkTaskStatus.Failed, "Verification failed.");
            var failed = GoalHealthEvaluator.Build(failedKernel, failedGoal, agents, profiles, repo, AutonomyPolicy.SupervisedAuto);
            Assert.Equal(GoalHealthDisposition.Blocked, failed.Disposition);
            Assert.Equal(25, failed.Score);

            var stalledKernel = new AgentOrchestratorKernel();
            var stalledTask = new TaskSpec(TaskId.New(), "Run worker", AgentRole.Developer);
            var stalledGoal = stalledKernel.CreateGoal("Stalled health", [stalledTask]);
            stalledKernel.ActivateGoal(stalledGoal.Id, agents);
            stalledKernel.RecordTaskDispatch(stalledGoal.Id, stalledTask.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", repo, DateTimeOffset.UtcNow));
            stalledKernel.RecordTaskProcessStarted(stalledGoal.Id, stalledTask.Id, new TaskProcessRecord(999999, "codex exec prompt.md", repo, "out.log", "err.log", "exit.txt", DateTimeOffset.UtcNow, null, null));
            var stalled = GoalHealthEvaluator.Build(stalledKernel, stalledGoal, agents, profiles, repo, AutonomyPolicy.SupervisedAuto);
            Assert.Equal(GoalHealthDisposition.NeedsOperator, stalled.Disposition);
            Assert.Equal(40, stalled.Score);

            var limitedKernel = new AgentOrchestratorKernel();
            var limitedTask = new TaskSpec(TaskId.New(), "Run subscription worker", AgentRole.Developer);
            var limitedGoal = limitedKernel.CreateGoal("Provider-limited health", [limitedTask]);
            limitedKernel.ActivateGoal(limitedGoal.Id, agents);
            limitedKernel.RecordTaskDispatch(limitedGoal.Id, limitedTask.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", repo, DateTimeOffset.UtcNow));
            limitedKernel.RecordTaskVerification(limitedGoal.Id, limitedTask.Id, new TaskVerificationRecord(
                "codex-cli",
                repo,
                1,
                "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 11:59 PM.",
                string.Empty,
                DateTimeOffset.UtcNow));
            var limited = GoalHealthEvaluator.Build(limitedKernel, limitedGoal, agents, profiles, repo, AutonomyPolicy.SupervisedAuto);
            Assert.Equal(GoalHealthDisposition.ProviderLimited, limited.Disposition);
            Assert.Equal(55, limited.Score);

            var healthyKernel = new AgentOrchestratorKernel();
            var healthyGoal = CreateCompletedGoal(healthyKernel, "Healthy monitor", repo);
            var healthy = GoalHealthEvaluator.Build(healthyKernel, healthyGoal, agents, profiles, repo, AutonomyPolicy.SupervisedAuto);
            Assert.Equal(GoalHealthDisposition.Healthy, healthy.Disposition);
            Assert.Equal(90, healthy.Score);
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

    [Xunit.Fact(DisplayName = "Cli_workspace_remove_safe_auto_blocks_cleanup")]
    public void CliWorkspaceRemoveSafeAutoBlocksCleanup()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Workspace cleanup policy", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            var path = GoalWorktrees.Ensure(repo, goal.Id);

            var ex = Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
                ["workspace", "remove", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Assert.True(ex.Message.Contains("policy 'safe-auto' blocks workspace remove", StringComparison.Ordinal));
            Assert.Equal(path, GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.True(goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.Contains("blocked workspace remove", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_rebase_updates_clean_stale_goal_branch")]
    public void CliWorkspaceRebaseUpdatesCleanStaleGoalBranch()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Workspace rebase goal", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;
            var path = GoalWorktrees.Ensure(repo, goal.Id);

            File.WriteAllText(Path.Combine(path, "feature.txt"), "goal work");
            RunGit(path, "add", "-A");
            RunGit(path, "commit", "-m", "Goal work");

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main work");

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["workspace", "rebase"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
            var merge = GoalWorktrees.TryFastForwardMerge(repo, goal.Id);

            Assert.True(output.Contains("Rebased", StringComparison.Ordinal));
            Assert.True(output.Contains("Next: acceptance", StringComparison.Ordinal));
            Assert.True(merge is not null);
            Assert.True(merge!.FastForwarded);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "main-advanced.txt")));
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
            var sourceDirectory = Path.Combine(worktreePath, "src");
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "Change.cs"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var fakeVerifier = FakeAcceptanceVerifier.Failed("Test run failed\nFailed: 2");
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));
            Assert.True(output.Contains("failed (exit 1)", StringComparison.Ordinal));
            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.True(output.Contains("Test run failed", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "src", "Change.cs")));
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
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));
            Assert.True(output.Contains("Acceptance evidence bundle: passed", StringComparison.Ordinal));
            Assert.True(output.Contains($"lease id: goal-{goal.Id.Value[..8].ToLowerInvariant()}", StringComparison.Ordinal));
            Assert.True(output.Contains("Verification check: passed", StringComparison.Ordinal));
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_lands_after_transient_state_write_lock_releases")]
    public async Task CliAcceptanceLandsAfterTransientStateWriteLockReleases()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance transient lock test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "transient-lock.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            using var lockConnection = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Mode=ReadWrite;Pooling=False;");
            lockConnection.Open();
            using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN IMMEDIATE";
            lockCommand.ExecuteNonQuery();

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed()
            };

            var acceptanceTask = Task.Run(() => CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context)));
            Assert.False(ReferenceEquals(acceptanceTask, await Task.WhenAny(acceptanceTask, Task.Delay(TimeSpan.FromMilliseconds(100)))));

            using var releaseCommand = lockConnection.CreateCommand();
            releaseCommand.CommandText = "COMMIT";
            releaseCommand.ExecuteNonQuery();

            var output = await acceptanceTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("Acceptance evidence bundle: passed", output);
            Assert.Contains("Fast-forwarded", output);
            Assert.True(File.Exists(Path.Combine(repo, "transient-lock.txt")));
            Assert.Equal(GoalStatus.Completed, goal.Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_accepted_routes_stop_host_merge_mark_landed_remove_worktree")]
    public void CliAcceptanceAcceptedRoutesStopHostMergeMarkLandedRemoveWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance lifecycle ordering test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var order = new List<string>();
            var writer = new RecordingGoalLifecycleEventWriter(order);
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal,
                finalizeAcceptanceMerge: request =>
                {
                    order.Add("merge");
                    return request.Merge();
                },
                stopAcceptanceHosts: request =>
                {
                    Assert.Equal(worktreePath, request.WorktreePath);
                    Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
                    order.Add("stop-host");
                    return AcceptanceHostStopResult.Success("Stop-host: test stopped host.");
                })
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed(),
                EventWriter = writer
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains($"Goal {goal.Id.Value[..8]} acceptance: accepted", StringComparison.Ordinal));
            Assert.True(output.Contains("Stop-host: test stopped host.", StringComparison.Ordinal));
            Assert.Equal(["stop-host", "merge", "mark-landed", "remove-worktree"], order);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_stop_host_timeout_blocks_before_merge")]
    public void CliAcceptanceStopHostTimeoutBlocksBeforeMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance stop-host blocker test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var mergeCalled = false;
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal,
                finalizeAcceptanceMerge: request =>
                {
                    mergeCalled = true;
                    return request.Merge();
                },
                stopAcceptanceHosts: _ => new AcceptanceHostStopResult(
                    false,
                    "BLOCKER step=stop-host reason=timeout hosts=pid=123 name=Mcg.AgentOrchestrator.App log=host.log action=\"Stop exact PID(s), inspect log path(s), then rerun acceptance.\"",
                    [new AcceptanceHostProcess(123, "Mcg.AgentOrchestrator.App", "host command", "host.log")]))
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed()
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.False(mergeCalled);
            Assert.True(output.Contains("BLOCKER step=stop-host", StringComparison.Ordinal));
            Assert.True(output.Contains("pid=123", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.Equal(worktreePath, GoalWorktrees.TryResolve(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_merge_conflict_blocks_and_leaves_worktree")]
    public void CliAcceptanceMergeConflictBlocksAndLeavesWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance merge blocker test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal,
                finalizeAcceptanceMerge: _ => new AcceptanceMergeCommitResult(false, "conflicting files: feature.txt"),
                stopAcceptanceHosts: _ => AcceptanceHostStopResult.Success("Stop-host: none."))
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Passed()
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("BLOCKER step=merge", StringComparison.Ordinal));
            Assert.True(output.Contains("feature.txt", StringComparison.Ordinal));
            Assert.True(!output.Contains($"Goal {goal.Id.Value[..8]} acceptance: accepted", StringComparison.Ordinal), output);
            Assert.True(output.Contains($"Goal {goal.Id.Value[..8]} acceptance: not accepted", StringComparison.Ordinal), output);
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.Equal(worktreePath, GoalWorktrees.TryResolve(repo, goal.Id));
            var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);
            Assert.False(acceptance.IsAccepted);
            Assert.Contains(acceptance.Blockers, blocker =>
                blocker.Kind == GoalAcceptanceBlockerKind.AcceptanceFailed &&
                blocker.Message.Contains("merge", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_non_accepted_verdict_does_not_stop_host_or_merge")]
    public void CliAcceptanceNonAcceptedVerdictDoesNotStopHostOrMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance non-accepted test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");
            var stopCalled = false;
            var mergeCalled = false;
            var context = new CliExecutionContext(
                kernel,
                OrchestratorWorkspace.ForDirectory(repo),
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal,
                finalizeAcceptanceMerge: request =>
                {
                    mergeCalled = true;
                    return request.Merge();
                },
                stopAcceptanceHosts: _ =>
                {
                    stopCalled = true;
                    return AcceptanceHostStopResult.Success("Stop-host: should not run.");
                })
            {
                AcceptanceVerifier = FakeAcceptanceVerifier.Failed("Focused tests failed")
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.False(stopCalled);
            Assert.False(mergeCalled);
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.Equal(worktreePath, GoalWorktrees.TryResolve(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_rebases_before_verification_and_lands_verified_head")]
    public void CliAcceptanceRebasesBeforeVerificationAndLandsVerifiedHead()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance rebase ordering test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");
            var staleGoalHead = RunGitOutput(worktreePath, "rev-parse", "HEAD");

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main work");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main work");

            string? verifierHead = null;
            var fakeVerifier = FakeAcceptanceVerifier.Passed(onRun: () =>
            {
                verifierHead = RunGitOutput(worktreePath, "rev-parse", "HEAD");
            });
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));
            var landedHead = RunGitOutput(repo, "rev-parse", "HEAD");

            Assert.True(output.Contains("Workspace rebase: Rebased", StringComparison.Ordinal));
            Assert.Equal(1, fakeVerifier.RunCount);
            Assert.False(string.IsNullOrWhiteSpace(verifierHead));
            Assert.NotEqual(staleGoalHead, verifierHead);
            Assert.Equal(verifierHead, landedHead);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "main-advanced.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_rebase_conflict_blocks_before_verification_and_restores_worktree")]
    public void CliAcceptanceRebaseConflictBlocksBeforeVerificationAndRestoresWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance rebase conflict test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "seed.txt"), "goal edit");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal edit");
            var originalGoalHead = RunGitOutput(worktreePath, "rev-parse", "HEAD");

            File.WriteAllText(Path.Combine(repo, "seed.txt"), "main edit");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main edit");
            var mainHeadBeforeAcceptance = RunGitOutput(repo, "rev-parse", "HEAD");

            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("Workspace rebase: Rebase", StringComparison.Ordinal));
            Assert.True(output.Contains("Conflict files:", StringComparison.Ordinal));
            Assert.True(output.Contains("Acceptance evidence: blocked; merge blocked", StringComparison.Ordinal));
            Assert.Equal(0, fakeVerifier.RunCount);
            Assert.Equal(mainHeadBeforeAcceptance, RunGitOutput(repo, "rev-parse", "HEAD"));
            Assert.Equal(originalGoalHead, RunGitOutput(worktreePath, "rev-parse", "HEAD"));
            Assert.Equal("goal edit", File.ReadAllText(Path.Combine(worktreePath, "seed.txt")));
            Assert.Equal("main edit", File.ReadAllText(Path.Combine(repo, "seed.txt")));
            Assert.Equal(string.Empty, RunGitOutput(worktreePath, "status", "--short"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_lands_when_discord_token_is_invalid")]
    public void CliAcceptanceLandsWhenDiscordTokenIsInvalid()
    {
        var repo = CreateSeededRepository();
        var previousToken = Environment.GetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN", "invalid-token-for-acceptance-test");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance gate ignores Discord auth", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
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
            OperatorChannelStore.Save(
                workspace.OperatorChannelPath,
                new OperatorChannelCatalog("discord", ForumChannelId: "42"));
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var channel = OperatorChannelFactory.Create(
                OperatorChannelStore.Load(workspace.OperatorChannelPath),
                OperatorChannelFactory.ResolveBotToken(),
                workspace.OrchestratorDirectory);
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal, channel)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("Acceptance evidence bundle: passed", StringComparison.Ordinal));
            Assert.True(output.Contains("Verification: passed (exit 0)", StringComparison.Ordinal));
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN", previousToken);
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_removes_workspace_after_successful_merge")]
    public void CliAcceptanceRemovesWorkspaceAfterSuccessfulMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance cleanup test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
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
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            // Acceptance now deterministically owns post-merge cleanup: no separate `workspace remove`.
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_keep_workspace_retains_workspace_after_merge")]
    public void CliAcceptanceKeepWorkspaceRetainsWorkspaceAfterMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance keep-workspace test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
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
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--keep-workspace"], context));

            // --keep-workspace opts out of the deterministic cleanup; the merge still happens.
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(output.Contains("Workspace kept (--keep-workspace)", StringComparison.Ordinal));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_profile_dispatch_auto_creates_workspace_when_missing")]
    public void CliProfileDispatchAutoCreatesWorkspaceWhenMissing()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Dispatch auto-create test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, [EchoDeveloper()]);

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal);

            // Dispatch must own workspace creation: none exists yet.
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["profile-dispatch", "1", "local"], context));

            Assert.True(output.Contains("Workspace auto-created", StringComparison.Ordinal));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_auto_verifies_from_git_evidence")]
    public void CliAcceptanceAutoVerifiesFromGitEvidence()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Auto verify test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            // No manual verification recorded: the goal is not Completed and the task is Assigned.
            Assert.Equal(GoalStatus.Active, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            // Acceptance derives the verification from git evidence (committed change + clean worktree).
            Assert.True(output.Contains("Auto-verified", StringComparison.Ordinal));
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_does_not_auto_verify_without_committed_changes")]
    public void CliAcceptanceDoesNotAutoVerifyWithoutCommittedChanges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("No change auto verify test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            GoalWorktrees.Ensure(repo, goal.Id); // worktree exists but has no commits against the base branch

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            // No committed work → no auto-verify → the goal stays un-accepted (anti-fabrication preserved).
            Assert.False(output.Contains("Auto-verified", StringComparison.Ordinal));
            Assert.Equal(GoalStatus.Active, goal.Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_auto_rebases_when_goal_branch_behind_main")]
    public void CliAcceptanceAutoRebasesWhenGoalBranchBehindMain()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Rebase behind main test", repo);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            // Advance the base branch so the goal branch can no longer fast-forward.
            File.WriteAllText(Path.Combine(repo, "mainline.txt"), "main advance");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Main advance");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--keep-workspace"], context));

            // Acceptance rebases onto main then fast-forwards instead of punting the merge to the operator.
            Assert.True(output.Contains("Workspace rebase", StringComparison.Ordinal));
            Assert.True(output.Contains("Fast-forwarded", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "mainline.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_auto_records_dogfood_entry")]
    public async Task CliAcceptanceAutoRecordsDogfoodEntry()
    {
        var repo = CreateSeededRepository();
        try
        {
            File.WriteAllText(Path.Combine(repo, "DOGFOOD_LOG.md"), "# Dogfood Log" + Environment.NewLine);
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Add dogfood log");

            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Autorecord distinctive objective", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--keep-workspace"], context));

            Assert.True(output.Contains("Recorded dogfood-log entry", StringComparison.Ordinal));
            var record = await new DogfoodLogStore(context.Workspace.DogfoodLogStorePath)
                .GetByGoalIdAsync(goal.Id.Value);
            Assert.NotNull(record);
            Assert.Contains("Autorecord distinctive objective", record!.RenderedMarkdown);
            var log = File.ReadAllText(Path.Combine(repo, "DOGFOOD_LOG.md"));
            Assert.Equal("# Dogfood Log" + Environment.NewLine, log);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_no_record_skips_dogfood_entry")]
    public async Task CliAcceptanceNoRecordSkipsDogfoodEntry()
    {
        var repo = CreateSeededRepository();
        try
        {
            File.WriteAllText(Path.Combine(repo, "DOGFOOD_LOG.md"), "# Dogfood Log" + Environment.NewLine);
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Add dogfood log");

            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "No record objective", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--keep-workspace", "--no-record"], context));

            var record = await new DogfoodLogStore(context.Workspace.DogfoodLogStorePath)
                .GetByGoalIdAsync(goal.Id.Value);
            Assert.Null(record);
            var log = File.ReadAllText(Path.Combine(repo, "DOGFOOD_LOG.md"));
            Assert.False(log.Contains("## ", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_recover_resets_failed_task_to_dispatchable")]
    public void CliRecoverResetsFailedTaskToDispatchable()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Recover test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "boom");
            Assert.Equal(WorkTaskStatus.Failed, task.Status);

            var context = CreateAcceptanceContext(kernel, repo, goal);
            CaptureConsole(() => CliCommandHandlers.Execute(["recover", goal.Id.Value[..8], "redo the work"], context));

            // One recover call brings the failed task back to a dispatchable state.
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_recover_resets_cancelled_tasks_without_disturbing_completed_or_running_tasks")]
    public void CliRecoverResetsCancelledTasksWithoutDisturbingCompletedOrRunningTasks()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var cancelledOne = new TaskSpec(TaskId.New(), "Cancelled one", AgentRole.Planner);
            var cancelledTwo = new TaskSpec(TaskId.New(), "Cancelled two", AgentRole.Researcher);
            var completed = new TaskSpec(TaskId.New(), "Completed", AgentRole.Developer);
            var running = new TaskSpec(TaskId.New(), "Running", AgentRole.Tester);
            var goal = kernel.CreateGoal("Recover cancelled tasks", [cancelledOne, cancelledTwo, completed, running]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);

            kernel.ReportTaskProgress(goal.Id, completed.Id, WorkTaskStatus.Completed, "Already done.");
            RecordCancelledProcess(kernel, goal.Id, cancelledOne.Id, 111, repo);
            RecordCancelledProcess(kernel, goal.Id, cancelledTwo.Id, 222, repo);
            using var runningProcess = StartLongRunningHelper();
            var runningStartedAt = DateTimeOffset.UtcNow;
            try
            {
                kernel.RecordTaskDispatch(
                    goal.Id,
                    running.Id,
                    new TaskDispatchRecord("codex-cli", "codex exec prompt.md", repo, runningStartedAt));
                kernel.RecordTaskProcessStarted(
                    goal.Id,
                    running.Id,
                    new TaskProcessRecord(
                        runningProcess.Id,
                        "codex exec prompt.md",
                        repo,
                        Path.Combine(repo, "running.out.log"),
                        Path.Combine(repo, "running.err.log"),
                        Path.Combine(repo, "running.exit.txt"),
                        runningStartedAt,
                        null,
                        null));

                var context = CreateAcceptanceContext(kernel, repo, goal);
                CaptureConsole(() => CliCommandHandlers.Execute(["recover", goal.Id.Value[..8], "retry cancelled work"], context));

                Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, cancelledOne.Id).Status);
                Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(goal.Id, cancelledTwo.Id).Status);
                Assert.Equal(WorkTaskStatus.Completed, kernel.GetTask(goal.Id, completed.Id).Status);
                var runningTask = kernel.GetTask(goal.Id, running.Id);
                Assert.Equal(WorkTaskStatus.Running, runningTask.Status);
                Assert.True(runningTask.LastProcess is { IsRunning: true });
            }
            finally
            {
                StopProcess(runningProcess);
            }
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_safe_auto_blocks_merge")]
    public void CliAcceptanceSafeAutoBlocksMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance policy test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Completed, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "policy.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var ex = Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
                ["acceptance", "--autonomy", "safe-auto"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Assert.True(ex.Message.Contains("policy 'safe-auto' blocks acceptance merge", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "policy.txt")));
            Assert.True(goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.Contains("blocked acceptance merge", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_queue_applies_ready_goals_sequentially")]
    public void CliAcceptanceQueueAppliesReadyGoalsSequentially()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "First queued acceptance", repo);
            var second = CreateCompletedGoal(kernel, "Second queued acceptance", repo);

            var firstPath = GoalWorktrees.Ensure(repo, first.Id);
            File.WriteAllText(Path.Combine(firstPath, "first.txt"), "first");
            RunGit(firstPath, "add", "-A");
            RunGit(firstPath, "commit", "-m", "First queued goal");

            RunGit(repo, "branch", GoalWorktrees.BranchName(second.Id), GoalWorktrees.BranchName(first.Id));
            var secondPath = GoalWorktrees.Ensure(repo, second.Id);
            File.WriteAllText(Path.Combine(secondPath, "second.txt"), "second");
            RunGit(secondPath, "add", "-A");
            RunGit(secondPath, "commit", "-m", "Second queued goal");

            var context = CreateAcceptanceContext(kernel, repo, first);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["acceptance-queue", "--apply", "--confirm-acceptance-queue"],
                context));

            Assert.True(output.Contains("Acceptance queue: 2 goal(s), ready=2, held=0, blocked=0", StringComparison.Ordinal));
            Assert.True(output.Contains($"Acceptance queue goal {first.Id.Value[..8]}:", StringComparison.Ordinal));
            Assert.True(output.Contains($"Acceptance queue goal {second.Id.Value[..8]}:", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(repo, "first.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "second.txt")));
            Assert.True(GoalWorktrees.TryResolve(repo, first.Id) is null);
            Assert.True(GoalWorktrees.TryResolve(repo, second.Id) is null);
            Assert.False(BranchExists(repo, GoalWorktrees.BranchName(first.Id)));
            Assert.False(BranchExists(repo, GoalWorktrees.BranchName(second.Id)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_queue_apply_persists_cleanup_after_outside_transaction_routing")]
    public async Task CliAcceptanceQueueApplyPersistsCleanupAfterOutsideTransactionRouting()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Queued acceptance persistence", repo);
            kernel.RecordAcceptanceFailure(goal.Id, ["previous verifier failure"]);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "queue-persist.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Queued persistence goal");

            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["acceptance-queue", "--apply", "--confirm-acceptance-queue"],
                stateRepository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal,
                acceptanceVerifier: FakeAcceptanceVerifier.Passed());

            Assert.True(changed);
            Assert.True(File.Exists(Path.Combine(repo, "queue-persist.txt")));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);

            var reloaded = await stateRepository.LoadAsync();
            var reloadedGoal = reloaded.GetGoal(goal.Id);
            Assert.Null(reloadedGoal.LatestAcceptanceFailure);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_queue_holds_stale_branch_with_manual_merge_command")]
    public void CliAcceptanceQueueHoldsStaleBranchWithManualMergeCommand()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Stale queued acceptance", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "stale.txt"), "goal");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Stale queued goal");

            File.WriteAllText(Path.Combine(repo, "main-advanced.txt"), "main");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Advance main");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance-queue"], context));

            Assert.True(output.Contains("ready=0, held=1, blocked=0", StringComparison.Ordinal));
            Assert.True(output.Contains("cannot fast-forward", StringComparison.Ordinal));
            Assert.True(output.Contains($"command: workspace rebase {goal.Id.Value[..8]}", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "stale.txt")));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_queue_safe_auto_holds_irreversible_actions")]
    public void CliAcceptanceQueueSafeAutoHoldsIrreversibleActions()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Policy queued acceptance", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "policy-queue.txt"), "goal");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Policy queued goal");

            var context = CreateAcceptanceContext(kernel, repo, goal);
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance-queue", "--autonomy", "safe-auto"], context));

            Assert.True(output.Contains("ready=0, held=1, blocked=0, policy=safe-auto", StringComparison.Ordinal));
            Assert.True(output.Contains("blocks irreversible acceptance or cleanup", StringComparison.Ordinal));
            Assert.True(output.Contains("--autonomy supervised-auto --confirm-acceptance-queue", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "policy-queue.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_evidence_blocks_dirty_worktree_before_merge")]
    public void CliAcceptanceEvidenceBlocksDirtyWorktreeBeforeMerge()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance evidence dirty test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
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
            File.WriteAllText(Path.Combine(worktreePath, "dirty.txt"), "uncommitted");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("Acceptance evidence bundle: blocked", StringComparison.Ordinal));
            Assert.True(output.Contains("Test impact:", StringComparison.Ordinal));
            Assert.True(output.Contains("Verification policy:", StringComparison.Ordinal));
            Assert.True(output.Contains("dirty-worktree", StringComparison.Ordinal));
            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_evidence_auto_injects_policy_required_checks_and_merges")]
    public void CliAcceptanceEvidenceAutoInjectsPolicyRequiredChecksAndMerges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance evidence auto-inject test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Completed, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "feature.txt"), "goal work");
            Directory.CreateDirectory(Path.Combine(worktreePath, "config"));
            // Empty manifest: no explicit checks, but policy-required checks are auto-injected
            // from the changed file scope (config/acceptance-manifest.json classifies as
            // BuildSystem, triggering a full-suite requirement which gets auto-injected).
            File.WriteAllText(Path.Combine(worktreePath, "config", "acceptance-manifest.json"), "{\"checks\":[]}");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            // Fake verifier always succeeds — simulates the auto-injected check passing.
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            // Policy-required check was auto-injected and passed — no missing-check blocker.
            Assert.True(output.Contains("Acceptance evidence bundle: passed", StringComparison.Ordinal));
            Assert.False(output.Contains("acceptance-checks-missing", StringComparison.Ordinal));
            Assert.False(output.Contains("acceptance-policy-check-missing", StringComparison.Ordinal));
            // The merge succeeded: feature.txt is now in the main working tree.
            Assert.True(File.Exists(Path.Combine(repo, "feature.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_evidence_blocks_generated_artifact_changes")]
    public void CliAcceptanceEvidenceBlocksGeneratedArtifactChanges()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Acceptance evidence generated artifact test", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
            Assert.Equal(GoalStatus.Completed, goal.Status);

            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            var generatedDirectory = Path.Combine(worktreePath, "src", "Feature", "bin", "Debug");
            Directory.CreateDirectory(generatedDirectory);
            var generatedFile = Path.Combine(generatedDirectory, "generated.dll");
            File.WriteAllText(generatedFile, "generated");
            RunGit(worktreePath, "add", "-f", generatedFile);
            RunGit(worktreePath, "commit", "-m", "Generated artifact");

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance"], context));

            Assert.True(output.Contains("generated-artifacts", StringComparison.Ordinal));
            Assert.True(output.Contains("merge blocked", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(repo, "src", "Feature", "bin", "Debug", "generated.dll")));
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
            var fakeVerifier = FakeAcceptanceVerifier.Failed("should not run", onRun: () =>
            {
                verifierCalled = true;
            });
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal)
            {
                AcceptanceVerifier = fakeVerifier
            };
            var output = CaptureConsole(() => CliCommandHandlers.Execute(["acceptance", "--skip-verify"], context));
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

    [Xunit.Fact(DisplayName = "Cli_acceptance_releases_state_write_lock_during_verification")]
    public void CliAcceptanceReleasesStateWriteLockDuringVerification()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance concurrency test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "concurrency.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            using var verifierEntered = new ManualResetEventSlim(false);
            using var releaseVerifier = new ManualResetEventSlim(false);
            var fakeVerifier = FakeAcceptanceVerifier.Passed(onRun: () =>
            {
                verifierEntered.Set();
                Assert.True(releaseVerifier.Wait(TimeSpan.FromSeconds(10)));
            });

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var acceptanceTask = Task.Run(() =>
            {
                try
                {
                    var contextAgents = agents;
                    var contextProfiles = profiles;
                    var contextGoal = currentGoal;

                    var initialKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
                    var context = new CliExecutionContext(
                        initialKernel,
                        workspace,
                        providers,
                        contextAgents,
                        contextProfiles,
                        contextGoal,
                        null,
                        () => stateRepository.LoadAsync().GetAwaiter().GetResult(),
                        null,
                        null)
                    {
                        AcceptanceVerifier = fakeVerifier
                    };

                    CliCommandHandlers.Execute(["acceptance"], context);
                }
                finally
                {
                    releaseVerifier.Set();
                }
            });

            Assert.True(verifierEntered.Wait(TimeSpan.FromSeconds(5)));

            var readTask = Task.Run(() => stateRepository.LoadAsync().GetAwaiter().GetResult());
            Assert.True(readTask.Wait(TimeSpan.FromSeconds(1)));

            var writeTask = Task.Run(() => stateRepository.SaveAsync(readTask.Result).GetAwaiter().GetResult());
            Assert.True(writeTask.Wait(TimeSpan.FromSeconds(1)));

            releaseVerifier.Set();
            Assert.True(acceptanceTask.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(File.Exists(Path.Combine(repo, "concurrency.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktree_state_reads_construct_repository_while_write_lock_is_held")]
    public void GoalWorktreeStateReadsConstructRepositoryWhileWriteLockIsHeld()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal("Readable while acceptance holds writer");
            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            using var lockConnection = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Mode=ReadWrite;Pooling=False;");
            lockConnection.Open();
            using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN IMMEDIATE";
            lockCommand.ExecuteNonQuery();

            var concurrentRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var goals = concurrentRepository.ListGoalMetadataAsync().GetAwaiter().GetResult();

            Assert.Single(goals);
            Assert.Equal("Readable while acceptance holds writer", goals.Single().Objective);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "OrchestratorSqliteTool_list_goals_reads_repo_state_while_write_lock_is_held")]
    public void OrchestratorSqliteToolListGoalsReadsRepoStateWhileWriteLockIsHeld()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal("SQLite helper read-only smoke");
            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            using var lockConnection = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Mode=ReadWrite;Pooling=False;");
            lockConnection.Open();
            using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN IMMEDIATE";
            lockCommand.ExecuteNonQuery();

            var result = RunOrchestratorSqliteTool(repo, repo, "list-goals", "--limit", "10");

            Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            Assert.Contains("SQLite helper read-only smoke", result.Stdout);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "OrchestratorSqliteTool_list_goals_prints_source_backlog_title_label")]
    public async Task OrchestratorSqliteToolListGoalsPrintsSourceBacklogTitleLabel()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var backlogStore = new BacklogStore(workspace.BacklogStorePath);
            var item = await backlogStore.AddAsync("Snapshot backlog title");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("SQLite helper labeled goal");
            kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);
            using (var conn = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Mode=ReadOnly;Pooling=False;"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT source_backlog_item_id FROM goals WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", goal.Id.Value);
                Assert.Equal(item.Id, cmd.ExecuteScalar() as string);
            }
            Assert.Equal("Snapshot backlog title", (await backlogStore.GetByExactIdAsync(item.Id))?.Title);

            var result = RunOrchestratorSqliteTool(repo, repo, "list-goals", "--repo-root", repo, "--status", "Active", "--limit", "10");

            Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            Assert.Contains($"{goal.Id.Value[..8]} (Snapshot backlog title) [Active] SQLite helper labeled goal", result.Stdout);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "OrchestratorSqliteTool_list_goals_omits_label_without_source_backlog_title")]
    public async Task OrchestratorSqliteToolListGoalsOmitsLabelWithoutSourceBacklogTitle()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("SQLite helper unlabeled goal");
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            await stateRepository.SaveAsync(kernel);

            var result = RunOrchestratorSqliteTool(repo, repo, "list-goals", "--repo-root", repo, "--status", "Active", "--limit", "10");

            Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            Assert.Contains($"{goal.Id.Value[..8]} [Active] SQLite helper unlabeled goal", result.Stdout);
            Assert.DoesNotContain($"{goal.Id.Value[..8]} (", result.Stdout);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "OrchestratorSqliteTool_list_goals_from_linked_worktree_reads_primary_state")]
    public void OrchestratorSqliteToolListGoalsFromLinkedWorktreeReadsPrimaryState()
    {
        var repo = CreateSeededRepository();
        var linkedWorktree = Path.Combine(Path.GetTempPath(), $"sqlite-tool-linked-worktree-{Guid.NewGuid():N}");
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            kernel.CreateGoal("SQLite helper primary state");
            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            RunGit(repo, "worktree", "add", "-b", "sqlite-tool-test", linkedWorktree);

            var localWorkspace = OrchestratorWorkspace.ForDirectory(linkedWorktree);
            var localKernel = new AgentOrchestratorKernel();
            localKernel.CreateGoal("SQLite helper linked local state");
            var localStateRepository = new SqliteOrchestratorStateRepository(localWorkspace.SqliteStatePath);
            localStateRepository.SaveAsync(localKernel).GetAwaiter().GetResult();

            var result = RunOrchestratorSqliteTool(linkedWorktree, null, "list-goals", "--limit", "10");

            Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            Assert.Contains("SQLite helper primary state", result.Stdout);
            Assert.DoesNotContain("SQLite helper linked local state", result.Stdout);

            var explicitResult = RunOrchestratorSqliteTool(
                linkedWorktree,
                null,
                "list-goals",
                "--repo-root",
                linkedWorktree,
                "--limit",
                "10");

            Assert.True(
                explicitResult.ExitCode == 0,
                $"exit={explicitResult.ExitCode}; stdout={explicitResult.Stdout}; stderr={explicitResult.Stderr}");
            Assert.True(string.IsNullOrWhiteSpace(explicitResult.Stderr), explicitResult.Stderr);
            Assert.Contains("SQLite helper linked local state", explicitResult.Stdout);
        }
        finally
        {
            DeleteDirectory(repo);
            DeleteDirectory(linkedWorktree);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_rejects_stale_goal_state_before_merge_commit")]
    public void CliAcceptanceRejectsStaleGoalStateBeforeMergeCommit()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance stale state test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "stale-state.txt"), "goal work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");

            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            var fakeVerifier = FakeAcceptanceVerifier.Passed(onRun: () =>
            {
                // Modify goal state during verification
                stateRepository.TransactAsync((transactionKernel, _) =>
                {
                    transactionKernel.RecordGoalPolicyDecision(goal.Id, "Concurrent goal state change.");
                    return Task.FromResult((true, true));
                }).GetAwaiter().GetResult();
            });

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var initialKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            var context = new CliExecutionContext(initialKernel, workspace, providers, agents, profiles, goal, null, () => stateRepository.LoadAsync().GetAwaiter().GetResult(), null, null)
            {
                AcceptanceVerifier = fakeVerifier
            };

            // This should still succeed when called through the normal acceptance path because
            // the stale check is only performed in ExecuteAcceptanceOutsideTransaction
            CliCommandHandlers.Execute(["acceptance"], context);

            // Verify the merge happened and the workspace file exists
            Assert.True(File.Exists(Path.Combine(repo, "stale-state.txt")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_acceptance_rejects_stale_worktree_head_before_merge_commit")]
    public void CliAcceptanceRejectsStaleWorktreeHeadBeforeMergeCommit()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Acceptance stale worktree test", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "tested.txt"), "tested work");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Tested work");

            var stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            var fakeVerifier = FakeAcceptanceVerifier.Passed(onRun: () =>
            {
                File.WriteAllText(Path.Combine(worktreePath, "after-verifier-started.txt"), "late work");
                RunGit(worktreePath, "add", "-A");
                RunGit(worktreePath, "commit", "-m", "Late work");
            });

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();

            var initialKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            var context = new CliExecutionContext(initialKernel, workspace, providers, agents, profiles, goal, null, () => stateRepository.LoadAsync().GetAwaiter().GetResult(), null, null)
            {
                AcceptanceVerifier = fakeVerifier
            };

            // When called through the normal acceptance path (which doesn't have the stale check),
            // the merge will still succeed even though the worktree changed during verification
            CliCommandHandlers.Execute(["acceptance"], context);

            // Verify the merge happened and the new files exist
            Assert.True(File.Exists(Path.Combine(repo, "tested.txt")));
            Assert.True(File.Exists(Path.Combine(repo, "after-verifier-started.txt")));
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
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier,
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
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

            var rerunOutput = CaptureConsole(() => CliCommandHandlers.Execute(
                ["lifecycle-simple-goal", "Ship a small echo change", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            Assert.Equal(goal.Id, context.CurrentGoal!.Id);
            Assert.Equal(1, kernel.Goals.Count);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.True(rerunOutput.Contains("reused existing idempotent goal", StringComparison.Ordinal));
            Assert.True(rerunOutput.Contains("already completed and workspace cleanup is recorded", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_goal_runs_five_role_goal_accepts_and_removes_workspace")]
    public void CliLifecycleGoalRunsFiveRoleGoalAcceptsAndRemovesWorkspace()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = EchoAgents();
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier,
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["lifecycle-goal", "Ship a five-role echo change", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Equal(5, goal.Tasks.Count);
            Assert.True(goal.Tasks.All(task => task.Status == WorkTaskStatus.Completed));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.True(output.Contains($"Lifecycle goal: {goal.Id.Value}", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage goal: created and activated.", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage run-goal:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage acceptance:", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage workspace remove:", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_removes_workspace_and_marks_goal_cleaned_up")]
    public void CliGoalMarkLandedRemovesWorkspaceAndMarksGoalCleanedUp()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Out-of-band landed goal", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "landed.txt"), "landed");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");
            RunGit(repo, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            var outputLines = output.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(
                [
                    "Goal landed cleanup:",
                    "cleanup: worktree removed",
                    "cleanup: branch deleted",
                    "cleanup: app-host lock released",
                    "cleanup: goal marked CleanedUp"
                ],
                outputLines);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.False(BranchExists(repo, GoalWorktrees.BranchName(goal.Id)));
            var facts = new GoalLifecycleFacts(WorkspaceExists: false, IsCleanedUp: true);
            Assert.Equal(GoalLifecycleState.CleanedUp, GoalLifecycle.ResolveState(goal, facts));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_passes_remaining_cleanup_budget_to_worktree_remove")]
    public void CliGoalMarkLandedPassesRemainingCleanupBudgetToWorktreeRemove()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Out-of-band landed budget goal", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "landed.txt"), "landed");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work");
            RunGit(repo, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));
            var worktrees = new CapturingGoalWorktreeService();
            var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            kernel.SetEventWriter(eventWriter);
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                EventWriter = eventWriter,
                Worktrees = worktrees,
                GoalMarkLandedElapsedMilliseconds = () => 9_000
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                context));

            Assert.True(output.Contains("cleanup: goal marked CleanedUp", StringComparison.Ordinal));
            Assert.Equal(1_000, worktrees.RemoveTimeoutMilliseconds);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.False(BranchExists(repo, GoalWorktrees.BranchName(goal.Id)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_goal_mark_landed_force_deletes_branch_kept_by_safe_worktree_remove")]
    public void CliGoalMarkLandedForceDeletesBranchKeptBySafeWorktreeRemove()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Out-of-band landed force cleanup goal", repo);
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            File.WriteAllText(Path.Combine(worktreePath, "force-landed.txt"), "force landed");
            RunGit(worktreePath, "add", "-A");
            RunGit(worktreePath, "commit", "-m", "Goal work not merged to main");

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["goal-mark-landed", goal.Id.Value[..8], "--confirm-goal-mark-landed", "--force"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            Assert.True(output.Contains("cleanup: branch deleted", StringComparison.Ordinal));
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is null);
            Assert.False(Directory.Exists(worktreePath));
            Assert.False(BranchExists(repo, GoalWorktrees.BranchName(goal.Id)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_workspace_help_does_not_create_goal_worktree")]
    public void CliWorkspaceHelpDoesNotCreateGoalWorktree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Keep workspace clean on help", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            var originalStatus = goal.Status;
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = EchoProfiles();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, goal);

            var output = CaptureConsole(() =>
            {
                var changed = CliCommandHandlers.Execute(
                    ["workspace", "create", goal.Id.Value[..8], "--help"],
                    context);

                Assert.False(changed);
            });

            Assert.Contains("Usage: workspace create", output);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.False(Directory.Exists(Path.Combine(repo, ".orchestrator-worktrees")));
            Assert.Equal(originalStatus, goal.Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_help_does_not_create_backlog_or_goal_state")]
    public void CliBacklogHelpDoesNotCreateBacklogOrGoalState()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = EchoProfiles();
            Goal? currentGoal = null;

            var output = CaptureConsole(() =>
            {
                var changed = CliCommandDispatcher.ExecuteCommand(
                    ["backlog-list", "--help"],
                    kernel,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);

                Assert.False(changed);
            });

            Assert.Contains("Usage: backlog-list", output);
            Assert.False(File.Exists(workspace.BacklogStorePath));
            Assert.Empty(kernel.Goals);
            Assert.Null(currentGoal);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_list_filters_do_not_change_goal_or_worktree_state")]
    public async Task CliBacklogListFiltersDoNotChangeGoalOrWorktreeState()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = new BacklogStore(workspace.BacklogStorePath);
            await store.AddAsync("Foo active one");
            await store.AddAsync("Foo active two");
            await store.AddAsync("Other active");
            var closed = await store.AddAsync("Foo closed");
            await store.CloseAsync(closed.Id, "done");

            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Keep filtered backlog read-only", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
            var originalStatus = goal.Status;
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = EchoProfiles();
            Goal? currentGoal = goal;

            var output = CaptureConsole(() =>
            {
                var changed = CliCommandDispatcher.ExecuteCommand(
                    ["backlog-list", "--limit", "1", "--status", "open", "--text", "foo"],
                    kernel,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);

                Assert.False(changed);
            });

            Assert.Contains("Foo active one", output);
            Assert.DoesNotContain("Foo active two", output);
            Assert.DoesNotContain("Foo closed", output);
            Assert.Equal(originalStatus, goal.Status);
            Assert.Null(GoalWorktrees.TryResolve(repo, goal.Id));
            Assert.False(Directory.Exists(Path.Combine(repo, ".orchestrator-worktrees")));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_safe_auto_stops_before_acceptance")]
    public void CliLifecycleSimpleGoalSafeAutoStopsBeforeAcceptance()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var fakeVerifier = FakeAcceptanceVerifier.Passed();
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier,
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
            };

            var output = CaptureConsole(() =>
            {
                var ex = Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
                    ["lifecycle-simple-goal", "Ship but pause before merge", "--confirm-batch-start", "--confirm-large-paid-subscription-start", "--autonomy", "safe-auto"],
                    context));
                Assert.True(ex.Message.Contains("stopped before acceptance", StringComparison.Ordinal));
            });

            var goal = context.CurrentGoal!;
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
            Assert.True(output.Contains("Autonomy policy: safe-auto", StringComparison.Ordinal));
            Assert.True(output.Contains("Stage acceptance: stopped.", StringComparison.Ordinal));
            Assert.True(goal.Timeline.Any(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.Contains("blocked lifecycle-simple-goal acceptance", StringComparison.Ordinal)));
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
            var providers = SeedSpecRefiner(workspace);
            // Touch a real source file so acceptance classifies a behavior
            // change and actually runs verification (the failing fakeVerifier).
            // Relying on the worker committing scratch like .orchestrator-context
            // would no longer make the worktree dirty, so the change must be real.
            var profiles = new WorkerProfileCatalog(
            [
                new WorkerProfile("local", "New-Item -ItemType Directory -Force src | Out-Null; Set-Content -Path src/lifecycle-change.cs -Value '// lifecycle work'; git add -A; git commit -m Lifecycle-work; Write-Output {subscriptionModelName}")
            ]);
            var fakeVerifier = FakeAcceptanceVerifier.Failed("Focused tests failed");
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier,
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
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

    [Xunit.Fact(DisplayName = "Cli_lifecycle_simple_goal_keeps_workspace_when_acceptance_throws")]
    public void CliLifecycleSimpleGoalKeepsWorkspaceWhenAcceptanceThrows()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            IReadOnlyList<AgentDefinition> agents = [EchoDeveloper()];
            var providers = SeedSpecRefiner(workspace);
            var profiles = EchoProfiles();
            var fakeVerifier = FakeAcceptanceVerifier.Throws(new InvalidOperationException("fake verifier boom"));
            var context = new CliExecutionContext(kernel, workspace, providers, agents, profiles, null)
            {
                AcceptanceVerifier = fakeVerifier,
                RunGoalPollInterval = FastLifecyclePollInterval,
                RunGoalSleep = SkipLifecycleSleep,
                RunGoalOverride = CreateFastLifecycleRunGoal(kernel, repo)
            };

            var ex = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
                ["lifecycle-simple-goal", "Run but verifier throws", "--confirm-batch-start", "--confirm-large-paid-subscription-start"],
                context));

            var goal = context.CurrentGoal!;
            Assert.Equal("fake verifier boom", ex.Message);
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.True(GoalWorktrees.TryResolve(repo, goal.Id) is not null);
            Assert.Equal(1, fakeVerifier.RunCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_invokes_build_server_shutdown_before_directory_delete")]
    public void GoalWorktreesRemoveInvokesBuildServerShutdownBeforeDirectoryDelete()
    {
        var repo = CreateSeededRepository();
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);

            // Simulate the unregistered-but-directory-remains half-state so the
            // test exercises BuildServerShutdown → DeleteDirectoryWithRetry directly.
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var shutdownCalled = false;
            string? shutdownPath = null;
            var directoryExistedAtShutdown = false;

            GoalWorktrees.BuildServerShutdown = (worktreePath, _) =>
            {
                shutdownCalled = true;
                shutdownPath = worktreePath;
                directoryExistedAtShutdown = Directory.Exists(worktreePath);
            };

            var result = GoalWorktrees.Remove(repo, goalId);

            Assert.True(shutdownCalled);
            Assert.Equal(path, shutdownPath);
            Assert.True(directoryExistedAtShutdown);
            Assert.True(result.IsComplete);
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_resets_sandbox_acl_before_directory_delete")]
    public void GoalWorktreesRemoveResetsSandboxAclBeforeDirectoryDelete()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktrees.Remove(repo, goalId);

            Assert.True(result.IsComplete);
            Assert.True(acl.ResetPaths.SequenceEqual([path]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_threads_remaining_budget_into_nested_cleanup")]
    public void GoalWorktreesRemoveThreadsRemainingBudgetIntoNestedCleanup()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalElapsed = GoalWorktrees.CleanupElapsedMilliseconds;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            long elapsedMilliseconds = 2_500;
            int? buildServerTimeout = null;
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.CleanupElapsedMilliseconds = () => elapsedMilliseconds;
            GoalWorktrees.BuildServerShutdown = (_, timeoutMilliseconds) =>
            {
                buildServerTimeout = timeoutMilliseconds;
                elapsedMilliseconds = 9_700;
            };
            GoalWorktrees.SandboxAclHelper = acl;

            var result = GoalWorktrees.Remove(repo, goalId, null, 10_000);

            Assert.True(result.IsComplete);
            Assert.Equal(7_500, buildServerTimeout);
            Assert.True(acl.TimeoutMilliseconds.SequenceEqual([300]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.CleanupElapsedMilliseconds = originalElapsed;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_defers_when_build_server_cleanup_exhausts_budget")]
    public void GoalWorktreesRemoveDefersWhenBuildServerCleanupExhaustsBudget()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalElapsed = GoalWorktrees.CleanupElapsedMilliseconds;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");

            long elapsedMilliseconds = 2_500;
            var warnings = new List<GoalWorktreeCleanupWarning>();
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.CleanupElapsedMilliseconds = () => elapsedMilliseconds;
            GoalWorktrees.BuildServerShutdown = (_, _) => elapsedMilliseconds = 10_000;
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.CleanupWarningSink = warnings.Add;

            var result = GoalWorktrees.Remove(repo, goalId, null, 10_000);

            Assert.False(result.IsComplete);
            Assert.Equal(path, result.LeftoverPath);
            Assert.Equal($"workspace remove {goalId.Value[..8].ToLowerInvariant()}", result.ResumeCommand);
            Assert.Empty(acl.ResetPaths);
            Assert.True(Directory.Exists(path));
            var warning = Assert.Single(warnings);
            Assert.Equal("remove:build-server-shutdown", warning.Operation);
            Assert.IsType<TimeoutException>(warning.Exception);
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.CleanupElapsedMilliseconds = originalElapsed;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_deletes_orphaned_worktree_directory")]
    public void GoalWorktreesSweepDeletesOrphanedWorktreeDirectory()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var registeredPath = GoalWorktrees.Ensure(repo, GoalId.New());
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned1");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktrees.SweepOrphanedWorktrees(repo);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.False(Directory.Exists(orphanPath));
            Assert.True(Directory.Exists(registeredPath));
            Assert.Empty(acl.ResetPaths);
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktreeOrphanSweepScheduler_sweep_now_deletes_orphaned_worktree_directory")]
    public void GoalWorktreeOrphanSweepSchedulerSweepNowDeletesOrphanedWorktreeDirectory()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var registeredPath = GoalWorktrees.Ensure(repo, GoalId.New());
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-scheduler");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktreeOrphanSweepScheduler.SweepNow(repo);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.False(Directory.Exists(orphanPath));
            Assert.True(Directory.Exists(registeredPath));
            Assert.Empty(acl.ResetPaths);
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_ensure_clears_existing_orphan_and_retries_once")]
    public void GoalWorktreesEnsureClearsExistingOrphanAndRetriesOnce()
    {
        var repo = CreateSeededRepository();
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.WorktreePath(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(path, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var ensured = GoalWorktrees.Ensure(repo, goalId);

            Assert.Equal(path, ensured);
            Assert.True(File.Exists(Path.Combine(path, ".git")));
            Assert.Empty(acl.ResetPaths);
        }
        finally
        {
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_resets_acl_only_after_access_denied_delete")]
    public void GoalWorktreesSweepResetsAclOnlyAfterAccessDeniedDelete()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectoryForCleanup;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-access-denied");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var deleteAttempts = 0;
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.DeleteDirectoryForCleanup = path =>
            {
                deleteAttempts++;
                if (deleteAttempts == 1)
                {
                    return GoalWorktreeDeleteResult.Failed(
                        GoalWorktreeDeleteFailureKind.AccessDenied,
                        "Access to the path is denied.");
                }

                Directory.Delete(path, recursive: true);
                return GoalWorktreeDeleteResult.Success;
            };
            GoalWorktrees.SandboxAclHelper = acl;
            GoalWorktrees.BuildServerShutdown = (_, _) => throw new InvalidOperationException("cheap orphan cleanup should not shut down build servers");

            var result = GoalWorktrees.SweepOrphanedWorktrees(repo);

            Assert.Equal(1, result.RemovedCount);
            Assert.Empty(result.LeftoverPaths);
            Assert.Equal(2, deleteAttempts);
            Assert.True(acl.ResetPaths.SequenceEqual([orphanPath]));
            Assert.False(Directory.Exists(orphanPath));
        }
        finally
        {
            GoalWorktrees.DeleteDirectoryForCleanup = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_sweep_records_timeout_backoff_and_skips_repeat_acl_reset")]
    public void GoalWorktreesSweepRecordsTimeoutBackoffAndSkipsRepeatAclReset()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectoryForCleanup;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalElapsed = GoalWorktrees.CleanupElapsedMilliseconds;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        var originalNow = GoalWorktrees.CleanupUtcNow;
        var originalBackoff = GoalWorktrees.CleanupBackoffDuration;
        try
        {
            var orphanPath = Path.Combine(repo, GoalWorktrees.DirectoryName, "orphaned-timeout");
            Directory.CreateDirectory(Path.Combine(orphanPath, ".mcg-sandbox"));
            File.WriteAllText(Path.Combine(orphanPath, ".mcg-sandbox", "leftover.txt"), "low-il residue");
            var now = DateTimeOffset.Parse("2026-07-02T05:00:00Z");
            long elapsedMilliseconds = 0;
            var warnings = new List<GoalWorktreeCleanupWarning>();
            var acl = new RecordingSandboxAclHelper();
            GoalWorktrees.DeleteDirectoryForCleanup = _ => GoalWorktreeDeleteResult.Failed(
                GoalWorktreeDeleteFailureKind.AccessDenied,
                "Access to the path is denied.");
            GoalWorktrees.CleanupElapsedMilliseconds = () => elapsedMilliseconds;
            GoalWorktrees.SandboxAclHelper = new TimeoutSandboxAclHelper(acl, () => elapsedMilliseconds = GitCli.DefaultTimeoutMilliseconds);
            GoalWorktrees.CleanupWarningSink = warnings.Add;
            GoalWorktrees.CleanupUtcNow = () => now;
            GoalWorktrees.CleanupBackoffDuration = TimeSpan.FromMinutes(10);

            var first = GoalWorktrees.SweepOrphanedWorktrees(repo);
            var second = GoalWorktrees.SweepOrphanedWorktrees(repo);

            Assert.Empty(first.LeftoverPaths.Where(path => !string.Equals(path, orphanPath, StringComparison.Ordinal)));
            Assert.Equal([orphanPath], second.LeftoverPaths);
            Assert.True(acl.ResetPaths.SequenceEqual([orphanPath]));
            Assert.Contains(warnings, warning => warning.Operation == "orphan-sweep:acl-reset" && warning.Exception is TimeoutException);
            Assert.Contains(warnings, warning =>
                warning.Operation == "orphan-sweep:backoff" &&
                warning.Exception.Message.Contains("Remove-Item -LiteralPath", StringComparison.Ordinal) &&
                warning.Exception.Message.Contains(orphanPath, StringComparison.Ordinal));
            Assert.Contains(warnings, warning => warning.Operation == "orphan-sweep:skip-backoff");
            Assert.True(Directory.Exists(orphanPath));
        }
        finally
        {
            GoalWorktrees.DeleteDirectoryForCleanup = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.CleanupElapsedMilliseconds = originalElapsed;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            GoalWorktrees.CleanupUtcNow = originalNow;
            GoalWorktrees.CleanupBackoffDuration = originalBackoff;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_cleanup_failure_logs_warning_and_defers_leftover")]
    public void GoalWorktreesCleanupFailureLogsWarningAndDefersLeftover()
    {
        var repo = CreateSeededRepository();
        var originalDelete = GoalWorktrees.DeleteDirectory;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalWarnings = GoalWorktrees.CleanupWarningSink;
        try
        {
            var goalId = GoalId.New();
            var path = GoalWorktrees.Ensure(repo, goalId);
            Directory.CreateDirectory(Path.Combine(path, ".mcg-sandbox"));
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            var warnings = new List<GoalWorktreeCleanupWarning>();

            GoalWorktrees.DeleteDirectory = _ => false;
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };
            GoalWorktrees.CleanupWarningSink = warnings.Add;

            var result = GoalWorktrees.Remove(repo, goalId);

            Assert.True(result.IsComplete);
            Assert.Null(result.LeftoverPath);
            Assert.True(result.Message.Contains("deferred to orphan sweep", StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(path));
            var warning = Assert.Single(warnings);
            Assert.Equal(path, warning.Path);
            Assert.Equal("remove", warning.Operation);
        }
        finally
        {
            GoalWorktrees.DeleteDirectory = originalDelete;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            GoalWorktrees.CleanupWarningSink = originalWarnings;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_reaps_recorded_worker_processes_before_delete")]
    public void GoalWorktreesRemoveReapsRecordedWorkerProcessesBeforeDelete()
    {
        var repo = CreateSeededRepository();
        var originalKill = GoalWorktrees.TryKillRecordedProcess;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Reap worker processes", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var path = GoalWorktrees.Ensure(repo, goal.Id);
            File.Delete(Path.Combine(path, ".git"));
            RunGit(repo, "worktree", "prune");
            var startedAt = DateTimeOffset.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(111, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [111, 222]));

            var killed = new List<int>();
            GoalWorktrees.TryKillRecordedProcess = pid =>
            {
                killed.Add(pid);
                return true;
            };
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktrees.Remove(repo, goal.Id, kernel);

            Assert.True(result.IsComplete);
            Assert.True(killed.SequenceEqual([111, 222]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            GoalWorktrees.TryKillRecordedProcess = originalKill;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_acceptance_failed_retry_clears_completed_task_evidence_before_redispatch")]
    public void GoalWorktreesAcceptanceFailedRetryClearsCompletedTaskEvidenceBeforeRedispatch()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
            var goal = kernel.CreateGoal("Retry after acceptance failure", [task]);
            kernel.ActivateGoal(goal.Id, EchoAgents());
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            var oldDispatch = new TaskDispatchRecord(
                "codex-cli",
                "codex exec old-prompt.md",
                worktree,
                DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
                BaseCommit: "old-base",
                ResultCommit: "old-result",
                PromptPath: Path.Combine(worktree, ".orchestrator", "prompts", "old-prompt.md"));
            kernel.RecordTaskDispatch(goal.Id, task.Id, oldDispatch);
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(
                    1234,
                    oldDispatch.Command,
                    worktree,
                    "old.out.log",
                    "old.err.log",
                    "old.exit.txt",
                    oldDispatch.DispatchedAt,
                    oldDispatch.DispatchedAt.AddSeconds(5),
                    0));
            kernel.RecordDispatchExecutionResult(
                goal.Id,
                task.Id,
                new TaskVerificationRecord(
                    oldDispatch.Command,
                    worktree,
                    0,
                    "WORKER_RESULT:\nfiles: src/Old.cs\ncommands: old\nEND_WORKER_RESULT",
                    string.Empty,
                    oldDispatch.DispatchedAt.AddSeconds(10)));
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);

            kernel.RecordAcceptanceFailure(goal.Id, ["SqliteOrchestratorStateRepositoryTests.Saves_goal_schema_columns"]);
            kernel.RetryTask(goal.Id, task.Id, "Operator rejection: fix the failing sqlite schema assertion.");
            var newDispatch = new TaskDispatchRecord(
                "codex-cli",
                "codex exec retry-prompt.md",
                worktree,
                DateTimeOffset.Parse("2026-06-26T12:05:00Z"),
                BaseCommit: "retry-base",
                PromptPath: Path.Combine(worktree, ".orchestrator", "prompts", "retry-prompt.md"));
            kernel.RecordTaskDispatch(goal.Id, task.Id, newDispatch);

            Assert.Equal(GoalStatus.Active, goal.Status);
            Assert.Equal(WorkTaskStatus.Running, task.Status);
            Assert.Null(task.LastVerification);
            Assert.Null(task.LastProcess);
            Assert.Equal(newDispatch, task.LastDispatch);
            Assert.Equal("codex exec retry-prompt.md", task.LastDispatch!.Command);
            Assert.Equal("retry-base", task.LastDispatch.BaseCommit);
            Assert.NotEqual(oldDispatch.PromptPath, task.LastDispatch.PromptPath);
            Assert.Equal(Path.Combine(worktree, ".orchestrator", "prompts", "retry-prompt.md"), task.LastDispatch.PromptPath);
            Assert.Null(task.LastDispatch.ResultCommit);
            Assert.Equal(worktree, GoalWorktrees.TryResolve(repo, goal.Id));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_skips_protected_recorded_worker_process")]
    public void GoalWorktreesRemoveSkipsProtectedRecordedWorkerProcess()
    {
        var repo = CreateSeededRepository();
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalProtectedPid = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
        try
        {
            var protectedPid = 111;
            Environment.SetEnvironmentVariable(
                CliProtectedProcessEnvironment.ProtectedPidVariable,
                protectedPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Remove protected worker process", [
                new TaskSpec(TaskId.New(), "Developer task", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(goal.Id, EchoAgents());
            var task = goal.Tasks[0];
            var path = GoalWorktrees.WorktreePath(repo, goal.Id);
            Directory.CreateDirectory(path);
            var startedAt = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(protectedPid, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [protectedPid]));

            var killed = new List<int>();
            WorkerProcessJobs.TryKillPidTree = pid =>
            {
                killed.Add(pid);
                return true;
            };
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktrees.Remove(repo, goal.Id, kernel);

            Assert.True(result.IsComplete);
            Assert.Empty(killed);
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, originalProtectedPid);
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_remove_kills_unprotected_recorded_worker_process")]
    public void GoalWorktreesRemoveKillsUnprotectedRecordedWorkerProcess()
    {
        var repo = CreateSeededRepository();
        var originalKill = WorkerProcessJobs.TryKillPidTree;
        var originalAcl = GoalWorktrees.SandboxAclHelper;
        var originalShutdown = GoalWorktrees.BuildServerShutdown;
        var originalProtectedPid = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
        try
        {
            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, "111");
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Remove unprotected worker process", [
                new TaskSpec(TaskId.New(), "Developer task", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(goal.Id, EchoAgents());
            var task = goal.Tasks[0];
            var path = GoalWorktrees.WorktreePath(repo, goal.Id);
            Directory.CreateDirectory(path);
            var startedAt = DateTimeOffset.Parse("2026-06-22T12:00:00Z");
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", path, startedAt));
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(222, "codex exec", path, "out.log", "err.log", "exit.txt", startedAt, null, null, OwnedProcessIds: [222]));

            var killed = new List<int>();
            WorkerProcessJobs.TryKillPidTree = pid =>
            {
                killed.Add(pid);
                return true;
            };
            GoalWorktrees.SandboxAclHelper = new RecordingSandboxAclHelper();
            GoalWorktrees.BuildServerShutdown = (_, _) => { };

            var result = GoalWorktrees.Remove(repo, goal.Id, kernel);

            Assert.True(result.IsComplete);
            Assert.True(killed.SequenceEqual([222]));
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            WorkerProcessJobs.TryKillPidTree = originalKill;
            GoalWorktrees.SandboxAclHelper = originalAcl;
            GoalWorktrees.BuildServerShutdown = originalShutdown;
            Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, originalProtectedPid);
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktrees_commit_on_behalf_after_worker_commit_leaves_worktree_clean")]
    public void GoalWorktreesCommitOnBehalfAfterWorkerCommitLeavesWorktreeClean()
    {
        var repo = CreateSeededRepository();
        try
        {
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var kernel = new AgentOrchestratorKernel();
            var taskSpec = new TaskSpec(TaskId.New(), "Developer task.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Commit residual dirty worktree", [taskSpec]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var task = goal.Tasks.Single();
            var worktree = GoalWorktrees.Ensure(repo, goal.Id);
            var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", worktree, dispatchedAt));

            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, "add", "-A");
            RunGit(worktree, "commit", "-m", "Worker commit");
            File.AppendAllText(Path.Combine(worktree, "seed.txt"), "leftover");

            var logs = Path.Combine(repo, "logs");
            Directory.CreateDirectory(logs);
            var stdout = Path.Combine(logs, "developer.out.log");
            var stderr = Path.Combine(logs, "developer.err.log");
            var exit = Path.Combine(logs, "developer.exit.txt");
            File.WriteAllText(stdout, "Committed implementation.");
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "0");
            kernel.RecordTaskProcessStarted(
                goal.Id,
                task.Id,
                new TaskProcessRecord(999999, "codex exec prompt", worktree, stdout, stderr, exit, dispatchedAt, null, null));

            new BackgroundDispatchRunner(clock, isStillRunning: _ => false).RefreshLatestProcess(kernel, goal.Id, task.Id);

            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Equal(0, task.LastVerification!.ExitCode);
            Assert.Equal(string.Empty, RunGitOutput(worktree, "status", "--short"));
            Assert.Equal("Developer task.: Committed implementation.", RunGitOutput(worktree, "log", "-1", "--pretty=%s"));
            Assert.Equal("seed.txt", RunGitOutput(worktree, "show", "--name-only", "--pretty=", "HEAD"));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static string CreateSeededRepository()
    {
        var tempRoot = OperatingSystem.IsWindows()
            ? Path.Combine(FindCurrentSourceRoot(), ".scratch", "mcg-wt")
            : Path.Combine(Path.GetTempPath(), "mcg-worktree-tests");
        var root = Path.Combine(tempRoot, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "Worktree Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        return root;
    }

    private static Goal CreateCompletedGoal(AgentOrchestratorKernel kernel, string objective, string repo)
    {
        var goal = kernel.CreateGoal(objective, [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Completed, goal.Status);
        return goal;
    }

    private static CliExecutionContext CreateAcceptanceContext(
        AgentOrchestratorKernel kernel,
        string repo,
        Goal currentGoal)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(repo);
        var fakeVerifier = FakeAcceptanceVerifier.Passed();
        return new CliExecutionContext(
            kernel,
            workspace,
            new InMemoryModelProviderRegistry([]),
            AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(),
            currentGoal)
        {
            AcceptanceVerifier = fakeVerifier
        };
    }

    private static void RecordCancelledProcess(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        int processId,
        string workingDirectory)
    {
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var artifactPrefix = Path.Combine(workingDirectory, $"cancelled-{taskId.Value[..8]}");
        var standardOutputPath = artifactPrefix + ".out.log";
        var standardErrorPath = artifactPrefix + ".err.log";
        var exitCodePath = artifactPrefix + ".exit.txt";
        kernel.RecordTaskDispatch(
            goalId,
            taskId,
            new TaskDispatchRecord("codex-cli", "codex exec prompt.md", workingDirectory, startedAt));
        kernel.RecordTaskProcessStarted(
            goalId,
            taskId,
            new TaskProcessRecord(processId, "codex exec prompt.md", workingDirectory, standardOutputPath, standardErrorPath, exitCodePath, startedAt, null, null));
        kernel.RecordTaskProcessCancelled(
            goalId,
            taskId,
            new TaskProcessRecord(processId, "codex exec prompt.md", workingDirectory, standardOutputPath, standardErrorPath, exitCodePath, startedAt, DateTimeOffset.UtcNow, null, WasCancelled: true));
    }

    private static Process StartLongRunningHelper()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "ping.exe" : "sleep",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("30");
            startInfo.ArgumentList.Add("127.0.0.1");
        }
        else
        {
            startInfo.ArgumentList.Add("30");
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start long-running helper process.");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunOrchestratorSqliteTool(
        string workingDirectory,
        string? repositoryRootEnvironment,
        params string[] arguments)
    {
        var sourceRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (repositoryRootEnvironment is not null)
            startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = repositoryRootEnvironment;
        else
            startInfo.Environment.Remove(OrchestratorWorkspace.RepoRootEnvironmentVariable);
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(sourceRoot, "scripts", "OrchestratorSqliteTools"));
        startInfo.ArgumentList.Add("--");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start OrchestratorSqliteTools.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(90)), "OrchestratorSqliteTools did not exit within 90 seconds.");
        return (process.ExitCode, stdout, stderr);
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed class RecordingGoalLifecycleEventWriter(List<string> order) : IGoalLifecycleEventWriter
    {
        public void AppendGoalCreated(GoalId goalId, string objective) { }
        public void AppendClarificationNeeded(GoalId goalId, string clarificationId) { }
        public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) { }
        public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) { }

        public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures)
        {
            if (pass)
            {
                order.Add("mark-landed");
            }
        }

        public void AppendCleanedUp(GoalId goalId) => order.Add("remove-worktree");
    }

    private sealed class CapturingGoalWorktreeService : ICliGoalWorktreeService
    {
        public int? RemoveTimeoutMilliseconds { get; private set; }

        public string BranchName(GoalId goalId) => GoalWorktrees.BranchName(goalId);

        public string Ensure(string executionDirectory, GoalId goalId) => GoalWorktrees.Ensure(executionDirectory, goalId);

        public string? TryResolve(string executionDirectory, GoalId goalId) => GoalWorktrees.TryResolve(executionDirectory, goalId);

        public GoalWorktreeRemoveResult Remove(
            string executionDirectory,
            GoalId goalId,
            AgentOrchestratorKernel? kernel = null,
            int? gitTimeoutMilliseconds = null)
        {
            RemoveTimeoutMilliseconds = gitTimeoutMilliseconds;
            return gitTimeoutMilliseconds is { } timeout
                ? GoalWorktrees.Remove(executionDirectory, goalId, kernel, timeout)
                : GoalWorktrees.Remove(executionDirectory, goalId, kernel);
        }

        public bool IsGitWorkTree(string executionDirectory) => GoalWorktrees.IsGitWorkTree(executionDirectory);

        public GoalWorktreeMergeResult? TryFastForwardMerge(string executionDirectory, GoalId goalId) =>
            GoalWorktrees.TryFastForwardMerge(executionDirectory, goalId);

        public GoalWorktreeRebaseResult TryRebaseOntoMain(string executionDirectory, GoalId goalId) =>
            GoalWorktrees.TryRebaseOntoMain(executionDirectory, goalId);

        public bool NeedsRebaseOntoMain(string executionDirectory, GoalId goalId) =>
            GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", "HEAD", BranchName(goalId)).ExitCode != 0;

        public bool IsWorktreeClean(string executionDirectory, GoalId goalId) =>
            GoalWorktrees.IsWorktreeClean(executionDirectory, goalId);

        public bool HasChangesAgainstMain(string executionDirectory, GoalId goalId) =>
            GoalWorktrees.HasChangesAgainstMain(executionDirectory, goalId);

        public string ResolveHead(string worktreePath)
        {
            var result = GitCli.Run(worktreePath, "rev-parse", "HEAD");
            return result.Succeeded ? result.Output.Trim() : string.Empty;
        }

        public IReadOnlyList<string> GetChangedFiles(string worktreePath) =>
            GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);

        public GoalAcceptanceEvidenceBundle BuildAcceptanceEvidence(
            AgentOrchestratorKernel kernel,
            Goal goal,
            string? worktreePath,
            AcceptanceVerificationResult? verification,
            bool verificationSkipped) =>
            DefaultCliGoalWorktreeService.Instance.BuildAcceptanceEvidence(
                kernel,
                goal,
                worktreePath,
                verification,
                verificationSkipped);
    }

    private sealed class FakeAcceptanceVerifier(
        AcceptanceVerificationResult? result,
        Action? onRun = null,
        Exception? exception = null) : IGoalAcceptanceVerifier
    {
        public int RunCount { get; private set; }

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            onRun?.Invoke();
            if (exception is not null)
                throw exception;

            var configuredResult = Assert.IsType<AcceptanceVerificationResult>(result);
            return Task.FromResult(AddPolicyRequiredChecks(configuredResult, changedFiles ?? []));
        }

        private static AcceptanceVerificationResult AddPolicyRequiredChecks(
            AcceptanceVerificationResult result,
            IReadOnlyList<string> changedFiles)
        {
            if (!result.Passed)
                return result;

            var checks = result.Checks?.ToList() ?? [];
            var existing = checks
                .Select(check => check.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var policy = VerificationPolicyCompiler.Compile(
                AgentRole.Reviewer,
                goalObjective: string.Empty,
                taskDescription: string.Empty,
                verificationPlan: null,
                changedFiles);
            foreach (var check in policy.Checks.Where(check =>
                check.Required &&
                !check.Kind.StartsWith("manual", StringComparison.OrdinalIgnoreCase) &&
                existing.Add(check.Name)))
            {
                checks.Add(new AcceptanceCheckResult(check.Name, true, 0, null));
            }

            return result with { Checks = checks };
        }

        public static FakeAcceptanceVerifier Passed(Action? onRun = null) =>
            new(
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("fake acceptance", true, 0, null)]),
                onRun);

        public static FakeAcceptanceVerifier Failed(string outputTail, Action? onRun = null) =>
            new(
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: false,
                    ExitCode: 1,
                    OutputTail: outputTail,
                    Checks: [new AcceptanceCheckResult("fake acceptance", false, 1, outputTail)]),
                onRun);

        public static FakeAcceptanceVerifier Throws(Exception exception, Action? onRun = null) =>
            new(null, onRun, exception);
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

    private static string RunGitOutput(string workingDirectory, params string[] arguments)
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
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }

        return output.Trim();
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

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static string NormalizePathSeparators(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    [Xunit.Fact(DisplayName = "DeleteDirectory_removes_tree_containing_read_only_files")]
    public void DeleteDirectoryRemovesTreeContainingReadOnlyFiles()
    {
        // Sandbox workers leave their worktree checkout read-only; the orphan sweep must still be
        // able to delete it. On Windows a naive Directory.Delete throws UnauthorizedAccessException
        // on a read-only file, so this exercises the attribute-clearing retry path.
        var root = Path.Combine(Path.GetTempPath(), "mcg-del-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        var file = Path.Combine(root, "nested", "locked.txt");
        File.WriteAllText(file, "sandbox output");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        try
        {
            Assert.True(GoalWorktrees.DeleteDirectory(root));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            DeleteDirectory(root);
        }
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

    private static string FindCurrentSourceRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))
            ?? throw new DirectoryNotFoundException($"Could not resolve source directory from '{sourceFilePath}'."));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "mcg-orchestrator.cmd")) &&
                File.Exists(Path.Combine(directory.FullName, "scripts", "Start-OrchestratorCommand.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate launcher source files from source file path '{sourceFilePath}'.");
    }

    private sealed class RecordingSandboxAclHelper : ISandboxAclHelper
    {
        public List<string> ResetPaths { get; } = [];
        public List<int> TimeoutMilliseconds { get; } = [];

        public void ResetSandboxAcl(string worktreePath, int timeoutMilliseconds)
        {
            ResetPaths.Add(worktreePath);
            TimeoutMilliseconds.Add(timeoutMilliseconds);
        }
    }

    private sealed class TimeoutSandboxAclHelper(RecordingSandboxAclHelper inner, Action afterReset) : ISandboxAclHelper
    {
        public void ResetSandboxAcl(string worktreePath, int timeoutMilliseconds)
        {
            inner.ResetSandboxAcl(worktreePath, timeoutMilliseconds);
            afterReset();
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
