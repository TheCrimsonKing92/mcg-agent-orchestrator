using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

public sealed class LauncherScriptTests
{
    private static readonly string[] JsonLineSeparators = ["\r\n", "\n"];
    private static readonly string[] ExpectedStartCommandJsonProperties = ["args", "pid", "startedAt", "stderrPath", "stdoutPath"];
    private static readonly string?[] ExpectedAcceptanceGoalArguments = ["acceptance", "goal"];
    private static readonly string?[] ExpectedDoubleDashArguments =
    [
        "backlog-intake",
        "Launcher double dash smoke",
        "--create-goal",
        "--loop",
        "--watch",
        "--policy",
        "Permissive",
        "--poll-seconds",
        "15",
        "--max-duration",
        "00:01:00"
    ];

    [Xunit.Fact(DisplayName = "LandVerifiedGoal_stale_launcher_unknown_command_still_records_with_current_app")]
    public void LandVerifiedGoalStaleLauncherUnknownCommandStillRecordsWithCurrentApp()
    {
        using var sandbox = CreateLandVerifiedGoalSandbox("""
            @echo off
            echo %*>> dotnet-args.txt
            if "%~1"=="build" exit /b 0
            if "%~3"=="" (
              echo Usage: goal-mark-landed ^<goal-prefix^> --confirm-goal-mark-landed
              exit /b 0
            )
            echo marked
            exit /b 0
            """);

        var result = RunLandVerifiedGoal(sandbox.RepositoryPath);

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Assert.Contains("[land] DONE", result.Stdout);
        Assert.True(File.Exists(Path.Combine(sandbox.RepositoryPath, "goal.txt")));
        var dotnetArgs = File.ReadAllText(Path.Combine(sandbox.RepositoryPath, "dotnet-args.txt"));
        Assert.True(
            dotnetArgs.Contains(
                "Mcg.AgentOrchestrator.App.dll goal-mark-landed abcdef12 --confirm-goal-mark-landed",
                StringComparison.Ordinal),
            dotnetArgs);
    }

    [Xunit.Fact(DisplayName = "LandVerifiedGoal_goal_mark_landed_nonzero_exit_exits_nonzero_without_done")]
    public void LandVerifiedGoalGoalMarkLandedNonzeroExitExitsNonzeroWithoutDone()
    {
        using var sandbox = CreateLandVerifiedGoalSandbox("""
            @echo off
            if "%~1"=="build" exit /b 0
            if "%~3"=="" (
              echo Usage: goal-mark-landed ^<goal-prefix^> --confirm-goal-mark-landed
              exit /b 0
            )
            echo bookkeeping failed
            exit /b 7
            """);

        var result = RunLandVerifiedGoal(sandbox.RepositoryPath);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("[land] DONE", result.Stdout);
        Assert.Contains("goal-mark-landed", result.Stderr);
        Assert.Contains("Exit code: 7", result.Stderr);
        Assert.Contains("bookkeeping failed", result.Stderr);
    }

    [Xunit.Fact(DisplayName = "LandVerifiedGoal_goal_mark_landed_success_exits_zero_and_prints_done")]
    public void LandVerifiedGoalGoalMarkLandedSuccessExitsZeroAndPrintsDone()
    {
        using var sandbox = CreateLandVerifiedGoalSandbox("""
            @echo off
            if "%~1"=="build" exit /b 0
            if "%~3"=="" (
              echo Usage: goal-mark-landed ^<goal-prefix^> --confirm-goal-mark-landed
              exit /b 0
            )
            echo marked
            exit /b 0
            """);

        var result = RunLandVerifiedGoal(sandbox.RepositoryPath);

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Assert.Contains("[land] DONE", result.Stdout);
    }

    [Xunit.Fact(DisplayName = "Launcher_rebuild_freshness_includes_git_head_marker")]
    public void LauncherRebuildFreshnessIncludesGitHeadMarker()
    {
        var repoRoot = FindLauncherSourceRoot();
        var launcher = File.ReadAllText(Path.Combine(repoRoot, "mcg-orchestrator.cmd"));

        Assert.True(launcher.Contains("App.dll.git-head", StringComparison.Ordinal));
        Assert.True(launcher.Contains("rev-parse HEAD", StringComparison.Ordinal));
        Assert.True(launcher.Contains("Set-Content -LiteralPath '%APP_HEAD%'", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ResolveRunDir_repopulates_cached_copy_when_native_sqlite_asset_is_missing")]
    public void ResolveRunDirRepopulatesCachedCopyWhenNativeSqliteAssetIsMissing()
    {
        var repoRoot = FindLauncherSourceRoot();
        var root = Path.Combine(Path.GetTempPath(), $"resolve-run-dir-{Guid.NewGuid():N}");
        var appOutput = Path.Combine(root, "app");
        Directory.CreateDirectory(appOutput);
        try
        {
            var appDll = Path.Combine(appOutput, "Mcg.AgentOrchestrator.App.dll");
            File.WriteAllText(appDll, "fake app");
            var nativeSource = Path.Combine(appOutput, "runtimes", "win-x64", "native", "e_sqlite3.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(nativeSource)!);
            File.WriteAllText(nativeSource, "native");

            var runDir = Path.Combine(root, "mcg-run", AppDllHashPrefix(appDll));
            Directory.CreateDirectory(runDir);
            File.Copy(appDll, Path.Combine(runDir, Path.GetFileName(appDll)));

            var result = RunPowerShellCommand(repoRoot, $"""
                $ErrorActionPreference = 'Stop'
                $env:TEMP = '{EscapePowerShellSingleQuoted(root)}'
                $env:TMP = '{EscapePowerShellSingleQuoted(root)}'
                & '{EscapePowerShellSingleQuoted(Path.Combine(repoRoot, "scripts", "resolve-run-dir.ps1"))}' '{EscapePowerShellSingleQuoted(appDll)}'
                """);

            Assert.Equal(0, result.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            Assert.Equal(runDir, result.Stdout.Trim());
            Assert.True(File.Exists(Path.Combine(runDir, "runtimes", "win-x64", "native", "e_sqlite3.dll")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ResolveRunDir_missing_native_sqlite_asset_fails_with_repair_action")]
    public void ResolveRunDirMissingNativeSqliteAssetFailsWithRepairAction()
    {
        var repoRoot = FindLauncherSourceRoot();
        var root = Path.Combine(Path.GetTempPath(), $"resolve-run-dir-{Guid.NewGuid():N}");
        var appOutput = Path.Combine(root, "app");
        Directory.CreateDirectory(appOutput);
        try
        {
            var appDll = Path.Combine(appOutput, "Mcg.AgentOrchestrator.App.dll");
            File.WriteAllText(appDll, "fake app");

            var result = RunPowerShellCommand(repoRoot, $"""
                $env:TEMP = '{EscapePowerShellSingleQuoted(root)}'
                $env:TMP = '{EscapePowerShellSingleQuoted(root)}'
                & '{EscapePowerShellSingleQuoted(Path.Combine(repoRoot, "scripts", "resolve-run-dir.ps1"))}' '{EscapePowerShellSingleQuoted(appDll)}'
                """);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(result.Stdout), result.Stdout);
            Assert.True(result.Stderr.Contains("e_sqlite3", StringComparison.Ordinal), result.Stderr);
            Assert.True(result.Stderr.Contains("dotnet build", StringComparison.Ordinal), result.Stderr);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "InvokeRepoScript_no_trailing_arguments_forwards_zero_arguments")]
    public void InvokeRepoScriptNoTrailingArgumentsForwardsZeroArguments()
    {
        using var sandbox = CreateRepoScriptArgumentSandbox();

        var result = RunInvokeRepoScript(sandbox.RepositoryRoot, sandbox.RelativeScriptPath);

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        AssertForwardedArguments(result.Stdout, []);
    }

    [Xunit.Fact(DisplayName = "InvokeRepoScript_empty_argument_splat_forwards_zero_arguments")]
    public void InvokeRepoScriptEmptyArgumentSplatForwardsZeroArguments()
    {
        using var sandbox = CreateRepoScriptArgumentSandbox();
        var wrapperPath = Path.Combine(sandbox.RepositoryRoot, "scripts", "Invoke-RepoScript.ps1");

        var result = RunPowerShellCommand(sandbox.RepositoryRoot, $"""
            $ErrorActionPreference = 'Stop'
            $arguments = @()
            & '{EscapePowerShellSingleQuoted(wrapperPath)}' '{EscapePowerShellSingleQuoted(sandbox.RelativeScriptPath)}' @arguments
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        AssertForwardedArguments(result.Stdout, []);
    }

    [Xunit.Fact(DisplayName = "InvokeRepoScript_non_empty_arguments_are_forwarded_in_order")]
    public void InvokeRepoScriptNonEmptyArgumentsAreForwardedInOrder()
    {
        using var sandbox = CreateRepoScriptArgumentSandbox();
        var wrapperPath = Path.Combine(sandbox.RepositoryRoot, "scripts", "Invoke-RepoScript.ps1");

        var result = RunPowerShellCommand(sandbox.RepositoryRoot, $"""
            $ErrorActionPreference = 'Stop'
            $arguments = @('alpha', ' ', ' gamma ')
            & '{EscapePowerShellSingleQuoted(wrapperPath)}' '{EscapePowerShellSingleQuoted(sandbox.RelativeScriptPath)}' @arguments
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        AssertForwardedArguments(result.Stdout, ["alpha", " ", " gamma "]);
    }

    [Xunit.Fact(DisplayName = "InvokeRepoScript_orchestrator_sqlite_tool_list_goals_smoke")]
    public void InvokeRepoScriptOrchestratorSqliteToolListGoalsSmoke()
    {
        var repoRoot = FindRepositoryRoot();
        var dbPath = CreateSqliteToolSmokeDb();
        try
        {
            var wrapperPath = Path.Combine(repoRoot, "scripts", "Invoke-RepoScript.ps1");
            var result = RunPowerShellCommand(repoRoot, $"""
                $ErrorActionPreference = 'Stop'
                & '{EscapePowerShellSingleQuoted(wrapperPath)}' 'scripts\Invoke-OrchestratorSqliteTool.ps1' list-goals --db '{EscapePowerShellSingleQuoted(dbPath)}' --limit 10
                """);

            Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}; stdout={result.Stdout}; stderr={result.Stderr}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            Assert.Contains("SQLite wrapper smoke", result.Stdout);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Xunit.Fact(DisplayName = "GetOrchestratorSnapshot_status_timeout_kills_owned_status_process_and_reports_partial_data")]
    public void GetOrchestratorSnapshotStatusTimeoutKillsOwnedStatusProcessAndReportsPartialData()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = CreateSnapshotStatusSandbox();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = sandbox.RepositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = sandbox.DotnetShimPath;
            startInfo.Environment["DOTNET_STATUS_SENTINEL"] = sandbox.SentinelPath;
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(sandbox.RepositoryRoot, "scripts", "Get-OrchestratorSnapshot.ps1"));
            startInfo.ArgumentList.Add("-StatusTimeoutSeconds");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("hang");
            startInfo.ArgumentList.Add("ok");

            var stopwatch = Stopwatch.StartNew();
            var result = RunProcess(startInfo, "Get-OrchestratorSnapshot.ps1");
            stopwatch.Stop();

            Assert.True(
                result.ExitCode == 0,
                $"exit={result.ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Snapshot took {stopwatch.Elapsed}.");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            Assert.Contains("partial hang", result.Stdout);
            Assert.Contains("status timed out after 1s; killed pid=", result.Stdout);
            Assert.Contains("status ok ok", result.Stdout);
            WaitForFile(sandbox.SentinelPath, TimeSpan.FromSeconds(5));
            var childPid = int.Parse(File.ReadAllText(sandbox.SentinelPath).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(!IsProcessRunning(childPid), $"Expected hung status child pid {childPid} to be reaped.");
        }
        finally
        {
            sandbox.KillRecordedChild();
        }
    }

    [Xunit.Fact(DisplayName = "StartOrchestratorCommand_emits_json_pid_and_log_path_through_repo_script")]
    public void StartOrchestratorCommandEmitsJsonPidAndLogPathThroughRepoScript()
    {
        var repoRoot = Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            repoRoot = FindRepositoryRoot();
        }

        var appDll = Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll");
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
        startInfo.ArgumentList.Add("scripts\\Start-OrchestratorCommand.ps1");
        startInfo.ArgumentList.Add("-Name");
        startInfo.ArgumentList.Add("launcher-json-test");
        startInfo.ArgumentList.Add("-AppDll");
        startInfo.ArgumentList.Add(appDll);
        startInfo.ArgumentList.Add("goals");

        using var launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start launcher script.");
        var stdout = launcher.StandardOutput.ReadToEnd();
        var stderr = launcher.StandardError.ReadToEnd();
        Assert.True(launcher.WaitForExit(20000), "Launcher script did not exit within 20 seconds.");
        Assert.Equal(0, launcher.ExitCode);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);

        var outputLines = stdout.Split(
            JsonLineSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(outputLines);

        using var document = JsonDocument.Parse(outputLines[0]);
        var root = document.RootElement;
        var properties = root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(ExpectedStartCommandJsonProperties, properties);

        var pid = root.GetProperty("pid").GetInt32();
        var stdoutPath = root.GetProperty("stdoutPath").GetString();
        var stderrPath = root.GetProperty("stderrPath").GetString();
        var args = root.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray();
        var startedAt = root.GetProperty("startedAt").GetString();

        Assert.True(pid > 0);
        Assert.False(string.IsNullOrWhiteSpace(stdoutPath));
        Assert.False(string.IsNullOrWhiteSpace(stderrPath));
        Assert.True(Path.IsPathFullyQualified(stdoutPath!));
        Assert.True(Path.IsPathFullyQualified(stderrPath!));
        Assert.Equal(new[] { appDll, "goals" }, args);
        Assert.True(DateTimeOffset.TryParse(startedAt, out _), $"Expected parseable startedAt, got '{startedAt}'.");

        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.WaitForExit(20000), $"Launched command pid {pid} did not exit within 20 seconds.");
        }
        catch (ArgumentException)
        {
            // Short commands can exit before the test reopens the emitted PID.
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!File.Exists(stdoutPath) && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }

        Assert.True(File.Exists(stdoutPath), $"Expected launcher log path to exist: {stdoutPath}");
        Assert.True(File.Exists(stderrPath), $"Expected launcher stderr path to exist: {stderrPath}");
    }

    [Xunit.Fact(DisplayName = "StartOrchestratorCommand_forwards_double_dash_arguments_to_background_process")]
    public void StartOrchestratorCommandForwardsDoubleDashArgumentsToBackgroundProcess()
    {
        var repoRoot = Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            repoRoot = FindRepositoryRoot();
        }

        using var sandbox = CreateDoubleDashLauncherSandbox();
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = sandbox.HostPath;
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repoRoot, "scripts", "Invoke-RepoScript.ps1"));
        startInfo.ArgumentList.Add("scripts\\Start-OrchestratorCommand.ps1");
        startInfo.ArgumentList.Add("-Name");
        startInfo.ArgumentList.Add("launcher-double-dash-test");
        startInfo.ArgumentList.Add("-AppDll");
        startInfo.ArgumentList.Add(sandbox.EchoScriptPath);
        foreach (var argument in ExpectedDoubleDashArguments)
        {
            startInfo.ArgumentList.Add(argument!);
        }

        using var launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start launcher script.");
        var stdout = launcher.StandardOutput.ReadToEnd();
        var stderr = launcher.StandardError.ReadToEnd();
        Assert.True(launcher.WaitForExit(20000), "Launcher script did not exit within 20 seconds.");
        Assert.Equal(0, launcher.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);

        var outputLines = stdout.Split(
            JsonLineSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(outputLines);

        using var launcherDocument = JsonDocument.Parse(outputLines[0]);
        var launcherRoot = launcherDocument.RootElement;
        var pid = launcherRoot.GetProperty("pid").GetInt32();
        var stdoutPath = launcherRoot.GetProperty("stdoutPath").GetString()
            ?? throw new InvalidOperationException("Launcher did not emit stdoutPath.");
        var stderrPath = launcherRoot.GetProperty("stderrPath").GetString()
            ?? throw new InvalidOperationException("Launcher did not emit stderrPath.");
        var emittedArgs = launcherRoot.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray();
        Assert.Equal(new[] { sandbox.EchoScriptPath }.Concat(ExpectedDoubleDashArguments).ToArray(), emittedArgs);

        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.WaitForExit(20000), $"Launched command pid {pid} did not exit within 20 seconds.");
        }
        catch (ArgumentException)
        {
            // Short commands can exit before the test reopens the emitted PID.
        }

        WaitForFile(stdoutPath, TimeSpan.FromSeconds(10));
        WaitForFile(stderrPath, TimeSpan.FromSeconds(10));
        Assert.True(string.IsNullOrWhiteSpace(File.ReadAllText(stderrPath)), File.ReadAllText(stderrPath));

        using var childDocument = JsonDocument.Parse(File.ReadAllText(stdoutPath));
        var childArgs = childDocument.RootElement.EnumerateArray().Select(argument => argument.GetString()).ToArray();
        Assert.Equal(ExpectedDoubleDashArguments, childArgs);
    }

    [Xunit.Fact(DisplayName = "StartOrchestratorCommand_launch_failure_exits_nonzero_with_error_json_on_stderr")]
    public void StartOrchestratorCommandLaunchFailureExitsNonzeroWithErrorJsonOnStderr()
    {
        var repoRoot = FindLauncherSourceRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = Path.Combine(
            Path.GetTempPath(),
            $"missing-dotnet-{Guid.NewGuid():N}.exe");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repoRoot, "scripts", "Invoke-RepoScript.ps1"));
        startInfo.ArgumentList.Add("scripts\\Start-OrchestratorCommand.ps1");
        startInfo.ArgumentList.Add("-Name");
        startInfo.ArgumentList.Add("launcher-failure-test");
        startInfo.ArgumentList.Add("-AppDll");
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"));
        startInfo.ArgumentList.Add("goals");

        using var launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start launcher script.");
        var stdout = launcher.StandardOutput.ReadToEnd();
        var stderr = launcher.StandardError.ReadToEnd();
        Assert.True(launcher.WaitForExit(20000), "Launcher script did not exit within 20 seconds.");
        Assert.NotEqual(0, launcher.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stdout), stdout);

        var errorLines = stderr.Split(
            JsonLineSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(errorLines);

        using var document = JsonDocument.Parse(errorLines[0]);
        var root = document.RootElement;
        var reason = root.GetProperty("reason").GetString() ?? string.Empty;
        Assert.True(reason.Contains("cannot find", StringComparison.OrdinalIgnoreCase), reason);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("stdoutPath").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("stderrPath").GetString()));
        Assert.Equal(
            new[] { Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"), "goals" },
            root.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray());
    }

    [Xunit.Fact(DisplayName = "StartOrchestratorCommand_keeps_AppDll_named_only_and_forwards_remaining_arguments")]
    public void StartOrchestratorCommandKeepsAppDllNamedOnlyAndForwardsRemainingArguments()
    {
        var repoRoot = FindLauncherSourceRoot();
        var script = File.ReadAllText(Path.Combine(repoRoot, "scripts", "Start-OrchestratorCommand.ps1"));

        Assert.True(script.Contains("[CmdletBinding(PositionalBinding = $false)]", StringComparison.Ordinal));
        Assert.True(script.Contains("[Parameter(ValueFromRemainingArguments = $true)]", StringComparison.Ordinal));
        Assert.True(script.Contains("[string[]]$Arguments", StringComparison.Ordinal));
        Assert.True(script.Contains("$processArguments = @($resolvedAppDll) + $Arguments", StringComparison.Ordinal));
        Assert.False(script.Contains("Position =", StringComparison.OrdinalIgnoreCase));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            function Test-Binding {
                [CmdletBinding(PositionalBinding = $false)]
                param(
                    [string]$Name = 'command',
                    [string]$AppDll,
                    [Parameter(ValueFromRemainingArguments = $true)]
                    [string[]]$Arguments
                )
                [pscustomobject]@{
                    appDll = $AppDll
                    args = @($Arguments)
                } | ConvertTo-Json -Compress
            }
            Test-Binding -Name smoke acceptance goal
            """);

        using var bindingProbe = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PowerShell binding probe.");
        var stdout = bindingProbe.StandardOutput.ReadToEnd();
        var stderr = bindingProbe.StandardError.ReadToEnd();
        Assert.True(bindingProbe.WaitForExit(20000), "PowerShell binding probe did not exit within 20 seconds.");
        Assert.Equal(0, bindingProbe.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal(string.Empty, root.GetProperty("appDll").GetString());
        Assert.Equal(
            ExpectedAcceptanceGoalArguments,
            root.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray());
    }

    private static string FindLauncherSourceRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))
            ?? throw new DirectoryNotFoundException($"Could not resolve source directory from '{sourceFilePath}'."));
        while (directory is not null)
        {
            var launcherPath = Path.Combine(directory.FullName, "mcg-orchestrator.cmd");
            var landVerifiedGoalPath = Path.Combine(directory.FullName, "scripts", "Land-VerifiedGoal.ps1");

            if (File.Exists(launcherPath) && File.Exists(landVerifiedGoalPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate launcher source files from source file path '{sourceFilePath}'.");
    }

    private static DoubleDashLauncherSandbox CreateDoubleDashLauncherSandbox()
    {
        var sandboxPath = Path.Combine(Path.GetTempPath(), $"launcher-double-dash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandboxPath);

        var hostPath = Path.Combine(sandboxPath, "fake-dotnet.cmd");
        File.WriteAllText(hostPath, """
            @echo off
            powershell.exe -NoProfile -ExecutionPolicy Bypass -File %*
            """);

        var echoScriptPath = Path.Combine(sandboxPath, "echo-args.ps1");
        File.WriteAllText(echoScriptPath, """
            param(
                [Parameter(ValueFromRemainingArguments = $true)]
                [string[]]$Arguments
            )

            $Arguments | ConvertTo-Json -Compress
            """);

        return new DoubleDashLauncherSandbox(sandboxPath, hostPath, echoScriptPath);
    }

    private static RepoScriptArgumentSandbox CreateRepoScriptArgumentSandbox()
    {
        var repoRoot = FindLauncherSourceRoot();
        var relativeDirectory = Path.Combine(".scratch", "invoke-repo-script-tests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(repoRoot, relativeDirectory);
        Directory.CreateDirectory(directory);

        var scriptPath = Path.Combine(directory, "record-arguments.ps1");
        File.WriteAllText(scriptPath, """
            param(
                [Parameter(ValueFromRemainingArguments = $true)]
                [object[]]$Arguments
            )

            $forwarded = if ($null -eq $Arguments -or $Arguments.Count -eq 0) {
                @()
            } else {
                @($Arguments | ForEach-Object { [string]$_ })
            }

            [pscustomobject]@{
                count = $forwarded.Count
                args = @($forwarded)
            } | ConvertTo-Json -Compress
            """);

        return new RepoScriptArgumentSandbox(repoRoot, Path.Combine(relativeDirectory, "record-arguments.ps1"), directory);
    }

    private static ProcessResult RunInvokeRepoScript(string repositoryRoot, string relativeScriptPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "scripts", "Invoke-RepoScript.ps1"));
        startInfo.ArgumentList.Add(relativeScriptPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return RunProcess(startInfo, "Invoke-RepoScript.ps1");
    }

    private static ProcessResult RunPowerShellCommand(string repositoryRoot, string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        return RunProcess(startInfo, "PowerShell command");
    }

    private static void AssertForwardedArguments(string stdout, string[] expected)
    {
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal(expected.Length, root.GetProperty("count").GetInt32());
        Assert.Equal(expected, root.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray());
    }

    private static string EscapePowerShellSingleQuoted(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static string AppDllHashPrefix(string appDll)
    {
        using var stream = File.OpenRead(appDll);
        return Convert.ToHexString(SHA1.HashData(stream)).Substring(0, 16);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static SnapshotStatusSandbox CreateSnapshotStatusSandbox()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"snapshot-status-{Guid.NewGuid():N}");
        var scriptsPath = Path.Combine(repositoryRoot, "scripts");
        var appPath = Path.Combine(repositoryRoot, "src", "Mcg.AgentOrchestrator.App", "bin", "Debug", "net10.0");
        var shimPath = Path.Combine(repositoryRoot, "shim");
        Directory.CreateDirectory(scriptsPath);
        Directory.CreateDirectory(appPath);
        Directory.CreateDirectory(shimPath);

        File.Copy(
            Path.Combine(FindLauncherSourceRoot(), "scripts", "Get-OrchestratorSnapshot.ps1"),
            Path.Combine(scriptsPath, "Get-OrchestratorSnapshot.ps1"));
        File.WriteAllText(
            Path.Combine(scriptsPath, "Invoke-OrchestratorSqliteTool.ps1"),
            "Write-Output 'No active goals in snapshot sandbox.'\r\nexit 0\r\n");
        File.WriteAllText(Path.Combine(appPath, "Mcg.AgentOrchestrator.App.dll"), "dummy");

        var dotnetShimPath = Path.Combine(shimPath, "dotnet.cmd");
        File.WriteAllText(
            dotnetShimPath,
            """
            @echo off
            if "%~3"=="hang" (
              echo partial hang
              powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Set-Content -LiteralPath $env:DOTNET_STATUS_SENTINEL -Value $PID; Start-Sleep -Seconds 60"
              exit /b 0
            )
            echo status ok %~3
            exit /b 0
            """.Replace("\n", "\r\n", StringComparison.Ordinal));

        return new SnapshotStatusSandbox(
            repositoryRoot,
            dotnetShimPath,
            Path.Combine(repositoryRoot, "status-child.pid"));
    }

    private static LandVerifiedGoalSandbox CreateLandVerifiedGoalSandbox(string dotnetShimBody)
    {
        const string goalPrefix = "abcdef12";
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"land-verified-goal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repositoryPath, "scripts"));
        var shimPath = Path.Combine(repositoryPath, "shim");
        Directory.CreateDirectory(shimPath);
        File.Copy(
            Path.Combine(FindLauncherSourceRoot(), "scripts", "Land-VerifiedGoal.ps1"),
            Path.Combine(repositoryPath, "scripts", "Land-VerifiedGoal.ps1"));
        File.WriteAllText(Path.Combine(repositoryPath, "mcg-orchestrator.cmd"), """
            @echo off
            echo Unknown command.
            exit /b 0
            """);
        var dotnetShimPath = Path.Combine(shimPath, "dotnet.cmd");
        File.WriteAllText(dotnetShimPath, dotnetShimBody);

        RunGit(repositoryPath, "init", "--initial-branch=main");
        RunGit(repositoryPath, "config", "user.email", "test@example.invalid");
        RunGit(repositoryPath, "config", "user.name", "Launcher Script Test");
        File.WriteAllText(Path.Combine(repositoryPath, "README.md"), "base");
        RunGit(repositoryPath, "add", "README.md");
        RunGit(repositoryPath, "commit", "-m", "base");
        RunGit(repositoryPath, "checkout", "-b", $"goal/{goalPrefix}");
        File.WriteAllText(Path.Combine(repositoryPath, "goal.txt"), "goal change");
        RunGit(repositoryPath, "add", "goal.txt");
        RunGit(repositoryPath, "commit", "-m", "goal change");
        RunGit(repositoryPath, "checkout", "main");

        return new LandVerifiedGoalSandbox(repositoryPath, dotnetShimPath);
    }

    private static ProcessResult RunLandVerifiedGoal(string repositoryPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var dotnetShimPath = Path.Combine(repositoryPath, "shim", "dotnet.cmd");
        startInfo.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = dotnetShimPath;
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repositoryPath, "scripts", "Land-VerifiedGoal.ps1"));
        startInfo.ArgumentList.Add("-GoalPrefix");
        startInfo.ArgumentList.Add("abcdef12");
        startInfo.ArgumentList.Add("-SkipAcceptance");

        return RunProcess(startInfo, "Land-VerifiedGoal.ps1");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var result = RunProcess(startInfo, $"git {string.Join(' ', arguments)}");
        Assert.Equal(0, result.ExitCode);
    }

    private static ProcessResult RunProcess(ProcessStartInfo startInfo, string description)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {description}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(90000), $"{description} did not exit within 90 seconds.");
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string CreateSqliteToolSmokeDb()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sqlite-tool-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "state.db");
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False;");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE goals (
                id TEXT NOT NULL PRIMARY KEY,
                status TEXT NOT NULL,
                snapshot_json TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                version INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO goals (id, status, snapshot_json, updated_at, version)
            VALUES ('11111111111111111111111111111111', 'Active', '{"Objective":"SQLite wrapper smoke"}', '2026-06-30T00:00:00.0000000Z', 1);
            """;
        create.ExecuteNonQuery();
        return dbPath;
    }

    private static void WaitForFile(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!File.Exists(path) && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }

        Assert.True(File.Exists(path), $"Expected file to exist: {path}");
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class SnapshotStatusSandbox(
        string repositoryRoot,
        string dotnetShimPath,
        string sentinelPath) : IDisposable
    {
        public string RepositoryRoot { get; } = repositoryRoot;
        public string DotnetShimPath { get; } = dotnetShimPath;
        public string SentinelPath { get; } = sentinelPath;

        public void KillRecordedChild()
        {
            if (!File.Exists(SentinelPath))
            {
                return;
            }

            if (!int.TryParse(File.ReadAllText(SentinelPath).Trim(), out var pid))
            {
                return;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Dispose()
        {
            KillRecordedChild();
            try
            {
                Directory.Delete(RepositoryRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class DoubleDashLauncherSandbox(
        string sandboxPath,
        string hostPath,
        string echoScriptPath) : IDisposable
    {
        public string HostPath { get; } = hostPath;
        public string EchoScriptPath { get; } = echoScriptPath;

        public void Dispose()
        {
            try
            {
                Directory.Delete(sandboxPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class RepoScriptArgumentSandbox(
        string repositoryRoot,
        string relativeScriptPath,
        string sandboxPath) : IDisposable
    {
        public string RepositoryRoot { get; } = repositoryRoot;
        public string RelativeScriptPath { get; } = relativeScriptPath;

        public void Dispose()
        {
            try
            {
                Directory.Delete(sandboxPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class LandVerifiedGoalSandbox(string repositoryPath, string dotnetShimPath) : IDisposable
    {
        public string RepositoryPath { get; } = repositoryPath;
        public string DotnetShimPath { get; } = dotnetShimPath;

        public void Dispose()
        {
            try
            {
                Directory.Delete(RepositoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private static string FindRepositoryRoot()
    {
        return TryFindRepositoryRoot(Environment.CurrentDirectory)
            ?? TryFindRepositoryRoot(AppContext.BaseDirectory)
            ?? InfrastructureTestSupport.FindRepositoryRoot();
    }

    private static string? TryFindRepositoryRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(candidate));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
