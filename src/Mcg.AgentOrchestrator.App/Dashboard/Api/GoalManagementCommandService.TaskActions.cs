using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

/// <summary>
/// Dashboard transport adapter for single-task operations. It validates the operation name, parses
/// the request body, delegates the mutation to <see cref="GoalTaskCommandOperations"/> or
/// <see cref="GoalAdvancementOperations"/>, and maps the result to a dashboard DTO.
/// </summary>
internal static partial class GoalManagementCommandService
{
internal const string DashboardOperatorIntentChannel = "dashboard";
internal const string DashboardOperatorIntentAuthenticationAssurance = "dashboard-operator-control";

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
    var commands = new GoalTaskCommandOperations();
    switch (operation.ToLowerInvariant())
    {
        case "run":
            return ToStepResult(
                goal,
                await new GoalAdvancementOperations().RunAssignedTaskAsync(kernel, agents, providers, workspace, goal, task.Id));

        case "api-run":
            return DashboardResponseMapper.ToTaskDetailDto(
                goal,
                await new GoalAdvancementOperations().ApiRunAssignedTaskAsync(kernel, agents, providers, workspace, goal, task.Id));

        case "dispatch":
            // Deliberate ordering change from the pre-move adapter: the refinement guard now runs
            // inside GoalTaskCommandOperations.RecordDispatch, so a malformed body fails parsing
            // first and no longer launches refinement as a side effect of an invalid request. The
            // only observable difference is which error a caller gets when the body is malformed
            // and refinement is pending at the same time; the success path is unchanged.
            var dispatch = DashboardRequestParser.ParseDispatchSubmission(body);
            commands.RecordDispatch(kernel, workspace, providers, goal, task, dispatch.WorkerName, dispatch.Command);
            return null;

        case "profile-dispatch":
            var submission = DashboardRequestParser.ParseProfileDispatchReadySubmission(body);
            var profile = WorkerProfileStore.Load(workspace.WorkerProfilePath).GetRequired(submission.ProfileName);
            var profileDispatch = commands.ProfileDispatch(kernel, workspace, goal, task, profile, agents, providers);
            return DashboardResponseMapper.ToProfileDispatchDto(goal, profileDispatch);

        case "subscription-dispatch":
            var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
            var subscriptionDispatch = commands.SubscriptionDispatch(
                kernel,
                workspace,
                goal,
                task,
                agents,
                profiles,
                providers,
                DashboardRequestParser.ParseLimitReviewSubmission(body)?.Note);
            return DashboardResponseMapper.ToProfileDispatchDto(goal, subscriptionDispatch);

        case "start":
            var startFailure = commands.PrepareAndStartDispatch(
                kernel,
                workspace,
                goal,
                task,
                agents,
                WorkerProfileStore.Load(workspace.WorkerProfilePath),
                providers);
            return ToDispatchProcessStartFailureDto(goal, startFailure);

        case "refresh":
            commands.RefreshProcess(kernel, goal, task);
            return null;

        case "cancel":
            commands.CancelProcess(kernel, goal, task);
            return null;

        case "verify":
            var verify = DashboardRequestParser.ParseVerifySubmission(body);
            await commands.VerifyAsync(kernel, workspace, goal, task, verify.Command);
            return null;

        case "verify-manual":
            var manual = DashboardRequestParser.ParseManualVerifySubmission(body);
            return await EnqueueOperatorIntentDtoAsync(
                commands,
                workspace,
                goal,
                task,
                OperatorIntentVerbs.VerifyManual,
                new ManualVerificationOperatorIntentPayload(Request: new ManualVerificationRequest(
                    manual.Passed, manual.Note, workspace.ExecutionDirectory)),
                manual.IdempotencyKey);

        case "complete-verify":
            var complete = DashboardRequestParser.ParseManualVerifySubmission(body);
            commands.CompleteVerify(kernel, workspace, goal, task, complete.Note);
            return null;

        case "progress":
            var progress = DashboardRequestParser.ParseProgressSubmission(body);
            var progressStatus = CliArgumentParser.ParseReportableStatus(progress.Status);
            return await EnqueueOperatorIntentDtoAsync(
                commands,
                workspace,
                goal,
                task,
                OperatorIntentVerbs.Progress,
                new ProgressOperatorIntentPayload(progressStatus, progress.Message),
                idempotencyKey: null);

        case "retry":
            var retry = DashboardRequestParser.ParseRetrySubmission(body);
            return await EnqueueOperatorIntentDtoAsync(
                commands,
                workspace,
                goal,
                task,
                OperatorIntentVerbs.Retry,
                new RetryOperatorIntentPayload(
                    retry.Message,
                    retry.Mechanical ? RetryRoundKind.Mechanical : null,
                    RetryCause: Enum.Parse<RetryCause>(retry.Cause, ignoreCase: true)),
                retry.IdempotencyKey);

        case "verification-plan":
            var verificationPlan = DashboardRequestParser.ParseVerificationPlanSubmission(body);
            commands.SetVerificationPlan(kernel, goal, task, verificationPlan.Plan);
            return DashboardResponseMapper.ToTaskVerificationPlanDto(goal, task);

        case "ask":
            var ask = DashboardRequestParser.ParseAskSubmission(body);
            var request = commands.RequestHumanInput(kernel, goal, task, ask.Question);
            return DashboardResponseMapper.ToHumanInputDto(kernel, request);

        default:
            throw new ArgumentException("Task operation must be run, api-run, retry, verification-plan, dispatch, profile-dispatch, subscription-dispatch, start, refresh, cancel, verify, verify-manual, complete-verify, progress, or ask.");
    }
}

private static async Task<OperatorIntentDto> EnqueueOperatorIntentDtoAsync(
    GoalTaskCommandOperations commands,
    OrchestratorWorkspace workspace,
    Goal goal,
    TaskSpec task,
    string verb,
    object payload,
    string? idempotencyKey)
{
    var (persisted, warning) = await commands.EnqueueOperatorIntentAsync(
        workspace,
        goal,
        task,
        verb,
        payload,
        idempotencyKey,
        DashboardOperatorIntentChannel,
        DashboardOperatorIntentAuthenticationAssurance);
    return DashboardResponseMapper.ToOperatorIntentDto(persisted) with { Warning = warning };
}
}
