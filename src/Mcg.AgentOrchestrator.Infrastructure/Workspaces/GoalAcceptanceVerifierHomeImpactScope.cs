using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GoalAcceptanceVerifierHomeImpactScope
{
    // A project-scoped gate takes its checks only from that project's manifest.
    internal static bool Applies(string? projectHomeDirectory) =>
        string.IsNullOrWhiteSpace(projectHomeDirectory);

    internal static IReadOnlyList<AcceptanceManifestCheck> GetRequiredChecks(
        string? projectHomeDirectory,
        Func<IReadOnlyList<AcceptanceManifestCheck>> buildHomeChecks) =>
        Applies(projectHomeDirectory) ? buildHomeChecks() : [];
}
