using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every case owns its source copy, registry, data root and manifest output.
public sealed class ProjectCompareManifestAnswersCliTests
{
    private const string Unit = "tests/Loose.UndeterminedTests/Loose.UndeterminedTests.csproj";
    private const string Key = "commands/" + Unit + "/test";
    private const string Unresolved = "Unresolved " + Unit +
        " learned=- manifest=vstest (Runner requires owner answer: " + Key + ")";

    [Fact]
    public void Execute_AnsweredVstest_EmittedManifestMatches()
    {
        using var project = new TestProject();
        project.Discover();
        project.AnswerTimeouts();
        Assert.Equal(0, project.AnswerRunner("vstest").Exit);
        var emitted = project.Execute(["project", "emit-manifest", "sample", "--out", project.ManifestPath]);
        Assert.Equal(0, emitted.Exit);
        Assert.True(File.Exists(project.ManifestPath));

        var compared = project.Compare();
        Assert.Equal(0, compared.Exit);
        Assert.Equal($"Match {Unit} learned=vstest manifest=vstest", Assert.Single(compared.Lines));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_NoRunnerAnswer_RemainsUnresolved(bool timeoutAnswers)
    {
        using var project = new TestProject();
        project.Discover();
        if (timeoutAnswers)
        {
            project.AnswerTimeouts();
            Assert.True(File.Exists(project.AnswerPath));
        }
        else
            Assert.False(File.Exists(project.AnswerPath));
        project.WriteManifest("vstest");

        var compared = project.Compare();
        Assert.Equal(1, compared.Exit);
        Assert.Equal(Unresolved, Assert.Single(compared.Lines));
    }

    [Theory]
    [InlineData("vstest", 1, "RunnerDiffers", " (The learned and manifest runners differ.)")]
    [InlineData("mtp", 0, "Match", "")]
    public void Execute_AnsweredMtp_ComparesAnsweredRunner(
        string manifestRunner, int exit, string kind, string reason)
    {
        using var project = new TestProject();
        project.Discover();
        Assert.Equal(0, project.AnswerRunner("MTP").Exit);
        project.WriteManifest(manifestRunner);

        var compared = project.Compare();
        Assert.Equal(exit, compared.Exit);
        Assert.Equal($"{kind} {Unit} learned=mtp manifest={manifestRunner}{reason}", Assert.Single(compared.Lines));
    }

    [Fact]
    public void Execute_CorruptAnswers_AppendsDiagnosticToUnansweredComparison()
    {
        using var project = new TestProject();
        project.Discover();
        project.WriteManifest("vstest");
        Assert.False(File.Exists(project.AnswerPath));
        var baseline = project.Compare();
        Assert.Equal(1, baseline.Exit);
        Assert.Equal(Unresolved, Assert.Single(baseline.Lines));
        Assert.Equal(0, project.AnswerRunner("vstest").Exit);
        File.WriteAllText(project.AnswerPath, "not valid JSON");

        var compared = project.Compare();
        Assert.Equal(baseline.Exit, compared.Exit);
        Assert.Equal(baseline.Lines.Length + 1, compared.Lines.Length);
        Assert.Equal(baseline.Lines, compared.Lines[..^1]);
        Assert.StartsWith("Owner answers unreadable: ", compared.Lines[^1]);
    }

    private sealed class TestProject : IDisposable
    {
        private readonly ProjectOnboardingFixture home = new("no-solution");
        private readonly ProjectOnboardingFixture scratch = new("no-solution");
        private readonly ProjectOnboardingFixture source = new("no-solution");
        private readonly OrchestratorProjectRegistry registry;
        public string ManifestPath => Path.Combine(scratch.Root, "manifest.json");
        public string AnswerPath { get; }

        public TestProject()
        {
            registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
            var directory = registry.CreateProject("sample", source.Root).ResolveWorkspace().OrchestratorDirectory;
            AnswerPath = Path.Combine(directory, OwnerAnswerFile.FileName);
        }

        public (int Exit, string[] Lines) Execute(string[] parts)
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(parts, registry, home.Root, null, output);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        public void Discover() => Assert.Equal(0, Execute(["project", "discover", "sample"]).Exit);

        public (int Exit, string[] Lines) AnswerRunner(string value) =>
            Execute(["project", "answer", "sample", Key, value]);

        public (int Exit, string[] Lines) Compare() =>
            Execute(["project", "compare-manifest", "sample", "--manifest", ManifestPath]);

        public void AnswerTimeouts()
        {
            foreach (var (key, value) in new[] { ("defaultMinutes", "17"),
                ("buildServerShutdownMinutes", "3"), ("discoveryMinutes", "4") })
                Assert.Equal(0, Execute(["project", "answer", "sample", "engine/timeouts/" + key, value]).Exit);
        }

        public void WriteManifest(string runner) => File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new
        {
            checks = new[] { new { type = "dotnet-test", project = Unit, runner } }
        }));

        public void Dispose()
        {
            scratch.Dispose();
            source.Dispose();
            home.Dispose();
        }
    }
}
