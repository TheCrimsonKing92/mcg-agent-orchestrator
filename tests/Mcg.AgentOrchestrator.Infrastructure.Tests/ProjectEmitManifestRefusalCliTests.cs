using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private temp copies and registries; measurement never launches a process.
public sealed class ProjectEmitManifestRefusalCliTests
{
    [Fact]
    public void Emit_UnansweredMeasuredModel_RefusesUntilBothAnswersAreRecorded()
    {
        using var fixture = new Fixture();
        fixture.Discover(measured: true);
        var model = ProjectModelJson.Deserialize(File.ReadAllText(fixture.ModelPath));
        var learned = LearnedEngineSettingsDeriver.Derive(model);
        Assert.NotNull(learned.DefaultTimeoutMinutes);
        Assert.Equal(2, learned.OwnerQuestions.Count);
        Assert.False(File.Exists(fixture.AnswersPath));
        var before = fixture.Snapshot();
        var (exit, lines) = fixture.Emit(fixture.Destination);

        Assert.Equal(1, exit);
        string[] expected = ["Refused: 2 facts the gate needs are unanswered",
            .. learned.OwnerQuestions.Select(question => $"Blocked {question.FactKey} {question.Question}"),
            .. learned.OwnerQuestions.Select(question => $"OwnerQuestion {question.FactKey} {question.Question}")];
        Assert.Equal(expected, lines);
        Assert.False(File.Exists(fixture.Destination));
        Assert.False(Directory.Exists(fixture.Destination));
        fixture.AssertUnchanged(before);

        fixture.Answer("buildServerShutdownMinutes", "3");
        before = fixture.Snapshot();
        (exit, lines) = fixture.Emit(fixture.Destination);
        Assert.Equal(1, exit);
        Assert.Equal("Refused: 1 facts the gate needs are unanswered", lines[0]);
        Assert.StartsWith("Blocked engine/timeouts/discoveryMinutes ", lines[1]);
        Assert.StartsWith("OwnerQuestion engine/timeouts/discoveryMinutes ", lines[2]);
        Assert.Equal(3, lines.Length);
        fixture.AssertUnchanged(before);

        fixture.Answer("discoveryMinutes", "4");
        (exit, lines) = fixture.Emit(fixture.Destination);
        Assert.Equal(0, exit);
        Assert.Equal($"Emitted manifest: {fixture.Destination} (2 checks)", Assert.Single(lines));
        Assert.True(File.Exists(fixture.Destination));
        Assert.Empty(Directory.GetFiles(fixture.Scratch.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("existing-file", "destination already exists")]
    [InlineData("existing-directory", "destination already exists")]
    [InlineData("missing-parent", "parent directory does not exist")]
    [InlineData("source-root", "destination is inside the project source root")]
    [InlineData("model-directory", "destination is inside the model directory")]
    [InlineData("reserved-name", "destination file name acceptance-manifest.json is reserved")]
    [InlineData("source-traversal", "destination is inside the project source root")]
    [InlineData("model-case", "destination is inside the model directory")]
    public void Emit_UnsafeDestination_RefusesWithoutAnyMutation(string scenario, string reason)
    {
        using var fixture = new Fixture();
        fixture.Discover();
        fixture.AnswerAll();
        File.WriteAllText(fixture.Destination, "original bytes");
        var destination = scenario switch
        {
            "existing-file" => fixture.Destination,
            "existing-directory" => fixture.Scratch.Root,
            "missing-parent" => Path.Combine(fixture.Scratch.Root, "missing", "learned.json"),
            "source-root" => Path.Combine(fixture.Source.Root, "learned.json"),
            "model-directory" => Path.Combine(fixture.ModelDirectory, "learned.json"),
            "source-traversal" => Path.Combine(fixture.Source.Root, "tests", "..", "learned.json"),
            "model-case" => Path.Combine(fixture.ModelDirectory.ToUpperInvariant(), "learned.json"),
            _ => Path.Combine(fixture.Scratch.Root, "ACCEPTANCE-MANIFEST.JSON")
        };
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Emit(destination);

        Assert.Equal(1, exit);
        Assert.Equal($"Refused: {reason} {Path.GetFullPath(destination)}", Assert.Single(lines));
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("missing-out")]
    [InlineData("missing-value")]
    [InlineData("empty-value")]
    [InlineData("option-as-value")]
    [InlineData("unknown-flag")]
    [InlineData("short-flag")]
    [InlineData("duplicate-out")]
    [InlineData("two-names")]
    [InlineData("unregistered")]
    public void Emit_InvalidArguments_ThrowsUsageWithoutAnyMutation(string scenario)
    {
        using var fixture = new Fixture();
        fixture.Discover();
        fixture.AnswerAll();
        var path = fixture.Destination;
        string[] options = scenario switch
        {
            "missing-out" => ["sample"],
            "missing-value" => ["sample", "--out"],
            "empty-value" => ["sample", "--out="],
            "option-as-value" => ["sample", "--out", "--force"],
            "unknown-flag" => ["sample", "--out", path, "--force"],
            "short-flag" => ["-x", "--out", path],
            "duplicate-out" => ["sample", "--out", path, "--out=" + path],
            "two-names" => ["sample", "sample", "--out", path],
            _ => ["unknown-project", "--out", path]
        };
        var before = fixture.Snapshot();
        using var output = new StringWriter();

        var exception = Assert.Throws<ArgumentException>(() => ProjectCliCommand.Execute(
            ["project", "emit-manifest", .. options], fixture.Registry, fixture.Home.Root, null, output));

        Assert.Contains(ProjectEmitManifestCommand.Usage, exception.Message);
        Assert.Empty(output.ToString());
        fixture.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("missing-model")]
    [InlineData("malformed-model")]
    [InlineData("invalid-schema")]
    [InlineData("unreadable-answers")]
    public void Emit_UnreadableInputs_RefusesWithoutAnyMutation(string scenario)
    {
        using var fixture = new Fixture();
        fixture.Discover();
        fixture.AnswerAll();
        switch (scenario)
        {
            case "missing-model": File.Delete(fixture.ModelPath); break;
            case "malformed-model": File.WriteAllText(fixture.ModelPath, "{"); break;
            case "invalid-schema": File.WriteAllText(fixture.ModelPath, "{}"); break;
            default: File.WriteAllText(fixture.AnswersPath, "{"); break;
        }
        var before = fixture.Snapshot();

        var (exit, lines) = fixture.Emit(fixture.Destination);

        Assert.Equal(1, exit);
        var line = Assert.Single(lines);
        if (scenario == "unreadable-answers") Assert.StartsWith("Owner answers unreadable: ", line);
        else
        {
            Assert.StartsWith("Refused: ", line);
            Assert.Contains("project discover", line);
        }
        fixture.AssertUnchanged(before);
    }

    [Fact]
    public void Emit_UnsafeDestinationAndUnreadableModel_DestinationGuardWins()
    {
        using var fixture = new Fixture();
        fixture.Discover();
        File.WriteAllText(fixture.ModelPath, "{");
        var destination = Path.Combine(fixture.Scratch.Root, "acceptance-manifest.json");
        var before = fixture.Snapshot();
        var (exit, lines) = fixture.Emit(destination);
        Assert.Equal(1, exit);
        Assert.Equal($"Refused: destination file name acceptance-manifest.json is reserved {destination}", Assert.Single(lines));
        fixture.AssertUnchanged(before);
    }

    private sealed class Fixture : IDisposable
    {
        public ProjectOnboardingFixture Home { get; } = new("no-solution");
        public ProjectOnboardingFixture Source { get; } = new("answer-key-repository");
        public ProjectOnboardingFixture Scratch { get; } = new("no-solution");
        public OrchestratorProjectRegistry Registry { get; }
        public string ModelDirectory { get; }
        public string ModelPath => Path.Combine(ModelDirectory, "project-model.json");
        public string AnswersPath => Path.Combine(ModelDirectory, OwnerAnswerFile.FileName);
        public string Destination => Path.Combine(Scratch.Root, "learned.json");

        public Fixture()
        {
            Registry = new(Path.Combine(Home.Root, "registry"), Path.Combine(Home.Root, "data"));
            ModelDirectory = Registry.CreateProject("sample", Source.Root).ResolveWorkspace().OrchestratorDirectory;
            Directory.CreateDirectory(ModelDirectory);
            File.Copy(Path.Combine(Source.Root, "hand-acceptance-manifest.json"), Path.Combine(ModelDirectory, "acceptance-manifest.json"));
        }

        public void Discover(bool measured = false)
        {
            using var output = new StringWriter();
            var measurer = new RecordingUnitCommandMeasurer();
            Assert.Equal(0, ProjectCliCommand.Execute(measured
                ? ["project", "discover", "sample", "--measure", "test"] : ["project", "discover", "sample"],
                Registry, Home.Root, null, output, () => measurer));
            Assert.True(File.Exists(ModelPath));
            if (measured)
            {
                Assert.Equal(2, measurer.Calls.Count);
                Assert.All(measurer.Calls, call => Assert.Equal(UnitCommandKinds.Test, call.Kind));
            }
        }

        public void Answer(string key, string value)
        {
            using var output = new StringWriter();
            Assert.Equal(0, ProjectCliCommand.Execute(["project", "answer", "sample", "engine/timeouts/" + key, value],
                Registry, Home.Root, null, output));
        }
        public void AnswerAll()
        {
            Answer("defaultMinutes", "17");
            Answer("buildServerShutdownMinutes", "3");
            Answer("discoveryMinutes", "4");
        }
        public (int Exit, string[] Lines) Emit(string destination)
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(["project", "emit-manifest", "sample", "--out", destination],
                Registry, Home.Root, null, output);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
        public Dictionary<string, string> Snapshot() => new[] { Home.Root, Source.Root, Scratch.Root }
            .SelectMany(root => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
            .ToDictionary(path => path, path => Directory.Exists(path) ? "directory"
                : $"{new FileInfo(path).Length}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}");
        public void AssertUnchanged(Dictionary<string, string> before)
        {
            var after = Snapshot();
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
            foreach (var (path, hash) in before) Assert.Equal(hash, after[path]);
        }
        public void Dispose()
        {
            Scratch.Dispose();
            Source.Dispose();
            Home.Dispose();
        }
    }
}
