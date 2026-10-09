namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class ConductorSelfRelaunch
{
    internal static Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult>? CreateForHome(
        OrchestratorHome home, OrchestratorWorkspace workspace,
        ConductLoopHandoffOptions handoffOptions, Action<string> recordSkip)
    {
        var staging = home.CreateSuccessorStagingOptions(workspace);
        if (staging is null)
        {
            RecordNotHome(home, workspace, recordSkip);
            return null;
        }

        return Create(new ConductorSelfRelaunchOptions(
            staging.RepositoryRoot, staging.AppProjectPath, staging.AppDllPath,
            staging.UpdateHeadMarkerScriptPath, staging.ResolveRunDirectoryScriptPath,
            staging.StateStorePath, staging.AgentCatalogPath, staging.WorkerProfilePath,
            staging.ModelFunctionCatalogPath, staging.DotnetPath, staging.PowerShellPath, handoffOptions)
        {
            LandingAppBuildStore = staging.LandingAppBuildStore
        });
    }

    internal static Func<CancellationToken, ConductorPreparedSuccessor>? CreateSuccessorStagerForHome(
        OrchestratorHome home, OrchestratorWorkspace workspace, Action<string> recordSkip)
    {
        var staging = home.CreateSuccessorStagingOptions(workspace);
        if (staging is null)
        {
            RecordNotHome(home, workspace, recordSkip);
            return null;
        }

        return cancellationToken => PrepareSuccessor(staging, cancellationToken);
    }

    private static void RecordNotHome(
        OrchestratorHome home, OrchestratorWorkspace workspace, Action<string> recordSkip) =>
        recordSkip($"LOOP_HANDOFF_SKIPPED reason=target-not-home target={workspace.ExecutionDirectory} home={home.SourceRootDirectory ?? "none"}");
}
