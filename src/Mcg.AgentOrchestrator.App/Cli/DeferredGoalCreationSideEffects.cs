using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal enum GoalCreationDeliveryDisposition
{
    Pending,
    Delivered,
    AlreadyDelivered
}

internal sealed record GoalCreationCollaborationEffect(
    string DeliveryId,
    CollaborationItemType Type,
    string? GoalId,
    string Subject,
    string Body,
    string CorrelationKey);

internal enum GoalCreationLifecycleEffectKind
{
    TimelineEvent,
    GoalCreated,
    ClarificationNeeded,
    StaleClarificationDetected,
    TaskDispatched,
    WorkerProgress,
    AcceptanceResult,
    AcceptanceCriterionWaived,
    GoalLanded,
    GoalLandedFromAncestry,
    GoalLandedFromMergeEvidence,
    GoalEscalated,
    GoalEvictedFromConductor,
    CleanedUp,
    ProgressiveReviewGlanceReceipt,
    ProgressiveReviewGlanceGuardReceipt,
    ProgressiveReviewGlanceSummary
}

internal sealed record GoalCreationLifecycleEffect(
    string DeliveryId,
    GoalCreationLifecycleEffectKind Kind,
    string GoalId,
    string PayloadJson);

internal sealed record GoalCreationDeliveryReceipt(
    int Version,
    string GoalId,
    GoalCreationDeliveryDisposition Disposition,
    IReadOnlyList<GoalCreationCollaborationEffect> CollaborationEffects,
    IReadOnlyList<GoalCreationLifecycleEffect> LifecycleEffects);

internal sealed record GoalCreationDeliveryResult(
    string GoalId,
    GoalCreationDeliveryDisposition Disposition);

internal static class GoalCreationSideEffectDelivery
{
    internal const string OutboxKind = "goal-create-delivery";
    internal static Action<GoalId>? BeforeStateCommit { get; set; }
    internal static Action<string>? BeforeEffectDelivery { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static GoalCreationSideEffectDelivery()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static OrchestratorStateOutboxMessage CreateMessage(
        GoalId goalId,
        IReadOnlyList<GoalCreationCollaborationEffect> collaborationEffects,
        IReadOnlyList<GoalCreationLifecycleEffect> lifecycleEffects)
    {
        var receipt = new GoalCreationDeliveryReceipt(
            Version: 1,
            goalId.Value,
            GoalCreationDeliveryDisposition.Pending,
            collaborationEffects,
            lifecycleEffects);
        return new OrchestratorStateOutboxMessage(
            MessageId(goalId.Value),
            OutboxKind,
            JsonSerializer.Serialize(receipt, JsonOptions),
            DateTimeOffset.UtcNow);
    }

    public static string MessageId(string goalId) => $"{OutboxKind}:{goalId}";

    public static GoalCreationDeliveryReceipt Deserialize(OrchestratorStateOutboxMessage message)
    {
        if (!message.Kind.Equals(OutboxKind, StringComparison.Ordinal))
            throw new JsonException($"Expected outbox kind '{OutboxKind}', found '{message.Kind}'.");

        var receipt = JsonSerializer.Deserialize<GoalCreationDeliveryReceipt>(message.PayloadJson, JsonOptions)
            ?? throw new JsonException("Goal-creation delivery receipt payload is empty.");
        if (receipt.Version != 1 ||
            receipt.Disposition != GoalCreationDeliveryDisposition.Pending ||
            !MessageId(receipt.GoalId).Equals(message.Id, StringComparison.Ordinal))
        {
            throw new JsonException("Goal-creation delivery receipt identity or disposition is invalid.");
        }

        return receipt;
    }

    public static async Task DeliverAsync(
        GoalCreationDeliveryReceipt receipt,
        OrchestratorWorkspace workspace,
        AgentOrchestratorKernel kernel,
        CancellationToken cancellationToken)
    {
        var collaborationStore = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        foreach (var effect in receipt.CollaborationEffects)
        {
            BeforeEffectDelivery?.Invoke(effect.DeliveryId);
            _ = await collaborationStore.RaiseAsync(
                effect.Type,
                effect.GoalId,
                effect.Subject,
                effect.Body,
                effect.CorrelationKey,
                cancellationToken).ConfigureAwait(false);
        }

        var lifecycleWriter = new GoalLifecycleEventWriter(
            workspace.GoalLifecycleEventsDirectory,
            kernel: kernel);
        foreach (var effect in receipt.LifecycleEffects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeEffectDelivery?.Invoke(effect.DeliveryId);
            effect.DeliverIdempotently(lifecycleWriter);
        }
    }

    internal static string SerializePayload<T>(T payload) => JsonSerializer.Serialize(payload, JsonOptions);

    internal static T DeserializePayload<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, JsonOptions)
        ?? throw new JsonException($"Goal-creation lifecycle payload for '{typeof(T).Name}' is empty.");
}

internal static class GoalCreationLifecycleEffectDelivery
{
    internal sealed record TextPayload(string Value);
    internal sealed record TextPairPayload(string First, string Second);
    internal sealed record TextTriplePayload(string First, string Second, string Third);
    internal sealed record TextListPayload(IReadOnlyList<string> Values, string Text);
    internal sealed record TaskDispatchPayload(string TaskId, AgentRole Role, string WorkerName);
    internal sealed record WorkerProgressPayload(long StdoutBytes, long StderrBytes, DateTimeOffset LastProgressAt);
    internal sealed record AcceptancePayload(bool Pass, IReadOnlyList<string> Failures);
    internal sealed record WaiverPayload(string Criterion, string Actor, DateTimeOffset RecordedAt, string Reason, string Hash);
    internal sealed record EscalationPayload(GoalLifecycleState State, GoalStatus? Status, string Reason, string Source);
    internal sealed record EvictionPayload(GoalStatus Status, string Trigger);
    internal sealed record GlancePayload(
        string TaskId,
        string Trigger,
        string InputsHash,
        string Verdict,
        string Note,
        int InputTokens,
        int OutputTokens,
        int TotalTokens,
        long WallTimeMilliseconds,
        string? Model,
        string? Profile);
    internal sealed record GlanceGuardPayload(string TaskId, ProgressiveReviewGlanceGuardReceipt Receipt);
    internal sealed record GlanceSummaryPayload(
        int TotalGlances,
        int OnTrack,
        int Concern,
        int FundamentalMisdirection,
        int Invalid,
        int TotalTokens);

    public static void DeliverIdempotently(
        this GoalCreationLifecycleEffect effect,
        GoalLifecycleEventWriter writer)
    {
        var goalId = new GoalId(effect.GoalId);
        writer.AppendIdempotent(goalId, effect.DeliveryId, target => effect.Deliver(target, goalId));
    }

    private static void Deliver(
        this GoalCreationLifecycleEffect effect,
        IGoalLifecycleEventWriter writer,
        GoalId goalId)
    {
        switch (effect.Kind)
        {
            case GoalCreationLifecycleEffectKind.TimelineEvent:
                writer.AppendTimelineEvent(GoalCreationSideEffectDelivery.DeserializePayload<ProgressEvent>(effect.PayloadJson));
                break;
            case GoalCreationLifecycleEffectKind.GoalCreated:
                writer.AppendGoalCreated(goalId, GoalCreationSideEffectDelivery.DeserializePayload<TextPayload>(effect.PayloadJson).Value);
                break;
            case GoalCreationLifecycleEffectKind.ClarificationNeeded:
                writer.AppendClarificationNeeded(goalId, GoalCreationSideEffectDelivery.DeserializePayload<TextPayload>(effect.PayloadJson).Value);
                break;
            case GoalCreationLifecycleEffectKind.StaleClarificationDetected:
                var stale = GoalCreationSideEffectDelivery.DeserializePayload<TextListPayload>(effect.PayloadJson);
                writer.AppendStaleClarificationDetected(goalId, stale.Values, stale.Text);
                break;
            case GoalCreationLifecycleEffectKind.TaskDispatched:
                var dispatch = GoalCreationSideEffectDelivery.DeserializePayload<TaskDispatchPayload>(effect.PayloadJson);
                writer.AppendTaskDispatched(goalId, new TaskId(dispatch.TaskId), dispatch.Role, dispatch.WorkerName);
                break;
            case GoalCreationLifecycleEffectKind.WorkerProgress:
                var progress = GoalCreationSideEffectDelivery.DeserializePayload<WorkerProgressPayload>(effect.PayloadJson);
                writer.AppendWorkerProgress(goalId, progress.StdoutBytes, progress.StderrBytes, progress.LastProgressAt);
                break;
            case GoalCreationLifecycleEffectKind.AcceptanceResult:
                var acceptance = GoalCreationSideEffectDelivery.DeserializePayload<AcceptancePayload>(effect.PayloadJson);
                writer.AppendAcceptanceResult(goalId, acceptance.Pass, acceptance.Failures);
                break;
            case GoalCreationLifecycleEffectKind.AcceptanceCriterionWaived:
                var waiver = GoalCreationSideEffectDelivery.DeserializePayload<WaiverPayload>(effect.PayloadJson);
                writer.AppendAcceptanceCriterionWaived(goalId, waiver.Criterion, waiver.Actor, waiver.RecordedAt, waiver.Reason, waiver.Hash);
                break;
            case GoalCreationLifecycleEffectKind.GoalLanded:
                var landed = GoalCreationSideEffectDelivery.DeserializePayload<TextPairPayload>(effect.PayloadJson);
                writer.AppendGoalLanded(goalId, landed.First, landed.Second);
                break;
            case GoalCreationLifecycleEffectKind.GoalLandedFromAncestry:
                var ancestry = GoalCreationSideEffectDelivery.DeserializePayload<TextTriplePayload>(effect.PayloadJson);
                writer.AppendGoalLandedFromAncestry(goalId, ancestry.First, ancestry.Second, ancestry.Third);
                break;
            case GoalCreationLifecycleEffectKind.GoalLandedFromMergeEvidence:
                var merge = GoalCreationSideEffectDelivery.DeserializePayload<TextTriplePayload>(effect.PayloadJson);
                writer.AppendGoalLandedFromMergeEvidence(goalId, merge.First, merge.Second, merge.Third);
                break;
            case GoalCreationLifecycleEffectKind.GoalEscalated:
                var escalation = GoalCreationSideEffectDelivery.DeserializePayload<EscalationPayload>(effect.PayloadJson);
                if (escalation.Status is { } status)
                    writer.AppendGoalEscalated(goalId, escalation.State, status, escalation.Reason, escalation.Source);
                else
                    writer.AppendGoalEscalated(goalId, escalation.State, escalation.Reason, escalation.Source);
                break;
            case GoalCreationLifecycleEffectKind.GoalEvictedFromConductor:
                var eviction = GoalCreationSideEffectDelivery.DeserializePayload<EvictionPayload>(effect.PayloadJson);
                writer.AppendGoalEvictedFromConductor(goalId, eviction.Status, eviction.Trigger);
                break;
            case GoalCreationLifecycleEffectKind.CleanedUp:
                writer.AppendCleanedUp(goalId);
                break;
            case GoalCreationLifecycleEffectKind.ProgressiveReviewGlanceReceipt:
                var glance = GoalCreationSideEffectDelivery.DeserializePayload<GlancePayload>(effect.PayloadJson);
                writer.AppendProgressiveReviewGlanceReceipt(
                    goalId,
                    new TaskId(glance.TaskId),
                    glance.Trigger,
                    glance.InputsHash,
                    glance.Verdict,
                    glance.Note,
                    glance.InputTokens,
                    glance.OutputTokens,
                    glance.TotalTokens,
                    TimeSpan.FromMilliseconds(glance.WallTimeMilliseconds),
                    glance.Model,
                    glance.Profile);
                break;
            case GoalCreationLifecycleEffectKind.ProgressiveReviewGlanceGuardReceipt:
                var guard = GoalCreationSideEffectDelivery.DeserializePayload<GlanceGuardPayload>(effect.PayloadJson);
                writer.AppendProgressiveReviewGlanceGuardReceipt(goalId, new TaskId(guard.TaskId), guard.Receipt);
                break;
            case GoalCreationLifecycleEffectKind.ProgressiveReviewGlanceSummary:
                var summary = GoalCreationSideEffectDelivery.DeserializePayload<GlanceSummaryPayload>(effect.PayloadJson);
                writer.AppendProgressiveReviewGlanceSummary(
                    goalId,
                    summary.TotalGlances,
                    summary.OnTrack,
                    summary.Concern,
                    summary.FundamentalMisdirection,
                    summary.Invalid,
                    summary.TotalTokens);
                break;
            default:
                throw new InvalidOperationException($"Unsupported goal-creation lifecycle effect '{effect.Kind}'.");
        }
    }
}

/// <summary>
/// Holds goal-creation lifecycle events until state and a replay receipt commit atomically. After
/// successful receipt delivery, later events (for example from <c>goal --run</c>) flow directly.
/// </summary>
internal sealed class DeferredGoalLifecycleEventWriter : IGoalLifecycleEventWriter
{
    private readonly object _gate = new();
    private readonly List<GoalCreationLifecycleEffect> _pending = [];
    private IGoalLifecycleEventWriter? _committedWriter;
    private int _nextSequence;

    public IReadOnlyList<GoalCreationLifecycleEffect> SnapshotEffects()
    {
        lock (_gate)
            return [.. _pending];
    }

    public void CompleteDeliveryTo(IGoalLifecycleEventWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock (_gate)
        {
            if (_committedWriter is not null)
                throw new InvalidOperationException("Goal-creation lifecycle effects were already delivered.");

            _committedWriter = writer;
            _pending.Clear();
        }
    }

    private void Append(
        GoalCreationLifecycleEffectKind kind,
        GoalId goalId,
        object payload,
        Action<IGoalLifecycleEventWriter> directAppend)
    {
        IGoalLifecycleEventWriter? writer;
        lock (_gate)
        {
            writer = _committedWriter;
            if (writer is null)
            {
                var sequence = _nextSequence++;
                _pending.Add(new GoalCreationLifecycleEffect(
                    $"{goalId.Value}:lifecycle:{sequence:D4}:{kind}",
                    kind,
                    goalId.Value,
                    GoalCreationSideEffectDelivery.SerializePayload(payload)));
                return;
            }
        }

        directAppend(writer);
    }

    public void AppendTimelineEvent(ProgressEvent value) =>
        Append(GoalCreationLifecycleEffectKind.TimelineEvent, value.GoalId, value, writer => writer.AppendTimelineEvent(value));

    public void AppendGoalCreated(GoalId goalId, string objective) =>
        Append(GoalCreationLifecycleEffectKind.GoalCreated, goalId, new GoalCreationLifecycleEffectDelivery.TextPayload(objective), writer => writer.AppendGoalCreated(goalId, objective));

    public void AppendClarificationNeeded(GoalId goalId, string clarificationId) =>
        Append(GoalCreationLifecycleEffectKind.ClarificationNeeded, goalId, new GoalCreationLifecycleEffectDelivery.TextPayload(clarificationId), writer => writer.AppendClarificationNeeded(goalId, clarificationId));

    public void AppendStaleClarificationDetected(GoalId goalId, IReadOnlyList<string> keys, string command) =>
        Append(GoalCreationLifecycleEffectKind.StaleClarificationDetected, goalId, new GoalCreationLifecycleEffectDelivery.TextListPayload(keys, command), writer => writer.AppendStaleClarificationDetected(goalId, keys, command));

    public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) =>
        Append(GoalCreationLifecycleEffectKind.TaskDispatched, goalId, new GoalCreationLifecycleEffectDelivery.TaskDispatchPayload(taskId.Value, role, workerName), writer => writer.AppendTaskDispatched(goalId, taskId, role, workerName));

    public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) =>
        Append(GoalCreationLifecycleEffectKind.WorkerProgress, goalId, new GoalCreationLifecycleEffectDelivery.WorkerProgressPayload(stdoutBytes, stderrBytes, lastProgressAt), writer => writer.AppendWorkerProgress(goalId, stdoutBytes, stderrBytes, lastProgressAt));

    public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures) =>
        Append(GoalCreationLifecycleEffectKind.AcceptanceResult, goalId, new GoalCreationLifecycleEffectDelivery.AcceptancePayload(pass, failures), writer => writer.AppendAcceptanceResult(goalId, pass, failures));

    public void AppendAcceptanceCriterionWaived(GoalId goalId, string criterion, string actor, DateTimeOffset recordedAt, string reason, string hash) =>
        Append(GoalCreationLifecycleEffectKind.AcceptanceCriterionWaived, goalId, new GoalCreationLifecycleEffectDelivery.WaiverPayload(criterion, actor, recordedAt, reason, hash), writer => writer.AppendAcceptanceCriterionWaived(goalId, criterion, actor, recordedAt, reason, hash));

    public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) =>
        Append(GoalCreationLifecycleEffectKind.GoalLanded, goalId, new GoalCreationLifecycleEffectDelivery.TextPairPayload(integrationBranch, goalBranch), writer => writer.AppendGoalLanded(goalId, integrationBranch, goalBranch));

    public void AppendGoalLandedFromAncestry(GoalId goalId, string goalBranch, string branchTip, string mainSha) =>
        Append(GoalCreationLifecycleEffectKind.GoalLandedFromAncestry, goalId, new GoalCreationLifecycleEffectDelivery.TextTriplePayload(goalBranch, branchTip, mainSha), writer => writer.AppendGoalLandedFromAncestry(goalId, goalBranch, branchTip, mainSha));

    public void AppendGoalLandedFromMergeEvidence(GoalId goalId, string goalBranch, string integrateSha, string mainSha) =>
        Append(GoalCreationLifecycleEffectKind.GoalLandedFromMergeEvidence, goalId, new GoalCreationLifecycleEffectDelivery.TextTriplePayload(goalBranch, integrateSha, mainSha), writer => writer.AppendGoalLandedFromMergeEvidence(goalId, goalBranch, integrateSha, mainSha));

    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) =>
        Append(GoalCreationLifecycleEffectKind.GoalEscalated, goalId, new GoalCreationLifecycleEffectDelivery.EscalationPayload(state, null, reason, source), writer => writer.AppendGoalEscalated(goalId, state, reason, source));

    public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, GoalStatus status, string reason, string source) =>
        Append(GoalCreationLifecycleEffectKind.GoalEscalated, goalId, new GoalCreationLifecycleEffectDelivery.EscalationPayload(state, status, reason, source), writer => writer.AppendGoalEscalated(goalId, state, status, reason, source));

    public void AppendGoalEvictedFromConductor(GoalId goalId, GoalStatus status, string trigger) =>
        Append(GoalCreationLifecycleEffectKind.GoalEvictedFromConductor, goalId, new GoalCreationLifecycleEffectDelivery.EvictionPayload(status, trigger), writer => writer.AppendGoalEvictedFromConductor(goalId, status, trigger));

    public void AppendCleanedUp(GoalId goalId) =>
        Append(GoalCreationLifecycleEffectKind.CleanedUp, goalId, new { }, writer => writer.AppendCleanedUp(goalId));

    public void AppendProgressiveReviewGlanceReceipt(
        GoalId goalId,
        TaskId taskId,
        string trigger,
        string inputsHash,
        string verdict,
        string note,
        int inputTokens,
        int outputTokens,
        int totalTokens,
        TimeSpan wallTime,
        string? model,
        string? profile) =>
        Append(
            GoalCreationLifecycleEffectKind.ProgressiveReviewGlanceReceipt,
            goalId,
            new GoalCreationLifecycleEffectDelivery.GlancePayload(taskId.Value, trigger, inputsHash, verdict, note, inputTokens, outputTokens, totalTokens, (long)wallTime.TotalMilliseconds, model, profile),
            writer => writer.AppendProgressiveReviewGlanceReceipt(goalId, taskId, trigger, inputsHash, verdict, note, inputTokens, outputTokens, totalTokens, wallTime, model, profile));

    public void AppendProgressiveReviewGlanceGuardReceipt(GoalId goalId, TaskId taskId, ProgressiveReviewGlanceGuardReceipt receipt) =>
        Append(GoalCreationLifecycleEffectKind.ProgressiveReviewGlanceGuardReceipt, goalId, new GoalCreationLifecycleEffectDelivery.GlanceGuardPayload(taskId.Value, receipt), writer => writer.AppendProgressiveReviewGlanceGuardReceipt(goalId, taskId, receipt));

    public void AppendProgressiveReviewGlanceSummary(GoalId goalId, int totalGlances, int onTrack, int concern, int fundamentalMisdirection, int invalid, int totalTokens) =>
        Append(GoalCreationLifecycleEffectKind.ProgressiveReviewGlanceSummary, goalId, new GoalCreationLifecycleEffectDelivery.GlanceSummaryPayload(totalGlances, onTrack, concern, fundamentalMisdirection, invalid, totalTokens), writer => writer.AppendProgressiveReviewGlanceSummary(goalId, totalGlances, onTrack, concern, fundamentalMisdirection, invalid, totalTokens));
}

/// <summary>
/// Defers clarification raises while goal state is only prepared in memory. The durable outbox
/// receipt carries these idempotent, correlation-keyed effects across process failure.
/// </summary>
internal sealed class DeferredGoalCreationCollaborationWriter(ICollaborationItemStore store)
{
    private readonly object _gate = new();
    private readonly List<GoalCreationCollaborationEffect> _pending = [];
    private bool _committed;

    public Task<CollaborationItem> RaiseAsync(
        CollaborationItemType type,
        string? goalId,
        string subject,
        string body,
        string? correlationKey = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_committed)
                return store.RaiseAsync(type, goalId, subject, body, correlationKey, cancellationToken);

            if (string.IsNullOrWhiteSpace(correlationKey))
            {
                throw new InvalidOperationException(
                    "GOAL_CREATE_DELIVERY_UNSAFE reason=collaboration-correlation-key-missing");
            }

            var pending = new CollaborationItem(
                Guid.NewGuid().ToString("n"),
                type,
                goalId,
                CollaborationItemStatus.Raised,
                subject,
                body,
                correlationKey,
                DateTimeOffset.UtcNow,
                null,
                null);
            _pending.Add(new GoalCreationCollaborationEffect(
                pending.Id,
                type,
                goalId,
                subject,
                body,
                correlationKey));
            return Task.FromResult(pending);
        }
    }

    public IReadOnlyList<GoalCreationCollaborationEffect> SnapshotEffects()
    {
        lock (_gate)
            return [.. _pending];
    }

    public void CompleteDelivery()
    {
        lock (_gate)
        {
            if (_committed)
                throw new InvalidOperationException("Goal-creation collaboration effects were already delivered.");

            _committed = true;
            _pending.Clear();
        }
    }
}
