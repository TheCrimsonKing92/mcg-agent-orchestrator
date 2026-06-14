using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record OperatorChannelCatalog(
    string ChannelType,
    string? DashboardBaseUrl = null)
{
    public static OperatorChannelCatalog Default() => new("null");

    public bool IsNull =>
        string.IsNullOrEmpty(ChannelType) ||
        ChannelType.Equals("null", StringComparison.OrdinalIgnoreCase);
}

public static class OperatorChannelStore
{
    public static OperatorChannelCatalog Load(string path)
    {
        if (!File.Exists(path))
            return OperatorChannelCatalog.Default();

        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<OperatorChannelCatalog>(text, JsonOptions())
                ?? OperatorChannelCatalog.Default();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return OperatorChannelCatalog.Default();
        }
    }

    public static void Save(string path, OperatorChannelCatalog catalog)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(catalog, JsonOptions()));
    }

    private static JsonSerializerOptions JsonOptions() =>
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
}
