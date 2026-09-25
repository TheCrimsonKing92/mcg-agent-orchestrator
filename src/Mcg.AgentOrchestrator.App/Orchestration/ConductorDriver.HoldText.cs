namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal const string NoReadyBatchHoldPrefix = "Assigned tasks exist but no ready batch formed";

    internal static int FindFirstBlockerReasonEnd(string holdText)
    {
        var blockers = holdText.IndexOf("Blockers: ", StringComparison.Ordinal);
        if (blockers < 0)
        {
            return -1;
        }

        var firstReason = holdText.IndexOf("reason=", blockers + "Blockers: ".Length, StringComparison.Ordinal);
        var reasonStart = firstReason >= 0 ? firstReason : blockers + "Blockers: ".Length;
        var nextTask = holdText.IndexOf("; task ", reasonStart, StringComparison.Ordinal);
        return nextTask >= 0 ? nextTask : holdText.Length;
    }
}
