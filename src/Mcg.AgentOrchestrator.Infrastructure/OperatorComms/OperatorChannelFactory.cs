namespace Mcg.AgentOrchestrator.Infrastructure;

public static class OperatorChannelFactory
{
    public sealed record DiscordOperatorRuntime(
        DiscordGatewayListener Listener,
        DiscordCollaborationViewService CollaborationView,
        DiscordProgressViewService ProgressView,
        IAsyncDisposable Api) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Listener.DisposeAsync();
            await Api.DisposeAsync();
        }
    }

    // Resolves the Discord bot token from the process environment, falling back on Windows to the
    // persistent User-scoped variable. The harness/process tree may have started before the operator
    // set it, so it's absent from the process env yet present in the User registry; this lets a bare
    // `operator-listen` find the token without an inline env-assignment prefix (which trips approvals).
    public static string? ResolveBotToken()
    {
        var token = Environment.GetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token) && OperatingSystem.IsWindows())
        {
            token = Environment.GetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN", EnvironmentVariableTarget.User);
        }

        return token;
    }

    public static IOperatorChannel Create(
        OperatorChannelCatalog catalog,
        string? botToken,
        string stateDirectory)
    {
        if (!IsDiscordConfigured(catalog, botToken, out _))
            return NullOperatorChannel.Instance;

        return new DiscordOperatorChannel(
            CollaborationItemStore.ForDirectory(stateDirectory),
            BuildGoalStateVersionReader(stateDirectory));
    }

    public static IOperatorChannel CreateWithApi(
        OperatorChannelCatalog catalog,
        IDiscordForumApi api,
        string stateDirectory)
    {
        if (catalog.IsNull ||
            !catalog.ChannelType.Equals("discord", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(catalog.ForumChannelId) ||
            !ulong.TryParse(catalog.ForumChannelId, out _))
            return NullOperatorChannel.Instance;

        return new DiscordOperatorChannel(
            CollaborationItemStore.ForDirectory(stateDirectory),
            BuildGoalStateVersionReader(stateDirectory));
    }

    public static DiscordGatewayListener? CreateGatewayListener(
        OperatorChannelCatalog catalog,
        string? botToken,
        ICollaborationItemStore store,
        string stateDirectory,
        Func<string, string, CancellationToken, Task<bool>>? resolveClarificationAnswer = null,
        Func<string, CancellationToken, Task>? dispatchAction = null)
    {
        if (!IsDiscordConfigured(catalog, botToken, out var forumChannelId))
            return null;

        var allowedUserIds = catalog.OperatorUserIds ?? [];
        var api = DiscordNetForumApi.CreateAsync(botToken!).GetAwaiter().GetResult();
        var view = new DiscordCollaborationViewService(
            store,
            api,
            forumChannelId,
            stateDirectory,
            allowedUserIds,
            resolveClarificationAnswer,
            BuildCorrelationGoalStateVersionReader(store, stateDirectory),
            dispatchAction);
        var heartbeatOptions = BuildDeadManHeartbeatOptions(catalog);
        var heartbeat = CreateDeadManHeartbeatClient(heartbeatOptions);
        return DiscordGatewayListener.CreateAndConnectAsync(botToken!, view, heartbeat, heartbeatOptions.EffectiveInterval)
            .GetAwaiter().GetResult();
    }

    public static DiscordOperatorRuntime? CreateDiscordRuntime(
        OperatorChannelCatalog catalog,
        string catalogPath,
        string? botToken,
        ICollaborationItemStore store,
        string stateDirectory,
        Func<string, string, CancellationToken, Task<bool>>? resolveClarificationAnswer = null,
        Func<string, CancellationToken, Task>? dispatchAction = null)
    {
        if (!IsDiscordConfigured(catalog, botToken, out var forumChannelId))
            return null;

        var allowedUserIds = catalog.OperatorUserIds ?? [];
        var api = DiscordNetForumApi.CreateAsync(botToken!).GetAwaiter().GetResult();
        var collaborationView = new DiscordCollaborationViewService(
            store,
            api,
            forumChannelId,
            stateDirectory,
            allowedUserIds,
            resolveClarificationAnswer,
            BuildCorrelationGoalStateVersionReader(store, stateDirectory),
            dispatchAction);
        var heartbeatOptions = BuildDeadManHeartbeatOptions(catalog);
        var heartbeat = CreateDeadManHeartbeatClient(heartbeatOptions);
        var listener = DiscordGatewayListener.CreateAndConnectAsync(botToken!, collaborationView, heartbeat, heartbeatOptions.EffectiveInterval)
            .GetAwaiter().GetResult();
        var progressView = new DiscordProgressViewService(api, forumChannelId, catalogPath);
        return new DiscordOperatorRuntime(listener, collaborationView, progressView, api);
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
        await output.WriteLineAsync($"Test escalation queued via {channel.ChannelType}. The operator-listen collaboration view will render it in Discord.");
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

    private static Func<string, CancellationToken, Task<long?>> BuildGoalStateVersionReader(string stateDirectory)
    {
        var stateDbPath = Path.Combine(stateDirectory, "state.db");
        return (goalId, cancellationToken) =>
            SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(stateDbPath, goalId, cancellationToken);
    }

    public static DeadManHeartbeatOptions BuildDeadManHeartbeatOptions(OperatorChannelCatalog catalog)
    {
        if (!catalog.DeadManHeartbeatEnabled ||
            string.IsNullOrWhiteSpace(catalog.DeadManHeartbeatUrl) ||
            !Uri.TryCreate(catalog.DeadManHeartbeatUrl, UriKind.Absolute, out var endpoint))
        {
            return new DeadManHeartbeatOptions();
        }

        return new DeadManHeartbeatOptions(Enabled: true, Endpoint: endpoint);
    }

    private static DeadManHeartbeatClient? CreateDeadManHeartbeatClient(DeadManHeartbeatOptions options) =>
        options.Enabled && options.Endpoint is not null
            ? new DeadManHeartbeatClient(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, options)
            : null;

    private static Func<string, CancellationToken, Task<long?>> BuildCorrelationGoalStateVersionReader(
        ICollaborationItemStore store,
        string stateDirectory)
    {
        var goalVersionReader = BuildGoalStateVersionReader(stateDirectory);
        return async (correlationKey, cancellationToken) =>
        {
            var item = (await store.ListAsync(null, cancellationToken))
                .FirstOrDefault(candidate => string.Equals(candidate.CorrelationKey, correlationKey, StringComparison.Ordinal));
            return string.IsNullOrWhiteSpace(item?.GoalId)
                ? null
                : await goalVersionReader(item.GoalId, cancellationToken);
        };
    }
}
