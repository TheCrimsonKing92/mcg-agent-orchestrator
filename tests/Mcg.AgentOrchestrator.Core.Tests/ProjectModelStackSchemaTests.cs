using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: all snapshots and JSON documents are owned in memory by each test.
public sealed class ProjectModelStackSchemaTests
{
    [Fact]
    public void RoundTrip_GoStack_PreservesModelAndLowercaseStack()
    {
        var source = new FactSource("go.mod", 1);
        var model = new ProjectModel(ProjectModel.CurrentSchemaVersion, ".",
            [new("sample", new("sample", source, FactConfidence.High), new("go.mod", source, FactConfidence.High), new(false, source, FactConfidence.High))],
            [], [], [], [], [], [], [], new("main", source, FactConfidence.High), ProjectStack.Go);

        var json = ProjectModelJson.Serialize(model);
        var restored = ProjectModelJson.Deserialize(json);

        Assert.Contains("\"stack\": \"go\"", json);
        Assert.Equal(ProjectStack.Go, restored.Stack);
        Assert.Equal(model.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(model.RepositoryRoot, restored.RepositoryRoot);
        Assert.Equal(model.IntegrationBranch, restored.IntegrationBranch);
        Assert.Equal(model.Units, restored.Units);
        Assert.Equal(model.Dependencies, restored.Dependencies);
        Assert.Equal(model.TestSetups, restored.TestSetups);
        Assert.Equal(model.OwnerQuestions, restored.OwnerQuestions);
        Assert.Equal(model.Commands, restored.Commands);
        Assert.Equal(model.EnvironmentNeeds, restored.EnvironmentNeeds);
        Assert.Equal(model.Measurements, restored.Measurements);
        Assert.Equal(model.Hazards, restored.Hazards);
        Assert.Equal(json, ProjectModelJson.Serialize(restored));
    }

    [Fact]
    public void Deserialize_Schema4WithoutStack_DefaultsToDotnet()
    {
        const string legacy = """
            {"schemaVersion":4,"repositoryRoot":".","units":[],"dependencies":[],
             "testSetups":[],"ownerQuestions":[],"commands":[],"environmentNeeds":[],
             "measurements":[],"hazards":[]}
            """;
        Assert.False(JsonNode.Parse(legacy)!.AsObject().ContainsKey("stack"));

        var restored = ProjectModelJson.Deserialize(legacy);

        Assert.Equal(4, restored.SchemaVersion);
        Assert.Equal(ProjectStack.Dotnet, restored.Stack);
        Assert.Contains("\"stack\": \"dotnet\"", ProjectModelJson.Serialize(restored));
    }

    [Fact]
    public void CurrentSchemaVersion_StackAddition_Remains4() => Assert.Equal(4, ProjectModel.CurrentSchemaVersion);
}
