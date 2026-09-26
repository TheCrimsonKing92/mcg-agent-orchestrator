using Mcg.AgentOrchestrator.App.Cli;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorContinuitySupervisor
{
    private TimeSpan ResolveActivationTickGapTimeout(IReadOnlyList<string> args)
    {
        int pollSeconds;
        try
        {
            pollSeconds = CliCommandHandlers.ResolveConductPollSeconds(args);
        }
        catch (ArgumentException)
        {
            pollSeconds = ConductorBatchLoop.DefaultWatchIntervalSeconds;
        }

        var maximumWatchSleep = TimeSpan.FromSeconds(
            Math.Max(pollSeconds, ConductorBatchLoop.WatchStopPollIntervalSeconds));
        return maximumWatchSleep + _activationStallTimeout;
    }
}
