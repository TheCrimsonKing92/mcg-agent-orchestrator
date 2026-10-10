using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the hold policy and polling; the loop owns relaunch, rollback, and checkpoint state.
internal sealed record ConductorSelfRelaunchDrainHold(bool DispatchesPending, int LiveGates, TimeSpan Elapsed)
{
    internal bool ShouldWait => DispatchesPending || LiveGates > 0;
    internal bool TimedOut => Elapsed >= DispatchRecoveryPolicy.DefaultLiveIdleTimeout;

    internal string TimeoutReason =>
        $"active dispatches did not reach terminal receipts within {(int)DispatchRecoveryPolicy.DefaultLiveIdleTimeout.TotalMinutes} minutes" +
        (LiveGates > 0 ? $" liveGates={LiveGates}" : string.Empty);

    internal string DescribeProgress(int tick, string goalId, int activeDispatches, string capSuffix) =>
        $"LOOP_RELAUNCH_DRAIN tick={tick} goal={goalId} active={activeDispatches} admitting=false{capSuffix} liveGates={LiveGates}";

    internal void Wait(Func<TimeSpan, bool>? sleep, Action<TimeSpan> waitForWake)
    {
        var interval = TimeSpan.FromSeconds(ConductorBatchLoop.WatchStopPollIntervalSeconds);
        if (sleep is not null)
        {
            sleep(interval);
        }
        else
        {
            waitForWake(interval);
        }
    }
}
