using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceProjectResolver
{
    internal static bool TryResolve(string alias, AcceptanceGateEngineSettings? settings, out string project)
    {
        if (CrosswalkFocusedEvidence.Applies(settings))
            return CrosswalkFocusedEvidence.TryResolveProject(alias, settings, out project);

        var normalized = alias.Replace('\\', '/').Trim();
        project = normalized switch
        {
            "Core.Tests" or "Core" or "Mcg.AgentOrchestrator.Core.Tests" => CoreTestsProject,
            "Infrastructure.Tests" or "Infrastructure" or "Mcg.AgentOrchestrator.Infrastructure.Tests" => InfrastructureTestsProject,
            _ when normalized.EndsWith(CoreTestsProject, StringComparison.OrdinalIgnoreCase) => CoreTestsProject,
            _ when normalized.EndsWith(InfrastructureTestsProject, StringComparison.OrdinalIgnoreCase) => InfrastructureTestsProject,
            _ => string.Empty
        };
        if (project.Length > 0)
            return true;

        return DeclaredTestProjectInventory.TryResolve(
            normalized, settings, out project, settings?.DeclaredDotnetTestChecks) ||
            GoPackageFocusedEvidence.TryResolveProject(normalized, settings, out project);
    }
}
