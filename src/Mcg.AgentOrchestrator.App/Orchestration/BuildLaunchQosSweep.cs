using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class BuildLaunchQosSweep : IDisposable
{
    private readonly object sync = new();
    private readonly AcceptanceChildQos sweep;
    private readonly string kind;
    private readonly string goalId;
    private readonly string logPath;
    private bool disposed;

    private BuildLaunchQosSweep(AcceptanceChildQos sweep, string kind, string goalId, string logPath)
    {
        this.sweep = sweep;
        this.kind = kind;
        this.goalId = goalId;
        this.logPath = logPath;
    }

    internal static BuildLaunchQosSweep? Begin(string kind, string statePath, string goalId, int rootPid,
        Func<int, IReadOnlyList<int>?> members, Func<int, uint, uint, bool>? setter = null,
        TimeSpan? interval = null)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
            var policy = ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(directory);
            if (!policy.AcceptanceChildrenOptOutExecutionThrottling) return null;
            var logPath = Path.Combine(directory.FullName, "logs", ConductEventLogWriter.CurrentFileName);
            var sweep = AcceptanceChildQos.StartSweep(true, rootPid, interval ?? TimeSpan.FromSeconds(5),
                members, setter ?? ProcessExecutionThrottlingOptOut.TrySet)!;
            return new(sweep, kind, goalId, logPath);
        }
        catch (Exception) { return null; } // QoS must not change build results, including on policy-load failure.
    }

    internal static IReadOnlyList<int>? RootAndDescendants(int rootPid)
    {
        try { return [rootPid, .. WindowsNativeProcessInspection.ListIdentityBoundDescendantProcessIds(rootPid)]; }
        catch (Exception) { return null; }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            sweep.Dispose();
            try
            {
                new ConductEventLogWriter(logPath).Append("build-qos", goalId,
                    $"BUILD_QOS kind={kind} opted_out={sweep.OptedOutCount} tried={sweep.TriedCount}");
            }
            catch (Exception) { /* Logging is best-effort, after the sweep has stopped. */ }
        }
    }
}
