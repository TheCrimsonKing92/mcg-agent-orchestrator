using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DiscordInteractionResult(
    OperatorDecision? Decision,
    bool RequiresConfirmation,
    string? ConfirmationCustomId,
    string? ErrorMessage);

public static class DiscordInteractionHandler
{
    private const string DirectPrefix = "mcgo|";
    private const string ConfirmPrefix = "mcgo-confirm|";
    private const string ConfirmedPrefix = "mcgo-confirmed|";

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

        if (!allowedUserIds.Contains(userId, StringComparer.Ordinal))
            return Error($"User '{userId}' is not in the operator allowlist.");

        var customId = root["data"]?["custom_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(customId))
            return Error("Interaction missing custom_id.");

        var idempotencyKey = root["id"]?.GetValue<string>() ?? customId;

        if (customId.StartsWith(ConfirmedPrefix, StringComparison.Ordinal))
        {
            var rest = customId[ConfirmedPrefix.Length..];
            var (inboxItemId, command) = SplitCustomIdParts(rest);
            if (inboxItemId is null || command is null)
                return Error($"Malformed confirmed custom_id: {customId}");

            return new DiscordInteractionResult(
                new OperatorDecision(inboxItemId, command, null, $"discord:{userId}", idempotencyKey),
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
                new OperatorDecision(inboxItemId, command, null, $"discord:{userId}", idempotencyKey),
                RequiresConfirmation: false,
                ConfirmationCustomId: null,
                ErrorMessage: null);
        }

        return Error($"Unrecognised custom_id prefix: {customId}");
    }

    public static string BuildDirectCustomId(string inboxItemId, string command) =>
        $"{DirectPrefix}{inboxItemId}|{command}";

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
