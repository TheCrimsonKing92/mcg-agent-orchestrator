using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerTestImpactPlanSelection
{
    internal static RepositoryTestImpactPlan For(
        IReadOnlyList<string> changedFiles,
        string workingDirectory,
        WorkerTargetHome targetHome)
    {
        ArgumentNullException.ThrowIfNull(targetHome);
        return targetHome.IsOrchestratorHome
            ? RepositoryTestImpactPlanner.Plan(changedFiles, workingDirectory)
            : ForeignTargetTestImpactPlanner.Plan(changedFiles, workingDirectory);
    }
}
