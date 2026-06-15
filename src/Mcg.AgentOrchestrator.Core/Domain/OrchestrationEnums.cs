namespace Mcg.AgentOrchestrator.Core;

public enum AgentRole
{
    Planner,
    Ideation,
    Researcher,
    Developer,
    Tester,
    Reviewer,
    // Not a task-executing role: a Judge agent is never assigned work tasks (no task carries
    // RequiredRole=Judge), so task routing ignores it. It exists only to configure semantic-
    // acceptance judge models — one Judge agent per lane (free-local / cheap-API / capable).
    Judge
}

public static class AgentRoles
{
    // The SDLC work roles: every one carries a default catalog agent and can be assigned tasks.
    // Excludes Judge, which is an opt-in semantic-acceptance judge lane — never assigned tasks and
    // not present in the default catalog — so catalog/health/seeder code that requires "an agent per
    // role" must iterate THIS, not Enum.GetValues<AgentRole>() (which would demand a Judge agent).
    public static IReadOnlyList<AgentRole> Worker { get; } =
        [.. Enum.GetValues<AgentRole>().Where(role => role != AgentRole.Judge)];
}

public enum AgentStatus
{
    Available,
    Busy,
    Offline
}

public enum GoalStatus
{
    Draft,
    Active,
    WaitingForHuman,
    Completed,
    Failed,
    Cancelled,
    Superseded
}

public enum WorkTaskStatus
{
    Pending,
    Assigned,
    Running,
    WaitingForHuman,
    Completed,
    Failed,
    Cancelled
}

public enum ProgressKind
{
    GoalCreated = 0,
    TaskDelegated = 1,
    TaskStarted = 2,
    TaskUpdated = 3,
    HumanInputRequested = 4,
    HumanInputReceived = 5,
    TaskCompleted = 6,
    TaskFailed = 7,
    TaskOutputRecorded = 8,
    TaskVerificationRecorded = 9,
    TaskDispatchRecorded = 10,
    TaskProcessStarted = 11,
    TaskCancelled = 12,
    TaskAdded = 13,
    TaskRetried = 14,
    TaskVerificationPlanUpdated = 15,
    TaskNote = 16,
    TaskSubscriptionLimitReviewAcknowledged = 17,
    TaskRedelegated = 18,
    GoalCancelled = 19,
    GoalSuperseded = 20,
    GoalPolicyDecision = 21
}

public enum TaskAttentionKind
{
    PendingHumanInput,
    FailedTask,
    FailedVerification,
    RunningDispatch,
    MissingVerification
}

public enum NextActionKind
{
    AnswerHumanInput,
    InspectFailedTask,
    FixFailedVerification,
    RefreshRunningProcess,
    ExecuteRecordedDispatch,
    VerifyCompletedTask,
    RunAssignedTask,
    DelegatePendingTask,
    MonitorGoal
}

public enum NextActionAutomationKind
{
    None,
    RunAssignedTask,
    RefreshRunningProcess,
    StartRecordedDispatch,
    DelegatePendingTask
}

public enum VerificationGateStatus
{
    NotReady,
    MissingVerification,
    FailedVerification,
    Passed
}

public enum ProcessBatchActionKind
{
    StartDispatches,
    RefreshDispatches,
    CancelDispatches
}

public enum ProcessBatchItemStatus
{
    Ready,
    Skipped
}

public enum GoalAcceptanceBlockerKind
{
    PendingHumanInput,
    VerificationNotReady,
    VerificationMissing,
    VerificationFailed
}

public enum TaskEvidenceKind
{
    None,
    Execution,
    Dispatch,
    Process,
    RunningProcess,
    CompletedProcess,
    Verification,
    PassedVerification,
    FailedVerification
}

public enum StageReadinessStatus
{
    NeedsDelegation,
    ReadyToRun,
    InProgress,
    WaitingForHuman,
    NeedsVerification,
    VerificationFailed,
    Verified,
    FailedOrCancelled
}

[Flags]
public enum ModelCapability
{
    None = 0,
    Text = 1,
    Code = 2,
    ToolUse = 4,
    Vision = 8
}

public enum SubscriptionMode
{
    ApiKey,
    DesktopSubscription,
    LocalBridge
}

public enum AgentExecutionPolicy
{
    ApiOnly,
    SubscriptionOnly,
    PreferSubscription,
    AnyAvailable
}

public enum TaskComplexity
{
    Auto,
    Simple,
    Complex
}

public enum GoalLifecycleState
{
    Created,
    WorkspaceReady,
    Dispatched,
    Running,
    AwaitingVerification,
    Verified,
    Merged,
    Recorded,
    CleanedUp,
    Failed,
    Blocked,
    AwaitingHumanInput
}
