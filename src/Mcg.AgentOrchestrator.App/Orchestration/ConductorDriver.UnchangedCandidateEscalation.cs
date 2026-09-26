using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private const string UnchangedCandidateEscalationMarker = "UNCHANGED_CANDIDATE_ESCALATED key=";
    private readonly object _unchangedCandidateHoldLock = new();
    private readonly Dictionary<GoalId, (string Key, int ConsecutiveTicks, bool Escalated)> _unchangedCandidateHolds = new();

    // This named conductor option can be set explicitly by focused driver tests.
    internal int UnchangedCandidateHoldEscalationTicks { get; set; } = 3;

    private void ResetUnchangedCandidateHold(GoalId goalId)
    {
        lock (_unchangedCandidateHoldLock) _unchangedCandidateHolds.Remove(goalId);
    }

    private void TrackUnchangedCandidateHold(Goal goal, TaskSpec heldTask, UnchangedCandidateHoldReason reason)
    {
        var key = HoldKey(goal.Id, heldTask.Id, reason);
        lock (_unchangedCandidateHoldLock)
        {
            var count = _unchangedCandidateHolds.TryGetValue(goal.Id, out var prior) && prior.Key == key
                ? prior.ConsecutiveTicks + 1 : 1;
            var alreadyEscalated = prior.Key == key && prior.Escalated;
            if (!alreadyEscalated)
                alreadyEscalated = goal.Timeline.Any(evt => evt.TaskId == heldTask.Id &&
                    evt.Kind == ProgressKind.TaskNote &&
                    evt.Message == UnchangedCandidateEscalationMarker + key);
            _unchangedCandidateHolds[goal.Id] = (key, count, alreadyEscalated);
            if (count < Math.Max(1, UnchangedCandidateHoldEscalationTicks) || alreadyEscalated)
                return;

            var escalation = $"{reason.Render()} consecutive_holds={count} " +
                $"heldTask={heldTask.Id.Value}. If the verdict genuinely passed this candidate, " +
                "close the task mechanically; otherwise retry this role to re-run it.";
            RecordEscalation(goal, GoalLifecycleState.WorkspaceReady, escalation);
            _recordTaskNote(goal.Id, heldTask.Id, UnchangedCandidateEscalationMarker + key);
            _unchangedCandidateHolds[goal.Id] = (key, count, true);
        }
    }

    private static string HoldKey(GoalId goalId, TaskId heldTaskId, UnchangedCandidateHoldReason reason)
    {
        var identity = string.Join('\n', goalId.Value, heldTaskId.Value,
            reason.CandidateIdentity.Canonical, reason.PriorVerdictTaskId.Value,
            reason.PriorVerdictAt.ToString("O"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
