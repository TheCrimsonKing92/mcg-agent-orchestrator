using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static partial class GoalManagementCommandService
{
internal static bool IsInboxBackedTaskAction(string operation) =>
    operation.Equals(OperatorIntentVerbs.Retry, StringComparison.OrdinalIgnoreCase) ||
    operation.Equals(OperatorIntentVerbs.Progress, StringComparison.OrdinalIgnoreCase) ||
    operation.Equals(OperatorIntentVerbs.VerifyManual, StringComparison.OrdinalIgnoreCase);

public static async Task<object?> ApplyTaskActionAsync(
    AgentOrchestratorKernel kernel,
    IReadOnlyList<AgentDefinition> agents,
    IModelProviderRegistry providers,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskSpec task,
    string operation,
    string body)
{
    switch (operation.ToLowerInvariant())
    {
        case "run":
            return await AdvanceRunAssignedTaskAsync(kernel, agents, providers, workspace, goal, task.Id);

        case "api-run":
            return await AdvanceApiRunAssignedTaskAsync(kernel, agents, providers, workspace, goal, task.Id);

        case "dispatch":
            if (task.RequiredRole != AgentRole.Researcher || goal.RefinedSpec is not null)
                EnsureRefinedForSpecConsumer(kernel, workspace, providers, goal);
            var dispatch = DashboardRequestParser.ParseDispatchSubmission(body);
            kernel.RecordTaskDispatch(
                goal.Id,
                task.Id,
                new TaskDispatchRecord(dispatch.WorkerName, dispatch.Command, workspace.ResolveExecutionDirectory(goal.Id), DateTimeOffset.UtcNow));
            return null;

        case "profile-dispatch":
            var submission = DashboardRequestParser.ParseProfileDispatchReadySubmission(body);
            var profile = WorkerProfileStore.Load(workspace.WorkerProfilePath).GetRequired(submission.ProfileName);
            var profileDispatch = ProfileDispatchTask(kernel, workspace, goal, task, profile, agents, providers);
            return DashboardResponseMapper.ToProfileDispatchDto(goal, profileDispatch);

        case "subscription-dispatch":
            var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
            ApplySubscriptionLimitReviewAcknowledgement(kernel, goal, task, body);
            var subscriptionDispatch = SubscriptionDispatchTask(kernel, workspace, goal, task, agents, profiles, providers: providers);
            return DashboardResponseMapper.ToProfileDispatchDto(goal, subscriptionDispatch);

        case "start":
            RefreshPreparedDispatchBeforeStart(kernel, workspace, goal, task, agents, WorkerProfileStore.Load(workspace.WorkerProfilePath), providers);
            var startResult = new BackgroundDispatchRunner().TryStartLatestDispatch(kernel, goal.Id, task.Id, workspace.LogDirectory);
            if (startResult.RecoveryAction is { } recoveryAction)
            {
                throw new InvalidOperationException(recoveryAction.Reason);
            }

            if (startResult.RequeueSkipped)
            {
                throw new InvalidOperationException("Dispatch start was skipped after interrupted-dispatch state changed.");
            }

            if (startResult.FailureReason is { } failureReason)
            {
                return new DispatchProcessStartFailureDto(goal.Id.Value, task.Id.Value, failureReason);
            }

            return null;

        case "refresh":
            new BackgroundDispatchRunner().RefreshLatestProcess(kernel, goal.Id, task.Id);
            return null;

        case "cancel":
            new BackgroundDispatchRunner().CancelLatestProcess(kernel, goal.Id, task.Id);
            return null;

        case "verify":
            var verify = DashboardRequestParser.ParseVerifySubmission(body);
            var verification = await new LocalProcessVerifier().RunAsync(verify.Command, workspace.ResolveExecutionDirectory(goal.Id), goal.Id, task.Id);
            kernel.RecordTaskVerification(goal.Id, task.Id, verification);
            return null;

        case "verify-manual":
            var manual = DashboardRequestParser.ParseManualVerifySubmission(body);
            var manualVerification = ManualVerificationRecorder.Create(
                manual.Passed,
                manual.Note,
                workspace.ExecutionDirectory,
                DateTimeOffset.UtcNow);
            return await EnqueueOperatorIntentAsync(
                workspace,
                goal,
                task,
                OperatorIntentVerbs.VerifyManual,
                new ManualVerificationOperatorIntentPayload(manualVerification),
                manual.IdempotencyKey);

        case "complete-verify":
            var complete = DashboardRequestParser.ParseManualVerifySubmission(body);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, complete.Note);
            var completeVerification = ManualVerificationRecorder.Create(
                true,
                complete.Note,
                workspace.ExecutionDirectory,
                DateTimeOffset.UtcNow);
            kernel.RecordTaskVerification(goal.Id, task.Id, completeVerification);
            return null;

        case "progress":
            var progress = DashboardRequestParser.ParseProgressSubmission(body);
            var progressStatus = CliArgumentParser.ParseReportableStatus(progress.Status);
            return await EnqueueOperatorIntentAsync(
                workspace,
                goal,
                task,
                OperatorIntentVerbs.Progress,
                new ProgressOperatorIntentPayload(progressStatus, progress.Message),
                idempotencyKey: null);

        case "retry":
            var retry = DashboardRequestParser.ParseRetrySubmission(body);
            return await EnqueueOperatorIntentAsync(
                workspace,
                goal,
                task,
                OperatorIntentVerbs.Retry,
                new RetryOperatorIntentPayload(
                    retry.Message,
                    retry.Mechanical ? RetryRoundKind.Mechanical : null),
                retry.IdempotencyKey);

        case "verification-plan":
            var verificationPlan = DashboardRequestParser.ParseVerificationPlanSubmission(body);
            kernel.SetTaskVerificationPlan(goal.Id, task.Id, verificationPlan.Plan);
            return DashboardResponseMapper.ToTaskVerificationPlanDto(goal, task);

        case "ask":
            var ask = DashboardRequestParser.ParseAskSubmission(body);
            var request = kernel.RequestHumanInput(goal.Id, task.Id, ask.Question);
            return DashboardResponseMapper.ToHumanInputDto(kernel, request);

        default:
            throw new ArgumentException("Task operation must be run, api-run, retry, verification-plan, dispatch, profile-dispatch, subscription-dispatch, start, refresh, cancel, verify, verify-manual, complete-verify, progress, or ask.");
    }
}

private static async Task<OperatorIntentDto> EnqueueOperatorIntentAsync(
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskSpec task,
    string verb,
    object payload,
    string? idempotencyKey)
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
        Channel: "dashboard",
        AuthenticationAssurance: "dashboard-operator-control",
        CreatedAt: DateTimeOffset.UtcNow);
    var persisted = await SqliteOperatorIntentStore
        .ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
        .EnqueueAsync(intent);
    var warning = ConductorLoopLease.IsActive(workspace.OrchestratorDirectory)
        ? null
        : ConductorLoopLease.InactiveWarning;
    return DashboardResponseMapper.ToOperatorIntentDto(persisted) with { Warning = warning };
}

private static void ApplySubscriptionLimitReviewAcknowledgement(
    AgentOrchestratorKernel kernel,
    Goal goal,
    TaskSpec task,
    string body)
{
    var review = DashboardRequestParser.ParseLimitReviewSubmission(body);
    if (review is not null)
    {
        if (DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task) < DispatchFailureClassifier.RecoverableSubscriptionLimitReviewThreshold)
        {
            throw new ArgumentException("Subscription limit review acknowledgement requires repeated recoverable subscription usage-limit failures.");
        }

        kernel.AcknowledgeSubscriptionLimitReview(goal.Id, task.Id, review.Note);
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
