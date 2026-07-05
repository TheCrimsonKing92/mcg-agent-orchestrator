using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("ProcessSpawning")]
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
            if (File.Exists(path) &&
                int.TryParse(File.ReadAllText(path).Trim(), out var pid) &&
                pid > 0)
            {
                return pid;
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
