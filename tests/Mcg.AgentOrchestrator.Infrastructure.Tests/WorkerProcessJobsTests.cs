using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.CollectionDefinition("ProcessSpawning", DisableParallelization = true)]
public sealed class ProcessSpawningCollection;

[Xunit.Collection("ProcessSpawning")]
public sealed class WorkerProcessJobsTests
{
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
