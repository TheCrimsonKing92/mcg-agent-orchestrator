using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using static InfrastructureTestSupport;

// Parallel-safe: unique project/data/registry roots and AsyncLocal console capture.
public sealed class LearnedIntegrationBranchWorkspaceTests : HostCapacityBoundTestBase
{
    private readonly string directory = CreateTempDirectory();
    private string ProjectRoot => Path.Combine(directory, "repository");
    private OrchestratorProjectRegistry Registry => new(Path.Combine(directory, "registry"), Path.Combine(directory, "data"));

    [Fact]
    public void Registry_OptInControlsBothProjectAndWorkspaceBranch()
    {
        Directory.CreateDirectory(ProjectRoot);
        var registry = Registry;
        registry.CreateProject("sample", ProjectRoot);
        WriteModel(registry);
        var before = File.ReadAllBytes(registry.RegistryPath);

        registry.SetUseLearnedIntegrationBranch("sample", true);
        AssertBranches(registry, "master", IntegrationBranchResolution.Learned);
        registry.SetUseLearnedIntegrationBranch("sample", false);
        AssertBranches(registry, "main", IntegrationBranchResolution.Default);

        Assert.Equal(before, File.ReadAllBytes(registry.RegistryPath));
        Assert.DoesNotContain("UseLearnedIntegrationBranch", File.ReadAllText(registry.RegistryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registry_CliCreatedStoredBranch_WinsWithEitherSetting(bool enabled)
    {
        Directory.CreateDirectory(ProjectRoot);
        var sourceRoot = Path.Combine(directory, "source");
        Directory.CreateDirectory(sourceRoot);
        var sourceWorkspace = OrchestratorWorkspace.ForDirectory(sourceRoot);
        ModelFunctionCatalogStore.Save(sourceWorkspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var registry = Registry;
        CaptureConsole(() => Assert.Equal(0, ProjectCliCommand.Execute(
            ["project", "create", "sample", "--root", ProjectRoot, "--integration-branch", "trunk"],
            registry, sourceRoot, null)));
        WriteModel(registry);

        registry.SetUseLearnedIntegrationBranch("sample", enabled);

        AssertBranches(registry, "trunk", IntegrationBranchResolution.Registry);
        using var json = JsonDocument.Parse(File.ReadAllText(registry.RegistryPath));
        Assert.Equal("trunk", json.RootElement.GetProperty("Projects")[0].GetProperty("IntegrationBranch").GetString());
    }

    [Theory]
    [InlineData(null, "main", IntegrationBranchResolution.Default)]
    [InlineData("trunk", "trunk", IntegrationBranchResolution.Registry)]
    [InlineData(" trunk ", "trunk", IntegrationBranchResolution.Registry)]
    public void Registry_LegacyJson_ResolvesAsBeforeAndOffSaveIsByteIdentical(string? stored, string branch, string source)
    {
        Directory.CreateDirectory(ProjectRoot);
        var registry = Registry;
        Directory.CreateDirectory(registry.RegistryDirectory);
        var entry = new Dictionary<string, string> { ["Name"] = "sample", ["RootDirectory"] = ProjectRoot };
        if (stored is not null) entry["IntegrationBranch"] = stored;
        File.WriteAllText(registry.RegistryPath, JsonSerializer.Serialize(new { Projects = new[] { entry } },
            new JsonSerializerOptions { WriteIndented = true }));
        WriteModel(registry);
        var before = File.ReadAllBytes(registry.RegistryPath);

        AssertBranches(registry, branch, source);
        registry.SetUseLearnedIntegrationBranch("sample", false);
        AssertBranches(registry, branch, source);

        Assert.Equal(before, File.ReadAllBytes(registry.RegistryPath));
        Assert.DoesNotContain("UseLearnedIntegrationBranch", File.ReadAllText(registry.RegistryPath));
    }

    [Fact]
    public void Registry_AlreadyRegisteredComparison_UsesStoredBranchEvenWhenOptedIn()
    {
        Directory.CreateDirectory(ProjectRoot);
        var registry = Registry;
        registry.CreateProject("sample", ProjectRoot);
        WriteModel(registry);
        registry.SetUseLearnedIntegrationBranch("sample", true);
        var before = File.ReadAllBytes(registry.RegistryPath);

        var project = registry.ResolveProjectForCreation("sample", ProjectRoot, "main", out var alreadyRegistered);
        Assert.True(alreadyRegistered);
        Assert.Equal("main", project.IntegrationBranch);
        var error = Assert.Throws<InvalidOperationException>(() =>
            registry.ResolveProjectForCreation("sample", ProjectRoot, "master", out _));
        Assert.Contains("already registered with integration branch 'main'", error.Message);
        Assert.Equal(before, File.ReadAllBytes(registry.RegistryPath));
        AssertBranches(registry, "master", IntegrationBranchResolution.Learned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registry_InvalidStoredBranch_PreservesExistingError(bool enabled)
    {
        Directory.CreateDirectory(ProjectRoot);
        var registry = Registry;
        Directory.CreateDirectory(registry.RegistryDirectory);
        File.WriteAllText(registry.RegistryPath, JsonSerializer.Serialize(new
        {
            Projects = new[] { new { Name = "sample", RootDirectory = ProjectRoot,
                IntegrationBranch = "a b", UseLearnedIntegrationBranch = enabled } }
        }));
        WriteModel(registry);

        var error = Assert.Throws<InvalidOperationException>(() => registry.ListProjects());

        Assert.Equal("Project 'sample' has an invalid integration branch.", error.Message);
    }

    private static void AssertBranches(OrchestratorProjectRegistry registry, string branch, string source)
    {
        var reopened = new OrchestratorProjectRegistry(registry.RegistryDirectory, registry.DataRootDirectory);
        var project = reopened.GetRequiredProject("sample");
        Assert.Equal(branch, project.IntegrationBranch);
        Assert.Equal(source, project.IntegrationBranchSource);
        Assert.Equal(branch, project.ResolveWorkspace().IntegrationBranch);
        Assert.Equal(branch, project.ResolveWorkspace("tenant").IntegrationBranch);
    }

    private static void WriteModel(OrchestratorProjectRegistry registry)
    {
        var modelDirectory = OrchestratorDataRoot.FromDirectory(registry.DataRootDirectory).ProjectDirectory("sample");
        Directory.CreateDirectory(modelDirectory);
        var model = new ProjectModel(ProjectModel.CurrentSchemaVersion, ".", [], [], [], [], [], [], [], [],
            new ProjectFact<string>("master", new FactSource(".git/HEAD", 1), FactConfidence.High));
        File.WriteAllText(Path.Combine(modelDirectory, "project-model.json"), ProjectModelJson.Serialize(model));
    }

    public override async ValueTask DisposeAsync()
    {
        try { Directory.Delete(directory, recursive: true); }
        finally { await base.DisposeAsync(); }
    }
}
