using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleProjectLabel
{
    internal static string ForWorkspace(OrchestratorWorkspace workspace) =>
        workspace.IsProjectScoped ? workspace.ProjectName : "home";

    internal static string ForScope(OwnerConsoleBoardScope scope) => scope.Label +
        (scope.DisclosureWorkspace is { } workspace ? $" | decisions and activity: {workspace}" : "");
}
