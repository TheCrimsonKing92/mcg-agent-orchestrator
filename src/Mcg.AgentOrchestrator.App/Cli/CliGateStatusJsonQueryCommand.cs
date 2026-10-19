using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGateStatusJsonQueryCommand
{
    internal static bool IsGateStatusJsonCommand(IReadOnlyList<string> args) =>
        args.Count == 2 && args[0].Equals("gate-status", StringComparison.OrdinalIgnoreCase) &&
        args[1] == "--json" && !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(TimeProvider? time = null, DotnetBuildStorageRoot? storageRoot = null,
        OrchestratorWorkspace? workspace = null)
    {
        var observedAt = (time ?? TimeProvider.System).GetUtcNow();
        var statuses = storageRoot is null ? GateHeartbeatArtifacts.ReadStableSlots(observedAt) :
            GateHeartbeatArtifacts.ReadStableSlots(storageRoot, observedAt);
        Console.WriteLine(GateStatusAgentReadView.Render(statuses, observedAt, time,
            GateStatusRemoteWait.Count(workspace, observedAt)));
    }
}
