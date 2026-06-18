using Discord;
using Discord.WebSocket;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class DiscordGatewayListener : IAsyncDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly DiscordCollaborationViewService _view;

    private DiscordGatewayListener(
        DiscordSocketClient client,
        DiscordCollaborationViewService view)
    {
        _client = client;
        _view = view;
        _client.ButtonExecuted += OnButtonExecutedAsync;
    }

    public static async Task<DiscordGatewayListener> CreateAndConnectAsync(
        string botToken,
        DiscordCollaborationViewService view)
    {
        var config = new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds };
        var client = new DiscordSocketClient(config);
        await client.LoginAsync(TokenType.Bot, botToken);
        await client.StartAsync();
        await view.ReconcileAsync();
        return new DiscordGatewayListener(client, view);
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

            var result = await _view.ApplyInteractionAsync(customId, userId, interactionId);

            if (result.ErrorMessage is not null)
            {
                await component.FollowupAsync(result.ErrorMessage, ephemeral: true);
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
                return;
            }
            await component.FollowupAsync("Resolved.", ephemeral: true);
        }
        catch
        {
            try { await component.FollowupAsync("The interaction could not be applied. The thread remains unresolved; retry the button.", ephemeral: true); } catch { }
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
