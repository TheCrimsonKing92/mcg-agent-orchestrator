using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: unique roots, named events, child environments and process identities.
// Uses the prebuilt probe; no build leases or shared isolated dotnet root are needed.
public sealed class ConductorStartHandleIsolationTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Windows console attachment.", SkipUnless = nameof(IsWindows))]
    public async Task Start_Conductor_DoesNotInheritCallerConsole()
    {
        using var fixture = new LaunchFixture();
        var output = fixture.Caller.StandardOutput.ReadToEndAsync();
        Assert.True(fixture.Caller.WaitForExit(60000), "CLI caller did not exit.");
        Assert.Equal(0, fixture.Caller.ExitCode);
        await output.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Ready.WaitOne(TimeSpan.FromMinutes(1)), "Fake conductor did not report its console attachment.");
        using var callerConsole = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "caller-console.json")));
        var callerPid = callerConsole.RootElement.GetProperty("pid").GetUInt32();
        Assert.Contains(callerPid, callerConsole.RootElement.GetProperty("processIds")
            .EnumerateArray().Select(value => value.GetUInt32()));
        using var conductor = fixture.OpenConductor();
        Assert.False(conductor.HasExited);
        using var conductorConsole = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "conductor-console.json")));
        Assert.Equal(conductor.Id, conductorConsole.RootElement.GetProperty("pid").GetInt32());
        var conductorConsoleProcesses = conductorConsole.RootElement.GetProperty("processIds")
            .EnumerateArray().Select(value => value.GetUInt32()).ToArray();
        Assert.Contains((uint)conductor.Id, conductorConsoleProcesses);
        // CREATE_NO_WINDOW can provide a private windowless console. The caller
        // stays alive until this snapshot is written, so inheritance includes its pid.
        Assert.DoesNotContain(callerPid, conductorConsoleProcesses);
    }

    [Fact(Skip = "Requires Windows handle inheritance.", SkipUnless = nameof(IsWindows))]
    public async Task Start_MergedCallerPipe_EndsWhileConductorIsAlive()
    {
        using var fixture = new LaunchFixture();
        var output = fixture.Caller.StandardOutput.ReadToEndAsync();
        // WaitForExit with a bound observes process exit independently of pipe EOF.
        Assert.True(fixture.Caller.WaitForExit(60000), "CLI caller did not exit.");
        Assert.Equal(0, fixture.Caller.ExitCode);
        string text;
        try { text = await output.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException)
        {
            throw new TimeoutException("Merged caller pipe did not reach EOF after CLI exit.");
        }
        using var conductor = fixture.OpenConductor();
        Assert.False(conductor.HasExited);
        Assert.Contains($"Conductor launched: pid {conductor.Id}; stdout log: ", text);
        Assert.DoesNotContain("fake-conductor-stdout", text);
        Assert.DoesNotContain("fake-conductor-stderr", text);
    }

    [Fact(Skip = "Requires Windows handle inheritance.", SkipUnless = nameof(IsWindows))]
    public async Task Start_Receipt_IdentifiesConductorAndSeparateOutputLogs()
    {
        using var fixture = new LaunchFixture();
        var output = fixture.Caller.StandardOutput.ReadToEndAsync();
        Assert.True(fixture.Caller.WaitForExit(60000), "CLI caller did not exit.");
        Assert.Equal(0, fixture.Caller.ExitCode);
        var text = await output.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Ready.WaitOne(TimeSpan.FromMinutes(1)), "Fake conductor did not flush both log markers.");
        using var conductor = fixture.OpenConductor();
        Assert.False(conductor.HasExited);
        using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "launch-receipt.json")));
        var value = receipt.RootElement;
        Assert.Equal(conductor.Id, value.GetProperty("pid").GetInt32());
        var stdoutPath = value.GetProperty("stdoutPath").GetString()!;
        var stderrPath = value.GetProperty("stderrPath").GetString()!;
        Assert.Equal($"Conductor launched: pid {conductor.Id}; stdout log: {stdoutPath}" + Environment.NewLine +
            "Check status: mcg-orchestrator.cmd conductor status" + Environment.NewLine, text);
        Assert.Equal(Path.Combine(fixture.Root, ".orchestrator", "logs"), Path.GetDirectoryName(stdoutPath));
        Assert.Equal(Path.GetDirectoryName(stdoutPath), Path.GetDirectoryName(stderrPath));
        Assert.Matches(@"^operator-conduct-loop-daemon-\d{14}\.out\.log$", Path.GetFileName(stdoutPath));
        Assert.Equal(stdoutPath[..^8] + ".err.log", stderrPath);
        Assert.Equal($"fake-conductor-stdout pid={conductor.Id}" + Environment.NewLine, ReadLiveLog(stdoutPath));
        Assert.Equal($"fake-conductor-stderr pid={conductor.Id}" + Environment.NewLine, ReadLiveLog(stderrPath));
        Assert.Equal("60", value.GetProperty("args")[3].GetString());
    }

    private static string ReadLiveLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class LaunchFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "mcg-start-pipe-" + Guid.NewGuid().ToString("N"));
        internal EventWaitHandle Ready { get; }
        internal Process Caller { get; }

        internal LaunchFixture()
        {
            Directory.CreateDirectory(Root);
            var token = @"Local\mcg-start-pipe-" + Guid.NewGuid().ToString("N");
            Ready = new EventWaitHandle(false, EventResetMode.ManualReset, token);
            try
            {
                var location = DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority.LocateProbe(GetType());
                Assert.NotNull(location.Directory);
                var probe = Path.Combine(location.Directory!, "Mcg.AgentOrchestrator.SupervisorLaunchProbe.dll");
                var command = ConductorLoopHandoff.ResolveWindowsNativeCommand(
                    ["dotnet", probe, "conductor-start-caller", Root, token], Root);
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
                {
                    Arguments = "/d /s /c \"" + ConductorLoopHandoff.BuildWindowsProcessCommandLine(command) + " 2>&1\"",
                    WorkingDirectory = Root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // AllocConsole in the caller probe honors the startup visibility.
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.Environment.Clear();
                foreach (var key in new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "USERPROFILE",
                             "LOCALAPPDATA", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86" })
                    if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
                Caller = Process.Start(start) ?? throw new InvalidOperationException("Caller probe did not start.");
                Caller.StandardInput.Close();
            }
            catch
            {
                Ready.Dispose();
                Directory.Delete(Root, recursive: true);
                throw;
            }
        }

        internal Process OpenConductor()
        {
            using var identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "launch-identity.json")));
            var process = Process.GetProcessById(identity.RootElement.GetProperty("pid").GetInt32());
            try
            {
                // Retain the handle and validate start time/image before using the pid.
                _ = process.SafeHandle;
                Assert.Equal(identity.RootElement.GetProperty("startedAt").GetDateTime(), process.StartTime.ToUniversalTime());
                Assert.Equal(identity.RootElement.GetProperty("imagePath").GetString(), process.MainModule!.FileName);
                return process;
            }
            catch { process.Dispose(); throw; }
        }

        public void Dispose()
        {
            try
            {
                if (!Caller.HasExited) { Caller.Kill(entireProcessTree: true); Caller.WaitForExit(60000); }
                if (File.Exists(Path.Combine(Root, "launch-identity.json")))
                {
                    using var conductor = OpenConductor();
                    if (!conductor.HasExited) conductor.Kill();
                    Assert.True(conductor.WaitForExit(60000), "Test-owned fake conductor did not exit during cleanup.");
                }
            }
            finally
            {
                Caller.Dispose();
                Ready.Dispose();
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
