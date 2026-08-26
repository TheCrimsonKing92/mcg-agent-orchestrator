using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;


public sealed class GoalWorktreeTestsSqliteTooling : GoalWorktreeTestBase
{
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
        var stdout = process.StandardOutput.ReadToEnd();

        Assert.True(process.WaitForExit(30000), "Find-OrchestratorLocks.ps1 did not exit within 30 seconds.");
        Assert.True(
            process.ExitCode is 0 or 2,
            $"Expected Find-OrchestratorLocks.ps1 to exit 0 or 2, got {process.ExitCode}. stderr: {stderr}");
        Assert.DoesNotContain("A positional parameter cannot be found that accepts argument", stderr, StringComparison.Ordinal);
        Assert.False(
            ContainsUnexpectedLockQueryEcho(stdout),
            $"Find-OrchestratorLocks.ps1 stdout echoed the lock query outside a report row.{Environment.NewLine}{stdout}");
    }

    [Xunit.Fact(DisplayName = "LockReportCheck_accepts_sibling_report_rows_containing_the_lock_query")]
    public void LockReportCheckAcceptsSiblingReportRowsContainingTheLockQuery()
    {
        const string stdout =
            "LOCK id=17 kind=conduct-loop parent=9 name=dotnet created=2026-08-23T12:00:00.0000000Z path=C:\\Program Files\\dotnet\\dotnet.exe command=\"dotnet\" C:\\repo\\Mcg.AgentOrchestrator.App.dll repo-process-info --locks\r\n" +
            "PROCESS id=23 parent=9 name=dotnet created=2026-08-23T12:00:01.0000000Z path=C:\\Program Files\\dotnet\\dotnet.exe command=\"dotnet\" C:\\repo\\Mcg.AgentOrchestrator.App.dll repo-process-info --locks\n" +
            "2 lock-holder(s) running; in-tree build lock is HELD.";

        Assert.False(ContainsUnexpectedLockQueryEcho(stdout));
    }

    [Xunit.Fact(DisplayName = "LockReportCheck_rejects_self_echo_and_mangled_argument_output")]
    public void LockReportCheckRejectsSelfEchoAndMangledArgumentOutput()
    {
        const string selfEchoOutput =
            "\"dotnet\" \"C:\\Temp\\Mcg.AgentOrchestrator.App.dll\" repo-process-info --locks\r\n" +
            "No orchestrator lock-holders running; in-tree build lock is FREE.";

        Assert.True(ContainsUnexpectedLockQueryEcho(selfEchoOutput));
        Assert.True(ContainsUnexpectedLockQueryEcho("Unknown command 'repo-process-info --locks'"));
        Assert.True(ContainsUnexpectedLockQueryEcho("LOCK id=7 kind=conduct-loop repo-process-info --locks"));
    }

    [Xunit.Fact(DisplayName = "InvokeGit_through_repo_script_preserves_hyphenated_git_arguments")]
    public void InvokeGitThroughRepoScriptPreservesHyphenatedGitArguments()
    {
        var sourceRoot = FindCurrentSourceRoot();
        var repo = CreateSeededRepository();
        try
        {
            var scriptsPath = Path.Combine(repo, "scripts");
            Directory.CreateDirectory(scriptsPath);
            File.Copy(Path.Combine(sourceRoot, "scripts", "Invoke-RepoScript.ps1"), Path.Combine(scriptsPath, "Invoke-RepoScript.ps1"));
            File.Copy(Path.Combine(sourceRoot, "scripts", "Invoke-Git.ps1"), Path.Combine(scriptsPath, "Invoke-Git.ps1"));

            RunGit(repo, "branch", "invoke-git-delete");
            var delete = RunInvokeRepoGit(repo, "branch", "-d", "invoke-git-delete");
            Assert.True(delete.ExitCode == 0, delete.Stdout + delete.Stderr);
            Assert.False(BranchExists(repo, "invoke-git-delete"));

            RunGit(repo, "branch", "invoke-git-force-delete");
            var forceDelete = RunInvokeRepoGit(repo, "branch", "-D", "invoke-git-force-delete");
            Assert.True(forceDelete.ExitCode == 0, forceDelete.Stdout + forceDelete.Stderr);
            Assert.False(BranchExists(repo, "invoke-git-force-delete"));

            RunGit(repo, "branch", "invoke-git-rename-source");
            var rename = RunInvokeRepoGit(repo, "branch", "-m", "invoke-git-rename-source", "invoke-git-rename-target");
            Assert.True(rename.ExitCode == 0, rename.Stdout + rename.Stderr);
            Assert.False(BranchExists(repo, "invoke-git-rename-source"));
            Assert.True(BranchExists(repo, "invoke-git-rename-target"));

            var config = RunInvokeRepoGit(repo, "-c", "advice.detachedHead=false", "branch", "--list", "invoke-git-rename-target");
            Assert.True(config.ExitCode == 0, config.Stdout + config.Stderr);
            Assert.True(config.Stdout.Contains("invoke-git-rename-target", StringComparison.Ordinal), config.Stdout);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "Repo_process_helpers_do_not_use_PowerShell_CIM_process_queries")]
    public void RepoProcessHelpersDoNotUsePowerShellCimProcessQueries()
    {
        var repoRoot = FindCurrentSourceRoot();
        var cliCommandText = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "Mcg.AgentOrchestrator.App",
            "Cli",
            "RepoProcessCliCommand.cs"));
        Assert.Contains("ProcessCommandLines.Snapshot", cliCommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenProcess", cliCommandText, StringComparison.Ordinal);
        var buildEnvironmentManagerText = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "Mcg.AgentOrchestrator.Infrastructure",
            "Workspaces",
            "DotnetBuildEnvironmentManager.cs"));
        Assert.Contains("ProcessCommandLines.SnapshotByNames", buildEnvironmentManagerText, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProcessesByName", buildEnvironmentManagerText, StringComparison.Ordinal);

        var inspectedFiles = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "src"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repoRoot, "scripts"), "*.ps1", SearchOption.AllDirectories))
            .Append(Path.Combine(repoRoot, "docs", "operator-runbook.md"))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        foreach (var path in inspectedFiles)
        {
            var text = File.ReadAllText(path);
            Assert.Null(FindForbiddenProcessQuery(text, path));
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData("wmic process get ProcessId")]
    [Xunit.InlineData("wmic.exe   process where ProcessId=42")]
    [Xunit.InlineData("new ProcessStartInfo {\r\n    FileName = \"wmic\",\r\n    Arguments = \"process get ProcessId,CommandLine /format:list\"\r\n}")]
    [Xunit.InlineData("Get-CimInstance Win32_Process")]
    [Xunit.InlineData("new ManagementObjectSearcher(query)")]
    public void ProcessQueryGuard_ForbiddenSpellings_AreRejected(string source)
    {
        var failure = FindForbiddenProcessQuery(source, "synthetic-source");

        Assert.NotNull(failure);
        Assert.Contains("Forbidden process-query mechanism", failure, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Get-CimInstance Win32_PhysicalMemory")]
    [Xunit.InlineData("Get-CimInstance Win32_BIOS")]
    public void ProcessQueryGuard_HardwareInventory_IsAllowed(string source) =>
        Assert.Null(FindForbiddenProcessQuery(source, "synthetic-source"));

    private static string? FindForbiddenProcessQuery(string source, string path)
    {
        string[] patterns =
        [
            @"(?im)\bwmic(?:\.exe)?\b[^\r\n]*\bprocess\b",
            """(?is)\bFileName\s*=\s*["']wmic(?:\.exe)?["']\s*,?\s*(?:\r?\n[^\r\n]*){0,4}\bArguments\s*=\s*["'][^"'\r\n]*\bprocess\b""",
            @"(?i)\bWin32_Process\b",
            @"(?i)\bManagementObjectSearcher\b"
        ];
        var pattern = patterns.FirstOrDefault(candidate => Regex.IsMatch(source, candidate));
        return pattern is null
            ? null
            : $"Forbidden process-query mechanism matched '{pattern}' in {path}.";
    }

    [Xunit.Fact(DisplayName = "GetRepoProcessInfo_reports_exact_pid_lineage_through_repo_prefix")]
    public void GetRepoProcessInfoReportsExactPidLineageThroughRepoPrefix()
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
        startInfo.ArgumentList.Add("scripts\\Get-RepoProcessInfo.ps1");
        startInfo.ArgumentList.Add("-Id");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Get-RepoProcessInfo.ps1.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(30000), "Get-RepoProcessInfo.ps1 did not exit within 30 seconds.");
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
        Assert.True(stdout.Contains($"PROCESS id={Environment.ProcessId}", StringComparison.Ordinal), stdout);
        Assert.True(stdout.Contains("parent=", StringComparison.Ordinal), stdout);
        Assert.True(stdout.Contains("command=", StringComparison.Ordinal), stdout);
    }

    [Xunit.Fact(DisplayName = "StopRepoProcess_refuses_exact_pid_when_command_guard_mismatches")]
    public void StopRepoProcessRefusesExactPidWhenCommandGuardMismatches()
    {
        var repoRoot = FindCurrentSourceRoot();
        var targetStartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        targetStartInfo.ArgumentList.Add("-NoProfile");
        targetStartInfo.ArgumentList.Add("-Command");
        targetStartInfo.ArgumentList.Add("Start-Sleep -Seconds 120");
        using var target = Process.Start(targetStartInfo)
            ?? throw new InvalidOperationException("Failed to start disposable process.");

        try
        {
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
            startInfo.ArgumentList.Add("scripts\\Stop-RepoProcess.ps1");
            startInfo.ArgumentList.Add("-Id");
            startInfo.ArgumentList.Add(target.Id.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-CommandContains");
            startInfo.ArgumentList.Add("definitely-not-in-this-process-command-line");
            startInfo.ArgumentList.Add("-Force");

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start Stop-RepoProcess.ps1.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(30000), "Stop-RepoProcess.ps1 did not exit within 30 seconds.");
            Assert.Equal(1, process.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
            Assert.True(
                stdout.Contains($"PROCESS id={target.Id} status=refused reason=command-mismatch", StringComparison.Ordinal),
                stdout);

            target.Refresh();
            Assert.False(target.HasExited, "Stop-RepoProcess.ps1 stopped a process after the command guard mismatched.");
        }
        finally
        {
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
                Assert.True(target.WaitForExit(5000), "Disposable process did not exit after test cleanup.");
            }
        }
    }

    [Xunit.Fact(DisplayName = "Infrastructure_partition_helper_keeps_reconcile_tests_in_remainder")]
    public void InfrastructurePartitionHelperKeepsReconcileTestsInRemainder()
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
        startInfo.ArgumentList.Add("scripts\\Invoke-InfrastructureTestPartition.ps1");
        startInfo.ArgumentList.Add("-List");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Invoke-InfrastructureTestPartition.ps1.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(30000), "Invoke-InfrastructureTestPartition.ps1 -List did not exit within 30 seconds.");
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("- Remainder: Infrastructure coverage outside the named focused partitions.", stdout);
        Assert.DoesNotContain("ConductorBatchLoopVerificationReconcileTests", stdout, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_from_goal_worktree_leaves_repository_status_clean")]
    public void InvokeIsolatedDotnetFromGoalWorktreeLeavesRepositoryStatusClean()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sourceRoot = FindCurrentSourceRoot();
        var repo = CreateSeededRepository();
        var sandboxPath = Path.Combine(Path.GetTempPath(), $"isolated-dotnet-worktree-{Guid.NewGuid():N}");
        var shimDirectory = Path.Combine(sandboxPath, "shim");
        var isolatedRoot = Path.Combine(sandboxPath, "isolated-root");
        Directory.CreateDirectory(shimDirectory);
        try
        {
            File.WriteAllText(Path.Combine(repo, ".gitignore"), ".orchestrator-worktrees/" + Environment.NewLine);
            RunGit(repo, "add", ".gitignore");
            RunGit(repo, "commit", "-m", "Ignore local worktrees");
            var goalId = new GoalId("feedbeeffeedbeeffeedbeeffeedbeef");
            var worktreePath = GoalWorktrees.Ensure(repo, goalId);
            Assert.Equal(string.Empty, RunGitOutput(repo, "status", "--short"));

            var logPath = Path.Combine(sandboxPath, "dotnet.log");
            var shimPath = Path.Combine(shimDirectory, "dotnet.cmd");
            File.WriteAllText(
                shimPath,
                """
                @echo off
                >> "%DOTNET_SHIM_LOG%" echo args=%*
                >> "%DOTNET_SHIM_LOG%" echo temp=%TEMP%
                if not exist "%TEMP%" mkdir "%TEMP%"
                > "%TEMP%\shim-scratch.tmp" echo scratch
                if "%~1"=="build-server" exit /b 0
                if /I not "%~1"=="build" exit /b 0
                set "ARTIFACTS="
                :parse_artifacts
                if "%~1"=="" goto create_artifacts
                if /I "%~1"=="--artifacts-path" set "ARTIFACTS=%~2"
                shift
                goto parse_artifacts
                :create_artifacts
                set "TEST_DIR=%ARTIFACTS%\bin\Fake.Infrastructure.Tests\debug"
                mkdir "%TEST_DIR%" >nul 2>nul
                > "%TEST_DIR%\Fake.Infrastructure.Tests.dll" echo managed
                > "%TEST_DIR%\Fake.Infrastructure.Tests.exe" echo apphost
                exit /b 0
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = worktreePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-InputFormat");
            startInfo.ArgumentList.Add("None");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(sourceRoot, "scripts", "Invoke-IsolatedDotnet.ps1"));
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add(goalId.Value[..8]);
            startInfo.ArgumentList.Add("test");
            startInfo.ArgumentList.Add("Fake.Infrastructure.Tests.csproj");
            startInfo.ArgumentList.Add("--filter");
            startInfo.ArgumentList.Add("FullyQualifiedName~FocusedInfrastructure");
            startInfo.Environment["PATH"] = shimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
            startInfo.Environment["DOTNET_SHIM_LOG"] = logPath;
            startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = isolatedRoot;
            startInfo.Environment.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start Invoke-IsolatedDotnet.ps1.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30000), "Invoke-IsolatedDotnet.ps1 did not exit within 30 seconds.");
            Assert.True(
                process.ExitCode == 0,
                $"Invoke-IsolatedDotnet.ps1 exited {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

            var log = File.ReadAllText(logPath);
            var repoScratch = Path.Combine(repo, ".t");
            Assert.True(!log.Contains(repoScratch, StringComparison.OrdinalIgnoreCase), log);
            var expectedProcessTempRoot = Path.Combine(Path.GetTempPath(), "pt", $"goal-{goalId.Value[..8]}");
            Assert.True(log.Contains(expectedProcessTempRoot, StringComparison.OrdinalIgnoreCase), log);
            Assert.True(!Directory.Exists(repoScratch), $"Root scratch directory should not exist: {repoScratch}");
            Assert.Equal(string.Empty, RunGitOutput(repo, "status", "--short"));
        }
        finally
        {
            DeleteDirectory(repo);
            DeleteDirectory(sandboxPath);
        }
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

    [Xunit.Fact(DisplayName = "Cli_repo_process_commands_skip_persistent_state_loading")]
    public void CliRepoProcessCommandsSkipPersistentStateLoading()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var stateRepository = new ThrowingTransactionalStateRepository();
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;

            Assert.True(CliPersistentStateRunner.SkipsKernelState(["repo-process-info", "--id", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)]));
            var output = CaptureConsole(() =>
            {
                var changed = CliPersistentStateRunner.ExecuteCommand(
                    ["repo-process-info", "--id", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)],
                    stateRepository,
                    workspace,
                    ref agents,
                    providers,
                    ref profiles,
                    ref currentGoal);
                Assert.False(changed);
            });

            Assert.Contains($"PROCESS id={Environment.ProcessId}", output);
            Assert.Equal(0, stateRepository.LoadCount);
            Assert.Equal(0, stateRepository.TransactionCount);

            Assert.True(CliPersistentStateRunner.SkipsKernelState(["repo-process-stop"]));
            var usage = Assert.ThrowsAny<ArgumentException>(() => CliPersistentStateRunner.ExecuteCommand(
                ["repo-process-stop"],
                stateRepository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
            Assert.Contains("Usage: repo-process-stop", usage.Message);
            Assert.Equal(0, stateRepository.LoadCount);
            Assert.Equal(0, stateRepository.TransactionCount);
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
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            using var lockConnection = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Mode=ReadWrite;Pooling=False;");
            lockConnection.Open();
            using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN IMMEDIATE";
            lockCommand.ExecuteNonQuery();

            // The primary helper already migrated this store; this read intentionally opens under a write lock.
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
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
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
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
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
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
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
            var stateRepository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();

            RunGit(repo, "worktree", "add", "-b", "sqlite-tool-test", linkedWorktree);

            var localWorkspace = OrchestratorWorkspace.ForDirectory(linkedWorktree);
            var localKernel = new AgentOrchestratorKernel();
            localKernel.CreateGoal("SQLite helper linked local state");
            var localStateRepository = CreateMigratedStateRepository(localWorkspace.SqliteStatePath);
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
            Assert.Contains("SQLite helper primary state", explicitResult.Stdout);
            Assert.DoesNotContain("SQLite helper linked local state", explicitResult.Stdout);
        }
        finally
        {
            DeleteDirectory(repo);
            DeleteDirectory(linkedWorktree);
        }
    }

    private const string LockQueryCommandText = "repo-process-info --locks";

    // A correct --locks report prints other processes' command lines, so the query text legitimately
    // appears inside command=. Only text outside a report row indicates a self-echo or mangled arguments.
    private static bool ContainsUnexpectedLockQueryEcho(string stdout)
    {
        foreach (var rawLine in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.IndexOf(LockQueryCommandText, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (!line.StartsWith("LOCK id=", StringComparison.Ordinal) &&
                !line.StartsWith("PROCESS id=", StringComparison.Ordinal))
            {
                return true;
            }

            var commandFieldIndex = line.IndexOf(" command=", StringComparison.Ordinal);
            if (commandFieldIndex < 0 ||
                line.IndexOf(LockQueryCommandText, 0, commandFieldIndex, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

}

[Xunit.Collection(TestCollections.GoalWorktreeCleanupHooks)]
public sealed class GoalWorktreeTestsCleanupHookDelegates : GoalWorktreeTestBase
{
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
}
