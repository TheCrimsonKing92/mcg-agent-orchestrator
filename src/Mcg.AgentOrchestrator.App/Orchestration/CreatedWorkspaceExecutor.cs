namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record CreatedWorkspaceExecution(string? CreatedPath, string? LeaseUnavailableMessage);

internal static class CreatedWorkspaceExecutor
{
    internal static CreatedWorkspaceExecution Execute(Func<string> createWorkspace)
    {
        try
        {
            return new(createWorkspace(), null);
        }
        catch (ConductorDriver.EvidenceMutationLeaseUnavailableException ex)
        {
            return new(null, ex.Message);
        }
    }
}
