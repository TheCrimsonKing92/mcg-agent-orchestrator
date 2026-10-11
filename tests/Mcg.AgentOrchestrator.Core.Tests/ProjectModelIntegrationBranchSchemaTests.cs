using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: immutable snapshots and unique temporary files, with no shared state.
public sealed class ProjectModelIntegrationBranchSchemaTests
{
    [Fact]
    public void Schema4_Branch_RoundTripsValueSourceConfidenceAndBytes()
    {
        var model = Snapshot();
        var json = ProjectModelJson.Serialize(model);
        var restored = ProjectModelJson.Deserialize(json);

        Assert.Equal(4, ProjectModel.CurrentSchemaVersion);
        Assert.Equal(4, restored.SchemaVersion);
        Assert.NotNull(restored.IntegrationBranch);
        Assert.Equal("trunk", restored.IntegrationBranch.Value);
        Assert.Equal(model.IntegrationBranch!.Source, restored.IntegrationBranch.Source);
        Assert.Equal(FactConfidence.High, restored.IntegrationBranch.Confidence);
        Assert.Equal(json, ProjectModelJson.Serialize(restored));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacySnapshot_UpgradesWithoutBranchOrChangesToOtherFields(int version)
    {
        var legacy = LegacySnapshot(version);
        var restored = ProjectModelJson.Deserialize(legacy.ToJsonString());

        Assert.Equal(4, restored.SchemaVersion);
        Assert.Null(restored.IntegrationBranch);
        if (version == 2) Assert.Empty(restored.Hazards);
        var upgraded = JsonNode.Parse(ProjectModelJson.Serialize(restored))!.AsObject();
        foreach (var field in legacy.Where(field => field.Key != "schemaVersion"))
            Assert.True(JsonNode.DeepEquals(field.Value, upgraded[field.Key]), field.Key);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacySnapshot_LoadingPersistedBytes_DoesNotRewriteFile(int version)
    {
        var path = Path.Combine(Path.GetTempPath(), $"project-model-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, LegacySnapshot(version).ToJsonString());
            var before = File.ReadAllBytes(path);
            var restored = ProjectModelJson.Deserialize(File.ReadAllText(path));

            Assert.Equal(4, restored.SchemaVersion);
            Assert.Null(restored.IntegrationBranch);
            if (version == 2) Assert.Empty(restored.Hazards);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Schema4_MissingBranch_MeansNotLearned()
    {
        var document = JsonNode.Parse(ProjectModelJson.Serialize(Snapshot()))!.AsObject();
        document.Remove("integrationBranch");

        var restored = ProjectModelJson.Deserialize(document.ToJsonString());

        Assert.Equal(4, restored.SchemaVersion);
        Assert.Null(restored.IntegrationBranch);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacySnapshot_StrayBranch_IsDiscarded(int version)
    {
        var document = JsonNode.Parse(ProjectModelJson.Serialize(Snapshot()))!.AsObject();
        document["schemaVersion"] = version;
        if (version == 2) document.Remove("hazards");

        var restored = ProjectModelJson.Deserialize(document.ToJsonString());

        Assert.Equal(4, restored.SchemaVersion);
        Assert.Null(restored.IntegrationBranch);
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
    public void SupportedSchemas_MissingSnapshotList_FailsLoudly(string list)
    {
        foreach (var version in new[] { 2, 3, 4 })
        {
            if (version == 2 && list == "hazards") continue;
            var document = version == 4
                ? JsonNode.Parse(ProjectModelJson.Serialize(Snapshot()))!.AsObject()
                : LegacySnapshot(version);
            document.Remove(list);
            Assert.Throws<JsonException>(() => ProjectModelJson.Deserialize(document.ToJsonString()));
        }
    }

    private static JsonObject LegacySnapshot(int version)
    {
        var document = JsonNode.Parse(ProjectModelJson.Serialize(Snapshot()))!.AsObject();
        document["schemaVersion"] = version;
        document.Remove("integrationBranch");
        if (version == 2) document.Remove("hazards");
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
            [new("tests", "Serial", new("xunit:Serial", source, FactConfidence.High))],
            new("trunk", new(".git/refs/remotes/origin/HEAD", 1), FactConfidence.High));
    }
}
