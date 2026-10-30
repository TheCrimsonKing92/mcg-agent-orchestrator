namespace Mcg.AgentOrchestrator.App.Orchestration;

// Resolves the supervisor capabilities supplied to a loop without staging or building an artifact.
internal sealed record ConductorSupervisorLoopSetup(
    bool DelegateSelfRelaunchToSupervisor, HomeBuildAdoptionDetector? HomeBuildAdoption)
{
    internal static ConductorSupervisorLoopSetup Create(bool supervisedChild,
        OrchestratorWorkspace workspace, string? blockedCommit)
    {
        var restageSetting = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_MAX_DURATION_RESTAGE");
        var delegateRelaunch = supervisedChild &&
            !string.Equals(restageSetting, "false", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(restageSetting, "0", StringComparison.OrdinalIgnoreCase);
        var home = OrchestratorHome.ResolveForProcess();
        var detector = supervisedChild && home.SourceRootDirectory is { } homeRoot && !home.IsHome(workspace)
            ? new HomeBuildAdoptionDetector(homeRoot, SystemConductorSupervisorHandoffSeam.ReadOwnBuild(),
                blockedCommit, diagnostic: Console.WriteLine) : null;
        return new(delegateRelaunch, detector);
    }
}
