using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>
/// Pushes conductor batch-loop tick events to a running dashboard process via HTTP POST.
/// Silently skips if no dashboard URL is configured or the dashboard is not reachable.
/// </summary>
internal static class ConductorTickPusher
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static string? TryReadDashboardUrl(string dashboardUrlFilePath)
    {
        try
        {
            if (!File.Exists(dashboardUrlFilePath))
                return null;
            var url = File.ReadAllText(dashboardUrlFilePath).Trim();
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch
        {
            return null;
        }
    }

    public static Action<BatchTickSummary> CreateCallback(string dashboardBaseUrl)
    {
        return tick => TryPush(dashboardBaseUrl, tick);
    }

    public static void TryPush(string dashboardBaseUrl, BatchTickSummary tick)
    {
        try
        {
            var dto = new
            {
                tick.Tick,
                tick.Advanced,
                tick.Held,
                tick.Escalated,
                tick.Retried,
                tick.Done,
                tick.WatchSleeping,
                progressLines = (IReadOnlyList<string>?)tick.ProgressLines ?? Array.Empty<string>()
            };
            var json = JsonSerializer.Serialize(dto);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = $"{dashboardBaseUrl.TrimEnd('/')}/api/conductor/tick";
            _http.PostAsync(url, content).GetAwaiter().GetResult();
        }
        catch
        {
            // Silently ignore: dashboard may not be running.
        }
    }
}
