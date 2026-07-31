using System.Diagnostics;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("EnvMutation")]
public sealed class WorkerProcessJobsTests : IDisposable
{
    private readonly string? _originalProtectedPid = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_PROTECTED_PID");

    public WorkerProcessJobsTests()
    {
        Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_PROTECTED_PID", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MCG_ORCHESTRATOR_PROTECTED_PID", _originalProtectedPid);
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_startup_sweep_reaps_only_registry_owned_pid")]
    public void WorkerProcessJobsStartupSweepReapsOnlyRegistryOwnedPid()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? owned = null;
        Process? sentinel = null;
        try
        {
            owned = StartLongRunningShell();
            sentinel = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(owned, out var identity));

            new SpawnRegistry(dbPath).Register("prior-dispatch", identity);
            WorkerProcessJobs.ConfigureRegistry(dbPath);

            var reaped = WorkerProcessJobs.SweepStartupOrphans();

            Assert.Equal(1, reaped);
            Assert.True(WaitUntilNotRunning(owned.Id, TimeSpan.FromSeconds(5)));
            Assert.True(IsRunning(sentinel.Id));
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (owned is not null)
            {
                try { owned.Kill(entireProcessTree: true); } catch { }
                owned.Dispose();
            }

            if (sentinel is not null)
            {
                try { sentinel.Kill(entireProcessTree: true); } catch { }
                sentinel.Dispose();
            }

            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_register_writes_durable_pid_identity")]
    public void WorkerProcessJobsRegisterWritesDurablePidIdentity()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"), "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? wrapper = null;
        try
        {
            WorkerProcessJobs.ConfigureRegistry(dbPath);
            wrapper = StartLongRunningShell();

            Assert.True(WorkerProcessJobs.TryRegister(wrapper, "dispatch-1"));
            var entry = Assert.Single(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
            Assert.Equal("dispatch-1", entry.OwnerId);
            Assert.Equal(wrapper.Id, entry.ProcessId);
            Assert.False(string.IsNullOrWhiteSpace(entry.ImagePath));

            WorkerProcessJobs.Release(wrapper.Id);
            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(5)));
            Assert.Empty(WorkerProcessJobs.ListActiveRegistryEntriesForTests());
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (wrapper is not null)
            {
                try { wrapper.Kill(entireProcessTree: true); } catch { }
                wrapper.Dispose();
            }

            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "ProgramStartupLifecycle_handoff_configures_registry_without_sweeping_incumbent_processes")]
    public void ProgramStartupLifecycleHandoffConfiguresRegistryWithoutSweepingIncumbentProcesses()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n"));
        var dbPath = Path.Combine(root, "state.db");
        Directory.CreateDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(dbPath);
        Process? incumbentWorker = null;
        Process? successorWorker = null;
        try
        {
            incumbentWorker = StartLongRunningShell();
            Assert.True(SpawnProcessIdentityReader.TryRead(incumbentWorker, out var incumbentIdentity));
            new SpawnRegistry(dbPath).Register("incumbent-dispatch", incumbentIdentity);

            ProgramStartupLifecycle.InitializeWorkerProcessTracking(
                runsStartupCleanup: true,
                authorityTransferRequested: true,
                dbPath,
                root);

            Assert.True(IsRunning(incumbentWorker.Id));
            successorWorker = StartLongRunningShell();
            Assert.True(WorkerProcessJobs.TryRegister(successorWorker, "successor-dispatch"));
            var activeEntries = WorkerProcessJobs.ListActiveRegistryEntriesForTests();
            Assert.Contains(activeEntries, entry => entry.OwnerId == "incumbent-dispatch");
            Assert.Contains(activeEntries, entry => entry.OwnerId == "successor-dispatch");
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            if (incumbentWorker is not null)
            {
                try { incumbentWorker.Kill(entireProcessTree: true); } catch { }
                incumbentWorker.Dispose();
            }

            if (successorWorker is not null)
            {
                try { successorWorker.Kill(entireProcessTree: true); } catch { }
                successorWorker.Dispose();
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "ProgramStartupLifecycle_authority_transfer_signal_suppresses_cleanup_for_every_command")]
    public void ProgramStartupLifecycleAuthorityTransferSignalSuppressesCleanupForEveryCommand()
    {
        const string variable = "MCG_ORCHESTRATOR_HANDOFF_READY_PATH";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "successor.ready");

            Assert.True(ProgramStartupLifecycle.IsAuthorityTransferRequested(["conduct", "--loop"]));
            Assert.True(ProgramStartupLifecycle.IsAuthorityTransferRequested(["status"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_registers_and_releases_wrapper_job")]
    public void WorkerProcessJobsRegistersAndReleasesWrapperJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
                    "Start-Sleep -Seconds 9999"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");

            Assert.True(WorkerProcessJobs.TryRegister(wrapper));
            Assert.True(WorkerProcessJobs.HasRegisteredJob(wrapper.Id));
            WorkerProcessJobs.Release(wrapper.Id);

            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(2)));
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

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_release_waits_for_gate_owned_child_process_tree")]
    public void WorkerProcessJobsReleaseWaitsForGateOwnedChildProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests");
        var marker = Path.Combine(directory, Guid.NewGuid().ToString("n") + ".pid");
        var startSignal = Path.Combine(directory, Guid.NewGuid().ToString("n") + ".go");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
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
                    $"while (-not (Test-Path -LiteralPath '{startSignal}')) {{ Start-Sleep -Milliseconds 25 }}; " +
                    "$p = Start-Process ping.exe -ArgumentList '-n 9999 127.0.0.1' -PassThru -WindowStyle Hidden; " +
                    $"Set-Content -LiteralPath '{marker}' -Value $p.Id; " +
                    "Start-Sleep -Seconds 9999"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");

            Assert.True(WorkerProcessJobs.TryRegister(wrapper, "acceptance:test-slot"));
            File.WriteAllText(startSignal, "go");
            var childPid = WaitForPidFile(marker);
            WorkerProcessJobs.Release(wrapper.Id);

            Assert.False(WorkerProcessJobs.HasRegisteredJob(wrapper.Id));
            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(2)));
            Assert.True(WaitUntilNotRunning(childPid, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (wrapper is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(wrapper.Id); } catch { }
                wrapper.Dispose();
            }

            try { File.Delete(marker); } catch { }
            try { File.Delete(startSignal); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_fallback_taskkill_tree_kills_unregistered_wrapper_and_grandchild")]
    public void WorkerProcessJobsFallbackTaskkillTreeKillsUnregisteredWrapperAndGrandchild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var marker = Path.Combine(Path.GetTempPath(), "mcg-worker-job-tests", Guid.NewGuid().ToString("n") + ".pid");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
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
                    "$p = Start-Process ping.exe -ArgumentList '-n 9999 127.0.0.1' -PassThru -WindowStyle Hidden; " +
                    $"Set-Content -LiteralPath '{marker}' -Value $p.Id; " +
                    "Start-Sleep -Seconds 9999"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");
            var grandchildPid = WaitForPidFile(marker);

            Assert.True(WorkerProcessJobs.TryKillOrFallback(wrapper.Id));

            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(2)));
            Assert.True(WaitUntilNotRunning(grandchildPid, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (wrapper is not null)
            {
                try { WorkerProcessJobs.TryKillOrFallback(wrapper.Id); } catch { }
                wrapper.Dispose();
            }

            try { File.Delete(marker); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_kill_or_fallback_and_wait_returns_after_wrapper_exit")]
    public void WorkerProcessJobsKillOrFallbackAndWaitReturnsAfterWrapperExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process? wrapper = null;
        try
        {
            wrapper = StartLongRunningShell();

            Assert.True(WorkerProcessJobs.TryKillOrFallbackAndWait(wrapper.Id, TimeSpan.FromSeconds(5)));
            Assert.False(IsRunning(wrapper.Id));
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

    private static Process StartLongRunningShell()
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }.WithArguments(
            WorkerShell.BaseArguments().Concat([
                "Start-Sleep -Seconds 9999"
            ])))
            ?? throw new InvalidOperationException("Failed to start wrapper process.");
    }

    private static int WaitForPidFile(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path) &&
                    int.TryParse(File.ReadAllText(path).Trim(), out var pid) &&
                    pid > 0)
                {
                    return pid;
                }
            }
            catch (IOException)
            {
                // The writer can still hold the pid file open when it first appears; a sharing
                // violation here means "not ready yet", not failure — keep polling until the
                // deadline. (Killed a full acceptance-gate attempt as a flake on 2026-07-25.)
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException("Timed out waiting for grandchild pid file.");
    }

    private static bool WaitUntilNotRunning(int processId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsRunning(processId))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return !IsRunning(processId);
    }

    private static bool IsRunning(int processId)
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
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_release_returns_job_accounting_counters")]
    public void WorkerProcessJobsReleaseReturnsJobAccountingCounters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
                    "$bytes = New-Object byte[] 1048576; Start-Sleep -Milliseconds 250; [GC]::KeepAlive($bytes)"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");

            Assert.True(WorkerProcessJobs.TryRegister(wrapper));
            wrapper.WaitForExit(5000);

            WorkerProcessJobs.Release(wrapper.Id, out var accounting);

            Assert.NotNull(accounting);
            Assert.True(accounting.CpuMilliseconds >= 0);
            Assert.True(accounting.PeakMemoryBytes > 0);
            Assert.True(accounting.IoBytes >= 0);
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

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_try_kill_returns_job_accounting_counters")]
    public void WorkerProcessJobsTryKillReturnsJobAccountingCounters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
                    "$bytes = New-Object byte[] 1048576; Start-Sleep -Seconds 30; [GC]::KeepAlive($bytes)"
                ])))
                ?? throw new InvalidOperationException("Failed to start wrapper process.");

            Assert.True(WorkerProcessJobs.TryRegister(wrapper));

            Assert.True(WorkerProcessJobs.TryKillOrFallback(wrapper.Id, out var accounting));

            Assert.NotNull(accounting);
            Assert.True(accounting.CpuMilliseconds >= 0);
            Assert.True(accounting.PeakMemoryBytes > 0);
            Assert.True(accounting.IoBytes >= 0);
            Assert.True(WaitUntilNotRunning(wrapper.Id, TimeSpan.FromSeconds(2)));
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

    [Xunit.Fact(DisplayName = "WorkerProcessJobs_source_routes_owned_group_close_through_accounting_helper")]
    public void WorkerProcessJobsSourceRoutesOwnedGroupCloseThroughAccountingHelper()
    {
        AssertOwnedGroupCloseRoutesThroughAccountingHelper();
    }

    private static void AssertOwnedGroupCloseRoutesThroughAccountingHelper(
        [CallerFilePath] string sourceFilePath = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath)!, "..", ".."));
        var productionRoot = Path.Combine(repoRoot, "src", "Mcg.AgentOrchestrator.Infrastructure");
        var offenders = Directory.EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => new { path, line, lineNumber = index + 1 }))
            .Where(item =>
                item.path.EndsWith("OwnedProcessGroup.cs", StringComparison.OrdinalIgnoreCase) is false &&
                IsReadAccountingAndDisposeHelperLine(item.path, item.lineNumber) is false &&
                (item.line.Contains("workerGroup?.Dispose()", StringComparison.Ordinal) ||
                 item.line.Contains("workerGroup?.Kill()", StringComparison.Ordinal) ||
                 item.line.Contains("processGroup?.Dispose()", StringComparison.Ordinal) ||
                 item.line.Contains("processGroup?.Kill()", StringComparison.Ordinal) ||
                 item.line.Contains("group.Dispose()", StringComparison.Ordinal) ||
                 item.line.Contains("group.Kill()", StringComparison.Ordinal)) &&
                !item.line.Contains("ReadAccountingAndDispose", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(repoRoot, item.path)}:{item.lineNumber}:{item.line.Trim()}")
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join(Environment.NewLine, offenders));
    }

    private static bool IsReadAccountingAndDisposeHelperLine(string path, int lineNumber)
    {
        if (!path.EndsWith(Path.Combine("Processes", "WorkerProcessJobs.cs"), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lines = File.ReadAllLines(path);
        var start = Array.FindIndex(lines, line => line.Contains("internal static bool ReadAccountingAndDispose(", StringComparison.Ordinal));
        var end = Array.FindIndex(lines, line => line.Contains("internal static bool HasRegisteredJob", StringComparison.Ordinal));
        return start >= 0 && end > start && lineNumber > start && lineNumber <= end;
    }
}

internal static class ProcessStartInfoTestExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
