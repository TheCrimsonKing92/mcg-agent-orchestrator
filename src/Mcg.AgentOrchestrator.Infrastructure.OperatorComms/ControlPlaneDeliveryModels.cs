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

    public static string ComputeFingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())))[..16].ToLowerInvariant();

    private static string NormalizeKeyPart(string value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToLowerInvariant();
}

public interface IControlPlaneMessageTransport
{
    Task<ulong> SendAsync(
        ControlPlaneDeliveryChannel channel,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default);

    Task EditAsync(
        ControlPlaneDeliveryChannel channel,
        ulong messageId,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default);
}

public sealed class RecordingControlPlaneMessageTransport : IControlPlaneMessageTransport
{
    private ulong _nextMessageId = 1000;

    public List<(ControlPlaneDeliveryChannel Channel, string Content, IReadOnlyList<DiscordButtonDefinition> Buttons)> Sent { get; } = [];

    public List<(ControlPlaneDeliveryChannel Channel, ulong MessageId, string Content, IReadOnlyList<DiscordButtonDefinition> Buttons)> Edited { get; } = [];

    public Task<ulong> SendAsync(
        ControlPlaneDeliveryChannel channel,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default)
    {
        Sent.Add((channel, content, buttons));
        return Task.FromResult(++_nextMessageId);
    }

    public Task EditAsync(
        ControlPlaneDeliveryChannel channel,
        ulong messageId,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default)
    {
        Edited.Add((channel, messageId, content, buttons));
        return Task.CompletedTask;
    }
}
