using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintVerificationHistory(TaskSpec task)
{
    if (task.VerificationHistory.Count == 0)
    {
        Console.WriteLine("No verification history.");
        return;
    }

    for (var index = 0; index < task.VerificationHistory.Count; index++)
    {
        var verification = task.VerificationHistory[index];
        Console.WriteLine($"{index + 1}. exit={verification.ExitCode} completed={verification.CompletedAt:u}");
        Console.WriteLine($"   command: {verification.Command}");
        Console.WriteLine($"   working directory: {verification.WorkingDirectory}");

        if (!string.IsNullOrWhiteSpace(verification.StandardOutput))
        {
            Console.WriteLine($"   stdout: {OutputTextPreview.CreateVerificationLog(verification.StandardOutput, verification.StandardOutputPath).Text.TrimEnd()}");
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardError))
        {
            Console.WriteLine($"   stderr: {OutputTextPreview.CreateVerificationLog(verification.StandardError, verification.StandardErrorPath).Text.TrimEnd()}");
        }
    }
}

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

public static string BuildStageSuggestedCommand(
    Goal goal,
    TaskStageReadiness stage,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    var taskNumber = GetTaskDisplayNumber(goal, stage.TaskId);
    return stage.StageStatus switch
    {
        StageReadinessStatus.ReadyToRun => BuildSuggestedCommand(
            goal,
            new NextActionItem(NextActionKind.RunAssignedTask, stage.TaskId, null, stage.SuggestedAction),
            agents),
        StageReadinessStatus.InProgress when stage.LatestEvidence == TaskEvidenceKind.Dispatch => BuildSuggestedCommand(
            goal,
            new NextActionItem(NextActionKind.ExecuteRecordedDispatch, stage.TaskId, null, stage.SuggestedAction),
            agents),
        _ => BuildStageSuggestedCommand(taskNumber, stage)
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
        _ => "monitor"
    };
}

public static int GetTaskDisplayNumber(Goal goal, TaskId taskId)
{
    return TaskDisplayNumber.Resolve(goal, taskId);
}
}


