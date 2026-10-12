using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every case owns its source copy, registry, data root and output directory.
public sealed class ProjectAnswerRunnerCliTests
{
    private const string Unit = "tests/Loose.UndeterminedTests/Loose.UndeterminedTests.csproj";
    private const string Key = "commands/" + Unit + "/test";

    [Fact]
    public void Execute_VstestAnswer_EmitsAndInstallsLearnedManifest()
    {
        using var project = new TestProject("no-solution");
        Assert.Equal(0, project.Execute(["project", "discover", "sample"]).Exit);
        var initial = project.Emit();
        Assert.Equal(1, initial.Exit);
        Assert.Contains(initial.Lines, line => line.StartsWith("Blocked " + Key + " ", StringComparison.Ordinal));
        Assert.False(File.Exists(project.Destination));

        project.AnswerTimeouts();
        var unsettled = project.Emit();
        Assert.Equal(1, unsettled.Exit);
        Assert.Contains("Refused: 1 facts the gate needs are unanswered", unsettled.Lines);
        Assert.False(File.Exists(project.Destination));

        var answer = project.Answer("vstest");
        Assert.Equal(0, answer.Exit);
        Assert.StartsWith($"Recorded owner answer: {Key} = VSTest (High, owner answer recorded ",
            Assert.Single(answer.Lines));
        var emitted = project.Emit();
        Assert.Equal(0, emitted.Exit);
        Assert.Equal($"Emitted manifest: {project.Destination} (1 checks)", Assert.Single(emitted.Lines));
        var bytes = File.ReadAllBytes(project.Destination);
        var json = File.ReadAllText(project.Destination);
        var manifest = JsonSerializer.Deserialize<GoalAcceptanceVerifier.AcceptanceManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(manifest);
        var check = Assert.Single(manifest.Checks);
        Assert.Equal("dotnet-test", check.Type);
        Assert.Equal(Unit, check.Project);
        Assert.Equal("vstest", check.Runner);
        _ = AcceptanceGateEngineSettings.Parse(json);

        var installed = project.Execute(["project", "install-manifest", "sample", "--from", project.Destination]);
        Assert.Equal(0, installed.Exit);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(project.ModelDirectory, "acceptance-manifest.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_InvalidRunner_PreservesAnswerFile(bool existingAnswers)
    {
        using var project = new TestProject("no-solution");
        Assert.Equal(0, project.Execute(["project", "discover", "sample"]).Exit);
        if (existingAnswers) project.AnswerTimeouts();
        var prior = project.ReadAnswerBytes();
        var refused = project.Answer("xunit");
        Assert.Equal(1, refused.Exit);
        Assert.StartsWith("Refused: ", Assert.Single(refused.Lines));
        Assert.Contains(Key, refused.Lines[0]);
        project.AssertAnswersUnchanged(prior);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_ResolvedRepository_RefusesStaleRunnerAndPreservesAnswers(bool existingAnswers)
    {
        using var project = new TestProject("answer-key-repository");
        Assert.Equal(0, project.Execute(["project", "discover", "sample"]).Exit);
        var model = ProjectModelJson.Deserialize(File.ReadAllText(Path.Combine(project.ModelDirectory, "project-model.json")));
        Assert.DoesNotContain(model.OwnerQuestions,
            question => question.FactKey.StartsWith("commands/", StringComparison.Ordinal));
        if (existingAnswers) project.AnswerTimeouts();
        var prior = project.ReadAnswerBytes();
        var refused = project.Answer("VSTest");
        Assert.Equal(1, refused.Exit);
        Assert.StartsWith("Refused: ", Assert.Single(refused.Lines));
        Assert.Contains("the model no longer asks or derives this key", refused.Lines[0]);
        project.AssertAnswersUnchanged(prior);
    }

    [Fact]
    public void Execute_MtpAnswer_BlocksInvocationsWithoutChangingAnswers()
    {
        using var project = new TestProject("no-solution");
        Assert.Equal(0, project.Execute(["project", "discover", "sample"]).Exit);
        project.AnswerTimeouts();
        var answer = project.Answer("MTP");
        Assert.Equal(0, answer.Exit);
        Assert.StartsWith($"Recorded owner answer: {Key} = MTP ", Assert.Single(answer.Lines));
        var prior = project.ReadAnswerBytes();
        Assert.NotNull(prior);
        var refused = project.Emit();
        Assert.Equal(1, refused.Exit);
        Assert.Contains("Refused: 1 facts the gate needs are unanswered", refused.Lines);
        var blocker = Assert.Single(refused.Lines.Where(line => line.StartsWith("Blocked ", StringComparison.Ordinal)));
        Assert.StartsWith("Blocked engine/mtpInvocations ", blocker);
        Assert.DoesNotContain(refused.Lines, line => line.StartsWith("Blocked commands/", StringComparison.Ordinal));
        Assert.False(File.Exists(project.Destination));
        project.AssertAnswersUnchanged(prior);
    }

    private sealed class TestProject : IDisposable
    {
        private readonly ProjectOnboardingFixture home = new("no-solution");
        private readonly ProjectOnboardingFixture scratch = new("no-solution");
        private readonly ProjectOnboardingFixture source;
        private readonly OrchestratorProjectRegistry registry;
        public string ModelDirectory { get; }
        public string Destination => Path.Combine(scratch.Root, "learned.json");
        private string AnswerPath => Path.Combine(ModelDirectory, OwnerAnswerFile.FileName);

        public TestProject(string fixture)
        {
            source = new ProjectOnboardingFixture(fixture);
            registry = new OrchestratorProjectRegistry(Path.Combine(home.Root, "registry"), Path.Combine(home.Root, "data"));
            ModelDirectory = registry.CreateProject("sample", source.Root).ResolveWorkspace().OrchestratorDirectory;
        }

        public (int Exit, string[] Lines) Execute(string[] parts)
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(parts, registry, home.Root, null, output);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        public (int Exit, string[] Lines) Answer(string value) =>
            Execute(["project", "answer", "sample", Key, value]);

        public (int Exit, string[] Lines) Emit() =>
            Execute(["project", "emit-manifest", "sample", "--out", Destination]);

        public void AnswerTimeouts()
        {
            foreach (var (key, value) in new[] { ("defaultMinutes", "17"),
                ("buildServerShutdownMinutes", "3"), ("discoveryMinutes", "4") })
                Assert.Equal(0, Execute(["project", "answer", "sample", "engine/timeouts/" + key, value]).Exit);
        }

        public byte[]? ReadAnswerBytes() => File.Exists(AnswerPath) ? File.ReadAllBytes(AnswerPath) : null;

        public void AssertAnswersUnchanged(byte[]? prior)
        {
            if (prior is null) Assert.False(File.Exists(AnswerPath));
            else Assert.Equal(prior, File.ReadAllBytes(AnswerPath));
        }

        public void Dispose()
        {
            scratch.Dispose();
            source.Dispose();
            home.Dispose();
        }
    }
}
