using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: immutable snapshots and uniquely owned temporary files.
public sealed class ProjectModelHazardSchemaTests
{
    [Fact]
    public void RoundTrip_HighAndUnknownHazards_PreservesEveryField()
    {
        var model = Snapshot();
        var json = ProjectModelJson.Serialize(model);
        var restored = ProjectModelJson.Deserialize(json);
        Assert.Equal(4, ProjectModel.CurrentSchemaVersion);
        Assert.Equal(4, restored.SchemaVersion);
        Assert.Equal(json, ProjectModelJson.Serialize(restored));
        Assert.Equal(2, restored.Hazards.Count);
        for (var index = 0; index < model.Hazards.Count; index++)
        {
            Assert.Equal(model.Hazards[index].UnitId, restored.Hazards[index].UnitId);
            Assert.Equal(model.Hazards[index].Name, restored.Hazards[index].Name);
            Assert.Equal(model.Hazards[index].IsolationKey.Value, restored.Hazards[index].IsolationKey.Value);
            Assert.Equal(model.Hazards[index].IsolationKey.Source, restored.Hazards[index].IsolationKey.Source);
            Assert.Equal(model.Hazards[index].IsolationKey.Confidence, restored.Hazards[index].IsolationKey.Confidence);
        }
        Assert.Equal("", restored.Hazards[1].IsolationKey.Value);
        Assert.Equal(FactConfidence.Low, restored.Hazards[1].IsolationKey.Confidence);
    }

    [Fact]
    public void Schema2_AllExistingFacts_UpgradesWithoutChangingOtherLists()
    {
        var legacy = LegacySnapshot();
        var restored = ProjectModelJson.Deserialize(legacy.ToJsonString());
        Assert.Equal(4, restored.SchemaVersion);
        Assert.Empty(restored.Hazards);
        var upgraded = JsonNode.Parse(ProjectModelJson.Serialize(restored))!.AsObject();
        foreach (var field in legacy.Where(field => field.Key != "schemaVersion"))
            Assert.True(JsonNode.DeepEquals(field.Value, upgraded[field.Key]), field.Key);
    }

    [Fact]
    public void Schema2_LoadingPersistedBytes_DoesNotRewriteFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-model-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, LegacySnapshot().ToJsonString());
            var before = File.ReadAllBytes(path);
            var model = ProjectModelJson.Deserialize(File.ReadAllText(path));
            Assert.Equal(4, model.SchemaVersion);
            Assert.Empty(model.Hazards);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void UnsupportedVersion_CompleteSnapshot_ThrowsSchemaError(int version)
    {
        var document = JsonNode.Parse(ProjectModelJson.Serialize(Snapshot()))!.AsObject();
        document["schemaVersion"] = version;
        var exception = Assert.Throws<JsonException>(() => ProjectModelJson.Deserialize(document.ToJsonString()));
        Assert.Equal("A project model requires schema version 4 and all snapshot lists.", exception.Message);
    }

    [Theory]
    [InlineData("units")]
    [InlineData("dependencies")]
    [InlineData("testSetups")]
    [InlineData("ownerQuestions")]
    [InlineData("commands")]
    [InlineData("environmentNeeds")]
    [InlineData("measurements")]
    [InlineData("hazards")]
    public void Schema3_MissingList_FailsLoudly(string list)
    {
        var document = JsonNode.Parse(ProjectModelJson.Serialize(Snapshot()))!.AsObject();
        document.Remove(list);
        Assert.Throws<JsonException>(() => ProjectModelJson.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void Hazard_InvalidIdentityOrFact_Throws()
    {
        var key = new ProjectFact<string>("", new("Tests/Collections.cs", 2), FactConfidence.Low);
        Assert.Throws<ArgumentException>(() => new SharedStateHazard("", "name", key));
        Assert.Throws<ArgumentException>(() => new SharedStateHazard("tests", " ", key));
        Assert.Throws<ArgumentNullException>(() => new SharedStateHazard("tests", "name", null!));
        Assert.True(File.Exists(Path.Combine(VerifiedRepositoryRoot.Find(), "src",
            "Mcg.AgentOrchestrator.Core", "Onboarding", "SharedStateHazard.cs")));
    }

    private static JsonObject LegacySnapshot()
    {
        var document = JsonNode.Parse(ProjectModelJson.Serialize(Snapshot()))!.AsObject();
        document["schemaVersion"] = 2;
        document.Remove("hazards");
        return document;
    }

    private static ProjectModel Snapshot()
    {
        var source = new FactSource("Tests/Collections.cs", 7);
        var measured = new FactSource(measurementReference: "measurement:tests");
        return new ProjectModel(ProjectModel.CurrentSchemaVersion, ".",
            [new("tests", new("Tests", source, FactConfidence.High), new("Tests.csproj", source, FactConfidence.High),
                new(true, source, FactConfidence.High))],
            [new("tests", "library", source, FactConfidence.High)],
            [new("tests", new("xunit", source, FactConfidence.High), new("mtp", source, FactConfidence.High))],
            [new("tests/runner", "Confirm runner", source)],
            [new("tests", new("dotnet build Tests.csproj", source, FactConfidence.High),
                new("dotnet test Tests.csproj", source, FactConfidence.High))],
            [new("dotnet-sdk", new("10.0.100", source, FactConfidence.High))],
            [new("tests", new(1.5, measured, FactConfidence.High), new(2.5, measured, FactConfidence.High))],
            [new("tests", "Serial", new("xunit:Serial", source, FactConfidence.High)),
                new("tests", "Names.Computed", new("", new("Tests/Collections.cs", 11), FactConfidence.Low))]);
    }
}
