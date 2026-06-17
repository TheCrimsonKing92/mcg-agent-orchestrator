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
        DiscordDecisionApplier applier)
    {
        if (!IsDiscordConfigured(catalog, botToken, out _))
            return null;

        var allowedUserIds = catalog.OperatorUserIds ?? [];
        return DiscordGatewayListener.CreateAndConnectAsync(botToken!, applier, allowedUserIds)
            .GetAwaiter().GetResult();
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
