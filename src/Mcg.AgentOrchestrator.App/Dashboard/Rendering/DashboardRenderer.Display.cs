using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class DashboardRenderer
{
    private static string Display(GoalStatus status) => status switch
    {
        GoalStatus.Draft => "Draft",
        GoalStatus.Active => "Active",
        GoalStatus.WaitingForHuman => "Waiting for human",
        GoalStatus.Completed => "Completed",
        GoalStatus.Failed => "Failed",
        GoalStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    private static string Display(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Pending => "Pending",
        WorkTaskStatus.Assigned => "Assigned",
        WorkTaskStatus.Running => "Running",
        WorkTaskStatus.WaitingForHuman => "Waiting for human",
        WorkTaskStatus.Completed => "Completed",
        WorkTaskStatus.Failed => "Failed",
        WorkTaskStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    private static string Display(ProgressKind kind) => kind switch
    {
        ProgressKind.GoalCreated => "Goal created",
        ProgressKind.TaskDelegated => "Task delegated",
        ProgressKind.TaskStarted => "Task started",
        ProgressKind.TaskUpdated => "Task updated",
        ProgressKind.HumanInputRequested => "Human input requested",
        ProgressKind.HumanInputReceived => "Human input received",
        ProgressKind.TaskCompleted => "Task completed",
        ProgressKind.TaskFailed => "Task failed",
        ProgressKind.TaskOutputRecorded => "Task output recorded",
        ProgressKind.TaskVerificationRecorded => "Task verification recorded",
        ProgressKind.TaskDispatchRecorded => "Task dispatch recorded",
        ProgressKind.TaskProcessStarted => "Task process started",
        ProgressKind.TaskCancelled => "Task cancelled",
        ProgressKind.TaskAdded => "Task added",
        ProgressKind.TaskRetried => "Task retried",
        ProgressKind.TaskVerificationPlanUpdated => "Verification plan updated",
        ProgressKind.TaskNote => "Task note",
        _ => kind.ToString()
    };

    private static string Display(TaskAttentionKind kind) => kind switch
    {
        TaskAttentionKind.PendingHumanInput => "Pending human input",
        TaskAttentionKind.FailedTask => "Failed task",
        TaskAttentionKind.FailedVerification => "Failed verification",
        TaskAttentionKind.RunningDispatch => "Running dispatch",
        TaskAttentionKind.MissingVerification => "Missing verification",
        _ => kind.ToString()
    };

    private static string Display(NextActionKind kind) => kind switch
    {
        NextActionKind.AnswerHumanInput => "Answer human input",
        NextActionKind.InspectFailedTask => "Inspect failed task",
        NextActionKind.FixFailedVerification => "Fix failed verification",
        NextActionKind.RefreshRunningProcess => "Refresh running process",
        NextActionKind.ExecuteRecordedDispatch => "Start recorded dispatch",
        NextActionKind.VerifyCompletedTask => "Verify completed task",
        NextActionKind.RunAssignedTask => "Run assigned task",
        NextActionKind.DelegatePendingTask => "Delegate pending task",
        NextActionKind.MonitorGoal => "Monitor goal",
        _ => kind.ToString()
    };

    private static string Display(VerificationGateStatus status) => status switch
    {
        VerificationGateStatus.NotReady => "Not ready",
        VerificationGateStatus.MissingVerification => "Missing verification",
        VerificationGateStatus.FailedVerification => "Failed verification",
        VerificationGateStatus.Passed => "Passed",
        _ => status.ToString()
    };

    private static string Display(GoalAcceptanceBlockerKind kind) => kind switch
    {
        GoalAcceptanceBlockerKind.PendingHumanInput => "Pending human input",
        GoalAcceptanceBlockerKind.VerificationNotReady => "Verification not ready",
        GoalAcceptanceBlockerKind.VerificationMissing => "Verification missing",
        GoalAcceptanceBlockerKind.VerificationFailed => "Verification failed",
        _ => kind.ToString()
    };

    private static string Display(TaskEvidenceKind kind) => kind switch
    {
        TaskEvidenceKind.None => "No evidence",
        TaskEvidenceKind.Execution => "Model execution",
        TaskEvidenceKind.Dispatch => "Dispatch recorded",
        TaskEvidenceKind.Process => "Process recorded",
        TaskEvidenceKind.RunningProcess => "Running process",
        TaskEvidenceKind.CompletedProcess => "Completed process",
        TaskEvidenceKind.Verification => "Verification recorded",
        TaskEvidenceKind.PassedVerification => "Passed verification",
        TaskEvidenceKind.FailedVerification => "Failed verification",
        _ => kind.ToString()
    };

    private static string Display(StageReadinessStatus status) => status switch
    {
        StageReadinessStatus.NeedsDelegation => "Needs delegation",
        StageReadinessStatus.ReadyToRun => "Ready to run",
        StageReadinessStatus.InProgress => "In progress",
        StageReadinessStatus.WaitingForHuman => "Waiting for human",
        StageReadinessStatus.NeedsVerification => "Needs verification",
        StageReadinessStatus.VerificationFailed => "Verification failed",
        StageReadinessStatus.Verified => "Verified",
        StageReadinessStatus.FailedOrCancelled => "Failed or cancelled",
        _ => status.ToString()
    };

    private static string Display(AgentExecutionPolicy policy) => policy switch
    {
        AgentExecutionPolicy.ApiOnly => "API only",
        AgentExecutionPolicy.SubscriptionOnly => "Subscription only",
        AgentExecutionPolicy.PreferSubscription => "Prefer subscription",
        AgentExecutionPolicy.AnyAvailable => "Use anything available",
        _ => policy.ToString()
    };
}
