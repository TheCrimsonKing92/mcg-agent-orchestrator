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

    public static ProjectModel Deserialize(string json)
    {
        var model = JsonSerializer.Deserialize<ProjectModel>(json, Options)
            ?? throw new JsonException("A project model cannot be null.");
        if (model.SchemaVersion == 2)
            model = model with { SchemaVersion = ProjectModel.CurrentSchemaVersion, Hazards = [], IntegrationBranch = null };
        else if (model.SchemaVersion == 3)
            model = model with { SchemaVersion = ProjectModel.CurrentSchemaVersion, IntegrationBranch = null };
        if (model.SchemaVersion != ProjectModel.CurrentSchemaVersion || model.Units is null ||
            model.Dependencies is null || model.TestSetups is null || model.OwnerQuestions is null ||
            model.Commands is null || model.EnvironmentNeeds is null || model.Measurements is null || model.Hazards is null)
            throw new JsonException($"A project model requires schema version {ProjectModel.CurrentSchemaVersion} and all snapshot lists.");
        return model;
    }
}
