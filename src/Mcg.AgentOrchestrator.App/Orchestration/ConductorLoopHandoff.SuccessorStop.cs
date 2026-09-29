using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface ISuccessorProcessFacts
{
    ISuccessorProcessHandle? TryOpen(int processId);
}

internal interface ISuccessorProcessHandle : IDisposable
{
    bool HasExited { get; }
    DateTimeOffset? StartedAt { get; }
    void KillTree();
    bool WaitForExit(int milliseconds);
}

internal static partial class ConductorLoopHandoff
{
    internal static void StopFailedSuccessor(ConductorSupervisorProcessIdentity identity) =>
        StopFailedSuccessor(identity, SystemSuccessorProcessFacts.Instance, line =>
        {
            Console.WriteLine(line);
            Console.Out.Flush();
        });

    internal static void StopFailedSuccessor(ConductorSupervisorProcessIdentity identity,
        ISuccessorProcessFacts facts, Action<string> emit)
    {
        if (identity.ProcessId == Environment.ProcessId)
            throw new InvalidOperationException("Refusing to stop the incumbent conductor process.");

        using var process = facts.TryOpen(identity.ProcessId);
        if (process is null || process.HasExited)
        {
            emit($"SUCCESSOR_STOP_REFUSED pid={identity.ProcessId} reason=exited");
            return;
        }

        if (process.StartedAt != identity.StartedAt)
        {
            emit($"SUCCESSOR_STOP_REFUSED pid={identity.ProcessId} reason=identity-mismatch");
            return;
        }

        // The handle keeps the observed process object; the timestamp is checked immediately before the stop.
        process.KillTree();
        if (!process.WaitForExit(5000) || !process.HasExited)
            throw new InvalidOperationException(
                $"Conduct loop successor pid {identity.ProcessId} did not terminate within 5 seconds.");
    }

    internal static ConductLoopLaunchResult CreateDetachedLauncherResult(
        ConductLoopLaunchRequest request, string? pidText, Func<int, DateTimeOffset?> readStartedAt)
    {
        if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            throw new InvalidOperationException("Detached conduct loop launcher did not report a successor pid.");
        return new(pid, request.StdoutPath, request.StderrPath,
            "spawnPath=posix-shell-detached breakawayRequested=false breakawaySucceeded=not-applicable",
            readStartedAt(pid));
    }

    internal static ConductLoopLaunchResult CreateBreakawayLaunchResult(
        ConductLoopLaunchRequest request, int pid, string residualJobMembership, DateTimeOffset? startedAt) =>
        new(pid, request.StdoutPath, request.StderrPath,
            "spawnPath=windows-createprocess hostResolution=native-executable " +
            $"breakawayRequested=true breakawaySucceeded=true residualJobMembership={residualJobMembership}",
            startedAt);

    private static DateTimeOffset? ReadSuccessorStartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (Win32Exception) { return null; }
    }

    private static DateTimeOffset? ReadSuccessorStartTimeFromHandle(IntPtr processHandle) =>
        GetProcessTimes(processHandle, out var creation, out _, out _, out _)
            ? DateTimeOffset.FromFileTime(creation)
            : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr processHandle, out long creation,
        out long exit, out long kernel, out long user);

    private sealed class SystemSuccessorProcessFacts : ISuccessorProcessFacts
    {
        internal static readonly SystemSuccessorProcessFacts Instance = new();

        public ISuccessorProcessHandle? TryOpen(int processId)
        {
            try { return new SystemSuccessorProcessHandle(Process.GetProcessById(processId)); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (Win32Exception) { return UnreadableProcessHandle.Instance; }
        }
    }

    private sealed class UnreadableProcessHandle : ISuccessorProcessHandle
    {
        internal static readonly UnreadableProcessHandle Instance = new();
        public bool HasExited => false;
        public DateTimeOffset? StartedAt => null;
        public void KillTree() => throw new InvalidOperationException("Process identity was unreadable.");
        public bool WaitForExit(int milliseconds) => false;
        public void Dispose() { }
    }

    private sealed class SystemSuccessorProcessHandle(Process process) : ISuccessorProcessHandle
    {
        public bool HasExited
        {
            get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } }
        }

        public DateTimeOffset? StartedAt
        {
            get
            {
                try { return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero); }
                catch (InvalidOperationException) { return null; }
                catch (Win32Exception) { return null; }
            }
        }

        public void KillTree() => process.Kill(entireProcessTree: true);
        public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);
        public void Dispose() => process.Dispose();
    }
}
