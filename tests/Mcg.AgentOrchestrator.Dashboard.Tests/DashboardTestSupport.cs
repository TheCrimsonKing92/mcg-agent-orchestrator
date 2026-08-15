using Mcg.AgentOrchestrator.App.Orchestration;

internal static class DashboardTestSupport
{
    public static OrchestratorWorkspace CreateRefinedWorkspace(string root) =>
        (OrchestratorWorkspace)SharedTestSupport.CreateRefinedWorkspaceOpaque(root);
}
