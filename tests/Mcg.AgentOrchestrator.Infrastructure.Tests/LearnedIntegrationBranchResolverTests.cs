using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;
using static InfrastructureTestSupport;

// Parallel-safe: each test owns its temporary model files.
public sealed class LearnedIntegrationBranchResolverTests : IDisposable
{
    private readonly string directory = CreateTempDirectory();
    private string ModelPath => Path.Combine(directory, "project-model.json");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_HighConfidenceWithBlankRegistry_UsesLearnedBranch(string? registry)
    {
        WriteModel("master", FactConfidence.High);

        Assert.Equal(new IntegrationBranchResolution("master", IntegrationBranchResolution.Learned, null),
            LearnedIntegrationBranchResolver.Resolve(registry, true, ModelPath));
    }

    [Theory]
    [InlineData("trunk", true)]
    [InlineData(" trunk ", true)]
    [InlineData("master", false)]
    public void Resolve_RegistryWins_ReportsOnlyHighConfidenceMismatch(string registry, bool mismatch)
    {
        WriteModel("master", FactConfidence.High);

        var resolution = LearnedIntegrationBranchResolver.Resolve(registry, true, ModelPath);

        Assert.Equal(registry.Trim(), resolution.Branch);
        Assert.Equal(IntegrationBranchResolution.Registry, resolution.Source);
        if (mismatch)
        {
            Assert.Contains("trunk", resolution.Note);
            Assert.Contains("master", resolution.Note);
            Assert.Contains("using the registry value", resolution.Note);
            AssertOneLine(resolution.Note);
        }
        else Assert.Null(resolution.Note);
    }

    [Theory]
    [InlineData("medium")]
    [InlineData("low")]
    [InlineData("null-branch")]
    [InlineData("missing")]
    [InlineData("missing-directory")]
    [InlineData("json")]
    [InlineData("schema")]
    [InlineData("invalid-fact")]
    [InlineData("unreadable")]
    [InlineData("whitespace")]
    [InlineData("padding")]
    [InlineData("null-value")]
    public void Resolve_UnusableModel_FallsBackWithOneLineNote(string scenario)
    {
        var path = ModelPath;
        switch (scenario)
        {
            case "medium": WriteModel("master", FactConfidence.Medium); break;
            case "low": WriteModel("master", FactConfidence.Low); break;
            case "null-branch": WriteModel(null, FactConfidence.High); break;
            case "missing": break;
            case "missing-directory": path = Path.Combine(directory, "absent", "project-model.json"); break;
            case "json": File.WriteAllText(path, "{not json"); break;
            case "unreadable": path = directory; break;
            case "whitespace": WriteModel("mas ter", FactConfidence.High); break;
            case "padding": WriteModel(" master ", FactConfidence.High); break;
            default:
                WriteModel("master", FactConfidence.High);
                var json = JsonNode.Parse(File.ReadAllText(path))!;
                if (scenario == "schema") json["schemaVersion"] = -1;
                else if (scenario == "null-value") json["integrationBranch"]!["value"] = null;
                else json["integrationBranch"]!["source"] = null;
                File.WriteAllText(path, json.ToJsonString());
                break;
        }

        var resolution = LearnedIntegrationBranchResolver.Resolve(null, true, path);

        Assert.Equal("main", resolution.Branch);
        Assert.Equal(IntegrationBranchResolution.Default, resolution.Source);
        AssertOneLine(resolution.Note);
    }

    [Theory]
    [InlineData(null, "main", IntegrationBranchResolution.Default)]
    [InlineData("trunk", "trunk", IntegrationBranchResolution.Registry)]
    public void Resolve_OptInOff_IgnoresMissingAndExistingModels(string? registry, string branch, string source)
    {
        var expected = new IntegrationBranchResolution(branch, source, null);
        Assert.Equal(expected, LearnedIntegrationBranchResolver.Resolve(registry, false, ModelPath));
        WriteModel("master", FactConfidence.High);
        Assert.Equal(expected, LearnedIntegrationBranchResolver.Resolve(registry, false, ModelPath));
        // An invalid path would throw on file access; the disabled path must not open it.
        Assert.Equal(expected, LearnedIntegrationBranchResolver.Resolve(registry, false, "\0"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("json")]
    [InlineData("medium")]
    [InlineData("low")]
    [InlineData("null")]
    public void Resolve_RegistryWithUnusableModel_HasNoNote(string scenario)
    {
        if (scenario == "json") File.WriteAllText(ModelPath, "{not json");
        else if (scenario != "missing") WriteModel(scenario == "null" ? null : "master",
            scenario == "medium" ? FactConfidence.Medium : FactConfidence.Low);

        Assert.Equal(new IntegrationBranchResolution("trunk", IntegrationBranchResolution.Registry, null),
            LearnedIntegrationBranchResolver.Resolve("trunk", true, ModelPath));
    }

    [Fact]
    public void Resolve_RegistryMismatchWithMultilineModel_KeepsNoteOnOneLine()
    {
        WriteModel("mas\r\nter", FactConfidence.High);

        var resolution = LearnedIntegrationBranchResolver.Resolve("trunk", true, ModelPath);

        Assert.Equal("trunk", resolution.Branch);
        Assert.Contains("mas\\r\\nter", resolution.Note);
        AssertOneLine(resolution.Note);
    }

    private void WriteModel(string? branch, FactConfidence confidence)
    {
        var fact = branch is null ? null : new ProjectFact<string>(branch, new FactSource(".git/HEAD", 1), confidence);
        var model = new ProjectModel(ProjectModel.CurrentSchemaVersion, ".", [], [], [], [], [], [], [], [], fact);
        File.WriteAllText(ModelPath, ProjectModelJson.Serialize(model));
    }

    private static void AssertOneLine(string? note)
    {
        Assert.False(string.IsNullOrWhiteSpace(note));
        Assert.DoesNotContain("\r", note);
        Assert.DoesNotContain("\n", note);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
