using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum ControlPlaneCardSource
{
    CollaborationDecision = 1,
    OperatorInboxEscalation = 2
}

public enum ControlPlaneDeliveryChannel
{
    Decisions = 1,
    Board = 2,
    Digest = 3
}

public enum ControlPlaneDeliveryOperationKind
{
    Send = 1,
    Edit = 2,
    Suppressed = 3
}

public sealed record ControlPlaneAction(
    string Label,
    string CustomId,
    DiscordButtonStyle Style = DiscordButtonStyle.Primary);

public sealed record ControlPlaneDecisionCard(
    ControlPlaneCardSource Source,
    string GoalId,
    string Kind,
    string TaskOrGateGeneration,
    string CauseFingerprint,
    string Title,
    string Body,
    DateTimeOffset RaisedAt,
    bool IsResolved = false,
    bool IsBoardIntegrity = false,
    IReadOnlyList<ControlPlaneAction>? Actions = null)
{
    public string DedupKey =>
        $"{NormalizeKeyPart(GoalId)}:{NormalizeKeyPart(Kind)}:{NormalizeKeyPart(TaskOrGateGeneration)}:{NormalizeKeyPart(CauseFingerprint)}";

    public IReadOnlyList<ControlPlaneAction> ActionList => Actions ?? [];

    public static ControlPlaneDecisionCard FromCollaborationItem(CollaborationItem item)
    {
        var goalId = string.IsNullOrWhiteSpace(item.GoalId) ? "orchestrator" : item.GoalId!;
        var generation = string.IsNullOrWhiteSpace(item.CorrelationKey) ? item.Id : item.CorrelationKey!;
        var fingerprint = ComputeFingerprint($"{item.Type}|{item.Subject}|{item.Body}");
        return new ControlPlaneDecisionCard(
            ControlPlaneCardSource.CollaborationDecision,
            goalId,
            item.Type.ToString(),
            generation,
            fingerprint,
            item.Subject,
            item.Body,
            item.RaisedAt,
            CollaborationItemLifecycle.IsTerminal(item.Status),
            IsBoardIntegrityKind(item.Type.ToString()));
    }

    public static string ComputeFingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))[..16].ToLowerInvariant();

    private static bool IsBoardIntegrityKind(string kind) =>
        kind.Equals("BoardWedge", StringComparison.OrdinalIgnoreCase) ||
        kind.Equals("BoardIntegrity", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeKeyPart(string value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();
}

public sealed record ControlPlaneBoardSnapshot(
    bool LoopAlive,
    TimeSpan TickAge,
    int ActiveLanes,
    int HeldLanes,
    int EscalatedLanes,
    DateTimeOffset ObservedAt);

public sealed record ControlPlaneBacklogDigestItem(
    string Id,
    string Title,
    BacklogItemStatus Status,
    DateTimeOffset UpdatedAt,
    string? SourceGoalId)
{
    public static ControlPlaneBacklogDigestItem FromBacklogItem(BacklogItem item) =>
        new(item.Id, item.Title, item.Status, item.UpdatedAt, item.SourceGoalId);
}

public sealed record ControlPlaneDeliveryPolicy(
    int DailyDecisionBudget = 6,
    int SystemicMergeThreshold = 3,
    TimeSpan? StormWindow = null,
    TimeSpan? BoardHeartbeatCadence = null,
    TimeSpan? DigestCadence = null,
    TimeSpan? BacklogDigestCadence = null,
    TimeSpan? ReminderCadence = null,
    int QuietHoursStartHour = 22,
    int QuietHoursEndHour = 7,
    DateTimeOffset? MutedUntil = null)
{
    public TimeSpan EffectiveStormWindow => StormWindow ?? TimeSpan.FromMinutes(30);
    public TimeSpan EffectiveBoardHeartbeatCadence => BoardHeartbeatCadence ?? TimeSpan.FromMinutes(10);
    public TimeSpan EffectiveDigestCadence => DigestCadence ?? TimeSpan.FromHours(4);
    public TimeSpan EffectiveBacklogDigestCadence => BacklogDigestCadence ?? TimeSpan.FromHours(24);
    public TimeSpan EffectiveReminderCadence => ReminderCadence ?? TimeSpan.FromHours(24);

    public bool IsMuted(DateTimeOffset now) => MutedUntil is not null && now < MutedUntil.Value;

    public bool IsQuietHours(DateTimeOffset now)
    {
        var hour = now.Hour;
        return QuietHoursStartHour <= QuietHoursEndHour
            ? hour >= QuietHoursStartHour && hour < QuietHoursEndHour
            : hour >= QuietHoursStartHour || hour < QuietHoursEndHour;
    }
}

public sealed record SystemicStormDeliveryState(string Kind, string DedupKey);

public sealed record ControlPlaneDeliveryMark(
    string DedupKey,
    ControlPlaneDeliveryChannel Channel,
    ulong MessageId,
    DateTimeOffset FirstDeliveredAt,
    DateTimeOffset LastDeliveredAt,
    DateTimeOffset? LastReminderAt,
    string ContentHash,
    bool Resolved);

public sealed record ControlPlanePushReceipt(
    ControlPlaneDeliveryChannel Channel,
    string DedupKey,
    DateTimeOffset PushedAt,
    ControlPlaneDeliveryOperationKind Kind);

public sealed record ControlPlaneDeliveryOperation(
    ControlPlaneDeliveryOperationKind Kind,
    ControlPlaneDeliveryChannel Channel,
    string DedupKey,
    ulong? MessageId,
    string Reason,
    string Content);

public sealed record ControlPlaneDeliveryBatchResult(
    IReadOnlyList<ControlPlaneDeliveryOperation> Operations)
{
    public int Count(ControlPlaneDeliveryChannel channel, ControlPlaneDeliveryOperationKind kind) =>
        Operations.Count(operation => operation.Channel == channel && operation.Kind == kind);
}

public sealed record ControlPlaneReplayReport(
    int Decisions,
    int Board,
    int Digest,
    int Suppressed,
    IReadOnlyList<ControlPlaneDeliveryOperation> Operations)
{
    public string FormatCounts() =>
        $"#decisions={Decisions} #board={Board} #digest={Digest} suppressed={Suppressed}";
}

public static class ControlPlaneMuteControl
{
    public static bool TryParseMuteCommand(string command, DateTimeOffset now, out DateTimeOffset mutedUntil)
    {
        mutedUntil = default;
        var parts = command.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !parts[0].Equals("/mute", StringComparison.OrdinalIgnoreCase))
            return false;

        if (parts[1].EndsWith("h", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[1][..^1], out var hours) &&
            hours > 0)
        {
            mutedUntil = now.AddHours(hours);
            return true;
        }

        return false;
    }
}
