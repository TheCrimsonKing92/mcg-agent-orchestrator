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
            : ScopeToManifest(
                ForeignTargetTestImpactPlanner.Plan(changedFiles, workingDirectory),
                workingDirectory,
                WorkerManifestTestProjectReader.ResolvePath(workingDirectory, targetHome));
    }

    internal static RepositoryTestImpactPlan ScopeToManifest(
        RepositoryTestImpactPlan plan,
        string workingDirectory,
        string manifestPath)
    {
        var declared = WorkerManifestTestProjectReader.Read(manifestPath);
        var displayPath = string.IsNullOrWhiteSpace(manifestPath)
            ? "(unspecified)"
            : WorkerAcceptanceManifestDisplayPath.Render(workingDirectory, manifestPath);
        if (declared is not null)
        {
            return declared.Filter(plan) with
            {
                Summary = $"{plan.Summary}; checks scoped to dotnet-test projects declared by {displayPath}"
            };
        }

        var availability = File.Exists(manifestPath) ? "unreadable" : "missing";
        return plan with
        {
            Checks = [],
            Summary = $"{plan.Summary}; acceptance manifest is {availability} at {displayPath}; no project checks derived"
        };
    }
}
