using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

[Xunit.Collection("ProcessSpawning")]
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

    [Xunit.Fact(DisplayName = "Launcher_rebuild_freshness_includes_git_head_marker")]
    public void LauncherRebuildFreshnessIncludesGitHeadMarker()
    {
        var repoRoot = FindLauncherSourceRoot();
        var launcher = File.ReadAllText(Path.Combine(repoRoot, "mcg-orchestrator.cmd"));

        Assert.True(launcher.Contains("App.dll.git-head", StringComparison.Ordinal));
        Assert.True(launcher.Contains("rev-parse HEAD", StringComparison.Ordinal));
        Assert.True(launcher.Contains("scripts\\Update-AppDllGitHeadMarker.ps1", StringComparison.Ordinal));
        Assert.True(launcher.Contains("MCG_ORCHESTRATOR_DOTNET_PATH", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "OperatorCommands_points_landing_recovery_at_acceptance")]
    public void OperatorCommandsPointsLandingRecoveryAtAcceptance()
    {
        var repoRoot = FindLauncherSourceRoot();
        var helpSource = File.ReadAllText(Path.Combine(
            repoRoot,
            "src",
            "Mcg.AgentOrchestrator.App",
            "Cli",
            "CliCommandHelp.cs"));

        Assert.Contains("Acceptance:", helpSource);
        Assert.Contains("Invoke-OrchestratorCommand.ps1", helpSource);
        Assert.Contains("acceptance <goal>", helpSource);
        Assert.DoesNotContain(RetiredManualLandingScriptName(), helpSource, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "Runbook_documents_acceptance_rerun_without_gate_bypass")]
    public void RunbookDocumentsAcceptanceRerunWithoutGateBypass()
    {
        var repoRoot = FindLauncherSourceRoot();
        var runbook = File.ReadAllText(Path.Combine(repoRoot, "docs", "operator-runbook.md"));

        Assert.True(RunbookStatesNoGateBypassNorm(runbook));
        Assert.DoesNotContain(RetiredManualLandingScriptName(), runbook, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void RunbookNoGateBypassNorm_RemovedWarning_ReturnsFalse()
    {
        const string runbook = "Fix the gate defect before continuing through normal recovery.";

        Assert.False(RunbookStatesNoGateBypassNorm(runbook));
    }

    [Xunit.Fact]
    public void RunbookNoGateBypassNorm_RewordedAndReflowed_ReturnsTrue()
    {
        const string reworded = "Resolve the defect first; do not bypass the gate while recovery proceeds.";
        const string recasedAndReflowed = """
            Do not bypass
            the gate while recovery proceeds.
            """;

        Assert.True(RunbookStatesNoGateBypassNorm(reworded));
        Assert.True(RunbookStatesNoGateBypassNorm(recasedAndReflowed));
    }

    [Xunit.Fact(DisplayName = "Permission_allowlist_keeps_acceptance_and_excludes_retired_manual_landing_script")]
    public void PermissionAllowlistKeepsAcceptanceAndExcludesRetiredManualLandingScript()
    {
        var repoRoot = FindLauncherSourceRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, ".claude", "settings.json")));
        var allow = document.RootElement
            .GetProperty("permissions")
            .GetProperty("allow")
            .EnumerateArray()
            .Select(entry => entry.GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(allow, entry => entry.Contains("acceptance", StringComparison.OrdinalIgnoreCase));
        Xunit.Assert.False(
            allow.Any(entry => entry.Contains(RetiredManualLandingScriptName(), StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, allow));
    }

    [Xunit.Fact(DisplayName = "InvokeOrchestratorCommand_default_path_uses_fresh_launcher")]
    public void InvokeOrchestratorCommandDefaultPathUsesFreshLauncher()
    {
        using var sandbox = CreateDefaultLauncherSandbox();

        var result = RunInvokeRepoScript(
            sandbox.RepositoryRoot,
            "scripts\\Invoke-OrchestratorCommand.ps1",
            "workspace",
            "remove",
            "abc12345");

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Assert.Equal("launcher workspace remove abc12345", File.ReadAllText(sandbox.InvocationPath).Trim());
    }

    [Xunit.Fact(DisplayName = "InvokeOrchestratorCommand_default_path_refreshes_git_head_marker_after_stale_rebuild")]
    public void InvokeOrchestratorCommandDefaultPathRefreshesGitHeadMarkerAfterStaleRebuild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = CreateStaleMarkerLauncherSandbox();
        var result = RunInvokeRepoScript(
            sandbox.RepositoryRoot,
            "scripts\\Invoke-OrchestratorCommand.ps1",
            new Dictionary<string, string?> { ["MCG_ORCHESTRATOR_DOTNET_PATH"] = sandbox.DotnetShimPath },
            "help",
            "operator-commands");

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Assert.Equal(sandbox.ExpectedHead, File.ReadAllText(sandbox.MarkerPath).Trim());
        Assert.Contains("build ", File.ReadAllText(sandbox.DotnetLogPath));
    }

    [Xunit.Fact(DisplayName = "StartOrchestratorCommand_default_path_starts_fresh_launcher")]
    public void StartOrchestratorCommandDefaultPathStartsFreshLauncher()
    {
        using var sandbox = CreateDefaultLauncherSandbox(includeStartCommand: true);
        var result = RunInvokeRepoScript(
            sandbox.RepositoryRoot,
            "scripts\\Start-OrchestratorCommand.ps1",
            "-Name",
            "fresh-launcher-test",
            "workspace",
            "remove",
            "abc12345");

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        var args = root.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray();
        var launcherPath = Path.Combine(sandbox.RepositoryRoot, "mcg-orchestrator.cmd");
        Assert.Equal(new[] { launcherPath, "workspace", "remove", "abc12345" }, args);

        var stdoutPath = root.GetProperty("stdoutPath").GetString()
            ?? throw new InvalidOperationException("Start command did not emit stdoutPath.");
        WaitForFile(stdoutPath, TimeSpan.FromSeconds(10));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!File.Exists(sandbox.InvocationPath) && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }

        Assert.Equal("launcher workspace remove abc12345", File.ReadAllText(sandbox.InvocationPath).Trim());
    }

    [Xunit.Fact(DisplayName = "StartOrchestratorCommand_writes_last_drive_journal_for_conduct_loop")]
    public void StartOrchestratorCommandWritesLastDriveJournalForConductLoop()
    {
        using var sandbox = CreateStartJournalSandbox();
        var result = RunInvokeRepoScript(
            sandbox.RepositoryRoot,
            "scripts\\Start-OrchestratorCommand.ps1",
            new Dictionary<string, string?> { ["MCG_ORCHESTRATOR_DOTNET_PATH"] = sandbox.HostPath },
            "-Name",
            "batch055",
            "-AppDll",
            sandbox.EchoScriptPath,
            "conduct",
            "--loop",
            "--watch",
            "--policy",
            "Permissive",
            "--poll-seconds",
            "15",
            "--max-duration",
            "5400");

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);

        var journalPath = Path.Combine(sandbox.RepositoryRoot, ".orchestrator", "last-drive.json");
        Assert.True(File.Exists(journalPath), $"Expected journal: {journalPath}");
        using var document = JsonDocument.Parse(File.ReadAllText(journalPath));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("batch055", root.GetProperty("name").GetString());
        Assert.Equal(sandbox.EchoScriptPath, root.GetProperty("appDll").GetString());
        Assert.Equal("Permissive", root.GetProperty("policy").GetString());
        Assert.Equal("15", root.GetProperty("pollSeconds").GetString());
        Assert.Equal("5400", root.GetProperty("maxDuration").GetString());
        Assert.True(root.GetProperty("watch").GetBoolean());

        var args = root.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()).ToArray();
        Assert.Equal(
            new[] { "conduct", "--loop", "--watch", "--policy", "Permissive", "--poll-seconds", "15", "--max-duration", "5400" },
            args);
    }

    [Xunit.Fact(DisplayName = "ResumeOrchestratorLoop_noops_when_conduct_loop_is_running")]
    public void ResumeOrchestratorLoopNoopsWhenConductLoopIsRunning()
    {
        using var sandbox = CreateResumeSandbox("Write-Output 'PROCESS id=123 kind=conduct-loop command=conduct --loop'");
        WriteLastDriveJournal(sandbox.RepositoryRoot, "batch60");

        var result = RunInvokeRepoScript(sandbox.RepositoryRoot, "scripts\\Resume-OrchestratorLoop.ps1");

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Assert.Contains("RESUME_SKIPPED reason=conduct-loop-running", result.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.StartInvocationPath), "Resume should not relaunch when a conduct loop is running.");
    }

    [Xunit.Fact(DisplayName = "ResumeOrchestratorLoop_noops_when_stop_file_exists")]
    public void ResumeOrchestratorLoopNoopsWhenStopFileExists()
    {
        using var sandbox = CreateResumeSandbox("throw 'process helper should not run while stop file exists'");
        WriteLastDriveJournal(sandbox.RepositoryRoot, "batch60");
        File.WriteAllText(Path.Combine(sandbox.RepositoryRoot, ".conduct-stop"), "stop");

        var result = RunInvokeRepoScript(sandbox.RepositoryRoot, "scripts\\Resume-OrchestratorLoop.ps1");

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Assert.Contains("RESUME_SKIPPED reason=conduct-stop", result.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.StartInvocationPath), "Resume should not relaunch while .conduct-stop exists.");
    }

    [Xunit.Fact(DisplayName = "ResumeOrchestratorLoop_relaunches_from_journal_with_incremented_batch_name")]
    public void ResumeOrchestratorLoopRelaunchesFromJournalWithIncrementedBatchName()
    {
        using var sandbox = CreateResumeSandbox("Write-Output 'No matching repo processes found.'");
        WriteLastDriveJournal(sandbox.RepositoryRoot, "batch009");

        var result = RunInvokeRepoScript(sandbox.RepositoryRoot, "scripts\\Resume-OrchestratorLoop.ps1");

        Assert.Equal(0, result.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
        Assert.Contains("RESUME_LAUNCH", result.Stdout, StringComparison.Ordinal);

        using var receipt = JsonDocument.Parse(File.ReadAllText(sandbox.StartInvocationPath));
        var root = receipt.RootElement;
        Assert.Equal("batch010", root.GetProperty("name").GetString());
        var args = root.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()).ToArray();
        Assert.Equal(
            new[] { "conduct", "--loop", "--watch", "--policy", "Permissive", "--poll-seconds", "15", "--max-duration", "5400" },
            args);
        Assert.Contains("\"pid\":4242", result.Stdout, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "AutoResume_scripts_document_idempotency_and_scheduled_task_contract")]
    public void AutoResumeScriptsDocumentIdempotencyAndScheduledTaskContract()
    {
        var repoRoot = FindLauncherSourceRoot();
        var resume = File.ReadAllText(Path.Combine(repoRoot, "scripts", "Resume-OrchestratorLoop.ps1"));
        var installer = File.ReadAllText(Path.Combine(repoRoot, "scripts", "Install-OrchestratorAutoResume.ps1"));

        Assert.Contains(".SYNOPSIS", resume, StringComparison.Ordinal);
        Assert.Contains("RESUME_SKIPPED reason=conduct-loop-running", resume, StringComparison.Ordinal);
        Assert.Contains("RESUME_SKIPPED reason=conduct-stop", resume, StringComparison.Ordinal);
        Assert.Contains("Start-OrchestratorCommand.ps1", resume, StringComparison.Ordinal);
        Assert.Contains(".SYNOPSIS", installer, StringComparison.Ordinal);
        Assert.Contains("New-ScheduledTaskTrigger -AtLogOn", installer, StringComparison.Ordinal);
        Assert.Contains("-RepetitionInterval (New-TimeSpan -Minutes 10)", installer, StringComparison.Ordinal);
        Assert.Contains("New-ScheduledTaskPrincipal", installer, StringComparison.Ordinal);
        Assert.Contains("-LogonType Interactive", installer, StringComparison.Ordinal);
        Assert.Contains("Unregister-ScheduledTask", installer, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "InvokeOrchestratorCommand_defaults_to_launcher_refresh_path")]
    public void InvokeOrchestratorCommandDefaultsToLauncherRefreshPath()
    {
        var repoRoot = FindLauncherSourceRoot();
        var script = File.ReadAllText(Path.Combine(repoRoot, "scripts", "Invoke-OrchestratorCommand.ps1"));

        Assert.True(script.Contains("$launcher = Join-Path $repoRoot \"mcg-orchestrator.cmd\"", StringComparison.Ordinal), script);
        Assert.True(script.Contains("if ([string]::IsNullOrWhiteSpace($AppDll))", StringComparison.Ordinal), script);
        Assert.True(script.Contains("& $launcher @Arguments", StringComparison.Ordinal), script);
        Assert.True(script.Contains("[System.IO.Path]::GetFullPath($AppDll)", StringComparison.Ordinal), script);
        Assert.DoesNotContain("bin\\Debug\\net10.0\\Mcg.AgentOrchestrator.App.dll", script, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "StartOrchestratorCommand_defaults_to_launcher_refresh_path")]
    public void StartOrchestratorCommandDefaultsToLauncherRefreshPath()
    {
        var repoRoot = FindLauncherSourceRoot();
        var script = File.ReadAllText(Path.Combine(repoRoot, "scripts", "Start-OrchestratorCommand.ps1"));

        Assert.True(script.Contains("$launcher = Join-Path $repoRoot \"mcg-orchestrator.cmd\"", StringComparison.Ordinal), script);
        Assert.True(script.Contains("$usesLauncher = [string]::IsNullOrWhiteSpace($AppDll)", StringComparison.Ordinal), script);
        Assert.True(script.Contains("$processFilePath = $env:ComSpec", StringComparison.Ordinal), script);
        Assert.True(script.Contains("$launchArguments = @(\"/d\", \"/c\") + $processArguments", StringComparison.Ordinal), script);
        Assert.True(script.Contains("[System.IO.Path]::GetFullPath($AppDll)", StringComparison.Ordinal), script);
        Assert.DoesNotContain("bin\\Debug\\net10.0\\Mcg.AgentOrchestrator.App.dll", script, StringComparison.Ordinal);
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

            var runDir = Path.Combine(root, "mcg-run", OutputContentHashPrefix(appOutput));
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

    [Xunit.Fact(DisplayName = "ResolveRunDir_content_address_includes_changed_dependencies")]
    public void ResolveRunDirContentAddressIncludesChangedDependencies()
    {
        var repoRoot = FindLauncherSourceRoot();
        var root = Path.Combine(Path.GetTempPath(), $"resolve-run-dir-{Guid.NewGuid():N}");
        var appOutput = Path.Combine(root, "app");
        Directory.CreateDirectory(Path.Combine(appOutput, "runtimes", "win-x64", "native"));
        try
        {
            var appDll = Path.Combine(appOutput, "Mcg.AgentOrchestrator.App.dll");
            var dependency = Path.Combine(appOutput, "Mcg.AgentOrchestrator.Infrastructure.dll");
            File.WriteAllText(appDll, "unchanged app");
            File.WriteAllText(dependency, "dependency v1");
            File.WriteAllText(
                Path.Combine(appOutput, "runtimes", "win-x64", "native", "e_sqlite3.dll"),
                "native");

            string Resolve()
            {
                var result = RunPowerShellCommand(repoRoot, $"""
                    $ErrorActionPreference = 'Stop'
                    $env:TEMP = '{EscapePowerShellSingleQuoted(root)}'
                    $env:TMP = '{EscapePowerShellSingleQuoted(root)}'
                    & '{EscapePowerShellSingleQuoted(Path.Combine(repoRoot, "scripts", "resolve-run-dir.ps1"))}' '{EscapePowerShellSingleQuoted(appDll)}'
                    """);
                Assert.Equal(0, result.ExitCode);
                Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
                Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
                return result.Stdout.Trim();
            }

            var firstRunDirectory = Resolve();
            File.WriteAllText(dependency, "dependency v2");
            var secondRunDirectory = Resolve();

            Assert.NotEqual(firstRunDirectory, secondRunDirectory);
            Assert.True(Directory.Exists(firstRunDirectory));
            Assert.True(Directory.Exists(secondRunDirectory));
            Assert.Equal(
                "dependency v2",
                File.ReadAllText(Path.Combine(secondRunDirectory, Path.GetFileName(dependency))));
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
            startInfo.ArgumentList.Add("cleanup");

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
            Assert.Contains("Cleanup backoff: reason=remove:branch-delete-failed", result.Stdout);
            Assert.Contains("Cleanup retry: conduct cleanup --loop", result.Stdout);
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
        var childStdout = ReadAllTextShared(stdoutPath);
        var childStderr = ReadAllTextShared(stderrPath);
        Assert.True(string.IsNullOrWhiteSpace(childStderr), childStderr);

        using var childDocument = JsonDocument.Parse(childStdout);
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
        Assert.True(script.Contains("$processArguments = @($targetExecutable) + $Arguments", StringComparison.Ordinal));
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

            if (File.Exists(launcherPath))
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

    private static DefaultLauncherSandbox CreateDefaultLauncherSandbox(bool includeStartCommand = false)
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"default-launcher-{Guid.NewGuid():N}");
        var scriptsPath = Path.Combine(repositoryRoot, "scripts");
        Directory.CreateDirectory(scriptsPath);

        var sourceRoot = FindLauncherSourceRoot();
        File.Copy(
            Path.Combine(sourceRoot, "scripts", "Invoke-RepoScript.ps1"),
            Path.Combine(scriptsPath, "Invoke-RepoScript.ps1"));
        File.Copy(
            Path.Combine(sourceRoot, "scripts", "Invoke-OrchestratorCommand.ps1"),
            Path.Combine(scriptsPath, "Invoke-OrchestratorCommand.ps1"));
        if (includeStartCommand)
        {
            File.Copy(
                Path.Combine(sourceRoot, "scripts", "Start-OrchestratorCommand.ps1"),
                Path.Combine(scriptsPath, "Start-OrchestratorCommand.ps1"));
        }

        var invocationPath = Path.Combine(repositoryRoot, "launcher-invocation.txt");
        File.WriteAllText(Path.Combine(repositoryRoot, "mcg-orchestrator.cmd"), $"""
            @echo off
            echo launcher %*>"{invocationPath}"
            echo launcher %*
            exit /b 0
            """.Replace("\n", "\r\n", StringComparison.Ordinal));

        return new DefaultLauncherSandbox(repositoryRoot, invocationPath);
    }

    private static StaleMarkerLauncherSandbox CreateStaleMarkerLauncherSandbox()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"stale-marker-launcher-{Guid.NewGuid():N}");
        var scriptsPath = Path.Combine(repositoryRoot, "scripts");
        var appSourcePath = Path.Combine(repositoryRoot, "src", "Mcg.AgentOrchestrator.App");
        var appOutputPath = Path.Combine(appSourcePath, "bin", "Debug", "net10.0");
        var shimPath = Path.Combine(repositoryRoot, "shim");
        Directory.CreateDirectory(scriptsPath);
        Directory.CreateDirectory(appSourcePath);
        Directory.CreateDirectory(appOutputPath);
        Directory.CreateDirectory(shimPath);

        var sourceRoot = FindLauncherSourceRoot();
        File.Copy(
            Path.Combine(sourceRoot, "scripts", "Invoke-RepoScript.ps1"),
            Path.Combine(scriptsPath, "Invoke-RepoScript.ps1"));
        File.Copy(
            Path.Combine(sourceRoot, "scripts", "Invoke-OrchestratorCommand.ps1"),
            Path.Combine(scriptsPath, "Invoke-OrchestratorCommand.ps1"));
        File.Copy(
            Path.Combine(sourceRoot, "scripts", "resolve-run-dir.ps1"),
            Path.Combine(scriptsPath, "resolve-run-dir.ps1"));
        File.Copy(
            Path.Combine(sourceRoot, "scripts", "Update-AppDllGitHeadMarker.ps1"),
            Path.Combine(scriptsPath, "Update-AppDllGitHeadMarker.ps1"));
        File.Copy(
            Path.Combine(sourceRoot, "mcg-orchestrator.cmd"),
            Path.Combine(repositoryRoot, "mcg-orchestrator.cmd"));

        File.WriteAllText(Path.Combine(appSourcePath, "Mcg.AgentOrchestrator.App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(appSourcePath, "Program.cs"), "Console.WriteLine(\"test\");");

        var appDllPath = Path.Combine(appOutputPath, "Mcg.AgentOrchestrator.App.dll");
        var markerPath = appDllPath + ".git-head";
        File.WriteAllText(appDllPath, "stale app");
        File.WriteAllText(Path.Combine(appOutputPath, "e_sqlite3.dll"), "native");
        File.WriteAllText(markerPath, "stale-test-head");

        RunGit(repositoryRoot, "init", "--initial-branch=main");
        RunGit(repositoryRoot, "config", "user.email", "test@example.invalid");
        RunGit(repositoryRoot, "config", "user.name", "Launcher Script Test");
        RunGit(repositoryRoot, "add", ".");
        RunGit(repositoryRoot, "commit", "-m", "base");
        var expectedHead = RunGitForOutput(repositoryRoot, "rev-parse", "HEAD").Trim();

        var dotnetLogPath = Path.Combine(repositoryRoot, "dotnet.log");
        var dotnetShimPath = Path.Combine(shimPath, "dotnet.cmd");
        File.WriteAllText(dotnetShimPath, $"""
            @echo off
            echo %*>>"{dotnetLogPath}"
            if "%~1"=="build" (
              echo rebuilt>"{appDllPath}"
              echo native>"{Path.Combine(appOutputPath, "e_sqlite3.dll")}"
              exit /b 0
            )
            exit /b 0
            """.Replace("\n", "\r\n", StringComparison.Ordinal));

        return new StaleMarkerLauncherSandbox(repositoryRoot, dotnetShimPath, dotnetLogPath, markerPath, expectedHead);
    }

    private static StartJournalSandbox CreateStartJournalSandbox()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"start-journal-{Guid.NewGuid():N}");
        var scriptsPath = Path.Combine(repositoryRoot, "scripts");
        var shimPath = Path.Combine(repositoryRoot, "shim");
        Directory.CreateDirectory(scriptsPath);
        Directory.CreateDirectory(shimPath);

        var sourceRoot = FindLauncherSourceRoot();
        File.Copy(
            Path.Combine(sourceRoot, "scripts", "Invoke-RepoScript.ps1"),
            Path.Combine(scriptsPath, "Invoke-RepoScript.ps1"));
        File.Copy(
            Path.Combine(sourceRoot, "scripts", "Start-OrchestratorCommand.ps1"),
            Path.Combine(scriptsPath, "Start-OrchestratorCommand.ps1"));

        var hostPath = Path.Combine(shimPath, "fake-dotnet.cmd");
        File.WriteAllText(hostPath, """
            @echo off
            powershell.exe -NoProfile -ExecutionPolicy Bypass -File %*
            """.Replace("\n", "\r\n", StringComparison.Ordinal));

        var echoScriptPath = Path.Combine(repositoryRoot, "echo-args.ps1");
        File.WriteAllText(echoScriptPath, """
            param(
                [Parameter(ValueFromRemainingArguments = $true)]
                [string[]]$Arguments
            )

            $Arguments | ConvertTo-Json -Compress
            """);

        return new StartJournalSandbox(repositoryRoot, hostPath, echoScriptPath);
    }

    private static ResumeSandbox CreateResumeSandbox(string processHelperBody)
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), $"resume-loop-{Guid.NewGuid():N}");
        var scriptsPath = Path.Combine(repositoryRoot, "scripts");
        Directory.CreateDirectory(scriptsPath);

        var sourceRoot = FindLauncherSourceRoot();
        File.Copy(
            Path.Combine(sourceRoot, "scripts", "Invoke-RepoScript.ps1"),
            Path.Combine(scriptsPath, "Invoke-RepoScript.ps1"));
        File.Copy(
            Path.Combine(sourceRoot, "scripts", "Resume-OrchestratorLoop.ps1"),
            Path.Combine(scriptsPath, "Resume-OrchestratorLoop.ps1"));

        File.WriteAllText(
            Path.Combine(scriptsPath, "Get-RepoProcessInfo.ps1"),
            $"""
            param(
                [switch]$ConductLoop,
                [int]$Newest = 10
            )
            {processHelperBody}
            """);

        var startInvocationPath = Path.Combine(repositoryRoot, "start-invocation.json");
        File.WriteAllText(
            Path.Combine(scriptsPath, "Start-OrchestratorCommand.ps1"),
            $$"""
            [CmdletBinding(PositionalBinding = $false)]
            param(
                [string]$Name,
                [string]$AppDll,
                [Parameter(ValueFromRemainingArguments = $true)]
                [string[]]$Arguments
            )

            [pscustomobject]@{
                name = $Name
                appDll = $AppDll
                args = @($Arguments)
            } | ConvertTo-Json -Compress | Set-Content -LiteralPath '{{EscapePowerShellSingleQuoted(startInvocationPath)}}'

            [pscustomobject]@{
                pid = 4242
                stdoutPath = 'out.log'
                stderrPath = 'err.log'
                args = @($Arguments)
                startedAt = '2026-07-16T00:00:00.0000000Z'
            } | ConvertTo-Json -Compress
            """);

        return new ResumeSandbox(repositoryRoot, startInvocationPath);
    }

    private static void WriteLastDriveJournal(string repositoryRoot, string batchName)
    {
        var journalPath = Path.Combine(repositoryRoot, ".orchestrator", "last-drive.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            name = batchName,
            appDll = (string?)null,
            arguments = new[] { "conduct", "--loop", "--watch", "--policy", "Permissive", "--poll-seconds", "15", "--max-duration", "5400" }
        });
        File.WriteAllText(journalPath, json);
    }

    private static ProcessResult RunInvokeRepoScript(string repositoryRoot, string relativeScriptPath, params string[] arguments) =>
        RunInvokeRepoScript(repositoryRoot, relativeScriptPath, environment: null, arguments);

    private static ProcessResult RunInvokeRepoScript(
        string repositoryRoot,
        string relativeScriptPath,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
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
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

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

    private static bool RunbookStatesNoGateBypassNorm(string runbook)
    {
        const string noGateBypassNorm = "do not bypass the gate";
        return CollapseWhitespace(runbook).Contains(
            CollapseWhitespace(noGateBypassNorm),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string RetiredManualLandingScriptName() =>
        string.Concat("Land-", "Verified", "Goal.ps1");

    private static string OutputContentHashPrefix(string outputDirectory)
    {
        var payload = new StringBuilder();
        foreach (var file in Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            payload.Append(Path.GetRelativePath(outputDirectory, file).Replace('\\', '/'));
            payload.Append(':');
            using var stream = File.OpenRead(file);
            payload.Append(Convert.ToHexString(SHA256.HashData(stream)));
            payload.Append('\n');
        }

        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(payload.ToString())))
            .Substring(0, 16);
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
            if "%~3"=="cleanup" (
              echo Cleanup backoff: reason=remove:branch-delete-failed skip_until_utc=2026-07-03T12:15:00.0000000+00:00 remaining_wait=00:15:00
              echo Cleanup retry: conduct cleanup --loop
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

    private static string RunGitForOutput(string workingDirectory, params string[] arguments)
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
        return result.Stdout;
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

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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

    private sealed class StartJournalSandbox(
        string repositoryRoot,
        string hostPath,
        string echoScriptPath) : IDisposable
    {
        public string RepositoryRoot { get; } = repositoryRoot;
        public string HostPath { get; } = hostPath;
        public string EchoScriptPath { get; } = echoScriptPath;

        public void Dispose()
        {
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

    private sealed class ResumeSandbox(string repositoryRoot, string startInvocationPath) : IDisposable
    {
        public string RepositoryRoot { get; } = repositoryRoot;
        public string StartInvocationPath { get; } = startInvocationPath;

        public void Dispose()
        {
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

    private sealed class DefaultLauncherSandbox(string repositoryRoot, string invocationPath) : IDisposable
    {
        public string RepositoryRoot { get; } = repositoryRoot;
        public string InvocationPath { get; } = invocationPath;

        public void Dispose()
        {
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

    private sealed class StaleMarkerLauncherSandbox(
        string repositoryRoot,
        string dotnetShimPath,
        string dotnetLogPath,
        string markerPath,
        string expectedHead) : IDisposable
    {
        public string RepositoryRoot { get; } = repositoryRoot;
        public string DotnetShimPath { get; } = dotnetShimPath;
        public string DotnetLogPath { get; } = dotnetLogPath;
        public string MarkerPath { get; } = markerPath;
        public string ExpectedHead { get; } = expectedHead;

        public void Dispose()
        {
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
