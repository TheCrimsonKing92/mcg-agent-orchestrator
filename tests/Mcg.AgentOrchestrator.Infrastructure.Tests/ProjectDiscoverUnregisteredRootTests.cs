using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: source, home, registry and output are isolated to each test's fixture copies.
public sealed class ProjectDiscoverUnregisteredRootTests
{
    [Theory(DisplayName = "Unregistered discovery writes and prints its per-project file while preserving home state")]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignRootUsesHomeProjectsFolder(bool existingTopLevelModel)
    {
        using var home = new ProjectOnboardingFixture("solution");
        using var fixture = new ProjectOnboardingFixture("plain-library");
        var root = Path.Combine(fixture.Root, "sample-repo");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, ".orchestrator"));
        var workspace = OrchestratorWorkspace.ForDirectory(home.Root);
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        var topLevelModel = Path.Combine(workspace.OrchestratorDirectory, "project-model.json");
        if (existingTopLevelModel)
            File.WriteAllText(topLevelModel, "previous top-level snapshot");
        var homeFiles = Directory.GetFiles(workspace.OrchestratorDirectory).ToDictionary(path => path, File.ReadAllBytes);
        var sourceFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        using var output = new StringWriter();

        Assert.Equal(0, ProjectCliCommand.Execute(["project", "discover", "--root", root + Path.DirectorySeparatorChar],
            registry, home.Root, null, output));

        var modelPath = Path.Combine(home.Root, ".orchestrator", "projects", "sample-repo", "project-model.json");
        Assert.Equal(Path.Combine(OrchestratorWorkspace.ForProject("sample-repo", root,
            dataRootDirectory: registry.DataRootDirectory).OrchestratorDirectory, "project-model.json"), modelPath);
        Assert.True(File.Exists(modelPath));
        Assert.Equal($"Project model: {modelPath}{Environment.NewLine}", output.ToString());
        var model = ProjectModelJson.Deserialize(File.ReadAllText(modelPath));
        Assert.Equal(2, model.Units.Count);
        Assert.False(model.Units.Single(unit => unit.Id == "src/Plain.Library/Plain.Library.csproj").IsTest.Value);
        Assert.Single(model.TestSetups);
        Assert.Empty(model.OwnerQuestions);
        Assert.Equal(existingTopLevelModel, File.Exists(topLevelModel));
        Assert.Equal(homeFiles.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(workspace.OrchestratorDirectory).Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in homeFiles)
            Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(modelPath)!, ".project-model-*.tmp"));
        Assert.False(File.Exists(registry.RegistryPath));
        Assert.False(File.Exists(registry.CurrentProjectPath));
        Assert.Empty(registry.ListProjects());
        Assert.Equal(sourceFiles.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in sourceFiles)
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory(DisplayName = "Self-discovery keeps the top-level workspace with or without an explicit root")]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnRootKeepsTopLevelWorkspace(bool explicitRoot)
    {
        using var home = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        using var output = new StringWriter();
        string[] parts = explicitRoot
            ? ["project", "discover", "--root", Path.Combine(home.Root, ".") + Path.DirectorySeparatorChar]
            : ["project", "discover"];

        Assert.Equal(0, ProjectCliCommand.Execute(parts, registry, home.Root, null, output));

        var modelPath = Path.Combine(OrchestratorWorkspace.ForDirectory(home.Root).OrchestratorDirectory, "project-model.json");
        Assert.True(File.Exists(modelPath));
        Assert.Equal($"Project model: {modelPath}{Environment.NewLine}", output.ToString());
        Assert.Equal(3, ProjectModelJson.Deserialize(File.ReadAllText(modelPath)).Units.Count);
        Assert.False(Directory.Exists(Path.Combine(home.Root, ".orchestrator", "projects")));
    }

    [Theory(DisplayName = "Repository leaf names are preserved without applying registered-name restrictions")]
    [InlineData("default")]
    [InlineData("Contoso.Api")]
    [InlineData("Sample Repo")]
    [InlineData("Échantillon")]
    [InlineData("ARepositoryDirectoryWithMoreThanSixtyFourCharactersMustKeepItsOriginalName")]
    public void RepositoryLeafIsPreserved(string name)
    {
        using var home = new ProjectOnboardingFixture("solution");
        using var fixture = new ProjectOnboardingFixture("plain-library");
        var root = Path.Combine(fixture.Root, name);
        Directory.Move(Path.Combine(fixture.Root, "sample-repo"), root);
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        using var output = new StringWriter();

        Assert.Equal(0, ProjectCliCommand.Execute(
            ["project", "discover", "--root", root], registry, home.Root, null, output));

        var modelPath = Path.Combine(registry.DataRootDirectory, "projects", name, "project-model.json");
        Assert.True(File.Exists(modelPath));
        Assert.Equal($"Project model: {modelPath}{Environment.NewLine}", output.ToString());
        Assert.Equal(2, ProjectModelJson.Deserialize(File.ReadAllText(modelPath)).Units.Count);
        Assert.False(Directory.Exists(OrchestratorWorkspace.ForDirectory(home.Root).OrchestratorDirectory));
        Assert.False(File.Exists(registry.RegistryPath));
    }

    [Fact(DisplayName = "A filesystem root without a repository leaf fails before measurement or discovery")]
    public void DriveRootHasNoProjectIdentity()
    {
        using var home = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        using var output = new StringWriter();

        var error = Assert.Throws<ArgumentException>(() => ProjectCliCommand.Execute(
            ["project", "discover", "--root", Path.GetPathRoot(home.Root)!, "--measure", "all"],
            registry, home.Root, null, output,
            () => throw new InvalidOperationException("Invalid roots must fail before selecting a measurer.")));

        Assert.Equal("rootOverride", error.ParamName);
        Assert.Contains("Discovery root", error.Message);
        Assert.False(Directory.Exists(registry.DataRootDirectory));
        Assert.False(Directory.Exists(OrchestratorWorkspace.ForDirectory(home.Root).OrchestratorDirectory));
        Assert.Empty(output.ToString());
    }

    [Fact(DisplayName = "A missing foreign root fails without creating a project workspace")]
    public void MissingForeignRootCreatesNoProjectFolder()
    {
        using var home = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        using var output = new StringWriter();

        Assert.Throws<DirectoryNotFoundException>(() => ProjectCliCommand.Execute(
            ["project", "discover", "--root", Path.Combine(home.Root, "missing-repo")], registry, home.Root, null, output));

        Assert.False(Directory.Exists(Path.Combine(registry.DataRootDirectory, "projects", "missing-repo")));
        Assert.False(File.Exists(Path.Combine(OrchestratorWorkspace.ForDirectory(home.Root).OrchestratorDirectory, "project-model.json")));
        Assert.Empty(output.ToString());
    }
}
