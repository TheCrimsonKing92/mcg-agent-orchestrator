using System.Text.Json;

namespace Mcg.AgentOrchestrator.Core;

public static class ProjectModelJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Serialize(ProjectModel model) => JsonSerializer.Serialize(model, Options);

    public static ProjectModel Deserialize(string json) =>
        JsonSerializer.Deserialize<ProjectModel>(json, Options)
        ?? throw new JsonException("A project model cannot be null.");
}
