using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DiscordControlPlaneMessageTransportOptions
{
    public bool NotificationsEnabled { get; init; }

    public string? BotToken { get; init; }

    public Uri? ApiBaseUri { get; init; }

    public IReadOnlyDictionary<ControlPlaneDeliveryChannel, ulong> ChannelIds { get; init; } =
        new Dictionary<ControlPlaneDeliveryChannel, ulong>();

    public static DiscordControlPlaneMessageTransportOptions FromEnvironment()
    {
        var enabled = bool.TryParse(
            Environment.GetEnvironmentVariable("DISCORD_CONTROL_PLANE_NOTIFICATIONS_ENABLED"),
            out var parsedEnabled) && parsedEnabled;
        var apiBaseUrl = Environment.GetEnvironmentVariable("DISCORD_API_BASE_URL");
        return new DiscordControlPlaneMessageTransportOptions
        {
            NotificationsEnabled = enabled,
            BotToken = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN"),
            ApiBaseUri = string.IsNullOrWhiteSpace(apiBaseUrl) ? null : new Uri(apiBaseUrl, UriKind.Absolute),
            ChannelIds = ReadChannelIdsFromEnvironment()
        };
    }

    private static IReadOnlyDictionary<ControlPlaneDeliveryChannel, ulong> ReadChannelIdsFromEnvironment()
    {
        var ids = new Dictionary<ControlPlaneDeliveryChannel, ulong>();
        Add(ids, ControlPlaneDeliveryChannel.Decisions, "DISCORD_CONTROL_PLANE_DECISIONS_CHANNEL_ID");
        Add(ids, ControlPlaneDeliveryChannel.Board, "DISCORD_CONTROL_PLANE_BOARD_CHANNEL_ID");
        Add(ids, ControlPlaneDeliveryChannel.Digest, "DISCORD_CONTROL_PLANE_DIGEST_CHANNEL_ID");
        return ids;

        static void Add(
            Dictionary<ControlPlaneDeliveryChannel, ulong> ids,
            ControlPlaneDeliveryChannel channel,
            string name)
        {
            if (ulong.TryParse(Environment.GetEnvironmentVariable(name), out var id) && id > 0)
                ids[channel] = id;
        }
    }
}

public sealed class DiscordControlPlaneMessageTransport : IControlPlaneMessageTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly HttpClient _http;
    private readonly DiscordControlPlaneMessageTransportOptions _options;
    private readonly Uri _apiBaseUri;
    private readonly ConcurrentDictionary<string, Lazy<Task<ulong>>> _correlatedMessages = new(StringComparer.OrdinalIgnoreCase);

    public DiscordControlPlaneMessageTransport(
        HttpClient http,
        DiscordControlPlaneMessageTransportOptions? options = null)
    {
        _http = http;
        _options = options ?? new DiscordControlPlaneMessageTransportOptions();
        _apiBaseUri = EnsureTrailingSlash(
            _options.ApiBaseUri ??
            _http.BaseAddress ??
            new Uri("https://discord.com/api/v10/", UriKind.Absolute));
    }

    public async Task<ulong> SendOrEditAsync(
        string correlationKey,
        ControlPlaneDeliveryChannel channel,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default)
    {
        if (!_options.NotificationsEnabled)
            return 0;

        var key = NormalizeCorrelationKey(correlationKey);
        var createMessage = new Lazy<Task<ulong>>(
            () => SendAsync(channel, content, buttons, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var cachedCreate = _correlatedMessages.GetOrAdd(key, createMessage);
        ulong messageId;
        try
        {
            messageId = await cachedCreate.Value;
        }
        catch
        {
            if (ReferenceEquals(cachedCreate, createMessage))
                _correlatedMessages.TryRemove(key, out _);
            throw;
        }

        if (!ReferenceEquals(cachedCreate, createMessage))
        {
            await EditAsync(channel, messageId, content, buttons, cancellationToken);
            return messageId;
        }

        return messageId;
    }

    public async Task<ulong> SendAsync(
        ControlPlaneDeliveryChannel channel,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default)
    {
        if (!_options.NotificationsEnabled)
            return 0;

        var channelId = ChannelId(channel);
        using var request = JsonRequest(
            HttpMethod.Post,
            $"channels/{channelId}/messages",
            new DiscordMessagePayload(content, BuildComponents(buttons)));
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        return ReadMessageId(body);
    }

    public async Task EditAsync(
        ControlPlaneDeliveryChannel channel,
        ulong messageId,
        string content,
        IReadOnlyList<DiscordButtonDefinition> buttons,
        CancellationToken cancellationToken = default)
    {
        if (!_options.NotificationsEnabled)
            return;

        var channelId = ChannelId(channel);
        using var request = JsonRequest(
            HttpMethod.Patch,
            $"channels/{channelId}/messages/{messageId}",
            new DiscordMessagePayload(content, BuildComponents(buttons)));
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage JsonRequest(HttpMethod method, string relativePath, DiscordMessagePayload payload)
    {
        var request = new HttpRequestMessage(method, new Uri(_apiBaseUri, relativePath))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", RequiredBotToken());
        return request;
    }

    private string RequiredBotToken() =>
        string.IsNullOrWhiteSpace(_options.BotToken)
            ? throw new InvalidOperationException("Discord control-plane transport requires a bot token when notifications are enabled.")
            : _options.BotToken.Trim();

    private ulong ChannelId(ControlPlaneDeliveryChannel channel) =>
        _options.ChannelIds.TryGetValue(channel, out var id) && id > 0
            ? id
            : throw new InvalidOperationException($"Discord control-plane transport requires a channel id for {channel}.");

    private static ulong ReadMessageId(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("id", out var idElement))
            throw new InvalidOperationException("Discord create-message response did not include an id.");

        return idElement.ValueKind == JsonValueKind.String && ulong.TryParse(idElement.GetString(), out var id)
            ? id
            : idElement.GetUInt64();
    }

    private static IReadOnlyList<DiscordActionRowPayload> BuildComponents(
        IReadOnlyList<DiscordButtonDefinition> buttons) =>
        buttons.Count == 0
            ? []
            : [new DiscordActionRowPayload(1, buttons.Take(5).Select(ButtonPayload).ToList())];

    private static DiscordButtonPayload ButtonPayload(DiscordButtonDefinition button) =>
        new(
            2,
            button.Style switch
            {
                DiscordButtonStyle.Secondary => 2,
                DiscordButtonStyle.Success => 3,
                DiscordButtonStyle.Danger => 4,
                _ => 1
            },
            button.Label,
            button.CustomId,
            button.Disabled);

    private static string NormalizeCorrelationKey(string correlationKey) =>
        string.IsNullOrWhiteSpace(correlationKey)
            ? throw new ArgumentException("Correlation key is required.", nameof(correlationKey))
            : correlationKey.Trim();

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        var value = uri.AbsoluteUri;
        return value.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(value + "/", UriKind.Absolute);
    }

    private sealed record DiscordMessagePayload(
        string Content,
        IReadOnlyList<DiscordActionRowPayload> Components);

    private sealed record DiscordActionRowPayload(
        int Type,
        IReadOnlyList<DiscordButtonPayload> Components);

    private sealed record DiscordButtonPayload(
        int Type,
        int Style,
        string Label,
        [property: JsonPropertyName("custom_id")] string CustomId,
        bool Disabled);
}
