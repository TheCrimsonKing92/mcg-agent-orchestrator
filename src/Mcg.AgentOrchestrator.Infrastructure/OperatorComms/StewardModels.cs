using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum StewardWakeReason
{
    EscalationInboxItem = 1,
    StormThresholdCrossing = 2,
    DigestSchedule = 3,
    UnraisedEscalationTimeout = 4
}

public enum StewardWakeDecisionKind
{
    Dispatch = 1,
    Debounced = 2,
    NoWakeSignal = 3
}

public enum StewardOutputKind
{
    DecisionCard = 1,
    StormCollapseJudgment = 2,
    DailyBacklogDigest = 3,
    DailyBrief = 4,
    CatchUpReplay = 5,
    RawBypassEscalation = 6,
    FailOpenRawEscalation = 7
}

public enum StewardEscalationCategory
{
    Normal = 1,
    BoardWedge = 2,
    StewardFailure = 3,
    DenylistHitLanding = 4,
    SecurityOwnership = 5
}

public enum StewardDispositionKind
{
    RaisedRaw = 1,
    CardCreated = 2,
    ReceiptLinkedAction = 3
}

public sealed record StewardDispatchOptions
{
    public StewardDispatchOptions(TimeSpan debounceWindow, TimeSpan failOpenTimeout)
    {
        if (debounceWindow < TimeSpan.FromMinutes(2) || debounceWindow > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(debounceWindow), "Steward debounce must be between 2 and 5 minutes.");
        if (failOpenTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(failOpenTimeout), "Fail-open timeout must be positive.");

        DebounceWindow = debounceWindow;
        FailOpenTimeout = failOpenTimeout;
    }

    public TimeSpan DebounceWindow { get; }

    public TimeSpan FailOpenTimeout { get; }

    public static StewardDispatchOptions Default { get; } =
        new(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(30));
}

public sealed record StewardWakeSignal(
    StewardWakeReason Reason,
    string CorrelationKey,
    DateTimeOffset ObservedAt);

public sealed record StewardWakeDecision(
    StewardWakeDecisionKind Kind,
    StewardWakeReason? Reason,
    string? CorrelationKey,
    string Detail);

public sealed class StewardWakeScheduler
{
    private readonly StewardDispatchOptions _options;

    public StewardWakeScheduler(StewardDispatchOptions? options = null)
    {
        _options = options ?? StewardDispatchOptions.Default;
    }

    public StewardWakeDecision Evaluate(
        IReadOnlyList<StewardWakeSignal> signals,
        IReadOnlyList<StewardTriageReceipt> priorReceipts,
        DateTimeOffset now)
    {
        var signal = signals
            .Where(item => item.ObservedAt <= now)
            .OrderBy(item => item.ObservedAt)
            .FirstOrDefault();
        if (signal is null)
            return new StewardWakeDecision(StewardWakeDecisionKind.NoWakeSignal, null, null, "no-event-driven-signal");

        var lastDispatchAt = priorReceipts
            .Where(receipt => receipt.OutputKind is not StewardOutputKind.RawBypassEscalation)
            .OrderByDescending(receipt => receipt.CreatedAt)
            .Select(receipt => (DateTimeOffset?)receipt.CreatedAt)
            .FirstOrDefault();
        if (lastDispatchAt is not null && now - lastDispatchAt.Value < _options.DebounceWindow)
        {
            return new StewardWakeDecision(
                StewardWakeDecisionKind.Debounced,
                signal.Reason,
                signal.CorrelationKey,
                "debounce-window");
        }

        return new StewardWakeDecision(
            StewardWakeDecisionKind.Dispatch,
            signal.Reason,
            signal.CorrelationKey,
            "wake-signal");
    }
}

public sealed record StewardEscalationItem(
    string Id,
    StewardEscalationCategory Category,
    string GoalId,
    string Kind,
    string CauseFingerprint,
    int SeverityRank,
    DateTimeOffset RaisedAt,
    string Title,
    string EvidenceSummary);

public sealed record StewardGoalTaskRecord(
    string GoalId,
    string? TaskId,
    string State,
    int OpenTasks,
    int FailedTasks,
    int AwaitingVerificationTasks);

public sealed record StewardNumericReceiptSummary(
    int TrxCount,
    int FailedTrxCount,
    IReadOnlyList<int> ExitCodes,
    int DiffFiles,
    int DiffInsertions,
    int DiffDeletions);

public sealed record StewardPrecedentMatch(
    string Kind,
    string CauseFingerprint,
    int PriorCount,
    int ResolvedCount);

public sealed record StewardPolicySnapshot(
    string AutonomyPolicyName,
    IReadOnlyList<string> Denylist,
    DateTimeOffset ObservedAt);

public sealed record StewardInterruptBudgetLedger(
    int DailyBudget,
    int Used,
    int Remaining,
    DateTimeOffset WindowStartedAt);

public sealed record StewardProvenanceLink(
    string Stage,
    string ReceiptId,
    int ExitCode,
    DateTimeOffset OccurredAt);

public sealed record StewardQuotedWorkerProse(
    string Label,
    string Quote);

public sealed record StewardBriefingBundle(
    IReadOnlyList<StewardEscalationItem> Escalations,
    IReadOnlyList<StewardGoalTaskRecord> GoalTasks,
    StewardNumericReceiptSummary Receipts,
    IReadOnlyList<StewardPrecedentMatch> MatchedPrecedents,
    StewardPolicySnapshot PolicySnapshot,
    StewardInterruptBudgetLedger InterruptBudget,
    IReadOnlyList<StewardProvenanceLink> FailingRoundProvenance,
    IReadOnlyList<StewardQuotedWorkerProse> WorkerProse)
{
    public string InputsHash() => StewardInputHasher.Hash(this);
}

public sealed record StewardBacklogCandidate(
    string Id,
    string Title,
    int Priority,
    int PipelineUnits,
    IReadOnlyList<string> ScopeKeys);

public sealed record StewardRankedBacklogItem(
    string Id,
    string Title,
    int Rank,
    int PipelineUnits,
    bool ScopeDisjoint);

public sealed record StewardDailyBacklogDigest(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<StewardRankedBacklogItem> Items);

public sealed record StewardDailyBrief(
    DateTimeOffset GeneratedAt,
    int LandedGoals,
    int HumanBlockedItems,
    string Next,
    decimal Spend);

public sealed record StewardAutonomousAction(
    string ActionId,
    string Kind,
    string ReceiptId,
    DateTimeOffset OccurredAt);

public sealed record StewardBoardStateDiff(
    int ActiveDelta,
    int BlockedDelta,
    int LandedDelta);

public sealed record StewardCatchUpReplay(
    DateTimeOffset From,
    DateTimeOffset To,
    StewardBoardStateDiff BoardDiff,
    IReadOnlyList<StewardAutonomousAction> AutonomousActions);

public sealed record StewardStormCollapseJudgment(
    string Kind,
    string CauseFingerprint,
    int EscalationCount,
    bool ShouldCollapse,
    IReadOnlyList<ControlPlaneDecisionCard> CardsForDeliverer);

public sealed record StewardHeartbeat(
    int Triaged,
    int Acted,
    int Raised,
    double ActedRatio,
    double RaisedRatio);

public sealed record StewardCardLoadMeasurement(
    int LandedGoals,
    int NovelCards,
    int PrecedentCoveredCards)
{
    public double NovelCardsPerLandedGoal =>
        LandedGoals == 0 ? 0 : (double)NovelCards / LandedGoals;

    public double PrecedentCoveredCardsPerLandedGoal =>
        LandedGoals == 0 ? 0 : (double)PrecedentCoveredCards / LandedGoals;
}

public sealed record StewardOutput<T>(
    T Value,
    StewardTriageReceipt Receipt);

public sealed record StewardTriageBatch(
    IReadOnlyList<ControlPlaneDecisionCard> Cards,
    IReadOnlyList<StewardTriageReceipt> Receipts);

public sealed record StewardDispatchResult(
    IReadOnlyList<ControlPlaneDecisionCard> Cards,
    IReadOnlyList<StewardTriageReceipt> Receipts,
    bool FailedOpen);

public sealed record StewardInboxDisposition
{
    private StewardInboxDisposition(
        StewardDispositionKind kind,
        string inboxItemId,
        string? linkedCardDedupKey,
        string? linkedActionReceiptId)
    {
        Kind = kind;
        InboxItemId = Required(inboxItemId, nameof(inboxItemId));
        LinkedCardDedupKey = EmptyToNull(linkedCardDedupKey);
        LinkedActionReceiptId = EmptyToNull(linkedActionReceiptId);
        if (!HasTerminalLink)
            throw new ArgumentException("Steward disposition must link to a raw action, card, or action receipt.");
    }

    public StewardDispositionKind Kind { get; }

    public string InboxItemId { get; }

    public string? LinkedCardDedupKey { get; }

    public string? LinkedActionReceiptId { get; }

    public bool HasTerminalLink =>
        !string.IsNullOrWhiteSpace(LinkedCardDedupKey) ||
        !string.IsNullOrWhiteSpace(LinkedActionReceiptId);

    public static StewardInboxDisposition RaisedRaw(string inboxItemId, string rawReceiptId) =>
        new(StewardDispositionKind.RaisedRaw, inboxItemId, null, rawReceiptId);

    public static StewardInboxDisposition CardCreated(string inboxItemId, string cardDedupKey) =>
        new(StewardDispositionKind.CardCreated, inboxItemId, cardDedupKey, null);

    public static StewardInboxDisposition ReceiptLinkedAction(string inboxItemId, string actionReceiptId) =>
        new(StewardDispositionKind.ReceiptLinkedAction, inboxItemId, null, actionReceiptId);

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", name)
            : value.Trim();

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record StewardTriageReceipt
{
    public StewardTriageReceipt(
        string id,
        StewardOutputKind outputKind,
        string inputsHash,
        DateTimeOffset createdAt,
        IReadOnlyList<string> inputIds,
        IReadOnlyList<StewardInboxDisposition> dispositions,
        string summary,
        StewardCardLoadMeasurement? cardLoadMeasurement = null)
    {
        Id = Required(id, nameof(id));
        OutputKind = outputKind;
        InputsHash = Required(inputsHash, nameof(inputsHash));
        CreatedAt = createdAt;
        InputIds = inputIds;
        Dispositions = dispositions;
        Summary = Required(summary, nameof(summary));
        CardLoadMeasurement = cardLoadMeasurement;

        if (Dispositions.Any(disposition => !disposition.HasTerminalLink))
            throw new ArgumentException("Closed Steward inbox items must terminate in a receipt-linked action or a card.", nameof(dispositions));
    }

    public string Id { get; }

    public StewardOutputKind OutputKind { get; }

    public string InputsHash { get; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<string> InputIds { get; }

    public IReadOnlyList<StewardInboxDisposition> Dispositions { get; }

    public string Summary { get; }

    public StewardCardLoadMeasurement? CardLoadMeasurement { get; }

    public StewardHeartbeat ToHeartbeat() =>
        StewardHeartbeatCalculator.FromReceipts([this]);

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", name)
            : value.Trim();
}

public sealed class StewardBypassPolicy
{
    private readonly HashSet<StewardEscalationCategory> _categories;

    public StewardBypassPolicy(IEnumerable<StewardEscalationCategory>? categories = null)
    {
        _categories = (categories ?? DefaultCategories)
            .ToHashSet();
    }

    public static IReadOnlyList<StewardEscalationCategory> DefaultCategories { get; } =
        [
            StewardEscalationCategory.BoardWedge,
            StewardEscalationCategory.StewardFailure,
            StewardEscalationCategory.DenylistHitLanding,
            StewardEscalationCategory.SecurityOwnership
        ];

    public bool ShouldBypass(StewardEscalationItem item) =>
        _categories.Contains(item.Category);
}

public static class StewardHeartbeatCalculator
{
    public static StewardHeartbeat FromReceipts(IReadOnlyList<StewardTriageReceipt> receipts)
    {
        var triaged = receipts.Sum(receipt => receipt.InputIds.Count);
        var raised = receipts.Sum(receipt => receipt.Dispositions.Count(disposition =>
            disposition.Kind == StewardDispositionKind.RaisedRaw));
        var acted = receipts.Sum(receipt => receipt.Dispositions.Count(disposition =>
            disposition.Kind is StewardDispositionKind.CardCreated or StewardDispositionKind.ReceiptLinkedAction));
        return new StewardHeartbeat(
            triaged,
            acted,
            raised,
            triaged == 0 ? 0 : (double)acted / triaged,
            triaged == 0 ? 0 : (double)raised / triaged);
    }
}

internal static class StewardInputHasher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string Hash<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
