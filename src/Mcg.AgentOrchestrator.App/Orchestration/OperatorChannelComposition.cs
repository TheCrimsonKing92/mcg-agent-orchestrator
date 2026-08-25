using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OperatorChannelComposition
{
    internal static IOperatorChannel Create(
        OperatorChannelCatalog catalog,
        string? botToken,
        string stateDirectory)
    {
        if (!IsDiscordConfigured(catalog, botToken))
            return NullOperatorChannel.Instance;

        return OperatorChannelFactory.Create(
            catalog,
            botToken,
            CollaborationItemStore.ForDirectory(stateDirectory),
            BuildGoalStateVersionReader(stateDirectory));
    }

    internal static IOperatorChannel CreateWithApi(
        OperatorChannelCatalog catalog,
        IDiscordForumApi api,
        string stateDirectory)
    {
        if (catalog.IsNull ||
            !catalog.ChannelType.Equals("discord", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(catalog.ForumChannelId) ||
            !ulong.TryParse(catalog.ForumChannelId, out _))
        {
            return NullOperatorChannel.Instance;
        }

        return OperatorChannelFactory.CreateWithApi(
            catalog,
            api,
            CollaborationItemStore.ForDirectory(stateDirectory),
            BuildGoalStateVersionReader(stateDirectory));
    }

    internal static Func<string, CancellationToken, Task<long?>> BuildGoalStateVersionReader(string stateDirectory)
    {
        var stateDbPath = Path.Combine(stateDirectory, "state.db");
        return (goalId, cancellationToken) =>
            SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(stateDbPath, goalId, cancellationToken);
    }

    private static bool IsDiscordConfigured(OperatorChannelCatalog catalog, string? botToken) =>
        !catalog.IsNull &&
        catalog.ChannelType.Equals("discord", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(botToken) &&
        !string.IsNullOrWhiteSpace(catalog.ForumChannelId) &&
        ulong.TryParse(catalog.ForumChannelId, out _);
}
