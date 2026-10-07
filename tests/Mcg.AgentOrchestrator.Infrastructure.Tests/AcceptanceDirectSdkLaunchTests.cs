using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns a unique working directory, capture pipes and process job;
// observations are restricted to that job, and no process-wide hooks or settings are changed.
public sealed class AcceptanceDirectSdkLaunchTests
{
    [Fact]
    public async Task SdkVersionStartsDirectlyWithIdenticalOwnedCapture()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            string[] arguments = ["dotnet", "--version"];
            _ = await RunShellAsync(arguments, directory); // Warm the hermetic SDK profile.
            var reference = await RunShellAsync(arguments, directory);
            var actual = await RunOwnedAsync(arguments, directory);

            Assert.Equal(0, reference.ExitCode);
            Assert.NotEmpty(reference.Stdout);
            Assert.Equal(0, actual.ExitCode);
            Assert.Equal(reference.Stdout, actual.Stdout);
            Assert.Empty(actual.Stderr);
            Assert.Equal("dotnet.exe", actual.RootImage);
            Assert.NotEmpty(actual.JobImages);
            Assert.DoesNotContain(actual.JobImages, IsCmd);
            Assert.Equal(actual.RootProcessId, actual.CommandProcessId);
            Assert.Equal(actual.RootProcessId, actual.ObservedCommandProcessId);
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Fact]
    public async Task UnknownSdkCommandPreservesShellExitCodeAndStderr()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            string[] arguments = ["dotnet", "mcg-no-such-subcommand"];
            // --version returns before SDK first-use configuration and its workload integrity check.
            // Warm this command so neither compared capture includes the one-time diagnostics.
            _ = await RunShellAsync(arguments, directory);
            var reference = await RunShellAsync(arguments, directory);
            var actual = await RunOwnedAsync(arguments, directory);

            Assert.NotEqual(0, reference.ExitCode);
            Assert.Equal(reference.ExitCode, actual.ExitCode);
            Assert.NotEmpty(reference.Stderr);
            Assert.Equal(Normalize(reference.Stderr), Normalize(actual.Stderr));
            Assert.Equal("dotnet.exe", actual.RootImage);
            Assert.DoesNotContain(actual.JobImages, IsCmd);
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonSdkAndUtf8DiscoveryKeepShellLaunch(bool utf8Discovery)
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            var arguments = utf8Discovery ? new[] { "dotnet", "--version" } : ["git", "--version"];
            var actual = await RunOwnedAsync(arguments, directory, utf8Discovery);

            Assert.Equal(0, actual.ExitCode);
            Assert.NotEmpty(actual.Stdout);
            Assert.Equal("cmd.exe", actual.RootImage);
            Assert.Contains(actual.JobImages, IsCmd);
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    [Fact]
    public async Task ShellOnlySdkExecutableFallsBackWithFreshCapture()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = SharedTestSupport.CreateTempDirectory();
        try
        {
            var executable = Path.Combine(directory, "dotnet");
            await File.WriteAllTextAsync(executable + ".cmd", "@echo off\r\necho shell-only-sdk\r\nexit /b 0\r\n");
            var actual = await RunOwnedAsync([executable, "--version"], directory);

            Assert.Equal(0, actual.ExitCode);
            Assert.Equal("shell-only-sdk", Normalize(actual.Stdout));
            Assert.Empty(actual.Stderr);
            Assert.Equal("cmd.exe", actual.RootImage);
            Assert.Contains(actual.JobImages, IsCmd);
        }
        finally { SharedTestSupport.RemoveTempDirectory(directory); }
    }

    private static async Task<OwnedCapture> RunOwnedAsync(
        string[] arguments, string directory, bool utf8Discovery = false)
    {
        var registryPath = Path.Combine(directory, "spawn-registry.db");
        // The registry consumes the state schema; its constructor does not bootstrap one.
        _ = StateDbMigrations.EnsureUpToDate(registryPath);
        Assert.True(StateDbMigrations.IsUpToDate(registryPath));
        using var registry = WorkerProcessJobs.UseRegistryScopeForTests(registryPath);
        var images = new ConcurrentDictionary<int, string>();
        var rootProcessId = 0;
        var observedCommandProcessId = 0;
        void ObserveJob()
        {
            var root = Volatile.Read(ref rootProcessId);
            if (root == 0 || !WorkerProcessJobs.TryGetActiveProcessIds(root, out var ids)) return;
            foreach (var id in ids)
            {
                if (images.ContainsKey(id)) continue;
                try
                {
                    using var member = Process.GetProcessById(id);
                    images.TryAdd(id, Path.GetFileName(ReadImage(member)));
                }
                catch (ArgumentException) { } // A member exited after the job snapshot.
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            }
        }

        // The resume observer sees the suspended root, so a fast --version exit cannot hide the
        // shell wrapper. Subsequent snapshots cover live descendants of this particular job.
        var run = Task.Run(() => GoalAcceptanceVerifier.RunProcessForTestsAsync(
            arguments, directory, TimeSpan.FromMinutes(3),
            cleanupObserver: observation =>
            {
                if (observation.Stage == "started")
                {
                    Assert.True(observation.RegistryActive);
                    Assert.True(observation.JobActive);
                    Assert.True(WorkerProcessJobs.TryGetActiveProcessIds(observation.ProcessId, out _));
                    ObserveJob();
                }
            },
            commandIdentityObserver: identity => observedCommandProcessId = identity.ProcessId,
            keepCaptureFiles: true,
            resumeObserver: processId =>
            {
                using var process = Process.GetProcessById(processId);
                var image = ReadImage(process);
                images.TryAdd(processId, Path.GetFileName(image));
                Volatile.Write(ref rootProcessId, processId);
                Assert.True(WorkerProcessJobs.TryGetActiveProcessIds(processId, out var ids));
                Assert.Contains(processId, ids);
                ObserveJob();
            },
            forceUtf8ConsoleOutput: utf8Discovery));
        while (!run.IsCompleted)
        {
            ObserveJob();
            await Task.Yield();
        }
        var result = await run;
        try
        {
            Assert.False(result.TimedOut);
            Assert.NotNull(result.StdoutPath);
            Assert.NotNull(result.StderrPath);
            var stdout = await File.ReadAllBytesAsync(result.StdoutPath);
            var stderr = await File.ReadAllBytesAsync(result.StderrPath);
            Assert.Equal(stdout.LongLength, result.StdoutBytes);
            Assert.Equal(stderr.LongLength, result.StderrBytes);
            return new OwnedCapture(result.ExitCode, stdout, stderr, images[rootProcessId],
                images.Values.ToArray(), rootProcessId, result.ChildProcessId, observedCommandProcessId);
        }
        finally
        {
            File.Delete(result.StdoutPath!);
            File.Delete(result.StderrPath!);
        }
    }

    private static async Task<ShellCapture> RunShellAsync(string[] arguments, string directory)
    {
        var info = GoalAcceptanceVerifier.BuildAcceptanceProcessStartInfo(arguments, directory);
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(info.Environment, directory);
        using var process = ProcessTreeGuiSuppression.Start(info);
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var outputDrain = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var errorDrain = process.StandardError.BaseStream.CopyToAsync(stderr);
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(guard.Token);
            await Task.WhenAll(outputDrain, errorDrain).WaitAsync(guard.Token);
            return new ShellCapture(process.ExitCode, stdout.ToArray(), stderr.ToArray());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
            }
            await Task.WhenAll(outputDrain, errorDrain).WaitAsync(TimeSpan.FromMinutes(3));
        }
    }

    private static bool IsCmd(string image) => image.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    private static string ReadImage(Process process)
    {
        var capacity = 32768u;
        var path = new StringBuilder((int)capacity);
        if (!QueryFullProcessImageNameW(process.Handle, 0, path, ref capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);

    private sealed record ShellCapture(int ExitCode, byte[] Stdout, byte[] Stderr);
    private sealed record OwnedCapture(int ExitCode, byte[] Stdout, byte[] Stderr, string RootImage,
        string[] JobImages, int RootProcessId, int? CommandProcessId, int ObservedCommandProcessId);
}
