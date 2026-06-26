using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class DispatchProcessHostTests
{
    [Xunit.Fact(DisplayName = "DispatchProcessHost_parameters_round_trip_via_camelCase_json")]
    public void DispatchProcessHostParametersRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "dispatch.json");
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "Write-Output ok",
                dir,
                Path.Combine(dir, "out.log"),
                Path.Combine(dir, "err.log"),
                Path.Combine(dir, "exit.txt"),
                Path.Combine(dir, "heartbeat.json"),
                ShutdownBuildServerOnExit: true,
                DisableSharedCompilation: true);

            DispatchProcessHost.WriteParameters(path, parameters);

            var json = File.ReadAllText(path);
            // The detached host reads this with a camelCase policy, so the keys must be camelCase.
            Assert.True(json.Contains("\"command\"", StringComparison.Ordinal));
            Assert.True(json.Contains("\"disableSharedCompilation\"", StringComparison.Ordinal));

            var roundTripped = JsonSerializer.Deserialize<DispatchProcessHost.DispatchRunParameters>(
                json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.Equal(parameters, roundTripped);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_low_integrity_path_removes_windowsapps_and_prepends_shell_dir")]
    public void LowIntegrityPathRemovesWindowsAppsAndPrependsShellDir()
    {
        var shellDir = Path.Combine(Path.GetTempPath(), "real-powershell");
        var shell = Path.Combine(shellDir, OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh");
        var windowsApps = Path.Combine(Path.GetTempPath(), "Microsoft", "WindowsApps");
        var toolDir = Path.Combine(Path.GetTempPath(), "tooling");
        var originalPath = string.Join(Path.PathSeparator, windowsApps, toolDir, shellDir);

        var result = DispatchProcessHost.BuildLowIntegrityPath(originalPath, shell);
        var entries = result.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(shellDir, entries[0]);
        Assert.Contains(toolDir, entries);
        Assert.DoesNotContain(entries, DispatchProcessHost.IsWindowsAppsPathSegment);
        Assert.Equal(1, entries.Count(entry => string.Equals(entry, shellDir, StringComparison.OrdinalIgnoreCase)));
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_writes_exit_file_when_grandchild_holds_pipe_after_worker_exits")]
    public void DispatchProcessHostWritesExitFileWhenGrandchildHoldsPipeAfterWorkerExits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcg-dispatch-host-drain-test", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        Process? hostProcess = null;
        try
        {
            // Command: spawn a long-running grandchild (inheriting the pipe handles),
            // then the worker exits immediately. The dispatch host must time-out the
            // drain and write the exit-code file rather than blocking forever.
            var hangCommand = OperatingSystem.IsWindows()
                ? "$psi = [System.Diagnostics.ProcessStartInfo]::new('ping.exe', '-n 30 127.0.0.1'); $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; [void][System.Diagnostics.Process]::Start($psi); Write-Output 'done'; exit 0"
                : "$psi = [System.Diagnostics.ProcessStartInfo]::new('sleep', '60'); $psi.UseShellExecute = $false; [void][System.Diagnostics.Process]::Start($psi); Write-Output 'done'; exit 0";

            var parametersPath = Path.Combine(dir, "dispatch.json");
            var stdoutPath = Path.Combine(dir, "out.log");
            var stderrPath = Path.Combine(dir, "err.log");
            var exitCodePath = Path.Combine(dir, "exit.txt");

            DispatchProcessHost.WriteParameters(parametersPath, new DispatchProcessHost.DispatchRunParameters(
                hangCommand,
                dir,
                stdoutPath,
                stderrPath,
                exitCodePath,
                null,
                ShutdownBuildServerOnExit: false,
                DisableSharedCompilation: false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"));
            startInfo.ArgumentList.Add(DispatchProcessHost.SubcommandName);
            startInfo.ArgumentList.Add(parametersPath);

            hostProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start dispatch host.");

            // The exit file must appear within the drain timeout (~12 s) plus buffer.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!File.Exists(exitCodePath) && DateTimeOffset.UtcNow < deadline)
                Thread.Sleep(200);

            // Kill the process tree (including any grandchildren that inherited pipe handles)
            // and wait for the host to fully exit before reading exit.txt. This removes the
            // file-handle race where a lingering grandchild holds an inherited handle while
            // we read. FileShare.ReadWrite + retry in ReadExitCodeWithRetry covers any
            // remaining window.
            try { hostProcess.Kill(entireProcessTree: true); } catch { }
            try { hostProcess.WaitForExit(5000); } catch { }

            Assert.True(File.Exists(exitCodePath));
            Assert.Equal("0", ReadExitCodeWithRetry(exitCodePath));
        }
        finally
        {
            try { hostProcess?.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "DispatchProcessHost_ShouldReapWorker_spares_buffering_workers_until_maxRuntime")]
    public void ShouldReapWorkerSparesBufferingWorkers()
    {
        var maxRuntime = TimeSpan.FromMinutes(60);
        var maxIdle = TimeSpan.FromMinutes(20);

        // A worker that has produced NO output (e.g. claude-cli -p buffers to the end) is NOT reaped
        // on the idle cap even past it — only maxRuntime bounds it. This is the buffering-worker fix.
        Assert.False(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(25), hasProducedOutput: false, maxRuntime, maxIdle));
        Assert.True(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(61), TimeSpan.FromMinutes(25), hasProducedOutput: false, maxRuntime, maxIdle));

        // A worker that streamed then went quiet past the idle cap IS reaped (stall detection preserved).
        Assert.True(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(19), TimeSpan.FromMinutes(25), hasProducedOutput: true, maxRuntime, maxIdle));
        Assert.False(DispatchProcessHost.ShouldReapWorker(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), hasProducedOutput: true, maxRuntime, maxIdle));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_true_when_cpu_grows_above_epsilon_with_flat_bytes")]
    public void HasProgressedDetectsCpuGrowthWhenBytesFlat()
    {
        // CPU grows from 0 to 100ms (> 50ms epsilon), bytes flat
        Assert.True(DispatchProcessHost.HasProgressed(0, 0, 0, 100, 50));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_false_when_both_cpu_and_bytes_are_flat")]
    public void HasProgressedNoProgressWhenFlat()
    {
        // Both CPU and bytes flat
        Assert.False(DispatchProcessHost.HasProgressed(0, 0, 0, 0, 50));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_false_when_cpu_growth_equals_epsilon_exactly")]
    public void HasProgressedCpuAtEpsilonIsNotProgress()
    {
        // Delta = 50ms exactly at epsilon — not strictly greater, so not progress
        Assert.False(DispatchProcessHost.HasProgressed(0, 0, 0, 50, 50));
    }

    [Xunit.Fact(DisplayName = "HasProgressed_returns_true_when_bytes_grow_regardless_of_cpu")]
    public void HasProgressedDetectsByteGrowth()
    {
        Assert.True(DispatchProcessHost.HasProgressed(0, 10, 0, 0, 50));
    }

    private static string ReadExitCodeWithRetry(string path, int attempts = 5, int delayMs = 100)
    {
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException ex)
            {
                last = ex;
                if (i < attempts - 1) Thread.Sleep(delayMs);
            }
        }
        throw last!;
    }
}
