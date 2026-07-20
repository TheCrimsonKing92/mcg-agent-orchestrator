using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OperatorInboxControlPlaneProjection
{
    public static ControlPlaneDecisionCard Project(OperatorInboxItem item)
    {
        var generation = item.TaskId ?? item.TaskNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "gate";
        var fingerprint = ControlPlaneDecisionCard.ComputeFingerprint($"{item.Source}|{item.Message}|{item.Evidence}");
        return new ControlPlaneDecisionCard(
            ControlPlaneCardSource.OperatorInboxEscalation,
            item.GoalId,
            item.Kind.ToString(),
            generation,
            fingerprint,
            item.Title,
            $"{item.Message}\nEvidence: {item.Evidence}\nSuggested action: {item.SuggestedAction}",
            item.AcknowledgedAt ?? DateTimeOffset.UtcNow,
            item.Acknowledged,
            IsBoardIntegrityKind(item.Kind),
            [new ControlPlaneAction("Acknowledge", $"operator-inbox-ack:{item.Id}", DiscordButtonStyle.Secondary)]);
    }

    private static bool IsBoardIntegrityKind(OperatorInboxKind kind) =>
        kind is OperatorInboxKind.MissingVerification or OperatorInboxKind.AcceptanceGate;
}
