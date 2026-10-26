using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DispatchJobExecutionQos
{
    internal static bool IsEnabled(string statePath)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
            return ConductorAutonomyPolicy.LoadFromOrchestratorDirectory(directory)
                .AcceptanceChildrenOptOutExecutionThrottling;
        }
        catch (Exception) { return false; }
    }

    internal static IDisposable? Start(int rootPid, Func<int, IReadOnlyList<int>?> members) =>
        AcceptanceChildQos.StartSweep(true, rootPid, TimeSpan.FromSeconds(5), members);
}
