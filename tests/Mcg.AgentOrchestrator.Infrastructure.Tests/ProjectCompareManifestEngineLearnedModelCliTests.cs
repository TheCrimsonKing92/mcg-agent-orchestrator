using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each invocation owns its source, registry, data root and writer.
public sealed class ProjectCompareManifestEngineLearnedModelCliTests
{
    private const string NetHealthManifest = """
        {"engine":{"slotCount":1,"maxConcurrentShards":2,"timeouts":{
            "defaultMinutes":10,"buildServerShutdownMinutes":1,"discoveryMinutes":2}},
         "checks":[{"type":"dotnet-test","project":"sample.csproj","arguments":["--verbosity","minimal"]}]}
        """;

    [Fact]
    public void Compare_MeasuredModel_PrintsDerivedFactsAndQuestionsWithoutWriting()
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("no-solution");
        var registry = Registry(home);
        var project = registry.CreateProject("sample", source.Root);
        Store(project.ResolveWorkspace().OrchestratorDirectory, Model());
        var manifestPath = Manifest(source, NetHealthManifest);
        var before = Snapshot(home.Root, source.Root, registry.DataRootDirectory);
        using var output = new StringWriter();

        Assert.Equal(1, Execute(registry, home.Root, manifestPath, output, ["sample"]));

        var lines = Lines(output);
        Assert.Equal(14, lines.Length);
        Assert.StartsWith("Match engine.maxConcurrentShards hand=2 learned=2 (Agrees with the learned model: learned from project-model.json: 2 test units", lines[0]);
        Assert.StartsWith("Match engine.timeouts.defaultMinutes hand=10 learned=10 (Agrees with the learned model: learned from project-model.json: Slowest test unit slow measured 200 seconds", lines[2]);
        Assert.Contains("multiplier 3", lines[2]);
        Assert.Contains("5..60", lines[2]);
        Assert.StartsWith("DiffersFromGateDefault engine.timeouts.buildServerShutdownMinutes", lines[3]);
        Assert.StartsWith("DiffersFromGateDefault engine.timeouts.discoveryMinutes", lines[4]);
        AssertOwnerQuestions(lines, false);
        Assert.DoesNotContain(lines, line => line.StartsWith("Learned model:", StringComparison.Ordinal));
        AssertUnchanged(before, home.Root, source.Root, registry.DataRootDirectory);
    }

    [Theory]
    [InlineData(2, 10, 0)]
    [InlineData(4, 25, 1)]
    public void Compare_LearnedDifferencesAlone_DetermineExitCode(int shards, int timeout, int exit)
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("no-solution");
        var registry = Registry(home);
        Store(registry.CreateProject("sample", source.Root).ResolveWorkspace().OrchestratorDirectory, Model());
        var manifestPath = Manifest(source, JsonSerializer.Serialize(new
        {
            engine = new { maxConcurrentShards = shards, timeouts = new
                { defaultMinutes = timeout, buildServerShutdownMinutes = 25, discoveryMinutes = 25 } }
        }));
        using var output = new StringWriter();
        Assert.Equal(exit, Execute(registry, home.Root, manifestPath, output, ["sample"]));
        var lines = Lines(output);
        Assert.DoesNotContain(lines, line => line.StartsWith("DiffersFromGateDefault", StringComparison.Ordinal));
        if (exit == 1)
        {
            Assert.StartsWith("DiffersFromHand engine.maxConcurrentShards hand=4 learned=2 (The learned model derives 2: learned from project-model.json:", lines[0]);
            Assert.Contains("The hand manifest declares 4.", lines[0]);
            Assert.StartsWith("DiffersFromHand engine.timeouts.defaultMinutes hand=25 learned=10 (The learned model derives 10: learned from project-model.json:", lines[2]);
            Assert.Contains("The hand manifest declares 25.", lines[2]);
        }
        else Assert.DoesNotContain(lines, line => line.StartsWith("DiffersFromHand", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_UnmeasuredModel_LeavesTimeoutAtGateDefaultAndQuestionsOwner()
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("no-solution");
        var registry = Registry(home);
        Store(registry.CreateProject("sample", source.Root).ResolveWorkspace().OrchestratorDirectory,
            Model() with { Measurements = [new("slow", Fact(99999d), null)] });
        var manifestPath = Manifest(source, NetHealthManifest);
        using var output = new StringWriter();
        Assert.Equal(1, Execute(registry, home.Root, manifestPath, output, ["sample"]));
        var lines = Lines(output);
        Assert.Equal("DiffersFromGateDefault engine.timeouts.defaultMinutes hand=10 learned=25 (The learned value comes from the gate's undeclared default.)", lines[2]);
        AssertOwnerQuestions(lines, true);
        Assert.Contains("fast, slow", lines[^3]);
        Assert.Contains("project discover --measure test", lines[^3]);
    }

    [Theory]
    [InlineData("named")]
    [InlineData("selected")]
    [InlineData("active-override")]
    [InlineData("default-root-override")]
    public void Compare_ProjectSelection_ReadsDiscoveryWorkspace(string selection)
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("no-solution");
        var registry = Registry(home);
        var project = registry.CreateProject("sample", source.Root);
        registry.SelectProject(selection == "active-override" ? "default" : "sample");
        string[] options = selection switch
        {
            "named" => ["sample"],
            "default-root-override" => ["default", "--root=" + source.Root],
            _ => []
        };
        var modelDirectory = selection == "default-root-override"
            ? ProjectDiscoveryModelLocation.ResolveDirectory(new("default", home.Root), source.Root, home.Root, registry.DataRootDirectory)
            : project.ResolveWorkspace().OrchestratorDirectory;
        Store(modelDirectory, Model());
        var manifestPath = Manifest(source, NetHealthManifest);
        using var output = new StringWriter();
        Assert.Equal(1, Execute(registry, home.Root, manifestPath, output, options,
            selection == "active-override" ? "sample" : null));
        Assert.StartsWith("Match engine.maxConcurrentShards hand=2 learned=2", Lines(output)[0]);
        AssertOwnerQuestions(Lines(output), false);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("invalid-schema")]
    [InlineData("unknown")]
    [InlineData("unknown-active")]
    [InlineData("no-active")]
    public void Compare_NoUsableModel_PreservesOutputExitAndAllFiles(string scenario)
    {
        using var home = new ProjectOnboardingFixture("no-solution");
        using var source = new ProjectOnboardingFixture("no-solution");
        var registry = Registry(home);
        var project = registry.CreateProject("sample", source.Root);
        var directory = project.ResolveWorkspace().OrchestratorDirectory;
        if (scenario != "missing")
        {
            Store(directory, Model());
            var modelPath = Path.Combine(directory, "project-model.json");
            if (scenario == "malformed") File.WriteAllText(modelPath, "{");
            if (scenario == "invalid-schema") File.WriteAllText(modelPath, "{}");
            if (scenario == "unknown") registry.SelectProject("sample");
            if (scenario == "no-active") Store(new OrchestratorProject("default", home.Root).ResolveWorkspace().OrchestratorDirectory, Model());
        }
        const string manifest = """{"engine":{"maxConcurrentShards":1,"slotCount":1},"checks":[{"type":"dotnet-test","project":"sample.csproj","arguments":["--verbosity","minimal"]}]}""";
        var manifestPath = Manifest(source, manifest);
        var before = Snapshot(home.Root, source.Root, registry.DataRootDirectory);
        using var output = new StringWriter();
        string[] selection = scenario switch
        {
            "unknown" => ["unknown-project"],
            "unknown-active" or "no-active" => [],
            _ => ["sample"]
        };

        Assert.Equal(0, Execute(registry, home.Root, manifestPath, output, selection,
            scenario == "unknown-active" ? "also-unknown" : null));

        var expected = string.Concat(ManifestEngineSettingsComparer.Compare(manifest).Select(fact =>
            $"{fact.Kind} {fact.Fact} hand={fact.Hand} learned={fact.Learned} ({fact.Reason}){Environment.NewLine}"));
        Assert.Equal(expected, output.ToString());
        Assert.Equal(12, Lines(output).Length);
        Assert.DoesNotContain("OwnerQuestion", output.ToString());
        Assert.DoesNotContain("Learned model:", output.ToString());
        AssertUnchanged(before, home.Root, source.Root, registry.DataRootDirectory);
    }

    private static void AssertOwnerQuestions(string[] lines, bool missing)
    {
        var expected = missing
            ? new[] { "engine/timeouts/defaultMinutes", "engine/timeouts/buildServerShutdownMinutes", "engine/timeouts/discoveryMinutes" }
            : new[] { "engine/timeouts/buildServerShutdownMinutes", "engine/timeouts/discoveryMinutes" };
        Assert.Equal(expected, lines.Where(line => line.StartsWith("OwnerQuestion ", StringComparison.Ordinal))
            .Select(line => line.Split(' ')[1]));
        Assert.All(lines.TakeLast(2), line => Assert.Contains("Discovery measures neither", line));
    }

    private static ProjectModel Model()
    {
        var ids = new[] { "fast", "slow" };
        return new(ProjectModel.CurrentSchemaVersion, ".",
            ids.Select(id => new ProjectUnit(id, Fact(id), Fact(id), Fact<bool?>(true))).ToArray(), [], [], [],
            ids.Select(id => new UnitCommands(id, Fact("build"), Fact("test"))).ToArray(), [],
            [new("fast", null, Fact(100d)), new("slow", null, Fact(200d))], []);
    }

    private static ProjectFact<T> Fact<T>(T value) =>
        new(value, new FactSource(measurementReference: "fixture"), FactConfidence.High);
    private static void Store(string directory, ProjectModel model)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "project-model.json"), ProjectModelJson.Serialize(model));
    }

    private static string Manifest(ProjectOnboardingFixture source, string json)
    {
        var path = Path.Combine(source.Root, "manifest.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static OrchestratorProjectRegistry Registry(ProjectOnboardingFixture home) =>
        new(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
    private static int Execute(OrchestratorProjectRegistry registry, string root, string manifest, StringWriter output,
        string[] options, string? active = null) => ProjectCliCommand.Execute(
            ["project", "compare-manifest-engine", .. options, "--manifest", manifest], registry, root, active, output);
    private static string[] Lines(StringWriter output) =>
        output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    private static Dictionary<string, string> Snapshot(params string[] roots) => roots.Distinct(StringComparer.Ordinal)
        .SelectMany(root => Directory.Exists(root)
            ? Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root) : [])
        .Distinct(StringComparer.Ordinal).ToDictionary(path => path,
            path => Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)));
    private static void AssertUnchanged(Dictionary<string, string> before, params string[] roots)
    {
        var after = Snapshot(roots);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }
}
