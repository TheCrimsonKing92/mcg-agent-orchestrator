using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AppStatePersistence
{
    public static AgentOrchestratorKernel LoadKernel(string statePath)
    {
        return OrchestratorStateStore.Load(statePath);
    }

    public static Task<AgentOrchestratorKernel> LoadKernelAsync(string statePath, CancellationToken cancellationToken = default)
    {
        return new FileOrchestratorStateRepository(statePath).LoadAsync(cancellationToken);
    }

    public static void SaveKernel(string statePath, AgentOrchestratorKernel kernel)
    {
        OrchestratorStateStore.Save(statePath, kernel);
    }

    public static Task SaveKernelAsync(string statePath, AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
    {
        return new FileOrchestratorStateRepository(statePath).SaveAsync(kernel, cancellationToken);
    }
}

