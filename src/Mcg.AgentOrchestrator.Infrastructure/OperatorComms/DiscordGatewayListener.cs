using Discord;
using Discord.WebSocket;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class DiscordGatewayListener : IAsyncDisposable
{
    private readonly DiscordSocketClient _client;
    private readonly DiscordCollaborationViewService _view;
    private readonly CancellationTokenSource _deadManHeartbeatCts = new();
    private readonly Task? _deadManHeartbeatTask;

    private DiscordGatewayListener(
        DiscordSocketClient client,
        DiscordCollaborationViewService view,
        DeadManHeartbeatClient? deadManHeartbeat,
        TimeSpan deadManHeartbeatInterval)
    {
        _client = client;
        _view = view;
        _client.ButtonExecuted += OnButtonExecutedAsync;
        _client.ModalSubmitted += OnModalSubmittedAsync;
        _deadManHeartbeatTask = deadManHeartbeat is null
            ? null
            : RunDeadManHeartbeatLoopAsync(deadManHeartbeat, deadManHeartbeatInterval, _deadManHeartbeatCts.Token);
    }

    public static async Task<DiscordGatewayListener> CreateAndConnectAsync(
        string botToken,
        DiscordCollaborationViewService view,
        DeadManHeartbeatClient? deadManHeartbeat = null,
        TimeSpan? deadManHeartbeatInterval = null)
    {
        var config = new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds };
        var client = new DiscordSocketClient(config);
        await client.LoginAsync(TokenType.Bot, botToken);
        await client.StartAsync();
        await view.ReconcileAsync();
        return new DiscordGatewayListener(
            client,
            view,
            deadManHeartbeat,
            deadManHeartbeatInterval ?? TimeSpan.FromMinutes(5));
    }

    private Task OnButtonExecutedAsync(SocketMessageComponent component) =>
        HandleButtonExecutedAsync(
            _view,
            new ButtonInteractionContext(
                component.Data.CustomId,
                component.User.Id.ToString(),
                component.Id.ToString(),
                async (message, ephemeral) => await component.RespondAsync(message, ephemeral: ephemeral),
                async modal => await component.RespondWithModalAsync(modal),
                async ephemeral => await component.DeferAsync(ephemeral: ephemeral),
                async (message, ephemeral) => await component.FollowupAsync(message, ephemeral: ephemeral),
                async (message, confirmationCustomId, ephemeral) =>
                {
                    var builder = new ComponentBuilder();
                    builder.WithButton("Confirm", confirmationCustomId, ButtonStyle.Danger);
                    await component.FollowupAsync(
                        message,
                        components: builder.Build(),
                        ephemeral: ephemeral);
                }));

    internal static async Task HandleButtonExecutedAsync(
        DiscordCollaborationViewService view,
        ButtonInteractionContext component)
    {
        try
        {
            var customId = component.CustomId;
            var userId = component.UserId;
            var interactionId = component.InteractionId;

            var modal = view.TryBuildAnswerModalRequest(customId, userId, out var modalError);
            if (modalError is not null)
            {
                await component.RespondAsync(modalError, true);
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

            var actionInput = view.TryBuildActionInputModalRequest(customId, userId, out var actionInputError);
            if (actionInputError is not null)
            {
                await view.RecordRejectedActionReferenceAsync(
                    customId,
                    userId,
                    interactionId,
                    actionInputError);
                await component.RespondAsync(actionInputError, true);
                return;
            }

            if (actionInput is not null)
            {
                var builder = new ModalBuilder()
                    .WithTitle("Provide action input")
                    .WithCustomId(actionInput.ModalCustomId)
                    .AddTextInput("Input", actionInput.TextInputCustomId, TextInputStyle.Paragraph, required: true, maxLength: 1800);
                await component.RespondWithModalAsync(builder.Build());
                return;
            }

            // Acknowledge within Discord's 3-second window before doing any non-modal work.
            await component.DeferAsync(true);
            var result = await view.ApplyInteractionAsync(customId, userId, interactionId);

            if (result.ErrorMessage is not null)
            {
                await component.FollowupAsync(result.ErrorMessage, true);
                return;
            }

            if (result.RequiresConfirmation && result.ConfirmationCustomId is not null)
            {
                await component.FollowupWithConfirmationAsync(
                    "Please confirm this action:",
                    result.ConfirmationCustomId,
                    true);
                return;
            }
            await component.FollowupAsync(result.AcknowledgementMessage ?? "Applied.", true);
        }
        catch
        {
            try
            {
                await component.FollowupAsync(
                    "The interaction could not be applied. The thread remains unresolved; retry the button.",
                    true);
            }
            catch { }
        }
    }

    internal sealed record ButtonInteractionContext(
        string customId,
        string userId,
        string interactionId,
        Func<string, bool, Task> RespondAsync,
        Func<Modal, Task> RespondWithModalAsync,
        Func<bool, Task> DeferAsync,
        Func<string, bool, Task> FollowupAsync,
        Func<string, string, bool, Task> FollowupWithConfirmationAsync)
    {
        public string CustomId { get; } = customId;
        public string UserId { get; } = userId;
        public string InteractionId { get; } = interactionId;
    }

    private async Task OnModalSubmittedAsync(SocketModal modal)
    {
        await modal.DeferAsync(ephemeral: true);
        try
        {
            var answer = modal.Data.Components
                .FirstOrDefault(component => component.CustomId == DiscordInteractionHandler.AnswerTextInputCustomId)
                ?.Value ?? string.Empty;
            if (modal.Data.CustomId.StartsWith("mcgo-input-modal|", StringComparison.Ordinal))
            {
                var modalText = modal.Data.Components
                    .FirstOrDefault(component => component.CustomId == DiscordInteractionHandler.ActionInputTextInputCustomId)
                    ?.Value ?? string.Empty;
                var actionResult = await _view.ApplyActionInputModalAsync(
                    modal.Data.CustomId,
                    modalText,
                    modal.User.Id.ToString(),
                    modal.Id.ToString());
                await modal.FollowupAsync(
                    actionResult.ErrorMessage ?? actionResult.AcknowledgementMessage ?? "Applied.",
                    ephemeral: true);
                return;
            }

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
        await _deadManHeartbeatCts.CancelAsync();
        if (_deadManHeartbeatTask is not null)
        {
            try { await _deadManHeartbeatTask; } catch (OperationCanceledException) { }
        }

        _client.ButtonExecuted -= OnButtonExecutedAsync;
        _client.ModalSubmitted -= OnModalSubmittedAsync;
        await _client.StopAsync();
        await _client.LogoutAsync();
        _client.Dispose();
        _deadManHeartbeatCts.Dispose();
    }

    private static async Task RunDeadManHeartbeatLoopAsync(
        DeadManHeartbeatClient heartbeat,
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await heartbeat.SendAsync(cancellationToken); }
            catch when (!cancellationToken.IsCancellationRequested) { }

            await Task.Delay(interval, cancellationToken);
        }
    }
}
