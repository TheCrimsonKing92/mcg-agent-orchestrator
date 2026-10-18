using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintGateStatus(IReadOnlyList<GateHeartbeatStatus> statuses, DateTimeOffset? observedAt = null,
        int remoteWaits = 0) => GateStatusTextView.Print(statuses, observedAt, remoteWaits);
}
