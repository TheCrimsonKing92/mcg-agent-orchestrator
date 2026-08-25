using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DiscordProgressUpdateResult(
    bool Unchanged,
    bool CreatedThread,
    bool SentMessage,
    bool EditedMessage,
    ulong? ThreadId,
    ulong? MessageId);

public sealed class DiscordProgressViewService
{
    private const string ProgressThreadTitle = "Progress";
    private readonly IDiscordForumApi _api;
    private readonly ulong _forumChannelId;
    private readonly string _catalogPath;

    public DiscordProgressViewService(
        IDiscordForumApi api,
        ulong forumChannelId,
        string catalogPath)
    {
        _api = api;
        _forumChannelId = forumChannelId;
        _catalogPath = catalogPath;
    }

    public async Task<DiscordProgressUpdateResult> ReconcileAsync(
        StatusProjection projection,
        CancellationToken cancellationToken = default)
    {
        if (DiscordOperatorFaultClassifier.IsAuthDisabledForProcess)
            return Skipped();

        try
        {
            return await ReconcileCoreAsync(projection, cancellationToken);
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested &&
            DiscordOperatorFaultClassifier.IsAuthError(ex))
        {
            DiscordOperatorFaultClassifier.DisableForProcess("progress view", ex);
            return Skipped();
        }
    }

    private async Task<DiscordProgressUpdateResult> ReconcileCoreAsync(
        StatusProjection projection,
        CancellationToken cancellationToken)
    {
        var catalog = OperatorChannelStore.Load(_catalogPath);
        var content = TruncateContent(projection.RenderedContent);
        var contentHash = Hash(content);
        if (string.Equals(catalog.ProgressStatusContentHash, contentHash, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(catalog.ProgressThreadId) &&
            !string.IsNullOrWhiteSpace(catalog.ProgressStatusMessageId))
        {
            return new DiscordProgressUpdateResult(
                Unchanged: true,
                CreatedThread: false,
                SentMessage: false,
                EditedMessage: false,
                ParseNullableUInt64(catalog.ProgressThreadId),
                ParseNullableUInt64(catalog.ProgressStatusMessageId));
        }

        var createdThread = false;
        var sentMessage = false;
        var editedMessage = false;

        var threadId = ParseNullableUInt64(catalog.ProgressThreadId);
        if (threadId is null)
        {
            threadId = await _api.CreateThreadAsync(
                _forumChannelId,
                ProgressThreadTitle,
                "Progress view initialized. The live status appears below and is edited in place.",
                cancellationToken);
            createdThread = true;
        }

        var messageId = ParseNullableUInt64(catalog.ProgressStatusMessageId);
        if (messageId is null)
        {
            messageId = await _api.SendMessageAsync(
                threadId.Value,
                content,
                [],
                cancellationToken);
            sentMessage = true;
        }
        else
        {
            await _api.EditMessageAsync(
                threadId.Value,
                messageId.Value,
                content,
                [],
                cancellationToken);
            editedMessage = true;
        }

        OperatorChannelStore.Save(
            _catalogPath,
            catalog with
            {
                ProgressThreadId = threadId.Value.ToString(),
                ProgressStatusMessageId = messageId.Value.ToString(),
                ProgressStatusContentHash = contentHash
            });

        return new DiscordProgressUpdateResult(
            Unchanged: false,
            createdThread,
            sentMessage,
            editedMessage,
            threadId,
            messageId);
    }

    private static DiscordProgressUpdateResult Skipped() =>
        new(
            Unchanged: true,
            CreatedThread: false,
            SentMessage: false,
            EditedMessage: false,
            ThreadId: null,
            MessageId: null);

    private static ulong? ParseNullableUInt64(string? value) =>
        ulong.TryParse(value, out var parsed) ? parsed : null;

    private static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static string TruncateContent(string content)
    {
        const int limit = DiscordCollaborationViewService.DiscordMessageLimit;
        if (content.Length <= limit)
            return content;

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var kept = lines.Length;
        while (kept > 0)
        {
            var omitted = lines[kept..];
            var omittedGoals = omitted.Count(line => line.StartsWith("- ", StringComparison.Ordinal));
            var indicator =
                $"_…truncated: {omittedGoals} goal(s) / {omitted.Length} line(s) omitted to fit Discord's {limit}-character limit._";
            var candidate = string.Join('\n', lines[..kept]).TrimEnd() + Environment.NewLine + indicator;
            if (candidate.Length <= limit)
                return candidate;
            kept--;
        }

        return $"_…truncated: content omitted to fit Discord's {limit}-character limit._";
    }
}
