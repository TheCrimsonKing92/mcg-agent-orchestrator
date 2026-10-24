using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreTesterDeferredEvidenceReads
{
    internal static bool IsSupersededPreTesterCandidate(
        string worktreePath, string? candidateSha, string? liveHead)
    {
        if (string.IsNullOrWhiteSpace(candidateSha) || string.IsNullOrWhiteSpace(liveHead) ||
            string.Equals(candidateSha, liveHead, StringComparison.OrdinalIgnoreCase)) return false;
        var ancestry = GitCli.Run(worktreePath, "merge-base", "--is-ancestor", candidateSha, liveHead);
        // Only a completed negative ancestry query proves supersession; git faults keep today's behavior.
        return ancestry.ProcessStarted && !ancestry.DrainTimedOut && ancestry.ExitCode == 1 &&
               string.IsNullOrWhiteSpace(ancestry.Error);
    }

    internal static int LastOperatorRedLoopAnswerIndex(Goal goal)
    {
        var lastEscalation = goal.Timeline
            .Select((evt, index) => (evt, index))
            .Where(item => item.evt.TaskId is null && item.evt.Kind == ProgressKind.GoalPolicyDecision &&
                           item.evt.Message.Contains("PRE_TESTER_RED_LOOP:", StringComparison.Ordinal))
            .Select(item => item.index).DefaultIfEmpty(-1).Max();
        if (lastEscalation < 0) return -1;

        for (var index = goal.Timeline.Count - 1; index > lastEscalation; index--)
        {
            var evt = goal.Timeline[index];
            if (evt.Kind == ProgressKind.TaskRetried &&
                !evt.Message.StartsWith("ACTIONABLE_CANDIDATE_RED ", StringComparison.Ordinal) &&
                goal.Tasks.Any(task => task.Id == evt.TaskId && task.RequiredRole == AgentRole.Developer))
                return index;
        }
        return -1;
    }
}
