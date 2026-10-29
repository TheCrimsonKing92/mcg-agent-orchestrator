using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns manifest scoping for goal evidence and recovery plans on foreign targets.
internal static class GoalTargetImpactPlanScope
{
    internal static RepositoryTestImpactPlan Plan(
        RepositoryChangeSummary changeSummary, string? worktreePath, WorkerTargetHome? targetHome)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
            return RepositoryTestImpactPlanner.Plan(changeSummary);

        var plan = RepositoryTestImpactPlanner.Plan(changeSummary, worktreePath);
        return targetHome is null || targetHome.IsOrchestratorHome
            ? plan
            : Scope(plan, worktreePath, WorkerManifestTestProjectReader.ResolvePath(worktreePath, targetHome));
    }

    internal static RepositoryTestImpactPlan Scope(
        RepositoryTestImpactPlan plan, string worktreePath, string manifestPath)
    {
        var checks = WorkerManifestTestProjectReader.ReadChecks(manifestPath);
        var displayPath = string.IsNullOrWhiteSpace(manifestPath)
            ? "(unspecified)"
            : WorkerAcceptanceManifestDisplayPath.Render(worktreePath, manifestPath);
        if (checks is null)
        {
            var availability = File.Exists(manifestPath) ? "unreadable" : "missing";
            return plan with
            {
                RequiresBuild = true,
                Checks = [],
                Summary = $"acceptance manifest is {availability} at {displayPath}; no project checks derived"
            };
        }

        var scoped = ManifestDeclaredTestProjects.From(checks.Select(check => check.Project)).Filter(plan);
        if (scoped.Checks.Count > 0)
        {
            return scoped with
            {
                Summary = $"{plan.Summary}; checks scoped to dotnet-test projects declared by {displayPath}"
            };
        }

        if (checks.Count == 0)
        {
            return scoped with
            {
                Summary = $"acceptance manifest at {displayPath} declares no dotnet-test projects; no project checks derived"
            };
        }

        return plan with
        {
            RequiresBuild = true,
            Checks = checks.Select(check => new RepositoryTestImpactCheck(
                check.Name, ["dotnet", "test", "--project", check.Project],
                $"Declared dotnet-test check from {displayPath}.")).ToArray(),
            Summary = $"no change-scoped checks for this target; using the manifest's declared test checks from {displayPath}"
        };
    }
}
