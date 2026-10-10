using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class VerificationCommandAdvice
{
public static string BuildVerificationSuggestedCommand(int taskNumber, VerificationGateStatus gateStatus)
{
    return gateStatus switch
    {
        VerificationGateStatus.NotReady => $"task {taskNumber}",
        VerificationGateStatus.MissingVerification => $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
        VerificationGateStatus.FailedVerification => $"verifications {taskNumber} | retry {taskNumber} <note>",
        VerificationGateStatus.Passed => "gates",
        _ => "monitor"
    };
}

public static string BuildHumanInputSuggestedCommand(HumanInputRequestId requestId)
{
    return $"answer {requestId.Value[..8]} <answer>";
}

public static string BuildStageSuggestedCommand(int taskNumber, TaskStageReadiness stage)
{
    return stage.StageStatus switch
    {
        StageReadinessStatus.NeedsDelegation => "delegate",
        StageReadinessStatus.ReadyToRun => $"run {taskNumber} | profile-dispatch {taskNumber} <profile-name>",
        StageReadinessStatus.InProgress when stage.LatestEvidence == TaskEvidenceKind.RunningProcess => $"refresh-dispatch {taskNumber}",
        StageReadinessStatus.InProgress when stage.LatestEvidence == TaskEvidenceKind.Dispatch => $"execute-dispatch {taskNumber} --confirm-dispatch-start",
        StageReadinessStatus.InProgress => $"task {taskNumber}",
        StageReadinessStatus.WaitingForHuman => "input-needed",
        StageReadinessStatus.NeedsVerification => $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
        StageReadinessStatus.VerificationFailed => $"verifications {taskNumber} | retry {taskNumber} <note>",
        StageReadinessStatus.Verified => "stages",
        StageReadinessStatus.FailedOrCancelled => $"retry {taskNumber} <note>",
        _ => "monitor"
    };
}

public static string BuildAcceptanceSuggestedCommand(GoalAcceptanceBlocker blocker, int? taskNumber)
{
    return blocker.Kind switch
    {
        GoalAcceptanceBlockerKind.PendingHumanInput when blocker.HumanInputRequestId is not null =>
            BuildHumanInputSuggestedCommand(blocker.HumanInputRequestId),
        GoalAcceptanceBlockerKind.VerificationNotReady when taskNumber is not null =>
            $"task {taskNumber}",
        GoalAcceptanceBlockerKind.VerificationMissing when taskNumber is not null =>
            $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
        GoalAcceptanceBlockerKind.VerificationFailed when taskNumber is not null =>
            $"verifications {taskNumber} | retry {taskNumber} <note>",
        GoalAcceptanceBlockerKind.AcceptanceFailed =>
            blocker.SuggestedAction,
        GoalAcceptanceBlockerKind.AcceptanceAborted =>
            blocker.SuggestedAction,
        _ => "monitor"
    };
}
}
