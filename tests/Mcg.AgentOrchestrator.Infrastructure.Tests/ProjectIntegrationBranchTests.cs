using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static InfrastructureTestSupport;

// Parallel-safe: unique registry/workspace roots and AsyncLocal console capture.
public sealed class ProjectIntegrationBranchTests : HostCapacityBoundTestBase
{
    [Fact]
    public void Registry_CustomBranch_RoundTripsIntoWorkspace()
    {
        var registry = new OrchestratorProjectRegistry(CreateTempDirectory());
        var root = CreateTempDirectory();
        var created = registry.CreateProject("alpha", root, "master");
        var restored = new OrchestratorProjectRegistry(registry.RegistryDirectory)
            .GetRequiredProject("alpha");

        Assert.Equal("master", created.IntegrationBranch);
        Assert.Equal("master", restored.IntegrationBranch);
        Assert.Equal("master", restored.ResolveWorkspace().IntegrationBranch);
        Assert.Equal("master", restored.ResolveWorkspace("tenant").IntegrationBranch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Registry_LegacyBranchField_DefaultsToMain(string? branch)
    {
        var registry = new OrchestratorProjectRegistry(CreateTempDirectory());
        var entry = new Dictionary<string, string?>
        {
            ["Name"] = "legacy",
            ["RootDirectory"] = CreateTempDirectory()
        };
        if (branch is not null) entry["IntegrationBranch"] = branch;
        File.WriteAllText(registry.RegistryPath, JsonSerializer.Serialize(new { Projects = new[] { entry } }));

        var project = registry.GetRequiredProject("legacy");

        Assert.Equal("main", project.IntegrationBranch);
        Assert.Equal("main", project.ResolveWorkspace().IntegrationBranch);
        Assert.Equal("main", registry.ResolveActiveProject(project.RootDirectory, null).IntegrationBranch);
    }

    [Fact]
    public void Registry_RepeatedCreation_PreservesBranchAndRejectsChangingIt()
    {
        var registry = new OrchestratorProjectRegistry(CreateTempDirectory());
        var root = CreateTempDirectory();
        registry.CreateProject("alpha", root, "master");
        var before = File.ReadAllText(registry.RegistryPath);

        Assert.Equal("master", registry.CreateProject("alpha", root).IntegrationBranch);
        Assert.Equal("master", registry.CreateProject("alpha", root, "master").IntegrationBranch);
        Assert.Throws<InvalidOperationException>(() => registry.CreateProject("alpha", root, "other"));
        Assert.Equal(before, File.ReadAllText(registry.RegistryPath));
    }

    [Fact]
    public void Registry_CorruptBranch_FailsWithProjectIdentity()
    {
        var registry = new OrchestratorProjectRegistry(CreateTempDirectory());
        File.WriteAllText(registry.RegistryPath, JsonSerializer.Serialize(new
        {
            Projects = new[] { new { Name = "broken", RootDirectory = CreateTempDirectory(), IntegrationBranch = "a b" } }
        }));

        var error = Assert.Throws<InvalidOperationException>(() => registry.ListProjects());

        Assert.Contains("broken", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_CustomBranchFlag_IsPersistedAndShown(bool equalsForm)
    {
        var registry = new OrchestratorProjectRegistry(CreateTempDirectory());
        var source = CreateTempDirectory();
        var root = CreateTempDirectory();
        var sourceWorkspace = OrchestratorWorkspace.ForDirectory(source);
        ModelFunctionCatalogStore.Save(sourceWorkspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        var parts = new List<string> { "project", "create", "alpha", "--root", root };
        parts.Add(equalsForm ? "--integration-branch=master" : "--integration-branch");
        if (!equalsForm) parts.Add("master");

        CaptureConsole(() => Assert.Equal(0, ProjectCliCommand.Execute(parts, registry, source, null)));
        var reopened = new OrchestratorProjectRegistry(registry.RegistryDirectory);
        var output = CaptureConsole(() => Assert.Equal(0,
            ProjectCliCommand.Execute(["project", "show", "alpha"], reopened, source, null)));
        var defaultOutput = CaptureConsole(() => ProjectCliCommand.Execute(["project", "show", "default"], reopened, source, null));

        Assert.Equal("master", reopened.GetRequiredProject("alpha").ResolveWorkspace().IntegrationBranch);
        Assert.Contains("Integration branch: master", output);
        Assert.Contains("Integration branch: main", defaultOutput);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("   ", false)]
    [InlineData("a b", false)]
    [InlineData("a\tb", true)]
    public void Create_InvalidBranchFlag_RejectsBeforeAnyMutation(string branch, bool equalsForm)
    {
        var registry = new OrchestratorProjectRegistry(CreateTempDirectory());
        var source = CreateTempDirectory();
        var root = CreateTempDirectory();
        var parts = new List<string> { "project", "create", "alpha", "--root", root };
        parts.Add(equalsForm ? "--integration-branch=" + branch : "--integration-branch");
        if (!equalsForm) parts.Add(branch);

        var error = Assert.Throws<ArgumentException>(() => ProjectCliCommand.Execute(parts, registry, source, null));

        Assert.StartsWith("Usage:", error.Message);
        Assert.False(File.Exists(registry.RegistryPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }
}
