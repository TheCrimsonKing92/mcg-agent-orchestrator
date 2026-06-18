namespace Mcg.AgentOrchestrator.Infrastructure;

public static class OperatorChannelFactory
{
    public static IOperatorChannel Create(
        OperatorChannelCatalog catalog,
        string? botToken,
        string stateDirectory)
    {
        if (!IsDiscordConfigured(catalog, botToken, out var forumChannelId))
            return NullOperatorChannel.Instance;

        var api = DiscordNetForumApi.CreateAsync(botToken!).GetAwaiter().GetResult();
        return new DiscordOperatorChannel(api, forumChannelId, stateDirectory, catalog.DashboardBaseUrl);
    }

    public static IOperatorChannel CreateWithApi(
        OperatorChannelCatalog catalog,
        IDiscordForumApi api,
        string stateDirectory)
    {
        if (catalog.IsNull ||
            !catalog.ChannelType.Equals("discord", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(catalog.ForumChannelId) ||
            !ulong.TryParse(catalog.ForumChannelId, out var forumChannelId))
            return NullOperatorChannel.Instance;

        return new DiscordOperatorChannel(api, forumChannelId, stateDirectory, catalog.DashboardBaseUrl);
    }

    public static DiscordGatewayListener? CreateGatewayListener(
        OperatorChannelCatalog catalog,
        string? botToken,
        ICollaborationItemStore store,
        string stateDirectory)
    {
        if (!IsDiscordConfigured(catalog, botToken, out var forumChannelId))
            return null;

        var allowedUserIds = catalog.OperatorUserIds ?? [];
        var api = DiscordNetForumApi.CreateAsync(botToken!).GetAwaiter().GetResult();
        var view = new DiscordCollaborationViewService(store, api, forumChannelId, stateDirectory, allowedUserIds);
        return DiscordGatewayListener.CreateAndConnectAsync(botToken!, view)
            .GetAwaiter().GetResult();
    }

    public static async Task SendTestEscalationAsync(IOperatorChannel channel, TextWriter output)
    {
        if (channel is NullOperatorChannel)
        {
            await output.WriteLineAsync("operator-channel test: channel not configured or bot token (MCGO_DISCORD_BOT_TOKEN) missing.");
            await output.WriteLineAsync("  To configure: operator-channel set discord --forum-channel-id <id> --operator-user-id <id> [--dashboard-url <url>]");
            await output.WriteLineAsync("  To set token: export MCGO_DISCORD_BOT_TOKEN=<token>");
            return;
        }

        var testEscalation = new OperatorEscalation(
            $"test-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            "test-goal-id",
            "testgoal",
            "OperatorChannelTest",
            "[Test] Operator channel verification",
            "This is a test escalation. If you see this in Discord, the outbound channel is working correctly.",
            "Sent from: operator-channel test",
            [new OperatorEscalationAction("Check Status (safe)", "doctor", RequiresConfirm: false)],
            null);

        await channel.SendEscalationAsync(testEscalation);
        await output.WriteLineAsync($"Test escalation sent via {channel.ChannelType}. Check the Discord forum channel for the new thread.");
    }

    private static bool IsDiscordConfigured(OperatorChannelCatalog catalog, string? botToken, out ulong forumChannelId)
    {
        forumChannelId = 0;
        return !catalog.IsNull &&
               catalog.ChannelType.Equals("discord", StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(botToken) &&
               !string.IsNullOrWhiteSpace(catalog.ForumChannelId) &&
               ulong.TryParse(catalog.ForumChannelId, out forumChannelId);
    }
}
