using System.Net;

namespace Mcg.AgentOrchestrator.App.Providers;

internal static class LocalInferenceProbe
{
    internal static TimeSpan? ConnectTimeoutFor(Uri modelsUrl)
    {
        if (modelsUrl.IsLoopback
            || string.Equals(modelsUrl.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(modelsUrl.IdnHost, out var address) && IPAddress.IsLoopback(address)))
        {
            return TimeSpan.FromMilliseconds(500);
        }

        return null;
    }

    internal static bool IsReachable(string modelsUrl)
    {
        try
        {
            using var handler = new SocketsHttpHandler();
            if (ConnectTimeoutFor(new Uri(modelsUrl)) is { } connectTimeout)
            {
                handler.ConnectTimeout = connectTimeout;
            }

            using var probe = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var response = probe.GetAsync(modelsUrl).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
