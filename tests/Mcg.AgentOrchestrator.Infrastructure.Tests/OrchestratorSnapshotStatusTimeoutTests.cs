using System.Diagnostics;
using static LauncherScriptTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class OrchestratorSnapshotStatusTimeoutTests
{
    // Keep the status timeout comfortably above the shim's deliberate pre-sentinel delay so startup variance cannot decide the test.
    private const int SnapshotStatusTimeoutSeconds = 20;
    private const int SnapshotShimSentinelDelaySeconds = 6;

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
            startInfo.Environment["DOTNET_STATUS_STARTED_AT"] = sandbox.StartedAtPath;
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(sandbox.RepositoryRoot, "scripts", "Get-OrchestratorSnapshot.ps1"));
            startInfo.ArgumentList.Add("-StatusTimeoutSeconds");
            startInfo.ArgumentList.Add(SnapshotStatusTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-GoalPrefix");
            startInfo.ArgumentList.Add("hang");
            startInfo.ArgumentList.Add("ok");
            startInfo.ArgumentList.Add("cleanup");

            var stopwatch = Stopwatch.StartNew();
            var result = RunProcess(startInfo, "Get-OrchestratorSnapshot.ps1");
            stopwatch.Stop();

            Assert.True(
                result.ExitCode == 0,
                $"exit={result.ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}{Environment.NewLine}elapsed:{stopwatch.Elapsed}");
            Assert.True(string.IsNullOrWhiteSpace(result.Stderr), result.Stderr);
            Assert.Contains("partial hang", result.Stdout);
            Assert.Contains($"status timed out after {SnapshotStatusTimeoutSeconds}s; killed pid=", result.Stdout);
            Assert.Contains("status ok ok", result.Stdout);
            Assert.Contains("Cleanup backoff: reason=remove:branch-delete-failed", result.Stdout);
            Assert.Contains("Cleanup retry: conduct cleanup --loop", result.Stdout);
            WaitForFile(sandbox.SentinelPath, RealProcessFileHangGuard);
            var childPid = int.Parse(File.ReadAllText(sandbox.SentinelPath).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(!IsProcessRunning(childPid), $"Expected hung status child pid {childPid} to be reaped.");
        }
        finally
        {
            sandbox.KillRecordedChild();
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
            $"""
            @echo off
            if "%~3"=="hang" (
              echo partial hang
              powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Sleep -Seconds {SnapshotShimSentinelDelaySeconds}; Set-Content -LiteralPath $env:DOTNET_STATUS_STARTED_AT -Value (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o'); Set-Content -LiteralPath $env:DOTNET_STATUS_SENTINEL -Value $PID; Start-Sleep -Seconds 60"
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
            Path.Combine(repositoryRoot, "status-child.pid"),
            Path.Combine(repositoryRoot, "status-child.started-at"));
    }

    private sealed class SnapshotStatusSandbox(
        string repositoryRoot,
        string dotnetShimPath,
        string sentinelPath,
        string startedAtPath) : IDisposable
    {
        public string RepositoryRoot { get; } = repositoryRoot;
        public string DotnetShimPath { get; } = dotnetShimPath;
        public string SentinelPath { get; } = sentinelPath;
        public string StartedAtPath { get; } = startedAtPath;

        public void KillRecordedChild()
        {
            if (!File.Exists(SentinelPath) || !File.Exists(StartedAtPath))
            {
                return;
            }

            if (!int.TryParse(File.ReadAllText(SentinelPath).Trim(), out var pid))
            {
                return;
            }

            if (DateTimeOffset.TryParse(File.ReadAllText(StartedAtPath).Trim(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var startedAt))
                TestOwnedProcessStop.StopTreeIfSame(new(pid, startedAt));
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

}
