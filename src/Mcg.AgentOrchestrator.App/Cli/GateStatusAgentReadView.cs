using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GateStatusAgentReadView
{
    internal static string Render(IReadOnlyList<GateHeartbeatStatus> statuses,
        DateTimeOffset observedAt, TimeProvider? time = null, int remoteWaits = 0)
    {
        var verdicts = statuses.Select(status => GateLivenessVerdict.Classify(status, observedAt)).ToArray();
        var running = verdicts.Count(verdict => verdict.Liveness == GateLiveness.Running);
        var stalled = verdicts.Count(verdict => verdict.Liveness == GateLiveness.Stalled);
        var slots = statuses.Select((status, index) =>
        {
            var snapshot = status.IsAvailable ? status.Snapshot : null;
            return new
            {
                slot = status.SlotIndex,
                goal = string.IsNullOrWhiteSpace(snapshot?.GoalId) ? null : snapshot.GoalId,
                phase = snapshot?.Phase,
                state = snapshot?.State,
                target = snapshot?.CurrentTarget,
                elapsedSeconds = snapshot is null ? null : Seconds(status.HeartbeatAge is null
                    ? TimeSpan.Zero : snapshot.LastObservedAt - snapshot.StartedAt),
                childPid = snapshot?.ChildPid,
                heartbeatAgeSeconds = snapshot is null ? null : Seconds(status.HeartbeatAge),
                idleSeconds = snapshot is null ? null : Seconds(status.IdleDuration),
                outputBytes = snapshot?.OutputBytes,
                verdict = verdicts[index].Label
            };
        }).ToArray();
        var hostVerdict = GateHostVerdict.Label(running, stalled, remoteWaits);
        object host = remoteWaits > 0
            ? new { verdict = hostVerdict, running, stalled, slots = statuses.Count, remoteWaits }
            : new { verdict = hostVerdict, running, stalled, slots = statuses.Count };
        return AgentReadEnvelope.Serialize(AgentReadEnvelope.GateStatusSchema, AgentReadEnvelope.GateStatusSchemaVersion,
            new
            {
                slots,
                host
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web), time);
    }

    private static double? Seconds(TimeSpan? duration) =>
        duration is null ? null : Math.Round(Math.Max(0, duration.Value.TotalSeconds), 3);
}
