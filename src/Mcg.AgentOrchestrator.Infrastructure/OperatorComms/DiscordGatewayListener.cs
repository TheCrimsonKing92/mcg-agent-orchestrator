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
        _client.ModalSubmitted += OnModalSubmittedAsync;
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
        try
        {
            var customId = component.Data.CustomId;
            var userId = component.User.Id.ToString();
            var interactionId = component.Id.ToString();

            var modal = _view.TryBuildAnswerModalRequest(customId, userId, out var modalError);
            if (modalError is not null)
            {
                await component.RespondAsync(modalError, ephemeral: true);
                return;
            }

            if (modal is not null)
            {
                var builder = new ModalBuilder()
                    .WithTitle("Answer clarification")
                    .WithCustomId(modal.ModalCustomId)
                    .AddTextInput("Answer", modal.TextInputCustomId, TextInputStyle.Paragraph, required: true, maxLength: 1800);
                await component.RespondWithModalAsync(builder.Build());
                return;
            }

            // Acknowledge within Discord's 3-second window before doing any non-modal work.
            await component.DeferAsync(ephemeral: true);
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

    private async Task OnModalSubmittedAsync(SocketModal modal)
    {
        await modal.DeferAsync(ephemeral: true);
        try
        {
            var answer = modal.Data.Components
                .FirstOrDefault(component => component.CustomId == DiscordInteractionHandler.AnswerTextInputCustomId)
                ?.Value ?? string.Empty;
            var result = await _view.ApplyAnswerModalAsync(
                modal.Data.CustomId,
                answer,
                modal.User.Id.ToString(),
                modal.Id.ToString());

            await modal.FollowupAsync(result.ErrorMessage ?? "Answer recorded.", ephemeral: true);
        }
        catch
        {
            try { await modal.FollowupAsync("The clarification answer could not be recorded. The thread remains unresolved; retry the button.", ephemeral: true); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.ButtonExecuted -= OnButtonExecutedAsync;
        _client.ModalSubmitted -= OnModalSubmittedAsync;
        await _client.StopAsync();
        await _client.LogoutAsync();
        _client.Dispose();
    }
}
