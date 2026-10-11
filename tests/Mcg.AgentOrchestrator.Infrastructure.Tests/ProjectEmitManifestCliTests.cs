using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its copied source, registry, data root and output directory.
public sealed class ProjectEmitManifestCliTests
{
    [Theory]
    [InlineData("named")]
    [InlineData("selected")]
    [InlineData("equals")]
    [InlineData("name-after-out")]
    [InlineData("active-override")]
    public void Emit_SettledStoredModel_WritesOnlyRequestedArtifact(string selection)
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        using var scratch = new ProjectOnboardingFixture("no-solution");
        var outputDirectory = Path.Combine(scratch.Root, "output");
        Directory.CreateDirectory(outputDirectory);
        var destination = Path.Combine(outputDirectory, "learned.json");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        var project = registry.CreateProject("sample", source.Root);
        registry.SelectProject(selection == "active-override" ? "default" : "sample");
        Assert.Equal(0, Execute(registry, home.Root, ["project", "discover", "sample"]));
        foreach (var (key, value) in new[] { ("defaultMinutes", "17"), ("buildServerShutdownMinutes", "3"), ("discoveryMinutes", "4") })
            Assert.Equal(0, Execute(registry, home.Root, ["project", "answer", "sample", "engine/timeouts/" + key, value]));
        var directory = project.ResolveWorkspace().OrchestratorDirectory;
        var model = ProjectModelJson.Deserialize(File.ReadAllText(Path.Combine(directory, "project-model.json")));
        Assert.Equal(2, LearnedTestCheckDeriver.Derive(model).Checks.Count);
        var expected = LearnedAcceptanceManifest.Build(model, OwnerAnswerFile.Read(directory));
        Assert.NotNull(expected.Json);
        var before = Snapshot(home.Root, source.Root);
        using var output = new StringWriter();
        string[] options = selection switch
        {
            "selected" or "active-override" => ["--out", destination],
            "equals" => ["sample", "--out=" + destination],
            "name-after-out" => ["--out", destination, "sample"],
            _ => ["sample", "--out", destination]
        };

        var exit = ProjectCliCommand.Execute(["project", "emit-manifest", .. options], registry, home.Root,
            selection == "active-override" ? "sample" : null, output);

        Assert.Equal(0, exit);
        Assert.Equal($"Emitted manifest: {Path.GetFullPath(destination)} (2 checks){Environment.NewLine}", output.ToString());
        Assert.Equal(new[] { destination }, Directory.GetFileSystemEntries(outputDirectory));
        Assert.Equal(expected.Json, File.ReadAllText(destination));
        Assert.Equal(new UTF8Encoding(false).GetBytes(expected.Json), File.ReadAllBytes(destination));
        AssertUnchanged(before, home.Root, source.Root);
    }

    [Fact]
    public void Emit_SourceChangesAfterDiscovery_UsesStoredFactsOnly()
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("answer-key-repository");
        using var scratch = new ProjectOnboardingFixture("no-solution");
        var registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
        var project = registry.CreateProject("sample", source.Root);
        Assert.Equal(0, Execute(registry, home.Root, ["project", "discover", "sample"]));
        foreach (var key in new[] { "defaultMinutes", "buildServerShutdownMinutes", "discoveryMinutes" })
            Assert.Equal(0, Execute(registry, home.Root, ["project", "answer", "sample", "engine/timeouts/" + key, "8"]));
        var directory = project.ResolveWorkspace().OrchestratorDirectory;
        var expected = LearnedAcceptanceManifest.Build(
            ProjectModelJson.Deserialize(File.ReadAllText(Path.Combine(directory, "project-model.json"))), OwnerAnswerFile.Read(directory));
        Assert.NotNull(expected.Json);
        // A rediscovery would lose both checks. The stored snapshot remains authoritative.
        Directory.Delete(Path.Combine(source.Root, "tests"), recursive: true);
        var before = Snapshot(home.Root, source.Root);
        var destination = Path.Combine(scratch.Root, "learned.json");

        Assert.Equal(0, Execute(registry, home.Root, ["project", "emit-manifest", "sample", "--out", destination]));

        Assert.Equal(expected.Json, File.ReadAllText(destination));
        AssertUnchanged(before, home.Root, source.Root);
    }

    private static int Execute(OrchestratorProjectRegistry registry, string root, string[] parts)
    {
        using var output = new StringWriter();
        return ProjectCliCommand.Execute(parts, registry, root, null, output);
    }

    private static Dictionary<string, string> Snapshot(params string[] roots) => roots
        .SelectMany(root => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
        .ToDictionary(path => path, path => Directory.Exists(path) ? "directory"
            : $"{new FileInfo(path).Length}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}");
    private static void AssertUnchanged(Dictionary<string, string> before, params string[] roots)
    {
        var after = Snapshot(roots);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, hash) in before) Assert.Equal(hash, after[path]);
    }
}
