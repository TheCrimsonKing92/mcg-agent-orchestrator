using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class GoalTranscriptRenderer
{
private static string BuildSuggestedCommand(Goal goal, NextActionItem item)
{
    string? taskNumber = null;
    if (item.TaskId is not null)
    {
        taskNumber = GetTaskDisplayNumber(goal, item.TaskId).ToString();
    }

    return item.Kind switch
    {
        NextActionKind.AnswerHumanInput => item.HumanInputRequestId is null
            ? "pending"
            : $"answer {item.HumanInputRequestId.Value[..8]} <answer>",
        NextActionKind.InspectFailedTask => taskNumber is null ? "monitor" : $"task {taskNumber} | retry {taskNumber} <note>",
        NextActionKind.FixFailedVerification => taskNumber is null ? "monitor" : $"verifications {taskNumber} | retry {taskNumber} <note>",
        NextActionKind.RefreshRunningProcess => taskNumber is null ? "monitor" : $"refresh-dispatch {taskNumber}",
        NextActionKind.ExecuteRecordedDispatch => taskNumber is null ? "monitor" : $"execute-dispatch {taskNumber} --confirm-dispatch-start",
        NextActionKind.VerifyCompletedTask => taskNumber is null ? "monitor" : $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
        NextActionKind.RunAssignedTask => taskNumber is null ? "monitor" : $"run {taskNumber}",
        NextActionKind.DelegatePendingTask => "delegate",
        NextActionKind.MonitorGoal => "monitor",
        _ => "monitor"
    };
}

private static string BuildVerificationSuggestedCommand(int taskNumber, VerificationGateStatus gateStatus)
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

private static string BuildHumanInputSuggestedCommand(HumanInputRequestId requestId)
{
    return $"answer {requestId.Value[..8]} <answer>";
}

private static string BuildStageSuggestedCommand(int taskNumber, TaskStageReadiness stage)
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

private static string BuildAcceptanceSuggestedCommand(GoalAcceptanceBlocker blocker, int? taskNumber)
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
        _ => "monitor"
    };
}

private static int GetTaskDisplayNumber(Goal goal, TaskId taskId)
{
    for (var index = 0; index < goal.Tasks.Count; index++)
    {
        if (goal.Tasks[index].Id == taskId)
        {
            return index + 1;
        }
    }

    throw new KeyNotFoundException($"Task '{taskId}' was not found.");
}
}
