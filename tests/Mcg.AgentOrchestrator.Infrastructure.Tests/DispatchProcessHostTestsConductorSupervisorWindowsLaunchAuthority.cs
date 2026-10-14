using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class DispatchProcessHostTestsConductorSupervisorWindowsLaunchAuthority(Xunit.ITestOutputHelper output)
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Theory(Skip = "Requires Windows process/job semantics.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData(true, false, 0, true)]
    [Xunit.InlineData(false, false, 5, false)]
    [Xunit.InlineData(true, true, 0, true)]
    [Xunit.InlineData(false, true, 0, false)]
    public void NativeLaunch_JobPolicyComparison_RecordsAuthorityAndLifetime(
        bool allowBreakaway, bool nested, int expectedError, bool escapesBoundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-supervisor-authority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var token = @"Local\mcg-authority-" + Guid.NewGuid().ToString("N");
        using var ready = NewEvent(token + "-ready");
        using var boundary = NewEvent(token + "-boundary");
        using var survived = NewEvent(token + "-survived");
        using var stop = NewEvent(token + "-stop");
        using var resultReady = NewEvent(token + "-result");
        using var outer = new FixtureJob(token + "-outer", allowBreakaway);
        using var inner = nested ? new FixtureJob(token + "-inner", true) : null;
        Process? successor = null;
        try
        {
            var probe = LocateProbe(GetType());
            output.WriteLine(JsonSerializer.Serialize(new { probeDirectory = probe.Directory,
                probeTriedDirectories = probe.TriedPaths, requiredProbeFiles = ProbeFiles }));
            Assert.True(probe.Directory is not null,
                $"Supervisor launch probe requires {string.Join(", ", ProbeFiles)} in one build-bound directory. " +
                $"Tried directories: {string.Join(", ", probe.TriedPaths)}");
            var assembly = Path.Combine(probe.Directory!, ProbeFiles[0]);
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root
            };
            // The fixture owns every behavior switch. Only host/runtime prerequisites survive.
            var allowed = new[] { "SystemRoot", "WINDIR", "PATH", "TEMP", "TMP", "USERPROFILE",
                "LOCALAPPDATA", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86" };
            startInfo.Environment.Clear();
            foreach (var key in allowed)
                if (Environment.GetEnvironmentVariable(key) is { } value) startInfo.Environment[key] = value;
            foreach (var arg in new[] { assembly, "parent", root, token }) startInfo.ArgumentList.Add(arg);

            // Assignment completes while suspended, before any tested launch can occur.
            using var parent = OwnedProcessGroup.StartSuspended(startInfo);
            outer.Add(parent.Process);
            inner?.Add(parent.Process);
            Assert.True(outer.Contains(parent.Process));
            if (inner is not null) Assert.True(inner.Contains(parent.Process));
            output.WriteLine(JsonSerializer.Serialize(new { fixtureAssembly = assembly,
                predecessorPid = parent.Process.Id, predecessorStartedAt = parent.Process.StartTime.ToUniversalTime(),
                outerJob = outer.Name, outerLimits = outer.Limits,
                innerJob = inner?.Name, innerLimits = inner?.Limits,
                runnerAncestor = "Acceptance must enable and record -AllowBreakaway; other ancestors are unknown" }));
            parent.Resume();
            Assert.True(resultReady.WaitOne(TimeSpan.FromMinutes(1)), "Predecessor did not publish native launch receipt.");
            var receipt = File.ReadAllText(Path.Combine(root, "result.json"));
            output.WriteLine(receipt);
            using var result = JsonDocument.Parse(receipt);
            if (result.RootElement.GetProperty("ProcessId").GetInt32() is > 0 and var pid)
                successor = Process.GetProcessById(pid);
            Assert.Equal(expectedError, result.RootElement.GetProperty("nativeError").GetInt32());
            var events = File.ReadAllText(Path.Combine(root, "conduct-events.log"));
            output.WriteLine(events);
            var authorityLines = File.ReadAllLines(Path.Combine(root, "conduct-events.log"))
                .Where(line => line.Contains("\"eventKind\":\"loop-handoff-launch-authority\"", StringComparison.Ordinal));
            using var authorityEvent = JsonDocument.Parse(Assert.Single(authorityLines));
            using var launch = JsonDocument.Parse(authorityEvent.RootElement.GetProperty("detail").GetString()!);
            Assert.Equal(expectedError, launch.RootElement.GetProperty("nativeError").GetInt32());
            Assert.Equal("CreateProcessW", launch.RootElement.GetProperty("api").GetString());
            var authority = launch.RootElement.GetProperty("authority");
            Assert.Equal(parent.Process.Id, authority.GetProperty("incumbentPid").GetInt32());
            Assert.Equal("true", authority.GetProperty("incumbentJobMembership").GetString());
            Assert.Equal("innermost-only", authority.GetProperty("jobQueryScope").GetString());
            Assert.Equal(0, authority.GetProperty("jobQueryNativeError").GetInt32());
            Assert.Equal(inner?.Limits ?? outer.Limits, authority.GetProperty("innermostJobLimitFlags").GetUInt32());
            var flags = Convert.ToUInt32(authority.GetProperty("creationFlags").GetString()![2..], 16);
            Assert.Equal(0x01080600u, flags & 0x01080600u);
            if (expectedError == 0)
            {
                Assert.NotNull(successor);
                var launchedAt = result.RootElement.GetProperty("StartedAt").GetDateTimeOffset();
                Assert.Equal(launchedAt, new DateTimeOffset(successor.StartTime.ToUniversalTime()));
                Assert.Equal(!escapesBoundary, outer.Contains(successor));
                if (inner is not null) Assert.False(inner.Contains(successor));
                output.WriteLine($"successorPid={successor.Id} startedAt={launchedAt:O} outerMembership={!escapesBoundary}");
            }

            // Close precisely the named predecessor boundary, then demand a positive
            // successor acknowledgment or a positive exit observation, never a timed absence.
            outer.Dispose();
            Assert.True(parent.WaitForOwnedExit(TimeSpan.FromMinutes(1)), "Predecessor did not exit on boundary closure.");
            if (successor is not null)
            {
                if (escapesBoundary)
                {
                    boundary.Set();
                    Assert.True(survived.WaitOne(TimeSpan.FromMinutes(1)), "Successor did not acknowledge after boundary closure.");
                    Assert.Equal(result.RootElement.GetProperty("StartedAt").GetDateTimeOffset(),
                        new DateTimeOffset(successor.StartTime.ToUniversalTime()));
                    output.WriteLine("boundaryClosed=true successorAcknowledged=true identityPreserved=true");
                }
                else
                {
                    Assert.True(successor.WaitForExit(60000), "Contained successor did not exit when its named ancestor closed.");
                    output.WriteLine($"boundaryClosed=true containedSuccessorExited=true exitCode={successor.ExitCode}");
                }
            }
        }
        finally
        {
            stop.Set();
            if (successor is not null)
            {
                if (!successor.WaitForExit(60000)) throw new TimeoutException("Fixture successor did not acknowledge cleanup stop.");
                output.WriteLine($"cleanupSuccessorExited=true exitCode={successor.ExitCode}");
                successor.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private const string ProbeProject = "Mcg.AgentOrchestrator.SupervisorLaunchProbe";
    private static readonly string[] ProbeFiles = [ProbeProject + ".dll", ProbeProject + ".runtimeconfig.json"];

    internal static TestBuildOutputLocator.Location LocateProbe(Type anchor)
    {
        // Managed invocations publish the probe beside this exact test assembly. Native builds
        // can copy only its runtimeconfig here; resolve a complete build-bound sibling/export
        // in that case. Never combine files from separate directories or search other builds.
        var currentClosure = Path.GetDirectoryName(anchor.Assembly.Location)!;
        if (ProbeFiles.All(name => File.Exists(Path.Combine(currentClosure, name))))
            return new(currentClosure, [currentClosure]);

        var directory = FixtureProbeLocator.FindDirectory(anchor, ProbeProject, ProbeFiles);
        return new(directory, [currentClosure, .. FixtureProbeLocator.TriedPaths(anchor, ProbeProject, ProbeFiles)]);
    }

    private static EventWaitHandle NewEvent(string name) => new(false, EventResetMode.ManualReset, name);

    internal sealed class FixtureJob : IDisposable
    {
        private readonly SafeFileHandle _handle;
        public string Name { get; }
        public uint Limits { get; }
        public FixtureJob(string name, bool allowBreakaway)
        {
            Name = name;
            Limits = 0x2000u | (allowBreakaway ? 0x800u : 0u);
            _handle = CreateJobObjectW(IntPtr.Zero, name);
            if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = Limits } };
            if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            {
                var error = Marshal.GetLastWin32Error();
                _handle.Dispose();
                throw new Win32Exception(error);
            }
        }
        public void Add(Process process)
        {
            if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public bool Contains(Process process)
        {
            if (!IsProcessInJob(process.Handle, _handle, out var member)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return member;
        }
        public void Dispose() => _handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
}
