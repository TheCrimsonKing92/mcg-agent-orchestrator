using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OperatorIntentExecutionResult(
    bool MutatedGoalState,
    IReadOnlyList<string> ProgressLines);

internal sealed class OperatorIntentCoordinator
{
    internal const string ClaimOwner = "conduct-loop";
    internal const int MaxIntentsPerGoalPerTick = 32;
    internal const string TerminalGoalEvictedReasonCode = "goal-terminal-evicted";

    private readonly IOperatorIntentStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<GoalId, string?>? _goalHeadResolver;
    private readonly Dictionary<string, List<(string IntentId, string Outcome)>> _pendingCompletions =
        new(StringComparer.Ordinal);

    public OperatorIntentCoordinator(
        IOperatorIntentStore store,
        Func<DateTimeOffset>? utcNow = null,
        Func<GoalId, string?>? goalHeadResolver = null)
    {
        _store = store;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _goalHeadResolver = goalHeadResolver;
    }

    public static OperatorIntentCoordinator CreateDefault(OrchestratorWorkspace workspace) =>
        new(
            SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory),
            goalHeadResolver: goalId => ResolveGoalHead(workspace.ExecutionDirectory, goalId));

    public IReadOnlyList<string> ListActionableGoalIds() =>
        _store.ListActionableGoalIdsAsync().GetAwaiter().GetResult();

    public OperatorIntentExecutionResult ExecutePending(AgentOrchestratorKernel kernel, Goal goal)
    {
        if (_pendingCompletions.TryGetValue(goal.Id.Value, out var uncommitted) && uncommitted.Count > 0)
        {
            return new OperatorIntentExecutionResult(
                MutatedGoalState: true,
                [$"OPERATOR_INTENT goal={goal.Id.Value[..8]} result=waiting-for-state-commit count={uncommitted.Count}"]);
        }

        var mutated = false;
        var lines = new List<string>();
        for (var count = 0; count < MaxIntentsPerGoalPerTick; count++)
        {
            var intent = _store.ClaimNextAsync(goal.Id.Value, ClaimOwner).GetAwaiter().GetResult();
            if (intent is null)
            {
                break;
            }

            var marker = BuildApplicationMarker(intent);
            if (goal.Timeline.Any(item => item.Message.Contains(marker, StringComparison.Ordinal)))
            {
                var recoveredOutcome = $"Applied; recovered durable goal marker {marker}.";
                _store.CompleteAsync(
                    intent.Id,
                    ClaimOwner,
                    OperatorIntentStatus.Applied,
                    recoveredOutcome,
                    _utcNow()).GetAwaiter().GetResult();
                lines.Add($"OPERATOR_INTENT id={intent.Id} verb={intent.Verb} goal={goal.Id.Value[..8]} result=applied-recovered");
                continue;
            }

            try
            {
                Apply(kernel, goal, intent);
                kernel.RecordGoalPolicyDecision(
                    goal.Id,
                    $"{marker} verb={intent.Verb} task={intent.TaskId ?? "none"} actor={intent.Actor} channel={intent.Channel} auth={intent.AuthenticationAssurance}");
                var outcome = $"Applied {intent.Verb} to goal {goal.Id.Value[..8]}" +
                    (intent.TaskId is null ? "." : $" task {intent.TaskId[..Math.Min(8, intent.TaskId.Length)]}.");
                AddPendingCompletion(goal.Id.Value, intent.Id, outcome);
                lines.Add($"OPERATOR_INTENT id={intent.Id} verb={intent.Verb} goal={goal.Id.Value[..8]} result=applied-pending-commit");
                mutated = true;
                break;
            }
            catch (Exception ex)
            {
                var outcome = $"Rejected {intent.Verb}: {Sanitize(ex.Message)}";
                _store.CompleteAsync(
                    intent.Id,
                    ClaimOwner,
                    OperatorIntentStatus.Rejected,
                    outcome,
                    _utcNow()).GetAwaiter().GetResult();
                lines.Add($"OPERATOR_INTENT id={intent.Id} verb={intent.Verb} goal={goal.Id.Value[..8]} result=rejected reason={Sanitize(ex.Message)}");
            }
        }

        return new OperatorIntentExecutionResult(mutated, lines);
    }

    public IReadOnlyList<string> RejectPending(string goalId, string reason, string? reasonCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var lines = new List<string>();
        for (var count = 0; count < MaxIntentsPerGoalPerTick; count++)
        {
            var intent = _store.ClaimNextAsync(goalId, ClaimOwner).GetAwaiter().GetResult();
            if (intent is null)
            {
                break;
            }

            var reasonCodeText = string.IsNullOrWhiteSpace(reasonCode)
                ? string.Empty
                : $" reasonCode={Sanitize(reasonCode)}";
            var outcome = $"Rejected {intent.Verb}:{reasonCodeText} {Sanitize(reason)}";
            _store.CompleteAsync(
                intent.Id,
                ClaimOwner,
                OperatorIntentStatus.Rejected,
                outcome,
                _utcNow()).GetAwaiter().GetResult();
            lines.Add(
                $"OPERATOR_INTENT id={intent.Id} verb={intent.Verb} goal={ShortGoalId(goalId)} result=rejected{reasonCodeText} reason={Sanitize(reason)}");
        }

        return lines;
    }

    public void CompletePersisted(IReadOnlyCollection<GoalId> persistedGoalIds)
    {
        foreach (var goalId in persistedGoalIds)
        {
            if (!_pendingCompletions.TryGetValue(goalId.Value, out var completions))
            {
                continue;
            }

            while (completions.Count > 0)
            {
                var completion = completions[0];
                _store.CompleteAsync(
                    completion.IntentId,
                    ClaimOwner,
                    OperatorIntentStatus.Applied,
                    completion.Outcome,
                    _utcNow()).GetAwaiter().GetResult();
                completions.RemoveAt(0);
            }

            if (completions.Count == 0)
            {
                _pendingCompletions.Remove(goalId.Value);
            }
        }
    }

    private void Apply(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OperatorIntentRecord intent)
    {
        switch (intent.Verb)
        {
            case OperatorIntentVerbs.CriterionEvidenceMap:
                var mapping = Deserialize<CriterionEvidenceMappingOperatorIntentPayload>(intent);
                kernel.MapCriterionEvidenceOwner(
                    goal.Id,
                    mapping.CriterionIndex,
                    mapping.CriterionVersion,
                    mapping.Owner,
                    intent.Actor,
                    mapping.RequiredScope,
                    mapping.FindingStableId,
                    mapping.CandidateSha);
                return;
            case OperatorIntentVerbs.CriterionEvidenceRecord:
                var receipt = Deserialize<CriterionEvidenceReceiptOperatorIntentPayload>(intent);
                if (receipt.Owner != CriterionEvidenceOwner.Operator)
                {
                    throw new InvalidOperationException(
                        "Only the conductor may record Acceptance-owned criterion evidence; operator intents may record Operator-owned observations only.");
                }
                kernel.RecordCriterionEvidence(
                    goal.Id,
                    receipt.ObligationId,
                    receipt.Owner,
                    receipt.CandidateSha,
                    receipt.ReceiptId,
                    receipt.Scope,
                    receipt.Passed,
                    receipt.Detail);
                return;
            case OperatorIntentVerbs.CriterionEvidenceRepair:
                var repair = Deserialize<CriterionEvidenceRepairOperatorIntentPayload>(intent);
                kernel.RepairMalformedCriterionEvidenceObligation(
                    goal.Id,
                    repair.MalformedObligationId,
                    repair.CriterionIndex,
                    repair.CriterionVersion,
                    repair.Owner,
                    intent.Actor,
                    repair.Reason,
                    repair.RequiredScope,
                    repair.FindingStableId,
                    repair.CandidateSha);
                return;
        }

        if (intent.TaskId is null)
        {
            throw new InvalidOperationException($"Operator intent verb '{intent.Verb}' requires a task id.");
        }

        var taskId = new TaskId(intent.TaskId);
        var task = goal.Tasks.FirstOrDefault(task => task.Id == taskId);
        if (task is null)
        {
            throw new KeyNotFoundException($"Task '{intent.TaskId}' was not found in goal '{goal.Id.Value}'.");
        }

        switch (intent.Verb)
        {
            case OperatorIntentVerbs.Progress:
                var progress = Deserialize<ProgressOperatorIntentPayload>(intent);
                var observedCandidate = progress.Status == WorkTaskStatus.Completed &&
                    task.LatestRetryAt is not null &&
                    task.LastDispatch?.ResultCommit is null
                    ? TryResolveGoalHead(goal.Id)
                    : null;
                kernel.ReportTaskProgress(
                    goal.Id,
                    taskId,
                    progress.Status,
                    progress.Message,
                    observedCandidate);
                break;

            case OperatorIntentVerbs.Retry:
                var retry = Deserialize<RetryOperatorIntentPayload>(intent);
                var retryPolicy = AutonomyPolicy.Parse(retry.AutonomyPolicy);
                retryPolicy.ThrowIfDisallowed(AutonomyAction.Retry, OperatorIntentVerbs.Retry);
                AutonomyPolicyEvidence.Record(
                    kernel,
                    goal,
                    retryPolicy,
                    AutonomyAction.Retry,
                    OperatorIntentVerbs.Retry,
                    allowed: true);
                if (retry.RetryCause is null or RetryCause.Unknown)
                {
                    kernel.RequestHumanInputDeduplicated(
                        goal.Id,
                        taskId,
                        "The operator retry request has an Unknown retry cause. Classify the cause as " +
                        "NewSourceFinding, NewTestFinding, CriterionEvidenceOwnerMismatch, " +
                        "EnvironmentApparatusFailure, ContractClarification, MainDriftConflict, " +
                        "ProviderInterruption, or UnchangedContextRepeat; then resubmit the retry with " +
                        "the explicit cause. No retry was admitted.",
                        HumanWaitKind.SpecClarification,
                        isAutoDefaultable: false,
                        isDismissible: false,
                        isAnswerRequired: true,
                        isExternallyBlocked: false,
                        blockerFingerprint: $"operator-retry-cause:{intent.Id}");
                    break;
                }
                kernel.RetryTaskWithAuthoritativeFeedback(
                    goal.Id,
                    taskId,
                    retry.Message,
                    retryCause: retry.RetryCause.Value,
                    retryRoundKind: retry.RetryRoundKind,
                    invalidateDownstream: true);
                GoalLifecycleCommands.RecordCapabilityWarnings(
                    kernel,
                    goal.Id,
                    GoalObjectivePlanner.BuildCapabilityWarnings(retry.Message));
                break;

            case OperatorIntentVerbs.VerifyManual:
                var manual = Deserialize<ManualVerificationOperatorIntentPayload>(intent);
                kernel.RecordTaskVerification(goal.Id, taskId, manual.ResolveVerification(intent.CreatedAt));
                break;

            default:
                throw new InvalidOperationException($"Unsupported operator intent verb '{intent.Verb}'.");
        }
    }

    private string? TryResolveGoalHead(GoalId goalId)
    {
        try
        {
            return _goalHeadResolver?.Invoke(goalId);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveGoalHead(string executionDirectory, GoalId goalId)
    {
        var worktreePath = GoalWorktrees.TryResolve(executionDirectory, goalId);
        if (worktreePath is null)
        {
            return null;
        }

        var head = GitCli.Run(worktreePath, "rev-parse", "HEAD");
        return head.Succeeded ? head.Output.Trim() : null;
    }

    private void AddPendingCompletion(string goalId, string intentId, string outcome)
    {
        if (!_pendingCompletions.TryGetValue(goalId, out var completions))
        {
            completions = [];
            _pendingCompletions.Add(goalId, completions);
        }

        completions.Add((intentId, outcome));
    }

    internal static string BuildApplicationMarker(OperatorIntentRecord intent) =>
        $"operator-intent:{intent.Id}";

    private static string ShortGoalId(string goalId) =>
        goalId[..Math.Min(8, goalId.Length)];

    private static T Deserialize<T>(OperatorIntentRecord intent)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(intent.PayloadJson, OperatorIntentJson.Options)
                ?? throw new InvalidOperationException("Payload was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Operator intent '{intent.Id}' has an invalid {intent.Verb} payload: {ex.Message}",
                ex);
        }
    }

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

internal static class OperatorIntentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
