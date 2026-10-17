using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// An open window starts only with a scheduled relaunch; an outcome without one is inert.
internal static class ConductorRelaunchLogFold
{
    internal static readonly string[] Heads = ["LOOP_RELAUNCH_SCHEDULED ", "LOOP_RELAUNCH_DRAIN ",
        "ACTIVATION_ADOPTED ", "ACTIVATION_REVERTED ", "LOOP_HANDOFF_FAILED ", "SUPERVISOR_BUILD "];

    internal static ConductorRelaunchObservation Apply(ConductorRelaunchObservation state,
        DateTimeOffset at, string detail)
    {
        if (detail.StartsWith("SUPERVISOR_BUILD ", StringComparison.Ordinal))
        {
            var start = detail.IndexOf('{');
            if (start < 0) return state;
            try
            {
                using var document = JsonDocument.Parse(detail[start..]);
                if (document.RootElement.TryGetProperty("commitSha", out var commit) &&
                    commit.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(commit.GetString()))
                    return state with { RunningBuildCommit = commit.GetString() };
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }
        else if (detail.StartsWith("ACTIVATION_ADOPTED ", StringComparison.Ordinal))
            return ConductorRelaunchObservation.Empty with { RunningBuildCommit = state.RunningBuildCommit };
        else if (detail.StartsWith("LOOP_RELAUNCH_SCHEDULED ", StringComparison.Ordinal))
        {
            if (state.ScheduledAt is null || state.FailureOutcomeAt is not null)
            {
                var goal = detail.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(token => token.StartsWith("goal=", StringComparison.Ordinal))?[5..];
                return new(at, goal, null, null, state.RunningBuildCommit);
            }
        }
        else if (state.ScheduledAt is not null)
        {
            if (detail.StartsWith("LOOP_RELAUNCH_DRAIN ", StringComparison.Ordinal) &&
                ConductorInvariantLogProgress.TryGetProgress(detail))
                return state with { LastLiveGateAt = at };
            if (detail.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal) ||
                detail.StartsWith("LOOP_HANDOFF_FAILED ", StringComparison.Ordinal))
                return state with { FailureOutcomeAt = state.FailureOutcomeAt ?? at };
        }
        return state;
    }
}
