namespace Mcg.AgentOrchestrator.Core;

public sealed record OrchestratorSnapshot(
    IReadOnlyList<GoalSnapshot> Goals,
    IReadOnlyList<HumanInputRequestSnapshot> HumanInputRequests);

public sealed record GoalSnapshot(
    string Id,
    string Objective,
    GoalStatus Status,
    IReadOnlyList<TaskSnapshot> Tasks,
    IReadOnlyList<ProgressEventSnapshot> Timeline);

public sealed record TaskSnapshot(
    string Id,
    string Description,
    AgentRole RequiredRole,
    WorkTaskStatus Status,
    string? AssignedAgentId,
    TaskExecutionSnapshot? LastExecution,
    TaskVerificationSnapshot? LastVerification,
    IReadOnlyList<TaskVerificationSnapshot>? VerificationHistory,
    TaskDispatchSnapshot? LastDispatch,
    TaskProcessSnapshot? LastProcess,
    string? VerificationPlan = null,
    DateTimeOffset? SubscriptionRetryAfter = null);

public sealed record TaskExecutionSnapshot(
    string AgentId,
    string AgentName,
    string ProviderName,
    string ModelName,
    string Output,
    string StopReason,
    int? InputTokens,
    int? OutputTokens,
    DateTimeOffset CompletedAt);

public sealed record TaskVerificationSnapshot(
    string Command,
    string WorkingDirectory,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset CompletedAt);

public sealed record TaskDispatchSnapshot(
    string WorkerName,
    string Command,
    string WorkingDirectory,
    DateTimeOffset DispatchedAt);

public sealed record TaskProcessSnapshot(
    int ProcessId,
    string Command,
    string WorkingDirectory,
    string StandardOutputPath,
    string StandardErrorPath,
    string ExitCodePath,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? ExitCode,
    bool WasCancelled = false);

public sealed record ProgressEventSnapshot(
    string GoalId,
    string? TaskId,
    ProgressKind Kind,
    string Message,
    DateTimeOffset OccurredAt);

public sealed record HumanInputRequestSnapshot(
    string Id,
    string GoalId,
    string? TaskId,
    string Question,
    DateTimeOffset RequestedAt,
    bool IsCompleted,
    string? Answer,
    DateTimeOffset? AnsweredAt);
