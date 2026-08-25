namespace Mcg.AgentOrchestrator.Infrastructure;

public enum DiscordButtonStyle
{
    Primary = 1,
    Secondary = 2,
    Success = 3,
    Danger = 4
}

public sealed record DiscordButtonDefinition(
    string Label,
    string CustomId,
    DiscordButtonStyle Style = DiscordButtonStyle.Primary,
    bool Disabled = false);

public interface IDiscordForumApi
{
    Task<ulong> CreateThreadAsync(
        ulong forumChannelId,
        string title,
        string initialContent,
        CancellationToken cancellationToken = default);

    Task<ulong> SendMessageAsync(
        ulong threadId,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default);

    Task EditMessageAsync(
        ulong threadId,
        ulong messageId,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default);
}
