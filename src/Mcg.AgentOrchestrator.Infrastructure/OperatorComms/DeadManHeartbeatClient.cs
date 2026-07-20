using System.Net.Http.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DeadManHeartbeatOptions(
    bool Enabled = false,
    Uri? Endpoint = null,
    TimeSpan? Interval = null)
{
    public TimeSpan EffectiveInterval => Interval ?? TimeSpan.FromMinutes(5);
}

public sealed class DeadManHeartbeatClient
{
    private readonly HttpClient _http;
    private readonly DeadManHeartbeatOptions _options;

    public DeadManHeartbeatClient(HttpClient http, DeadManHeartbeatOptions? options = null)
    {
        _http = http;
        _options = options ?? new DeadManHeartbeatOptions();
    }

    public async Task<bool> SendAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _options.Endpoint is null)
            return false;

        using var response = await _http.PostAsJsonAsync(
            _options.Endpoint,
            new { kind = "operator-listener-heartbeat", ok = true },
            cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
