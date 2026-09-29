using System.Diagnostics;
using Mcg.AgentOrchestrator.App.Orchestration;

internal static class TestOwnedProcessStop
{
    internal static ConductorSupervisorProcessIdentity? Identify(Process process)
    {
        try
        {
            if (process.HasExited) return null;
            return new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
        }
        catch { return null; }
    }

    internal static ConductorSupervisorProcessIdentity? TryIdentify(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return Identify(process);
        }
        catch { return null; }
    }

    internal static bool StopTreeIfSame(ConductorSupervisorProcessIdentity? identity,
        ISuccessorProcessFacts? facts = null)
    {
        if (identity is null || identity.ProcessId == Environment.ProcessId) return false;
        try
        {
            using var process = (facts ?? ProcessFacts.Instance).TryOpen(identity.ProcessId);
            if (process is null || process.HasExited || process.StartedAt != identity.StartedAt)
                return false;
            process.KillTree();
            return true;
        }
        catch { return false; }
    }

    private sealed class ProcessFacts : ISuccessorProcessFacts
    {
        internal static readonly ProcessFacts Instance = new();
        public ISuccessorProcessHandle? TryOpen(int pid)
        {
            try { return new ProcessHandle(Process.GetProcessById(pid)); }
            catch (ArgumentException) { return null; }
        }
    }

    private sealed class ProcessHandle(Process process) : ISuccessorProcessHandle
    {
        public bool HasExited => process.HasExited;
        public DateTimeOffset? StartedAt =>
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        public void KillTree() => process.Kill(entireProcessTree: true);
        public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);
        public void Dispose() => process.Dispose();
    }
}
