namespace Mcg.AgentOrchestrator.Core;

// Every AgentRole is an SDLC worker that executes a tracked goal Task via dispatch. Orchestrator-
// INTERNAL model uses (the acceptance judge, future samplers/summarizers/oracles) are NOT roles —
// they are ModelFunctionBinding entries (see ModelFunctions.cs), so this enum stays exhaustive and
// catalog/health/seeder code can rely on "one agent per role" again.
public enum AgentRole
{
    Planner,
    Ideation,
    Researcher,
    Developer,
    Tester,
    Reviewer
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
    Parked,
    Verifying,
    Verified,
    AcceptanceFailed,
    Completed,
    Failed,
    Cancelled,
    Superseded
}

public static class GoalStatusSemantics
{
    public static bool ExcludesFromConductorWorkingSet(GoalStatus status) =>
        status is GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;
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

public enum RetryRoundKind
{
    Standard,
    Mechanical
}

public enum HumanWaitKind
{
    SpecClarification,
    OperatorApproval,
    RiskReview,
    ExternalCredential,
    ProviderAuth,
    RecoveryChoice,
    Other
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
    GoalPolicyDecision = 21,
    ReviewerEvidenceRequestReceived = 22,
    ReviewerEvidenceRunRecorded = 23,
    ReviewFindingContractViolationRecorded = 24,
    PreReviewEvidenceRecorded = 25,
    TaskRequeueSkipped = 26,
    DuplicateHumanInputSuppressed = 27
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
    VerificationFailed,
    AcceptanceFailed
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
    AwaitingClarification,
    WorkspaceReady,
    Dispatched,
    Running,
    AwaitingVerification,
    Verifying,
    Verified,
    AcceptanceFailed,
    Merged,
    Recorded,
    CleanedUp,
    Failed,
    Blocked,
    AwaitingHumanInput
}
