using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: fixture, registry, workspace and output writer are owned by each test.
public sealed class ProjectDiscoverCliTests
{
    [Fact(DisplayName = "Discovery writes workspace JSON and owner questions without changing configuration")]
    public void DiscoveryWritesModelWithoutChangingManifestsOrProfiles()
    {
        using var workspaceFixture = new ProjectOnboardingFixture("solution");
        using var sourceFixture = new ProjectOnboardingFixture("no-solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(workspaceFixture.Root, "registry"));
        var workspace = OrchestratorWorkspace.ForDirectory(workspaceFixture.Root);
        var protectedPaths = SeedConfiguration(workspaceFixture.Root, workspace.WorkerProfilePath)
            .Concat(SeedConfiguration(sourceFixture.Root, Path.Combine(sourceFixture.Root, ".orchestrator", "worker-profiles.json")))
            .Concat(new[] { registry.RegistryPath, registry.CurrentProjectPath }.Where(File.Exists))
            .ToArray();
        var snapshots = protectedPaths.ToDictionary(path => path,
            path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path)));
        var sourceFiles = Directory.GetFiles(sourceFixture.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        using var output = new StringWriter();

        var exit = ProjectCliCommand.Execute(["project", "discover", "--root", sourceFixture.Root],
            registry, workspaceFixture.Root, null, output);

        Assert.Equal(0, exit);
        var modelPath = Path.Combine(workspace.OrchestratorDirectory, "project-model.json");
        Assert.True(File.Exists(modelPath));
        var model = ProjectModelJson.Deserialize(File.ReadAllText(modelPath));
        Assert.Equal(Path.GetFullPath(sourceFixture.Root), model.RepositoryRoot);
        Assert.Equal(2, model.Units.Count);
        Assert.Equal(6, model.OwnerQuestions.Count);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"Project model: {modelPath}", lines[0]);
        Assert.Equal(model.OwnerQuestions.Select(question => $"Owner question: {question.Question}"), lines.Skip(1));
        foreach (var (path, snapshot) in snapshots)
        {
            Assert.Equal(snapshot.Bytes, File.ReadAllBytes(path));
            Assert.Equal(snapshot.Written, File.GetLastWriteTimeUtc(path));
        }
        Assert.Equal(sourceFiles.Keys.Order(StringComparer.Ordinal),
            Directory.GetFiles(sourceFixture.Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in sourceFiles)
            Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(workspace.OrchestratorDirectory, ".project-model-*.tmp"));
    }

    [Fact(DisplayName = "Discovery defaults to the selected project and overwrites its previous snapshot")]
    public void DiscoveryDefaultsToActiveProjectAndReplacesModel()
    {
        using var defaultFixture = new ProjectOnboardingFixture("no-solution");
        using var selectedFixture = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(defaultFixture.Root, "registry"));
        registry.CreateProject("sample", selectedFixture.Root);
        registry.SelectProject("sample");
        var workspace = registry.GetRequiredProject("sample").ResolveWorkspace();
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        var modelPath = Path.Combine(workspace.OrchestratorDirectory, "project-model.json");
        File.WriteAllText(modelPath, "previous snapshot");
        var selection = File.ReadAllBytes(registry.CurrentProjectPath);
        using var output = new StringWriter();

        Assert.Equal(0, ProjectCliCommand.Execute(["project", "discover"], registry, defaultFixture.Root, null, output));
        var model = ProjectModelJson.Deserialize(File.ReadAllText(modelPath));
        Assert.Equal(Path.GetFullPath(selectedFixture.Root), model.RepositoryRoot);
        Assert.Equal(3, model.Units.Count);
        Assert.Empty(model.OwnerQuestions);
        Assert.Equal(selection, File.ReadAllBytes(registry.CurrentProjectPath));
        Assert.False(File.Exists(Path.Combine(OrchestratorWorkspace.ForDirectory(defaultFixture.Root).OrchestratorDirectory, "project-model.json")));
    }

    [Fact(DisplayName = "An explicit project name determines the workspace while root selects the source")]
    public void ExplicitProjectNameDeterminesOutputWorkspace()
    {
        using var defaultFixture = new ProjectOnboardingFixture("no-solution");
        using var namedFixture = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(defaultFixture.Root, "registry"));
        registry.CreateProject("sample", namedFixture.Root);
        using var output = new StringWriter();

        Assert.Equal(0, ProjectCliCommand.Execute(["project", "discover", "sample", "--root=" + defaultFixture.Root],
            registry, defaultFixture.Root, null, output));
        var modelPath = Path.Combine(registry.GetRequiredProject("sample").ResolveWorkspace().OrchestratorDirectory, "project-model.json");
        Assert.Equal(Path.GetFullPath(defaultFixture.Root), ProjectModelJson.Deserialize(File.ReadAllText(modelPath)).RepositoryRoot);
    }

    [Fact(DisplayName = "A missing source root leaves the existing model intact")]
    public void MissingRootDoesNotOverwriteModel()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(fixture.Root, "registry"));
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        var modelPath = Path.Combine(workspace.OrchestratorDirectory, "project-model.json");
        File.WriteAllText(modelPath, "previous snapshot");
        using var output = new StringWriter();

        Assert.Throws<DirectoryNotFoundException>(() => ProjectCliCommand.Execute(
            ["project", "discover", "--root", Path.Combine(fixture.Root, "missing")], registry, fixture.Root, null, output));
        Assert.Equal("previous snapshot", File.ReadAllText(modelPath));
        Assert.Empty(output.ToString());
        Assert.Empty(Directory.GetFiles(workspace.OrchestratorDirectory, ".project-model-*.tmp"));
    }

    [Fact(DisplayName = "A missing root flag value fails without creating a model")]
    public void MissingFlagValueIsRejected()
    {
        using var fixture = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(fixture.Root, "registry"));
        using var output = new StringWriter();
        Assert.Throws<ArgumentException>(() => ProjectCliCommand.Execute(["project", "discover", "--root"],
            registry, fixture.Root, null, output));
        Assert.False(File.Exists(Path.Combine(OrchestratorWorkspace.ForDirectory(fixture.Root).OrchestratorDirectory, "project-model.json")));
    }

    private static string[] SeedConfiguration(string root, string workerProfilePath)
    {
        var manifests = new[] { Path.Combine(root, "config", "acceptance-manifest.json"),
            Path.Combine(root, ".orchestrator", "acceptance-manifest.json") };
        foreach (var path in manifests)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"preserve\":true}");
        }
        WorkerProfileStore.Save(workerProfilePath, WorkerProfileCatalog.Default());
        return [.. manifests, workerProfilePath];
    }
}
