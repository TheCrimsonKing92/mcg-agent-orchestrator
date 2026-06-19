using Discord;
using Discord.Rest;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class DiscordNetForumApi : IDiscordForumApi, IAsyncDisposable
{
    private readonly DiscordRestClient _client;

    private DiscordNetForumApi(DiscordRestClient client)
    {
        _client = client;
    }

    public static async Task<DiscordNetForumApi> CreateAsync(string botToken)
    {
        var client = new DiscordRestClient();
        await client.LoginAsync(TokenType.Bot, botToken);
        return new DiscordNetForumApi(client);
    }

    public async Task<ulong> CreateThreadAsync(
        ulong forumChannelId,
        string title,
        string initialContent,
        CancellationToken cancellationToken = default)
    {
        var channel = await _client.GetChannelAsync(forumChannelId) as IForumChannel
            ?? throw new InvalidOperationException(
                $"Channel {forumChannelId} is not a Discord forum channel.");

        var post = await channel.CreatePostAsync(
            title,
            ThreadArchiveDuration.OneWeek,
            text: initialContent);

        return post.Id;
    }

    public async Task<ulong> SendMessageAsync(
        ulong threadId,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default)
    {
        var channel = await _client.GetChannelAsync(threadId) as IMessageChannel
            ?? throw new InvalidOperationException(
                $"Thread {threadId} could not be retrieved as a message channel.");

        MessageComponent? components = null;
        if (buttons.Count > 0)
        {
            var builder = new ComponentBuilder();
            foreach (var button in buttons.Take(5))
            {
                var style = button.Style switch
                {
                    DiscordButtonStyle.Danger => ButtonStyle.Danger,
                    DiscordButtonStyle.Success => ButtonStyle.Success,
                    DiscordButtonStyle.Secondary => ButtonStyle.Secondary,
                    _ => ButtonStyle.Primary
                };
                builder.WithButton(button.Label, button.CustomId, style, disabled: button.Disabled);
            }
            components = builder.Build();
        }

        var message = await channel.SendMessageAsync(content, components: components);
        return message.Id;
    }

    public async Task EditMessageAsync(
        ulong threadId,
        ulong messageId,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default)
    {
        var channel = await _client.GetChannelAsync(threadId) as IMessageChannel
            ?? throw new InvalidOperationException(
                $"Thread {threadId} could not be retrieved as a message channel.");

        MessageComponent? components = null;
        if (buttons.Count > 0)
        {
            var builder = new ComponentBuilder();
            foreach (var button in buttons.Take(5))
            {
                var style = button.Style switch
                {
                    DiscordButtonStyle.Danger => ButtonStyle.Danger,
                    DiscordButtonStyle.Success => ButtonStyle.Success,
                    DiscordButtonStyle.Secondary => ButtonStyle.Secondary,
                    _ => ButtonStyle.Primary
                };
                builder.WithButton(button.Label, button.CustomId, style, disabled: button.Disabled);
            }
            components = builder.Build();
        }

        var message = await channel.GetMessageAsync(messageId) as IUserMessage
            ?? throw new InvalidOperationException(
                $"Message {messageId} could not be retrieved in thread {threadId}.");

        await message.ModifyAsync(properties =>
        {
            properties.Content = content;
            properties.Components = components;
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _client.LogoutAsync();
        _client.Dispose();
    }
}
