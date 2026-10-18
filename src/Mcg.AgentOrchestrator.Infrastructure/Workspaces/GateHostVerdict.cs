namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GateHostVerdict
{
    internal static string Label(int running, int stalled, int remoteWaits = 0) =>
        running == 0 && stalled == 0 && remoteWaits > 0 ? "RUNNING (remote)" :
        new GateLivenessVerdict(stalled > 0 ? GateLiveness.Stalled :
            running > 0 ? GateLiveness.Running : GateLiveness.None,
            0, null, null, null, null).Label;
}
