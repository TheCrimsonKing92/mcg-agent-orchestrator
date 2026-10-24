using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: each test owns its sources, workspace, registry, data and output writer.
public sealed class ProjectDiscoverStackSelectionCliTests
{
    [Fact]
    public void GoModRootIsRefusedWithoutWritingAModel()
    {
        using var workspace = new ProjectOnboardingFixture("solution");
        var sourceRoot = Path.Combine(workspace.Root, "go-repository");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "go.mod"), "module example.test/sample\n");
        var registry = new OrchestratorProjectRegistry(Path.Combine(workspace.Root, "registry"), Path.Combine(workspace.Root, "data"));
        var modelDirectory = OrchestratorWorkspace.ForProject(Path.GetFileName(sourceRoot), sourceRoot,
            dataRootDirectory: registry.DataRootDirectory).OrchestratorDirectory;
        using var output = new StringWriter();

        var exception = Assert.Throws<InvalidOperationException>(() => ProjectCliCommand.Execute(
            ["project", "discover", "--root", sourceRoot], registry, workspace.Root, null, output));

        Assert.Contains("go.mod", exception.Message);
        Assert.Contains("No discovery adapter is available", exception.Message);
        Assert.Equal("", output.ToString());
        Assert.False(File.Exists(Path.Combine(modelDirectory, "project-model.json")));
        Assert.False(File.Exists(Path.Combine(OrchestratorWorkspace.ForDirectory(workspace.Root).OrchestratorDirectory, "project-model.json")));
        Assert.Empty(Directory.GetFiles(workspace.Root, ".project-model-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void SolutionRootStillWritesADotnetModel()
    {
        using var workspace = new ProjectOnboardingFixture("solution");
        using var source = new ProjectOnboardingFixture("solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(workspace.Root, "registry"), Path.Combine(workspace.Root, "data"));
        using var output = new StringWriter();

        var exit = ProjectCliCommand.Execute(["project", "discover", "--root", source.Root], registry, workspace.Root, null, output);

        Assert.Equal(0, exit);
        var modelPath = Path.Combine(OrchestratorWorkspace.ForProject(Path.GetFileName(source.Root), source.Root,
            dataRootDirectory: registry.DataRootDirectory).OrchestratorDirectory, "project-model.json");
        Assert.True(File.Exists(modelPath));
        var json = File.ReadAllText(modelPath);
        Assert.Equal("dotnet", JsonNode.Parse(json)!["stack"]!.GetValue<string>());
        var model = ProjectModelJson.Deserialize(json);
        Assert.Equal(ProjectStack.Dotnet, model.Stack);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"Project model: {modelPath}", lines[0]);
        Assert.Equal("Integration branch: not learned", lines[1]);
        Assert.Equal(model.OwnerQuestions.Select(question => $"Owner question: {question.Question}"), lines.Skip(2));
    }
}
