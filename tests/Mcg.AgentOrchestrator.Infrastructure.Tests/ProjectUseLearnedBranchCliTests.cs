using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;
using static InfrastructureTestSupport;

// Parallel-safe: unique registry/data roots, private writers and AsyncLocal console capture.
public sealed class ProjectUseLearnedBranchCliTests : IDisposable
{
    private readonly string directory = CreateTempDirectory();
    private string ProjectRoot => Path.Combine(directory, "repository");
    private OrchestratorProjectRegistry Registry => new(Path.Combine(directory, "registry"), Path.Combine(directory, "data"));

    [Fact]
    public void Execute_SetAndClearOptIn_PrintsPersistedBranchAndSourceIdempotently()
    {
        var registry = CreateProject();
        WriteModel(registry);
        using var output = new StringWriter();

        for (var repeat = 0; repeat < 2; repeat++)
        {
            output.GetStringBuilder().Clear();
            Assert.Equal(0, Execute(["sample"], registry, output));
            Assert.Equal("Use learned integration branch: on; integration branch: master (source: Learned)" + Environment.NewLine,
                output.ToString());
            using var json = JsonDocument.Parse(File.ReadAllText(registry.RegistryPath));
            var entry = json.RootElement.GetProperty("Projects")[0];
            Assert.True(entry.GetProperty("UseLearnedIntegrationBranch").GetBoolean());
            Assert.False(entry.TryGetProperty("IntegrationBranch", out _));
            Assert.Equal("master", Reopen(registry).GetRequiredProject("sample").IntegrationBranch);
        }

        for (var repeat = 0; repeat < 2; repeat++)
        {
            output.GetStringBuilder().Clear();
            Assert.Equal(0, Execute(["sample", "--off"], registry, output));
            Assert.Equal("Use learned integration branch: off; integration branch: main (source: Default)" + Environment.NewLine,
                output.ToString());
            Assert.DoesNotContain("UseLearnedIntegrationBranch", File.ReadAllText(registry.RegistryPath));
            Assert.Equal("main", Reopen(registry).GetRequiredProject("sample").IntegrationBranch);
        }
    }

    [Fact]
    public void Execute_StoredRegistryBranch_RemainsAuthoritativeAndReportsMismatch()
    {
        var registry = CreateProject("trunk");
        WriteModel(registry);
        using var output = new StringWriter();

        Assert.Equal(0, Execute(["sample"], registry, output));

        var project = Reopen(registry).GetRequiredProject("sample");
        Assert.Equal("trunk", project.IntegrationBranch);
        Assert.Equal(IntegrationBranchResolution.Registry, project.IntegrationBranchSource);
        var lines = Lines(output.ToString());
        Assert.Equal(2, lines.Length);
        Assert.Equal("Use learned integration branch: on; integration branch: trunk (source: Registry)", lines[0]);
        Assert.Equal(project.IntegrationBranchNote, lines[1]);
        Assert.Contains("master", lines[1]);
        Assert.Contains("trunk", lines[1]);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void Execute_UnknownAndImplicitDefault_AreRefusedWithoutWriting(string name)
    {
        var registry = CreateProject();
        var before = File.ReadAllBytes(registry.RegistryPath);
        using var output = new StringWriter();

        var error = Assert.Throws<ArgumentException>(() => Execute([name], registry, output));

        Assert.Contains(name == "unknown" ? "Unknown project" : "The default project is implicit", error.Message);
        Assert.Equal(before, File.ReadAllBytes(registry.RegistryPath));
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("option-as-name")]
    [InlineData("unknown-option")]
    [InlineData("extra-name")]
    [InlineData("repeated-off")]
    public void Execute_InvalidArguments_ThrowsOwnUsageBeforeWriting(string scenario)
    {
        var registry = CreateProject();
        var before = File.ReadAllBytes(registry.RegistryPath);
        using var output = new StringWriter();
        string[] options = scenario switch
        {
            "missing" => [],
            "option-as-name" => ["--off"],
            "unknown-option" => ["sample", "--unknown"],
            "extra-name" => ["sample", "other"],
            _ => ["sample", "--off", "--off"]
        };

        var error = Assert.Throws<ArgumentException>(() => Execute(options, registry, output));

        Assert.Equal("Usage: project use-learned-branch <name> [--off]", error.Message);
        Assert.Equal(before, File.ReadAllBytes(registry.RegistryPath));
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData(null, false, "main", IntegrationBranchResolution.Default, false)]
    [InlineData(null, true, "master", IntegrationBranchResolution.Learned, false)]
    [InlineData("trunk", true, "trunk", IntegrationBranchResolution.Registry, true)]
    [InlineData("master", true, "master", IntegrationBranchResolution.Registry, false)]
    public void Show_SourceImmediatelyFollowsBranch_ThenOptionalMismatchNote(
        string? stored, bool enabled, string branch, string source, bool mismatch)
    {
        var registry = CreateProject(stored);
        WriteModel(registry);
        registry.SetUseLearnedIntegrationBranch("sample", enabled);
        var project = registry.GetRequiredProject("sample");

        var output = CaptureConsole(() => Assert.Equal(0,
            ProjectCliCommand.Execute(["project", "show", "sample"], registry, ProjectRoot, null)));

        var lines = Lines(output);
        Assert.Equal("Project: sample", lines[0]);
        Assert.Equal($"Integration branch: {branch}", lines[1]);
        Assert.Equal($"Integration branch source: {source}", lines[2]);
        var next = 3;
        if (mismatch)
        {
            Assert.Equal(project.IntegrationBranchNote, lines[next++]);
            Assert.Contains("trunk", project.IntegrationBranchNote);
            Assert.Contains("master", project.IntegrationBranchNote);
        }
        else Assert.Null(project.IntegrationBranchNote);
        Assert.Equal(new[]
        {
            "Active: False", $"Root: {ProjectRoot}", $"Workspace: {project.ResolveWorkspace().OrchestratorDirectory}",
            $"State: {project.ResolveWorkspace().SqliteStatePath}", $"Backlog: {project.ResolveWorkspace().BacklogStorePath}",
            $"Registry: {registry.RegistryPath}", $"Selection: {registry.CurrentProjectPath}"
        }, lines[next..]);
    }

    [Fact]
    public void Show_ImplicitDefault_AlsoPrintsSourceImmediatelyAfterBranch()
    {
        Directory.CreateDirectory(ProjectRoot);
        var output = CaptureConsole(() => Assert.Equal(0,
            ProjectCliCommand.Execute(["project", "show", "default"], Registry, ProjectRoot, null)));

        var lines = Lines(output);
        Assert.Equal("Integration branch: main", lines[1]);
        Assert.Equal("Integration branch source: Default", lines[2]);
        Assert.Equal("Active: True", lines[3]);
    }

    private OrchestratorProjectRegistry CreateProject(string? storedBranch = null)
    {
        Directory.CreateDirectory(ProjectRoot);
        var registry = Registry;
        registry.CreateProject("sample", ProjectRoot, storedBranch);
        return registry;
    }

    private int Execute(string[] options, OrchestratorProjectRegistry registry, TextWriter output) =>
        ProjectCliCommand.Execute(["project", "use-learned-branch", .. options], registry, ProjectRoot, null, output);

    private static OrchestratorProjectRegistry Reopen(OrchestratorProjectRegistry registry) =>
        new(registry.RegistryDirectory, registry.DataRootDirectory);

    private static string[] Lines(string output) => output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static void WriteModel(OrchestratorProjectRegistry registry)
    {
        var modelDirectory = OrchestratorDataRoot.FromDirectory(registry.DataRootDirectory).ProjectDirectory("sample");
        Directory.CreateDirectory(modelDirectory);
        var model = new ProjectModel(ProjectModel.CurrentSchemaVersion, ".", [], [], [], [], [], [], [], [],
            new ProjectFact<string>("master", new FactSource(".git/HEAD", 1), FactConfidence.High));
        File.WriteAllText(Path.Combine(modelDirectory, "project-model.json"), ProjectModelJson.Serialize(model));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
