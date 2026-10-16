namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class HostLedgerStateDirectoryLocator
{
    internal static string Resolve(string hostRoot, OrchestratorProjectRegistry registry) =>
        Resolve(hostRoot, registry, static value => value.ListProjects());

    internal static string Resolve(
        string hostRoot,
        OrchestratorProjectRegistry registry,
        Func<OrchestratorProjectRegistry, IReadOnlyList<OrchestratorProject>> listProjects) =>
        Path.GetDirectoryName(GoalOperationRunEventStoreLocator.Resolve(hostRoot, registry, listProjects))!;
}
