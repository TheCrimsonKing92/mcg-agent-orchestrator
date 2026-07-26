using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DiscordInteractionResult(
    OperatorDecision? Decision,
    bool RequiresConfirmation,
    string? ConfirmationCustomId,
    string? ErrorMessage,
    string? AcknowledgementMessage = null);

public sealed record DiscordClarificationAnswerModalRequest(
    string CorrelationKey,
    string ModalCustomId,
    string TextInputCustomId);

public sealed record DiscordActionInputModalRequest(
    string CorrelationKey,
    int ActionIndex,
    string ModalCustomId,
    string TextInputCustomId);

public sealed record DiscordClarificationAnswerSubmit(
    string CorrelationKey,
    string Answer,
    string UserId,
    string InteractionId,
    string? ErrorMessage);

public sealed record DiscordActionInputSubmit(
    OperatorDecision? Decision,
    string? ErrorMessage);

public static class DiscordInteractionHandler
{
    private const string DirectPrefix = "mcgo|";
    private const string ConfirmPrefix = "mcgo-confirm|";
    private const string ConfirmedPrefix = "mcgo-confirmed|";
    private const string AnswerPrefix = "mcgo-answer|";
    private const string AnswerModalPrefix = "mcgo-modal|";
    private const string ActionInputPrefix = "mcgo-input|";
    private const string ActionInputModalPrefix = "mcgo-input-modal|";
    public const string AnswerTextInputCustomId = "answer";
    public const string ActionInputTextInputCustomId = "action-input";

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
            var (inboxItemId, actionIndex) = SplitActionCustomIdParts(rest);
            if (inboxItemId is null || actionIndex is null)
                return Error($"Malformed confirmed custom_id: {customId}");

            return new DiscordInteractionResult(
                new OperatorDecision(inboxItemId, actionIndex.Value, null, $"discord:{userId}", interactionId),
                RequiresConfirmation: false,
                ConfirmationCustomId: null,
                ErrorMessage: null);
        }

        if (customId.StartsWith(ConfirmPrefix, StringComparison.Ordinal))
        {
            var rest = customId[ConfirmPrefix.Length..];
            var (inboxItemId, actionIndex) = SplitActionCustomIdParts(rest);
            if (inboxItemId is null || actionIndex is null)
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
            var (inboxItemId, actionIndex) = SplitActionCustomIdParts(rest);
            if (inboxItemId is null || actionIndex is null)
                return Error($"Malformed direct custom_id: {customId}");

            return new DiscordInteractionResult(
                new OperatorDecision(inboxItemId, actionIndex.Value, null, $"discord:{userId}", interactionId),
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

    public static DiscordActionInputModalRequest? TryBuildActionInputModalRequest(
        string customId,
        string userId,
        IReadOnlyList<string> allowedUserIds,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!customId.StartsWith(ActionInputPrefix, StringComparison.Ordinal))
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

        var rest = customId[ActionInputPrefix.Length..];
        var (correlationKey, actionIndex) = SplitActionCustomIdParts(rest);
        if (correlationKey is null || actionIndex is null)
        {
            errorMessage = $"Malformed action input custom_id: {customId}";
            return null;
        }

        return new DiscordActionInputModalRequest(
            correlationKey,
            actionIndex.Value,
            ActionInputModalPrefix + rest,
            ActionInputTextInputCustomId);
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

    public static DiscordActionInputSubmit ProcessActionInputModalSubmit(
        string modalCustomId,
        string modalText,
        string userId,
        string interactionId,
        IReadOnlyList<string> allowedUserIds)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return new DiscordActionInputSubmit(null, "Interaction missing user id.");

        if (!allowedUserIds.Contains(userId, StringComparer.Ordinal))
            return new DiscordActionInputSubmit(null, $"User '{userId}' is not in the operator allowlist.");

        if (!modalCustomId.StartsWith(ActionInputModalPrefix, StringComparison.Ordinal))
            return new DiscordActionInputSubmit(null, $"Unrecognised modal custom_id prefix: {modalCustomId}");

        var rest = modalCustomId[ActionInputModalPrefix.Length..];
        var (correlationKey, actionIndex) = SplitActionCustomIdParts(rest);
        if (correlationKey is null || actionIndex is null)
            return new DiscordActionInputSubmit(null, $"Malformed action input modal custom_id: {modalCustomId}");

        if (string.IsNullOrWhiteSpace(modalText))
            return new DiscordActionInputSubmit(null, "Action input cannot be empty.");

        return new DiscordActionInputSubmit(
            new OperatorDecision(correlationKey, actionIndex.Value, modalText, $"discord:{userId}", interactionId),
            null);
    }

    public static string BuildDirectCustomId(string inboxItemId, int actionIndex) =>
        $"{DirectPrefix}{inboxItemId}|{actionIndex}";

    public static string BuildActionInputCustomId(string inboxItemId, int actionIndex) =>
        $"{ActionInputPrefix}{inboxItemId}|{actionIndex}";

    public static string BuildAnswerCustomId(string correlationKey) =>
        $"{AnswerPrefix}{correlationKey}";

    public static string BuildConfirmCustomId(string inboxItemId, int actionIndex) =>
        $"{ConfirmPrefix}{inboxItemId}|{actionIndex}";

    internal static bool TryReadActionReference(
        string customId,
        out string correlationKey,
        out int? actionIndex)
    {
        correlationKey = string.Empty;
        actionIndex = null;
        string rest;
        if (customId.StartsWith(ConfirmedPrefix, StringComparison.Ordinal))
        {
            rest = customId[ConfirmedPrefix.Length..];
        }
        else if (customId.StartsWith(ActionInputModalPrefix, StringComparison.Ordinal))
        {
            rest = customId[ActionInputModalPrefix.Length..];
        }
        else if (customId.StartsWith(ActionInputPrefix, StringComparison.Ordinal))
        {
            rest = customId[ActionInputPrefix.Length..];
        }
        else if (customId.StartsWith(ConfirmPrefix, StringComparison.Ordinal))
        {
            rest = customId[ConfirmPrefix.Length..];
        }
        else if (customId.StartsWith(DirectPrefix, StringComparison.Ordinal))
        {
            rest = customId[DirectPrefix.Length..];
        }
        else
        {
            return false;
        }

        var parsed = SplitActionCustomIdParts(rest);
        if (parsed.InboxItemId is null)
            return false;
        correlationKey = parsed.InboxItemId;
        actionIndex = parsed.ActionIndex;
        return true;
    }

    private static (string? InboxItemId, int? ActionIndex) SplitActionCustomIdParts(string rest)
    {
        var sep = rest.IndexOf('|');
        if (sep < 0)
            return (null, null);
        var inboxItemId = rest[..sep];
        var indexText = rest[(sep + 1)..];
        return string.IsNullOrWhiteSpace(inboxItemId) ||
            !int.TryParse(indexText, out var actionIndex) ||
            actionIndex < 0
            ? (null, null)
            : (inboxItemId, actionIndex);
    }

    private static DiscordInteractionResult Error(string message) =>
        new(null, false, null, message);
}
