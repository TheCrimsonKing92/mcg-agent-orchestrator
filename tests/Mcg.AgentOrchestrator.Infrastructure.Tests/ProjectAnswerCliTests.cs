using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its source, registry, data root, clock and writers.
public sealed class ProjectAnswerCliTests
{
    private const string SdkKey = "environment/dotnet-sdk";
    private const string ShutdownKey = "engine/timeouts/buildServerShutdownMinutes";
    private const string DiscoveryKey = "engine/timeouts/discoveryMinutes";
    private const string Stamp = "owner answer recorded 2026-10-11T12:34:56Z";
    private static readonly DateTimeOffset Recorded = new(2026, 10, 11, 12, 34, 56, TimeSpan.Zero);

    [Theory]
    [InlineData("named", false)]
    [InlineData("selected", false)]
    [InlineData("override", true)]
    [InlineData("flag-first", true)]
    public void Answer_ProjectSelection_RecordsPinnedTimeAndVerbatimReason(string selection, bool equals)
    {
        using var project = new TestProject();
        project.Store(Model());
        project.Registry.SelectProject(selection == "override" ? "default" : "sample");
        const string reason = "  measured by hand  ";
        string[] positional = selection is "named" or "flag-first"
            ? ["sample", DiscoveryKey, "003"] : [DiscoveryKey, "003"];
        string[] flag = equals ? ["--reason=" + reason] : ["--reason", reason];
        string[] options = selection == "flag-first" ? [.. flag, .. positional] : [.. positional, .. flag];
        var before = project.Snapshot();
        using var output = new StringWriter();
        var calls = 0;

        Assert.Equal(0, ProjectAnswerCommand.Execute(["project", "answer", .. options],
            project.Registry, project.Home.Root, selection == "override" ? "sample" : null, output,
            () => { calls++; return Recorded.AddMilliseconds(987).ToOffset(TimeSpan.FromHours(-5)); }));

        Assert.Equal(1, calls);
        Assert.Equal($"Recorded owner answer: {DiscoveryKey} = 3 (High, {Stamp}){Environment.NewLine}", output.ToString());
        var answer = Assert.Single(OwnerAnswerFile.Read(project.Directory));
        Assert.Equal(new OwnerAnswer(DiscoveryKey, "3", reason, Recorded), answer);
        Assert.Equal(TimeSpan.Zero, answer.RecordedUtc.Offset);
        project.AssertOnlyAnswerChanged(before);
    }

    [Fact]
    public void Answer_SdkThenReplacement_PreservesOtherKeysAndAllProtectedFiles()
    {
        using var project = new TestProject();
        project.Store(Model());
        project.WriteManifest(1, 2);
        var before = project.Snapshot();
        Assert.Equal(0, project.Answer(["sample", SdkKey, "10.0.100", "--reason=first"]).Exit);
        Assert.Equal(0, project.Answer(["sample", DiscoveryKey, "3"]).Exit);
        var result = project.Answer(["sample", SdkKey, "9.0.300-preview.1", "--reason", "later"],
            Recorded.AddSeconds(1));
        Assert.Equal(0, result.Exit);
        var answers = OwnerAnswerFile.Read(project.Directory);
        Assert.Equal(2, answers.Count);
        Assert.Equal(new OwnerAnswer(SdkKey, "9.0.300-preview.1", "later", Recorded.AddSeconds(1)),
            Assert.Single(answers.Where(answer => answer.FactKey == SdkKey)));
        Assert.Equal("3", Assert.Single(answers.Where(answer => answer.FactKey == DiscoveryKey)).Value);
        project.AssertOnlyAnswerChanged(before);
    }

    [Fact]
    public void Execute_AnswerVerb_DispatchesToAnswerFile()
    {
        using var project = new TestProject();
        project.Store(Model());
        var before = project.Snapshot();
        using var output = new StringWriter();
        Assert.Equal(0, ProjectCliCommand.Execute(["project", "answer", "sample", DiscoveryKey, "3"],
            project.Registry, project.Home.Root, null, output));
        Assert.StartsWith($"Recorded owner answer: {DiscoveryKey} = 3 (High, owner answer recorded ", output.ToString());
        var answer = Assert.Single(OwnerAnswerFile.Read(project.Directory));
        Assert.Equal("3", answer.Value);
        Assert.Equal(TimeSpan.Zero, answer.RecordedUtc.Offset);
        Assert.Equal(0, answer.RecordedUtc.Ticks % TimeSpan.TicksPerSecond);
        project.AssertOnlyAnswerChanged(before);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("sdk")]
    [InlineData("unknown-key")]
    [InlineData("not-live")]
    [InlineData("no-model")]
    [InlineData("missing-value")]
    [InlineData("unknown-flag")]
    [InlineData("repeated-flag")]
    [InlineData("unknown-project")]
    [InlineData("missing-reason")]
    [InlineData("extra-positional")]
    public void Answer_Refusal_LeavesSourceDataRegistryAndModelUnchanged(string scenario)
    {
        using var project = new TestProject();
        if (scenario != "no-model") project.Store(Model(sdkQuestion: scenario != "not-live"));
        project.WriteManifest(1, 2);
        OwnerAnswerFile.Write(project.Directory, [new(DiscoveryKey, "4", "preserve", Recorded)]);
        var before = project.Snapshot();
        string[] options = scenario switch
        {
            "timeout" => ["sample", DiscoveryKey, "0"],
            "sdk" => ["sample", SdkKey, "latest"],
            "unknown-key" => ["sample", "integration-branch", "trunk"],
            "not-live" => ["sample", SdkKey, "10.0.100"],
            "missing-value" => [DiscoveryKey],
            "unknown-flag" => ["sample", DiscoveryKey, "3", "--unknown"],
            "repeated-flag" => ["sample", DiscoveryKey, "3", "--reason", "one", "--reason=two"],
            "unknown-project" => ["unregistered", DiscoveryKey, "3"],
            "missing-reason" => ["sample", DiscoveryKey, "3", "--reason"],
            "extra-positional" => ["sample", DiscoveryKey, "3", "extra"],
            _ => ["sample", DiscoveryKey, "3"]
        };
        using var output = new StringWriter();
        if (scenario is "missing-value" or "unknown-flag" or "repeated-flag" or "unknown-project"
            or "missing-reason" or "extra-positional")
        {
            var error = Assert.Throws<ArgumentException>(() => ProjectAnswerCommand.Execute(
                ["project", "answer", .. options], project.Registry, project.Home.Root, null, output, () => Recorded));
            Assert.Contains(ProjectAnswerCommand.Usage, error.Message);
            Assert.Empty(output.ToString());
        }
        else
        {
            Assert.Equal(1, ProjectAnswerCommand.Execute(["project", "answer", .. options],
                project.Registry, project.Home.Root, null, output, () => Recorded));
            Assert.Single(Lines(output.ToString()));
            if (scenario == "no-model")
            {
                Assert.Contains(project.ModelPath, output.ToString());
                Assert.Contains("project discover", output.ToString());
            }
            else Assert.Contains(options[1], output.ToString());
        }
        project.AssertUnchanged(before);
    }

    [Fact]
    public void Discover_NoAnswerFile_PreservesCompleteOriginalOutput()
    {
        using var project = new TestProject();
        ProjectOnboardingFixture.SeedRemoteDefault(project.Source.Root);
        var result = project.Discover();
        Assert.Equal(0, result.Exit);
        var model = project.ReadModel();
        Assert.Equal(2, model.Units.Count);
        Assert.Equal(8, model.OwnerQuestions.Count);
        Assert.Equal("trunk", model.IntegrationBranch!.Value);
        var expected = new[] { $"Project model: {project.ModelPath}",
            "Integration branch: trunk (High, .git/refs/remotes/origin/HEAD:1)" }
            .Concat(model.OwnerQuestions.Select(question => $"Owner question: {question.Question}"));
        Assert.Equal(Text(expected), result.Output);
        AssertNoAnswerLines(result.Output);
        Assert.False(File.Exists(project.AnswerPath));
    }

    [Fact]
    public void Discover_AnsweredSdk_SuppressesQuestionWithoutChangingSnapshotFacts()
    {
        using var project = new TestProject();
        ProjectOnboardingFixture.SeedRemoteDefault(project.Source.Root);
        Assert.Equal(0, project.Discover().Exit);
        var original = project.ReadModel();
        var sdkQuestion = Assert.Single(original.OwnerQuestions.Where(question => question.FactKey == SdkKey));
        Assert.Equal(0, project.Answer(["sample", SdkKey, "10.0.100"]).Exit);
        var answerBytes = File.ReadAllBytes(project.AnswerPath);
        var modelBytes = File.ReadAllBytes(project.ModelPath);

        var result = project.Discover();

        Assert.Equal(0, result.Exit);
        Assert.DoesNotContain($"Owner question: {sdkQuestion.Question}", result.Output);
        var expected = new[] { $"Project model: {project.ModelPath}",
            "Integration branch: trunk (High, .git/refs/remotes/origin/HEAD:1)" }
            .Concat(original.OwnerQuestions.Where(question => question.FactKey != SdkKey)
                .Select(question => $"Owner question: {question.Question}"))
            .Append($"Owner answer: {SdkKey} = 10.0.100 (High, {Stamp})");
        Assert.Equal(Text(expected), result.Output);
        Assert.Equal(answerBytes, File.ReadAllBytes(project.AnswerPath));
        Assert.Equal(modelBytes, File.ReadAllBytes(project.ModelPath));
        Assert.Contains(project.ReadModel().OwnerQuestions, question => question.FactKey == SdkKey);
    }

    [Fact]
    public void Discover_SdkPinAdded_ReportsStaleAnswerAndPreservesAnswerFile()
    {
        using var project = new TestProject();
        Assert.Equal(0, project.Discover().Exit);
        Assert.Equal(0, project.Answer(["sample", SdkKey, "10.0.100"]).Exit);
        var bytes = File.ReadAllBytes(project.AnswerPath);
        File.WriteAllText(Path.Combine(project.Source.Root, "global.json"), """{"sdk":{"version":"10.0.100"}}""");

        var result = project.Discover();

        Assert.Equal(0, result.Exit);
        Assert.DoesNotContain(project.ReadModel().OwnerQuestions, question => question.FactKey == SdkKey);
        Assert.Contains($"Stale owner answer: {SdkKey} = 10.0.100 (the model no longer asks or derives this key)", result.Output);
        Assert.DoesNotContain($"Owner answer: {SdkKey}", result.Output);
        Assert.Equal(bytes, File.ReadAllBytes(project.AnswerPath));
        Assert.Equal("10.0.100", Assert.Single(OwnerAnswerFile.Read(project.Directory)).Value);
    }

    [Fact]
    public void Discover_HandEditedInvalidValue_ReportsValidationAndKeepsQuestion()
    {
        using var project = new TestProject();
        Assert.Equal(0, project.Discover().Exit);
        Assert.Equal(0, project.Answer(["sample", SdkKey, "10.0.100"]).Exit);
        File.WriteAllText(project.AnswerPath, File.ReadAllText(project.AnswerPath).Replace("10.0.100", "latest", StringComparison.Ordinal));
        var bytes = File.ReadAllBytes(project.AnswerPath);
        var validation = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(SdkKey, "latest"));

        var result = project.Discover();

        Assert.Equal(0, result.Exit);
        var question = Assert.Single(project.ReadModel().OwnerQuestions.Where(question => question.FactKey == SdkKey));
        Assert.Contains("Owner question: " + question.Question, result.Output);
        Assert.Contains($"Stale owner answer: {SdkKey} = latest ({validation.Message})", result.Output);
        Assert.DoesNotContain("Owner answer:", result.Output);
        Assert.Equal(bytes, File.ReadAllBytes(project.AnswerPath));
    }

    [Theory]
    [InlineData(1, 2, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(1, 25, 0)]
    [InlineData(2, 25, 1)]
    public void Compare_AnsweredShutdown_UsesOwnerFactAndExistingExitRule(int hand, int discovery, int exit)
    {
        using var project = new TestProject();
        project.Store(Model());
        project.WriteManifest(hand, discovery);
        Assert.Equal(0, project.Answer(["sample", ShutdownKey, "1"]).Exit);
        var before = project.Snapshot();

        var result = project.Compare();

        Assert.Equal(exit, result.Exit);
        var line = Assert.Single(Lines(result.Output).Where(line => line.Contains(" engine.timeouts.buildServerShutdownMinutes ", StringComparison.Ordinal)));
        Assert.Equal(hand == 1
            ? $"Match engine.timeouts.buildServerShutdownMinutes hand=1 learned=1 (Agrees with the learned model: {Stamp})"
            : $"DiffersFromHand engine.timeouts.buildServerShutdownMinutes hand=2 learned=1 (The learned model derives 1: {Stamp}. The hand manifest declares 2.)", line);
        Assert.DoesNotContain("OwnerQuestion " + ShutdownKey, result.Output);
        Assert.Contains("OwnerQuestion " + DiscoveryKey, result.Output);
        Assert.Equal($"Owner answer: {ShutdownKey} = 1 (High, {Stamp})", Lines(result.Output)[^1]);
        project.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    public void Compare_NoAnswers_PreservesCompleteMeasuredModelOutput(string scenario)
    {
        using var project = new TestProject();
        var model = Model();
        project.Store(model);
        var manifest = project.WriteManifest(1, 2);
        if (scenario == "empty") OwnerAnswerFile.Write(project.Directory, []);
        var before = project.Snapshot();

        var result = project.Compare();

        Assert.Equal(1, result.Exit);
        Assert.Equal(ExpectedOriginalComparison(manifest, model), result.Output);
        var lines = Lines(result.Output);
        Assert.Equal(14, lines.Length);
        Assert.StartsWith("Match engine.maxConcurrentShards hand=2 learned=2 (Agrees with the learned model: learned from project-model.json:", lines[0]);
        Assert.StartsWith("Match engine.timeouts.defaultMinutes hand=10 learned=10 (Agrees with the learned model: learned from project-model.json:", lines[2]);
        Assert.Equal("DiffersFromGateDefault engine.timeouts.buildServerShutdownMinutes hand=1 learned=25 (The learned value comes from the gate's undeclared default.)", lines[3]);
        Assert.Equal("DiffersFromGateDefault engine.timeouts.discoveryMinutes hand=2 learned=25 (The learned value comes from the gate's undeclared default.)", lines[4]);
        AssertNoAnswerLines(result.Output);
        project.AssertUnchanged(before);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(25, 0)]
    public void Compare_StaleAnswer_LeavesComparisonAndExitUnchanged(int discovery, int exit)
    {
        using var project = new TestProject();
        project.Store(Model());
        Assert.Equal(0, project.Answer(["sample", SdkKey, "10.0.100"]).Exit);
        project.Store(Model(sdkQuestion: false));
        var manifest = project.WriteManifest(25, discovery);
        var before = project.Snapshot();

        var result = project.Compare();

        Assert.Equal(exit, result.Exit);
        var stale = $"Stale owner answer: {SdkKey} = 10.0.100 (the model no longer asks or derives this key)";
        Assert.Equal(ExpectedOriginalComparison(manifest, project.ReadModel()) + Text([stale]), result.Output);
        Assert.DoesNotContain("Owner answer:", result.Output);
        project.AssertUnchanged(before);
    }

    [Fact]
    public void Compare_InvalidStoredTimeout_NeverInjectsNumberIntoComparison()
    {
        using var project = new TestProject();
        project.Store(Model());
        var manifest = project.WriteManifest(25, 25);
        Assert.Equal(0, project.Answer(["sample", ShutdownKey, "1"]).Exit);
        File.WriteAllText(project.AnswerPath, File.ReadAllText(project.AnswerPath).Replace("\"value\": \"1\"", "\"value\": \"0\"", StringComparison.Ordinal));
        Assert.Equal("0", Assert.Single(OwnerAnswerFile.Read(project.Directory)).Value);
        var before = project.Snapshot();

        var result = project.Compare();

        Assert.Equal(0, result.Exit);
        var error = Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(ShutdownKey, "0"));
        Assert.Equal(ExpectedOriginalComparison(manifest, project.ReadModel()) +
            Text([$"Stale owner answer: {ShutdownKey} = 0 ({error.Message})"]), result.Output);
        project.AssertUnchanged(before);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":99,\"answers\":[]}")]
    public void Compare_UnreadableAnswers_PrintsOneDiagnosticAndOriginalOutput(string json)
    {
        using var project = new TestProject();
        project.Store(Model());
        project.WriteManifest(1, 2);
        var baseline = project.Compare();
        File.WriteAllText(project.AnswerPath, json);
        var before = project.Snapshot();

        var result = project.Compare();

        Assert.Equal(baseline.Exit, result.Exit);
        var lines = Lines(result.Output);
        var errorLine = Assert.Single(lines.Where(line => line.StartsWith("Owner answers unreadable:", StringComparison.Ordinal)));
        Assert.Contains("owner-answers.json", errorLine);
        Assert.Equal(baseline.Output, Text(lines.Where(line => line != errorLine)));
        project.AssertUnchanged(before);
    }

    private static void AssertNoAnswerLines(string output)
    {
        Assert.DoesNotContain("Owner answer:", output);
        Assert.DoesNotContain("Stale owner answer:", output);
        Assert.DoesNotContain("Owner answers unreadable:", output);
    }

    private static string ExpectedOriginalComparison(string manifest, ProjectModel model)
    {
        var learned = LearnedEngineSettingsDeriver.Derive(model);
        learned = learned with
        {
            MaxConcurrentShardsReason = "learned from project-model.json: " + learned.MaxConcurrentShardsReason,
            DefaultTimeoutReason = "learned from project-model.json: " + learned.DefaultTimeoutReason
        };
        return Text(ManifestEngineSettingsComparer.Compare(manifest, learned).Select(fact =>
            $"{fact.Kind} {fact.Fact} hand={fact.Hand} learned={fact.Learned} ({fact.Reason})")
            .Concat(learned.OwnerQuestions.Select(question => $"OwnerQuestion {question.FactKey} {question.Question}")));
    }

    private static string Text(IEnumerable<string> lines) => string.Concat(lines.Select(line => line + Environment.NewLine));
    private static string[] Lines(string output) => output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    private static ProjectFact<T> Fact<T>(T value) => new(value, new FactSource(measurementReference: "fixture"), FactConfidence.High);
    private static ProjectModel Model(bool sdkQuestion = true) => new(ProjectModel.CurrentSchemaVersion, ".",
        new[] { "fast", "slow" }.Select(id => new ProjectUnit(id, Fact(id), Fact(id), Fact<bool?>(true))).ToArray(),
        [], [], sdkQuestion ? [new(SdkKey, "Which SDK?", new FactSource(measurementReference: "fixture"))] : [],
        new[] { "fast", "slow" }.Select(id => new UnitCommands(id, Fact("build"), Fact("test"))).ToArray(),
        [], [new("fast", null, Fact(100d)), new("slow", null, Fact(200d))], []);

    private sealed class TestProject : IDisposable
    {
        public ProjectOnboardingFixture Home { get; } = new("no-solution");
        public ProjectOnboardingFixture Source { get; } = new("no-solution");
        public OrchestratorProjectRegistry Registry { get; }
        public string Directory { get; }
        public string ModelPath => Path.Combine(Directory, "project-model.json");
        public string AnswerPath => Path.Combine(Directory, "owner-answers.json");
        private string ManifestPath => Path.Combine(Registry.DataRootDirectory, "hand-manifest.json");

        public TestProject()
        {
            Registry = new(Path.Combine(Home.Root, "registry"), Path.Combine(Home.Root, "data"));
            Directory = Registry.CreateProject("sample", Source.Root).ResolveWorkspace().OrchestratorDirectory;
        }

        public void Store(ProjectModel model)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(ModelPath, ProjectModelJson.Serialize(model));
        }

        public ProjectModel ReadModel() => ProjectModelJson.Deserialize(File.ReadAllText(ModelPath));
        public (int Exit, string Output) Answer(string[] options, DateTimeOffset? recorded = null)
        {
            using var output = new StringWriter();
            var exit = ProjectAnswerCommand.Execute(["project", "answer", .. options], Registry,
                Home.Root, null, output, () => recorded ?? Recorded);
            return (exit, output.ToString());
        }

        public (int Exit, string Output) Discover()
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(["project", "discover", "sample"], Registry,
                Home.Root, null, output, () => throw new InvalidOperationException("Unexpected measurement"));
            return (exit, output.ToString());
        }

        public (int Exit, string Output) Compare()
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(["project", "compare-manifest-engine", "sample", "--manifest", ManifestPath],
                Registry, Home.Root, null, output);
            return (exit, output.ToString());
        }

        public string WriteManifest(int shutdown, int discovery)
        {
            var json = JsonSerializer.Serialize(new
            {
                engine = new { slotCount = 1, maxConcurrentShards = 2, timeouts = new
                    { defaultMinutes = 10, buildServerShutdownMinutes = shutdown, discoveryMinutes = discovery } },
                checks = new[] { new { type = "dotnet-test", project = "sample.csproj", arguments = new[] { "--verbosity", "minimal" } } }
            });
            System.IO.Directory.CreateDirectory(Registry.DataRootDirectory);
            File.WriteAllText(ManifestPath, json);
            return json;
        }

        public Dictionary<string, string> Snapshot() => new[] { Home.Root, Source.Root }
            .SelectMany(root => System.IO.Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root))
            .ToDictionary(path => path, path => System.IO.Directory.Exists(path) ? "directory" : Convert.ToBase64String(File.ReadAllBytes(path)),
                StringComparer.Ordinal);

        public void AssertUnchanged(Dictionary<string, string> before) => AssertSnapshot(before, Snapshot());
        public void AssertOnlyAnswerChanged(Dictionary<string, string> before)
        {
            var after = Snapshot();
            Assert.True(after.Remove(AnswerPath), "Successful answer must create or replace owner-answers.json.");
            before.Remove(AnswerPath);
            AssertSnapshot(before, after);
        }

        private static void AssertSnapshot(Dictionary<string, string> before, Dictionary<string, string> after)
        {
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
            foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
        }

        public void Dispose()
        {
            Source.Dispose();
            Home.Dispose();
        }
    }
}
