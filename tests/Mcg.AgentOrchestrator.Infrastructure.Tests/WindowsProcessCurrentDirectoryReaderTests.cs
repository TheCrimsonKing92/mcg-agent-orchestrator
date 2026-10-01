using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WindowsProcessCurrentDirectoryReaderTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Windows parameters-block reader", SkipUnless = nameof(IsWindows))]
    public async Task ReadCurrentDirectory_ChildWithUnrelatedCommand_NamesCwdHolder()
    {
        // This explicit OS contract test inspects only the child it owns, never the machine process set.
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var held = Path.Combine(root, "held");
        Directory.CreateDirectory(held);
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(WorkerShell.Executable)
            {
                WorkingDirectory = held,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        var inherited = child.StartInfo.Environment;
        var allowed = new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "PSModulePath" }
            .Select(key => (Key: key, Value: Environment.GetEnvironmentVariable(key))).ToArray();
        inherited.Clear();
        foreach (var (key, value) in allowed)
        {
            if (value is not null) inherited[key] = value;
        }
        foreach (var argument in WorkerShell.BaseArguments()) child.StartInfo.ArgumentList.Add(argument);
        child.StartInfo.ArgumentList.Add("Write-Output ready; $null = [Console]::In.ReadToEnd()");

        var started = false;
        Task<string>? stderr = null;
        Task<string?>? ready = null;
        Task<string>? stdout = null;
        try
        {
            Assert.True(child.Start(), "Owned child shell did not start.");
            started = true;
            stderr = child.StandardError.ReadToEndAsync();
            ready = child.StandardOutput.ReadLineAsync();
            string? signal;
            try { signal = await ready.WaitAsync(TimeSpan.FromSeconds(60)); }
            catch (TimeoutException)
            {
                Assert.Fail($"Child pid={child.Id} did not emit the ready line 'ready'.");
                throw;
            }
            stdout = child.StandardOutput.ReadToEndAsync();
            Assert.True(signal == "ready", $"Child pid={child.Id} did not emit the ready line 'ready'; received '{signal ?? "EOF"}'.");
            Assert.False(child.HasExited, $"Child pid={child.Id} exited before current-directory inspection.");

            var result = WindowsNativeProcessInspection.ReadCurrentDirectory(child.Id);
            Assert.True(result.Status == ProcessCurrentDirectoryStatus.Available,
                $"Reader status={result.Status} native_error={result.NativeError} pid={child.Id} expected directory='{held}'.");
            Assert.Equal(held, result.Path, ignoreCase: true);

            var snapshot = ProcessCommandLines.Snapshot([child.Id]);
            var record = Assert.Single(snapshot.Records).Value;
            Assert.Equal(ProcessInspectionStatus.Available, record.Status);
            Assert.NotNull(record.CommandLine);
            Assert.DoesNotContain(root, record.CommandLine, StringComparison.OrdinalIgnoreCase);
            var report = AssemblyTempRootCleanupHolderDiagnostics.Describe(root, () => snapshot,
                WindowsNativeProcessInspection.ReadCurrentDirectory);
            var holder = Assert.Single(report.Holders);
            Assert.Equal(child.Id, holder.ProcessId);
            Assert.Equal("cwd", holder.Match);
            Assert.Equal(held, holder.CurrentDirectory, ignoreCase: true);
        }
        finally
        {
            try
            {
                if (started)
                {
                    child.StandardInput.Close();
                    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
                    catch (TimeoutException)
                    {
                        Assert.Fail($"Child pid={child.Id} did not exit after its input was closed.");
                        throw;
                    }
                }
            }
            finally
            {
                if (started && !child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
                    catch (TimeoutException)
                    {
                        Assert.Fail($"Child pid={child.Id} did not exit after teardown killed it.");
                        throw;
                    }
                }
                if (ready is not null) await Drain(ready, "ready-line read", child.Id);
                if (stdout is not null) await Drain(stdout, "stdout drain", child.Id);
                if (stderr is not null) await Drain(stderr, "stderr drain", child.Id);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact(Skip = "Non-Windows reader contract", SkipUnless = nameof(IsNotWindows))]
    public void ReadCurrentDirectory_NonWindows_ReportsUnsupported() =>
        Assert.Equal(ProcessCurrentDirectoryStatus.Unsupported,
            WindowsNativeProcessInspection.ReadCurrentDirectory(Environment.ProcessId).Status);

    public static bool IsNotWindows => !IsWindows;

    private static async Task Drain(Task task, string eventName, int pid)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException)
        {
            Assert.Fail($"Child pid={pid} did not complete {eventName} after exit.");
            throw;
        }
    }
}
