using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DiscordInteractionResult(
    OperatorDecision? Decision,
    bool RequiresConfirmation,
    string? ConfirmationCustomId,
    string? ErrorMessage);

public sealed record DiscordClarificationAnswerModalRequest(
    string CorrelationKey,
    string ModalCustomId,
    string TextInputCustomId);

public sealed record DiscordClarificationAnswerSubmit(
    string CorrelationKey,
    string Answer,
    string UserId,
    string InteractionId,
    string? ErrorMessage);

public static class DiscordInteractionHandler
{
    private const string DirectPrefix = "mcgo|";
    private const string ConfirmPrefix = "mcgo-confirm|";
    private const string ConfirmedPrefix = "mcgo-confirmed|";
    private const string AnswerPrefix = "mcgo-answer|";
    private const string AnswerModalPrefix = "mcgo-modal|";
    public const string AnswerTextInputCustomId = "answer";

    public static DiscordInteractionResult Process(
        string interactionJson,
        IReadOnlyList<string> allowedUserIds)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(interactionJson);
        }
        catch (JsonException)
        {
            return Error("Invalid interaction payload.");
        }

        if (root is null)
            return Error("Empty interaction payload.");

        var userId = root["member"]?["user"]?["id"]?.GetValue<string>()
            ?? root["user"]?["id"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(userId))
            return Error("Interaction missing user id.");

        var customId = root["data"]?["custom_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(customId))
            return Error("Interaction missing custom_id.");

        var interactionId = root["id"]?.GetValue<string>() ?? customId;

        return Process(customId, userId, interactionId, allowedUserIds);
    }

    public static DiscordInteractionResult Process(
        string customId,
        string userId,
        string interactionId,
        IReadOnlyList<string> allowedUserIds)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Error("Interaction missing user id.");

        if (!allowedUserIds.Contains(userId, StringComparer.Ordinal))
            return Error($"User '{userId}' is not in the operator allowlist.");

        if (string.IsNullOrWhiteSpace(customId))
            return Error("Interaction missing custom_id.");

        if (customId.StartsWith(ConfirmedPrefix, StringComparison.Ordinal))
        {
            var rest = customId[ConfirmedPrefix.Length..];
            var (inboxItemId, command) = SplitCustomIdParts(rest);
            if (inboxItemId is null || command is null)
                return Error($"Malformed confirmed custom_id: {customId}");

            return new DiscordInteractionResult(
                new OperatorDecision(inboxItemId, command, null, $"discord:{userId}", interactionId),
                RequiresConfirmation: false,
                ConfirmationCustomId: null,
                ErrorMessage: null);
        }

        if (customId.StartsWith(ConfirmPrefix, StringComparison.Ordinal))
        {
            var rest = customId[ConfirmPrefix.Length..];
            var (inboxItemId, command) = SplitCustomIdParts(rest);
            if (inboxItemId is null || command is null)
                return Error($"Malformed confirm custom_id: {customId}");

            var confirmedId = ConfirmedPrefix + rest;
            return new DiscordInteractionResult(
                Decision: null,
                RequiresConfirmation: true,
                ConfirmationCustomId: confirmedId,
                ErrorMessage: null);
        }

        if (customId.StartsWith(DirectPrefix, StringComparison.Ordinal))
        {
            var rest = customId[DirectPrefix.Length..];
            var (inboxItemId, command) = SplitCustomIdParts(rest);
            if (inboxItemId is null || command is null)
                return Error($"Malformed direct custom_id: {customId}");

            return new DiscordInteractionResult(
                new OperatorDecision(inboxItemId, command, null, $"discord:{userId}", interactionId),
                RequiresConfirmation: false,
                ConfirmationCustomId: null,
                ErrorMessage: null);
        }

        return Error($"Unrecognised custom_id prefix: {customId}");
    }

    public static DiscordClarificationAnswerModalRequest? TryBuildAnswerModalRequest(
        string customId,
        string userId,
        IReadOnlyList<string> allowedUserIds,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!customId.StartsWith(AnswerPrefix, StringComparison.Ordinal))
            return null;

        if (string.IsNullOrWhiteSpace(userId))
        {
            errorMessage = "Interaction missing user id.";
            return null;
        }

        if (!allowedUserIds.Contains(userId, StringComparer.Ordinal))
        {
            errorMessage = $"User '{userId}' is not in the operator allowlist.";
            return null;
        }

        var correlationKey = customId[AnswerPrefix.Length..];
        if (string.IsNullOrWhiteSpace(correlationKey))
        {
            errorMessage = $"Malformed answer custom_id: {customId}";
            return null;
        }

        return new DiscordClarificationAnswerModalRequest(
            correlationKey,
            AnswerModalPrefix + correlationKey,
            AnswerTextInputCustomId);
    }

    public static DiscordClarificationAnswerSubmit ProcessAnswerModalSubmit(
        string modalCustomId,
        string answer,
        string userId,
        string interactionId,
        IReadOnlyList<string> allowedUserIds)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return new DiscordClarificationAnswerSubmit(string.Empty, string.Empty, userId, interactionId, "Interaction missing user id.");

        if (!allowedUserIds.Contains(userId, StringComparer.Ordinal))
            return new DiscordClarificationAnswerSubmit(string.Empty, string.Empty, userId, interactionId, $"User '{userId}' is not in the operator allowlist.");

        if (!modalCustomId.StartsWith(AnswerModalPrefix, StringComparison.Ordinal))
            return new DiscordClarificationAnswerSubmit(string.Empty, string.Empty, userId, interactionId, $"Unrecognised modal custom_id prefix: {modalCustomId}");

        var correlationKey = modalCustomId[AnswerModalPrefix.Length..];
        if (string.IsNullOrWhiteSpace(correlationKey))
            return new DiscordClarificationAnswerSubmit(string.Empty, string.Empty, userId, interactionId, $"Malformed modal custom_id: {modalCustomId}");

        if (string.IsNullOrWhiteSpace(answer))
            return new DiscordClarificationAnswerSubmit(correlationKey, string.Empty, userId, interactionId, "Clarification answer cannot be empty.");

        return new DiscordClarificationAnswerSubmit(correlationKey, answer.Trim(), userId, interactionId, null);
    }

    public static string BuildDirectCustomId(string inboxItemId, string command) =>
        $"{DirectPrefix}{inboxItemId}|{command}";

    public static string BuildAnswerCustomId(string correlationKey) =>
        $"{AnswerPrefix}{correlationKey}";

    public static string BuildConfirmCustomId(string inboxItemId, string command) =>
        $"{ConfirmPrefix}{inboxItemId}|{command}";

    private static (string? InboxItemId, string? Command) SplitCustomIdParts(string rest)
    {
        var sep = rest.IndexOf('|');
        if (sep < 0)
            return (null, null);
        return (rest[..sep], rest[(sep + 1)..]);
    }

    private static DiscordInteractionResult Error(string message) =>
        new(null, false, null, message);
}
