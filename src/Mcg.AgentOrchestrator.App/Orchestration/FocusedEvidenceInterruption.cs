using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum FocusedEvidenceInterruptionKind { NotInterrupted, Interrupted, RerunExhausted }

// A dead holder with neither terminal artifact supplies no evidence about the candidate.
internal static class FocusedEvidenceInterruption
{
    internal const int MaxReruns = 1;

    internal static FocusedEvidenceInterruptionKind Classify(
        ConductorParallelAcceptanceAttempt? attempt, Func<string, bool> fileExists)
    {
        if (attempt is not { Outcome: ConductorParallelAcceptanceAttemptOutcome.ProcessDied } ||
            fileExists(attempt.ResultPath) || fileExists(attempt.ExitCodePath))
            return FocusedEvidenceInterruptionKind.NotInterrupted;

        return attempt.TransientFailureCount <= MaxReruns
            ? FocusedEvidenceInterruptionKind.Interrupted
            : FocusedEvidenceInterruptionKind.RerunExhausted;
    }

    internal static string HoldPrefix(string attemptId) =>
        $"FOCUSED_EVIDENCE_INTERRUPTED attempt={attemptId} rerun={MaxReruns}/{MaxReruns}; ";

    internal static string ExhaustedReason(TaskId taskId, string candidateSha, string? attemptId) =>
        $"DEFERRED_NO_CHANGE_EVIDENCE_INTERRUPTED task={taskId.Value} candidate_sha={candidateSha} " +
        $"attempt={attemptId ?? "none"} reruns={MaxReruns}/{MaxReruns}; focused evidence holder died without terminal artifacts";
}
