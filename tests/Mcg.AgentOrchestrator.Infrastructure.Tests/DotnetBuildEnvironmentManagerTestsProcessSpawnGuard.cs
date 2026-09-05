using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;
using static DotnetBuildEnvironmentManagerTests;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DotnetBuildEnvironmentManagerTestsProcessSpawnGuard
{
    [Xunit.Fact(DisplayName = "ProcessSpawnGuard_clears_inheritable_state_db_file_handles")]
    public void ProcessSpawnGuardClearsInheritableStateDbFileHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"mcg-state-handle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        try
        {
            using var handle = CreateInheritableFileHandle(path);
            Assert.True(ProcessSpawnGuard.IsHandleInheritable(handle.DangerousGetHandle()));

            var cleared = ProcessSpawnGuard.ClearInheritableFileHandles("state.db");

            Assert.True(cleared >= 1);
            Assert.False(ProcessSpawnGuard.IsHandleInheritable(handle.DangerousGetHandle()));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    // A concurrent Process.Start on another thread holds its child's std pipe ends inheritable for the
    // instant before CreateProcess. A guard sweep that clears the flag on handles it cannot resolve to a
    // disk path lands in that window and starts the child with invalid std handles: git then exits with
    // nothing on either stream, and a native launcher whose listed handles lost inheritance fails to start.
    [Xunit.Fact(DisplayName = "ProcessSpawnGuard_leaves_inheritable_pipe_handles_untouched")]
    public void ProcessSpawnGuardLeavesInheritablePipeHandlesUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.Out,
            HandleInheritability.Inheritable);
        var clientHandle = pipe.ClientSafePipeHandle.DangerousGetHandle();
        Assert.True(ProcessSpawnGuard.IsHandleInheritable(clientHandle));

        ProcessSpawnGuard.ClearInheritableStateDatabaseHandles();

        Assert.True(
            ProcessSpawnGuard.IsHandleInheritable(clientHandle),
            "The guard cleared the inherit flag on an anonymous pipe handle it does not own.");
    }

    // The conductor's own standard handles are the pipes its continuity supervisor reads to EOF. A child
    // that inherits them keeps the pipe open past the conductor's exit, and the supervisor stalls the
    // max-duration handoff until that grandchild exits (38 minutes on 2026-09-05). The guard must strip
    // the inherit flag from this process's std handles before every child launch. stdin is used here
    // because nothing in the test host reads it; the original handle is restored afterwards.
    [Xunit.Fact(DisplayName = "ProcessSpawnGuard_clears_the_inherit_flag_on_this_processes_standard_handles")]
    public void ProcessSpawnGuardClearsTheInheritFlagOnThisProcessesStandardHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int StdInputHandle = -10;
        var original = GetStdHandle(StdInputHandle);
        using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(
            System.IO.Pipes.PipeDirection.In,
            HandleInheritability.Inheritable);
        var inheritableEnd = pipe.ClientSafePipeHandle.DangerousGetHandle();
        Assert.True(ProcessSpawnGuard.IsHandleInheritable(inheritableEnd));
        Assert.True(SetStdHandle(StdInputHandle, inheritableEnd), "SetStdHandle failed.");
        try
        {
            var cleared = ProcessSpawnGuard.ClearInheritableStateDatabaseHandles();

            Assert.True(cleared >= 1, "The guard reported no cleared handles although stdin was inheritable.");
            Assert.False(
                ProcessSpawnGuard.IsHandleInheritable(inheritableEnd),
                "The guard left this process's inheritable stdin handle inheritable; every child would keep the supervisor's pipe open.");
        }
        finally
        {
            SetStdHandle(StdInputHandle, original);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    [Xunit.Fact(DisplayName = "ProcessSpawnGuard_leaves_inheritable_non_target_file_handles_untouched")]
    public void ProcessSpawnGuardLeavesInheritableNonTargetFileHandlesUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var _ = EnvVarScope.ForIsolatedDotnetRoot();
        var directory = Path.Combine(Path.GetTempPath(), $"mcg-other-handle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "worker-output.log");
        try
        {
            using var handle = CreateInheritableFileHandle(path);
            Assert.True(ProcessSpawnGuard.IsHandleInheritable(handle.DangerousGetHandle()));

            ProcessSpawnGuard.ClearInheritableStateDatabaseHandles();

            Assert.True(ProcessSpawnGuard.IsHandleInheritable(handle.DangerousGetHandle()));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }
}
