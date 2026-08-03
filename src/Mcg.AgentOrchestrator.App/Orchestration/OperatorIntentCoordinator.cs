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
    private readonly Dictionary<string, List<(string IntentId, string Outcome)>> _pendingCompletions =
        new(StringComparer.Ordinal);

    public OperatorIntentCoordinator(
        IOperatorIntentStore store,
        Func<DateTimeOffset>? utcNow = null)
    {
        _store = store;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public static OperatorIntentCoordinator CreateDefault(OrchestratorWorkspace workspace) =>
        new(SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory));

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

    private static void Apply(
        AgentOrchestratorKernel kernel,
        Goal goal,
        OperatorIntentRecord intent)
    {
        if (intent.TaskId is null)
        {
            throw new InvalidOperationException($"Operator intent verb '{intent.Verb}' requires a task id.");
        }

        var taskId = new TaskId(intent.TaskId);
        if (!goal.Tasks.Any(task => task.Id == taskId))
        {
            throw new KeyNotFoundException($"Task '{intent.TaskId}' was not found in goal '{goal.Id.Value}'.");
        }

        switch (intent.Verb)
        {
            case OperatorIntentVerbs.Progress:
                var progress = Deserialize<ProgressOperatorIntentPayload>(intent);
                kernel.ReportTaskProgress(goal.Id, taskId, progress.Status, progress.Message);
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
                kernel.RetryTask(
                    goal.Id,
                    taskId,
                    retry.Message,
                    retryRoundKind: retry.RetryRoundKind);
                GoalLifecycleCommands.RecordCapabilityWarnings(
                    kernel,
                    goal.Id,
                    GoalObjectivePlanner.BuildCapabilityWarnings(retry.Message));
                break;

            case OperatorIntentVerbs.VerifyManual:
                var manual = Deserialize<ManualVerificationOperatorIntentPayload>(intent);
                kernel.RecordTaskVerification(goal.Id, taskId, manual.Verification);
                break;

            default:
                throw new InvalidOperationException($"Unsupported operator intent verb '{intent.Verb}'.");
        }
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
