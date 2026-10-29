using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Scopes gate impact checks to the loaded manifest's declared projects.</summary>
internal static class ManifestScopedImpactPlan
{
    internal static RepositoryTestImpactPlan? PlanChangeScoped(
        RepositoryChangeSummary summary,
        string worktreePath,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks)
    {
        var plan = Plan(summary, worktreePath, manifestChecks);
        return !plan.RequiresBuild ||
            plan.Checks.Count == 0 ||
            plan.RequiresBroadVerification ||
            plan.Checks.Any(check => check.Command.Count == 0)
            ? null
            : plan;
    }

    internal static RepositoryTestImpactPlan Plan(
        IEnumerable<string> changedFiles,
        string worktreePath,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks) =>
        Plan(RepositoryChangeClassifier.Classify(changedFiles), worktreePath, manifestChecks);

    internal static RepositoryTestImpactPlan Plan(
        RepositoryChangeSummary summary,
        string worktreePath,
        IReadOnlyList<AcceptanceManifestCheck> manifestChecks)
    {
        var plan = RepositoryTestImpactPlanner.Plan(summary, worktreePath);
        var declared = ManifestDeclaredTestProjects.From(manifestChecks
            .Where(check => check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
            .Select(check => check.Project));

        // Built-in declarations retain this repository's existing planning policy.
        // No-build plans carry non-executable evidence rather than project checks.
        return declared.DeclaresAnyBuiltInTestProject || !plan.RequiresBuild
            ? plan
            : declared.Filter(plan);
    }
}
