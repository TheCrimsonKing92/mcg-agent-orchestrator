using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class WorkerDispatchJobAccountingTests
{
    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_completed_refresh_does_not_reapply_stale_resource_verification")]
    public void BackgroundDispatchRunnerCompletedRefreshDoesNotReapplyStaleResourceVerification()
    {
        var root = CreateTempDirectory();
        var logs = Path.Combine(root, "logs");
        var stdout = Path.Combine(logs, "out.log");
        var stderr = Path.Combine(logs, "err.log");
        var exit = Path.Combine(logs, "worker.exit.txt");
        Directory.CreateDirectory(logs);
        File.WriteAllText(stdout, "done");
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");

        var now = DateTimeOffset.Parse("2026-07-09T11:43:56Z");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Avoid stale resource verification");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
        var dispatch = new TaskDispatchRecord("codex-cli", "Write-Output done", root, now);
        var started = new TaskProcessRecord(12345, dispatch.Command, root, stdout, stderr, exit, now, null, null);
        var completed = started with
        {
            CompletedAt = now.AddSeconds(1),
            ExitCode = 0,
            ResourceAccounting = new TaskProcessResourceAccounting(12, 4096, 128)
        };
        var verification = new TaskVerificationRecord(
            dispatch.Command,
            root,
            0,
            "done",
            "RESOURCE goal=e93b30f6 task=64f76097 cpu_ms=12 peak_mem_bytes=4096 io_bytes=128",
            completed.CompletedAt.Value);
        var outcome = new DispatchRefreshOutcome(completed, verification);

        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, started);

        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);
        BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);

        Assert.Single(task.VerificationHistory);
        Assert.Single(
            kernel.GetTimeline(goal.Id),
            evt => evt.Kind == ProgressKind.TaskNote &&
                evt.Message.Contains("RESOURCE ", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_non_local_dispatch_runs_with_shared_compilation_disabled")]
    public void BackgroundDispatchRunnerNonLocalDispatchRunsWithSharedCompilationDisabled()
    {
        var root = CreateTempDirectory();
        var logs = Path.Combine(root, "logs");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Disable shared compilation for worker dispatch");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "Write-Output $env:DOTNET_CLI_USE_MSBUILD_SERVER; Write-Output $env:MSBUILDDISABLENODEREUSE; Write-Output $env:UseSharedCompilation",
            root,
            DateTimeOffset.UtcNow));

        var process = new BackgroundDispatchRunner().StartLatestDispatch(kernel, goal.Id, task.Id, logs);
        WaitForExitFile(process.ExitCodePath);
        new BackgroundDispatchRunner().RefreshLatestProcess(kernel, goal.Id, task.Id);

        var output = File.ReadAllLines(process.StandardOutputPath);
        Assert.True(File.Exists(BackgroundDispatchRunner.GetHeartbeatPath(process)));
        Assert.Equal(3, output.Length);
        Assert.Equal("0", output[0]);
        Assert.Equal("1", output[1]);
        Assert.Equal("false", output[2]);
        if (OperatingSystem.IsWindows())
        {
            var refreshed = kernel.GetTask(goal.Id, task.Id).LastProcess!;
            Assert.NotNull(refreshed.ResourceAccounting);
            Assert.True(refreshed.ResourceAccounting.CpuMilliseconds >= 0);
            Assert.True(refreshed.ResourceAccounting.PeakMemoryBytes > 0);
            Assert.True(refreshed.ResourceAccounting.IoBytes >= 0);

            var standardError = kernel.GetTask(goal.Id, task.Id).LastVerification!.StandardError;
            Assert.Contains("RESOURCE ", standardError, StringComparison.Ordinal);
            Assert.Contains("cpu_ms=", standardError, StringComparison.Ordinal);
            Assert.Contains("peak_mem_bytes=", standardError, StringComparison.Ordinal);
            Assert.Contains("io_bytes=", standardError, StringComparison.Ordinal);
            Assert.Contains(
                kernel.GetTimeline(goal.Id),
                evt => evt.Kind == ProgressKind.TaskNote &&
                    evt.Message.Contains("RESOURCE ", StringComparison.Ordinal) &&
                    evt.Message.Contains("cpu_ms=", StringComparison.Ordinal) &&
                    evt.Message.Contains("peak_mem_bytes=", StringComparison.Ordinal) &&
                    evt.Message.Contains("io_bytes=", StringComparison.Ordinal));
        }
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reaped_startup_hang_records_job_accounting")]
    public void BackgroundDispatchRunnerReapedStartupHangRecordsJobAccounting()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
        var clock = new TestClock(now);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Startup hang accounting");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
        var startMarker = Path.Combine(root, "start-work.marker");
        var allocatedMarker = Path.Combine(root, "allocated.marker");
        File.WriteAllText(stdout, string.Empty);
        File.WriteAllText(stderr, string.Empty);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-40)));

        Process? wrapper = null;
        try
        {
            wrapper = Process.Start(new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }.WithArguments(
                WorkerShell.BaseArguments().Concat([
                    $"while (!(Test-Path -LiteralPath '{startMarker}')) {{ Start-Sleep -Milliseconds 50 }}; " +
                    "$bytes = New-Object byte[] 8388608; " +
                    $"Set-Content -LiteralPath '{allocatedMarker}' -Value 'allocated'; " +
                    "Start-Sleep -Seconds 30; [GC]::KeepAlive($bytes)"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");

            Assert.True(WorkerProcessJobs.TryRegister(wrapper));
            File.WriteAllText(startMarker, "go");
            WaitUntil(() => File.Exists(allocatedMarker), TimeSpan.FromSeconds(5));
            var process = new TaskProcessRecord(wrapper.Id, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-40), null, null);
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
            WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 0, 0, ownedCpuMs: 0L, childPid: null);

            var completed = new BackgroundDispatchRunner(
                    clock,
                    isStillRunning: processId => processId == wrapper.Id && !wrapper.HasExited,
                    startupHangTimeout: TimeSpan.FromMinutes(4))
                .RefreshLatestProcess(kernel, goal.Id, task.Id);

            Assert.Equal(1, completed.ExitCode);
            Assert.NotNull(completed.ResourceAccounting);
            Assert.True(completed.ResourceAccounting.Reaped);
            Assert.True(completed.ResourceAccounting.CpuMilliseconds >= 0);
            Assert.True(completed.ResourceAccounting.PeakMemoryBytes > 0);
            Assert.True(completed.ResourceAccounting.IoBytes >= 0);
            Assert.Equal("duplicate", completed.ResourceAccounting.AccountingSource);
            Assert.Contains("RESOURCE ", task.LastVerification!.StandardError, StringComparison.Ordinal);
            Assert.Contains("accounting_source=duplicate", task.LastVerification.StandardError, StringComparison.Ordinal);
            Assert.Contains("reaped=true", task.LastVerification.StandardError, StringComparison.Ordinal);
            Assert.Contains(kernel.GetTimeline(goal.Id), evt =>
                evt.Kind == ProgressKind.TaskNote &&
                evt.Message.Contains("RESOURCE ", StringComparison.Ordinal) &&
                evt.Message.Contains("accounting_source=duplicate", StringComparison.Ordinal) &&
                evt.Message.Contains("reaped=true", StringComparison.Ordinal));
        }
        finally
        {
            if (wrapper is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(wrapper.Id); } catch { }
                wrapper.Dispose();
            }
        }
    }

    private static void WriteHeartbeat(
        TaskProcessRecord process,
        DateTimeOffset lastObservedAt,
        DateTimeOffset lastProgressAt,
        string state,
        long stdoutBytes,
        long stderrBytes,
        int? childPid = 888888,
        long? ownedCpuMs = null)
    {
        var childPidJson = childPid.HasValue ? childPid.Value.ToString() : "null";
        var ownedCpuMsJson = ownedCpuMs.HasValue ? $",\"ownedCpuMs\":{ownedCpuMs.Value}" : string.Empty;
        File.WriteAllText(
            BackgroundDispatchRunner.GetHeartbeatPath(process),
            "{" +
            "\"pid\":999999," +
            $"\"childPid\":{childPidJson}," +
            "\"ownedPids\":[]," +
            $"\"startedAt\":\"{process.StartedAt:O}\"," +
            $"\"lastObservedAt\":\"{lastObservedAt:O}\"," +
            $"\"lastProgressAt\":\"{lastProgressAt:O}\"," +
            $"\"state\":\"{state}\"," +
            $"\"stdoutBytes\":{stdoutBytes}," +
            $"\"stderrBytes\":{stderrBytes}," +
            "\"exitFileExists\":false" +
            ownedCpuMsJson +
            "}");
    }

    private static void WaitForExitFile(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!File.Exists(path) && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        if (!File.Exists(path))
        {
            throw new TimeoutException($"Timed out waiting for exit file '{path}'.");
        }
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        if (!condition())
        {
            throw new TimeoutException("Timed out waiting for test condition.");
        }
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
