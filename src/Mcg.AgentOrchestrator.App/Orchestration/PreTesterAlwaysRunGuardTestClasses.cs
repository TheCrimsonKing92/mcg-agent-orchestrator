using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreTesterAlwaysRunGuardTestClasses
{
    internal sealed record Entry(string TestClass, string Reason);

    internal static IReadOnlyList<Entry> Entries { get; } = Array.AsReadOnly<Entry>(
    [
        new("GoalAcceptanceVerifierSplitFactParityTests", "Keep split verifier facts aligned with their baseline."),
        new("TestProcessStopIdentityGuardTests", "Require recorded identity before stopping a test process."),
        new("CallerFilePathRootSourceGuardTests", "Use the verified repository root for source discovery."),
        new("OrchestratorTempRootSourceGuardTests", "Keep temporary test state under the orchestrator temp root."),
        new("DirectGitLaunchSourceGuardTests", "Route git launches through the repository process runner."),
        new("ParallelSharedStateSourceGuardTests", "Keep parallel tests independent of shared mutable state.")
    ]);

    internal static IReadOnlyList<FindingEvidenceSelection> Select(string worktreePath) =>
        DeveloperDeferredTestSelections.ResolveNames(
            worktreePath, Entries.Select(entry => entry.TestClass).ToArray(),
            requireUniqueSourceFile: true).Selections;

    internal static bool ContainsClass(string? testClass) =>
        Entries.Any(entry => string.Equals(entry.TestClass, testClass, StringComparison.Ordinal));

    internal static bool IsListed(string testIdentity) =>
        ContainsClass(AcceptanceTestSourceResolver.ExtractClassName(testIdentity));
}
