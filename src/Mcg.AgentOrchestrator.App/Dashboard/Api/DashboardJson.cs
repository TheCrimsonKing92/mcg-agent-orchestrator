using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static class DashboardJson
{
    private static readonly JsonSerializerOptions SharedOptions = CreateOptions();

    public static JsonSerializerOptions Options()
    {
        return SharedOptions;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}


