using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every case owns both source copies, registry, data root and output directory.
public sealed class ProjectCheckTimeoutAnswerCliTests
{
    private const string Unit = "tests/Loose.UndeterminedTests/Loose.UndeterminedTests.csproj";
    private const string Key = "commands/" + Unit + "/timeoutMinutes";

    [Fact]
    public void Execute_UnitTimeout_EmitsAndComparesOwnerAnswer()
    {
        using var project = new TestProject();
        project.DiscoverAndAnswerPrerequisites("sample");

        var answer = project.Execute(["project", "answer", "sample", Key, "25"]);
        Assert.Equal(0, answer.Exit);
        Assert.StartsWith($"Recorded owner answer: {Key} = 25 ", Assert.Single(answer.Lines));
        var emitted = project.Execute(["project", "emit-manifest", "sample", "--out", project.Destination]);
        Assert.Equal(0, emitted.Exit);
        Assert.Equal($"Emitted manifest: {project.Destination} (1 checks)", Assert.Single(emitted.Lines));
        var json = File.ReadAllText(project.Destination);
        _ = AcceptanceGateEngineSettings.Parse(json);
        var manifest = JsonSerializer.Deserialize<GoalAcceptanceVerifier.AcceptanceManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);
        var check = Assert.Single(manifest.Checks);
        Assert.Equal(Unit, check.Project);
        Assert.Equal("dotnet-test", check.Type);
        Assert.Equal("vstest", check.Runner);
        Assert.Equal(25, check.TimeoutMinutes);
        Assert.Contains(project.Compare("sample", project.Destination).Lines,
            line => line.StartsWith($"Match check[{Unit}].timeoutMinutes hand=25 learned=25", StringComparison.Ordinal));

        var changed = JsonNode.Parse(json)!.AsObject();
        changed["checks"]![0]!["timeoutMinutes"] = 30;
        var differentPath = Path.Combine(project.OutputRoot, "different.json");
        File.WriteAllText(differentPath, changed.ToJsonString());
        Assert.Contains(project.Compare("sample", differentPath).Lines,
            line => line.StartsWith($"DiffersFromHand check[{Unit}].timeoutMinutes hand=30 learned=25", StringComparison.Ordinal));

        project.DiscoverAndAnswerPrerequisites("unanswered");
        Assert.Contains(project.Compare("unanswered", project.Destination).Lines,
            line => line.StartsWith($"DiffersFromGateDefault check[{Unit}].timeoutMinutes hand=25 learned=none", StringComparison.Ordinal));

        var prior = File.ReadAllBytes(project.AnswerPath);
        var refused = project.Execute(["project", "answer", "sample", Key, "0"]);
        Assert.Equal(1, refused.Exit);
        Assert.StartsWith("Refused: ", Assert.Single(refused.Lines));
        Assert.Contains(Key, refused.Lines[0]);
        Assert.Equal(prior, File.ReadAllBytes(project.AnswerPath));
    }

    private sealed class TestProject : IDisposable
    {
        private readonly ProjectOnboardingFixture home = new("no-solution");
        private readonly ProjectOnboardingFixture output = new("no-solution");
        private readonly ProjectOnboardingFixture source = new("no-solution");
        private readonly ProjectOnboardingFixture unansweredSource = new("no-solution");
        private readonly OrchestratorProjectRegistry registry;
        public string OutputRoot => output.Root;
        public string Destination => Path.Combine(OutputRoot, "learned.json");
        public string AnswerPath { get; }

        public TestProject()
        {
            registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
            var modelDirectory = registry.CreateProject("sample", source.Root).ResolveWorkspace().OrchestratorDirectory;
            AnswerPath = Path.Combine(modelDirectory, OwnerAnswerFile.FileName);
            registry.CreateProject("unanswered", unansweredSource.Root);
        }

        public (int Exit, string[] Lines) Execute(string[] parts)
        {
            using var writer = new StringWriter();
            var exit = ProjectCliCommand.Execute(parts, registry, home.Root, null, writer);
            return (exit, writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        public (int Exit, string[] Lines) Compare(string name, string manifest) =>
            Execute(["project", "compare-manifest-engine", name, "--manifest", manifest]);

        public void DiscoverAndAnswerPrerequisites(string name)
        {
            Assert.Equal(0, Execute(["project", "discover", name]).Exit);
            foreach (var (key, value) in new[] { ("defaultMinutes", "17"),
                ("buildServerShutdownMinutes", "3"), ("discoveryMinutes", "4") })
                Assert.Equal(0, Execute(["project", "answer", name, "engine/timeouts/" + key, value]).Exit);
            Assert.Equal(0, Execute(["project", "answer", name, "commands/" + Unit + "/test", "VSTest"]).Exit);
        }

        public void Dispose()
        {
            unansweredSource.Dispose();
            source.Dispose();
            output.Dispose();
            home.Dispose();
        }
    }
}
