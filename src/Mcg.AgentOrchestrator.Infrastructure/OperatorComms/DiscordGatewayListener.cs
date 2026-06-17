using Discord;
using Discord.WebSocket;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class DiscordGatewayListener : IAsyncDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly DiscordDecisionApplier _applier;
    private readonly IReadOnlyList<string> _allowedUserIds;

    private DiscordGatewayListener(
        DiscordSocketClient client,
        DiscordDecisionApplier applier,
        IReadOnlyList<string> allowedUserIds)
    {
        _client = client;
        _applier = applier;
        _allowedUserIds = allowedUserIds;
        _client.ButtonExecuted += OnButtonExecutedAsync;
    }

    public static async Task<DiscordGatewayListener> CreateAndConnectAsync(
        string botToken,
        DiscordDecisionApplier applier,
        IReadOnlyList<string> allowedUserIds)
    {
        var config = new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds };
        var client = new DiscordSocketClient(config);
        await client.LoginAsync(TokenType.Bot, botToken);
        await client.StartAsync();
        return new DiscordGatewayListener(client, applier, allowedUserIds);
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        // Acknowledge within Discord's 3-second window before doing any work.
        await component.DeferAsync(ephemeral: true);
        try
        {
            var customId = component.Data.CustomId;
            var userId = component.User.Id.ToString();
            var interactionId = component.Id.ToString();

            var result = DiscordInteractionHandler.Process(customId, userId, interactionId, _allowedUserIds);

            if (result.ErrorMessage is not null)
            {
                await component.DeleteOriginalResponseAsync();
                return;
            }

            if (result.RequiresConfirmation && result.ConfirmationCustomId is not null)
            {
                var builder = new ComponentBuilder();
                builder.WithButton("Confirm", result.ConfirmationCustomId, ButtonStyle.Danger);
                await component.FollowupAsync(
                    "Please confirm this action:",
                    components: builder.Build(),
                    ephemeral: true);
                await component.DeleteOriginalResponseAsync();
                return;
            }

            if (result.Decision is not null)
            {
                await _applier.ApplyAsync(result.Decision);
            }

            await component.DeleteOriginalResponseAsync();
        }
        catch
        {
            try { await component.DeleteOriginalResponseAsync(); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.ButtonExecuted -= OnButtonExecutedAsync;
        await _client.StopAsync();
        await _client.LogoutAsync();
        _client.Dispose();
    }
}
