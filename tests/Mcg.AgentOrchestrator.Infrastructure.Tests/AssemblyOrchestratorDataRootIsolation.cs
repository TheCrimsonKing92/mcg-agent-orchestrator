using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Orchestration;

internal static class AssemblyOrchestratorDataRootIsolation
{
    [ModuleInitializer]
    internal static void Install()
    {
        // Child CLI processes inherit a test-owned root, never the operator's data.
        Environment.SetEnvironmentVariable(OrchestratorDataRoot.EnvironmentVariable,
            Path.Combine(Path.GetTempPath(), "mcg-orchestrator-tests", "data", Guid.NewGuid().ToString("N")));
    }
}
