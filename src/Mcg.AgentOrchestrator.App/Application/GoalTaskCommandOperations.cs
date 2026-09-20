using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

/// <summary>
/// Owns single-task mutations an operator can request: recording a dispatch, starting, refreshing or
/// cancelling its process, recording verification, and enqueueing an operator intent. Request
/// parsing, status-code selection and response shaping stay with the calling transport adapter.
/// </summary>
public sealed class GoalTaskCommandOperations
{
    private readonly GoalDispatchOperations _dispatch;

    public GoalTaskCommandOperations()
        : this(new GoalDispatchOperations())
    {
    }

    internal GoalTaskCommandOperations(GoalDispatchOperations dispatch)
    {
        _dispatch = dispatch;
    }

    public void RecordDispatch(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry? providers,
        Goal goal,
        TaskSpec task,
        string workerName,
        string command)
    {
        if (task.RequiredRole != AgentRole.Researcher || goal.RefinedSpec is not null)
            GoalDispatchOperations.EnsureRefinedForSpecConsumer(kernel, workspace, providers, goal);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(workerName, command, workspace.ResolveExecutionDirectory(goal.Id), DateTimeOffset.UtcNow));
    }

    public WorkerProfileDispatchResult ProfileDispatch(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskSpec task,
        WorkerProfile profile,
        IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry? providers) =>
        _dispatch.ProfileDispatchTask(kernel, workspace, goal, task, profile, agents, providers);

    public WorkerProfileDispatchResult SubscriptionDispatch(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IModelProviderRegistry? providers,
        string? limitReviewNote)
    {
        ApplySubscriptionLimitReviewAcknowledgement(kernel, goal, task, limitReviewNote);
        return _dispatch.SubscriptionDispatchTask(kernel, workspace, goal, task, agents, profiles, providers: providers);
    }

    /// <summary>
    /// Starts the latest prepared dispatch. Returns null on success; a typed failure when the worker
    /// process could not be registered. Unrecoverable states still throw, as they did before.
    /// </summary>
    public DispatchProcessStartFailure? PrepareAndStartDispatch(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IModelProviderRegistry? providers)
    {
        _dispatch.RefreshPreparedDispatchBeforeStart(kernel, workspace, goal, task, agents, profiles, providers);
        var startResult = new BackgroundDispatchRunner().TryStartLatestDispatch(kernel, goal.Id, task.Id, workspace.LogDirectory);
        if (startResult.RecoveryAction is { } recoveryAction)
        {
            throw new InvalidOperationException(recoveryAction.Reason);
        }

        if (startResult.RequeueSkipped)
        {
            throw new InvalidOperationException("Dispatch start was skipped after interrupted-dispatch state changed.");
        }

        return startResult.FailureReason is { } failureReason
            ? new DispatchProcessStartFailure(task.Id, failureReason)
            : null;
    }

    public void RefreshProcess(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task) =>
        new BackgroundDispatchRunner().RefreshLatestProcess(kernel, goal.Id, task.Id);

    public void CancelProcess(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task) =>
        new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, task.Id);

    public async Task VerifyAsync(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskSpec task,
        string command)
    {
        var verification = await new LocalProcessVerifier().RunAsync(command, workspace.ResolveExecutionDirectory(goal.Id), goal.Id, task.Id);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
    }

    public void CompleteVerify(
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskSpec task,
        string note)
    {
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, note);
        var completeVerification = ManualVerificationRecorder.Create(
            true,
            note,
            workspace.ExecutionDirectory,
            DateTimeOffset.UtcNow);
        kernel.RecordTaskVerification(goal.Id, task.Id, completeVerification);
    }

    public void SetVerificationPlan(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string plan) =>
        kernel.SetTaskVerificationPlan(goal.Id, task.Id, plan);

    public HumanInputRequest RequestHumanInput(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string question) =>
        kernel.RequestHumanInput(goal.Id, task.Id, question);

    /// <summary>
    /// Persists an operator intent. <paramref name="channel"/> and
    /// <paramref name="authenticationAssurance"/> are required because the persisted row records who
    /// the intent came from; no surface may inherit another surface's provenance by default.
    /// </summary>
    public async Task<(OperatorIntentRecord Persisted, string? InactiveWarning)> EnqueueOperatorIntentAsync(
        OrchestratorWorkspace workspace,
        Goal goal,
        TaskSpec task,
        string verb,
        object payload,
        string? idempotencyKey,
        string channel,
        string authenticationAssurance)
    {
        var intentId = Guid.NewGuid().ToString("N");
        var intent = new OperatorIntentRecord(
            intentId,
            string.IsNullOrWhiteSpace(idempotencyKey) ? intentId : idempotencyKey,
            verb,
            goal.Id.Value,
            task.Id.Value,
            System.Text.Json.JsonSerializer.Serialize(payload, payload.GetType(), OperatorIntentJson.Options),
            [],
            Actor: "operator",
            Channel: channel,
            AuthenticationAssurance: authenticationAssurance,
            CreatedAt: DateTimeOffset.UtcNow);
        var persisted = await SqliteOperatorIntentStore
            .ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .EnqueueAsync(intent);
        var warning = ConductorLoopLease.IsActive(workspace.OrchestratorDirectory)
            ? null
            : ConductorLoopLease.InactiveWarning;
        return (persisted, warning);
    }

    public void ApplySubscriptionLimitReviewAcknowledgement(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string? acknowledgementNote)
    {
        if (acknowledgementNote is not null)
        {
            if (DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task) < DispatchFailureClassifier.RecoverableSubscriptionLimitReviewThreshold)
            {
                throw new ArgumentException("Subscription limit review acknowledgement requires repeated recoverable subscription usage-limit failures.");
            }

            kernel.AcknowledgeSubscriptionLimitReview(goal.Id, task.Id, acknowledgementNote);
            return;
        }

        if (DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, DateTimeOffset.UtcNow, out var retryAfter))
        {
            throw new ArgumentException($"Task '{task.Id}' hit a recoverable subscription usage limit; retry after {retryAfter:u}.");
        }

        if (!DispatchFailureClassifier.RequiresSubscriptionLimitReview(task))
        {
            return;
        }

        var failures = DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task);
        throw new ArgumentException(
            $"Task '{task.Id}' hit a recoverable subscription usage limit {failures} time(s); POST subscription-dispatch with confirmLimitReview=true and a non-empty note before redispatch.");
    }
}
