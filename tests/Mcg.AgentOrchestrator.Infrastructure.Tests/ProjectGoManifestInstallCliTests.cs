using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// All project, model and output paths belong to this class's private temporary roots.
// The CLI is invoked in process with a stored model; discovery and tool processes are never used.
[Collection("ProjectGoManifestInstallCli")]
public sealed class ProjectGoManifestInstallCliTests
{
    // Install's process-wide fault hook is replaced by ProjectInstallManifestCliTests.
    [CollectionDefinition("ProjectGoManifestInstallCli", DisableParallelization = true)]
    [ProcessLocalTestCollection("Install fault hooks are process-local; all project and output paths use private temporary roots.")]
    public sealed class SerialCollection;

    [Fact]
    public void HandGoManifestInstallsAsTheLiveManifest()
    {
        using var fixture = new Fixture();
        fixture.RecordAnswers();
        Assert.False(File.Exists(fixture.Live));

        var emitted = fixture.Execute(["project", "emit-manifest", "sample", "--out", fixture.Emitted]);

        Assert.Equal(0, emitted.Exit);
        Assert.Equal($"Emitted manifest: {fixture.Emitted} (3 checks)", Assert.Single(emitted.Lines));
        Assert.Equal(Encoding.UTF8.GetBytes(HandManifest), File.ReadAllBytes(fixture.Emitted));
        File.WriteAllText(fixture.Hand, HandManifest, new UTF8Encoding(false));

        var installed = fixture.Execute(["project", "install-manifest", "sample", "--from", fixture.Hand]);

        Assert.Equal(0, installed.Exit);
        Assert.Equal($"Installed manifest: {fixture.Live} (3 checks; no previous manifest)", Assert.Single(installed.Lines));
        Assert.Equal(Encoding.UTF8.GetBytes(HandManifest), File.ReadAllBytes(fixture.Live));
        Assert.Equal(File.ReadAllBytes(fixture.Hand), File.ReadAllBytes(fixture.Live));
        Assert.Empty(Directory.GetFiles(fixture.ModelDirectory, "*.bak-*"));
        Assert.Empty(Directory.GetFiles(fixture.ModelDirectory, "*.tmp"));
    }

    [Fact]
    public void ChangedGoManifestIsRefusedWithoutInstalling()
    {
        using var fixture = new Fixture();
        fixture.RecordAnswers();
        File.WriteAllText(fixture.Hand, HandManifest.Replace("-count=1", "-count=2", StringComparison.Ordinal),
            new UTF8Encoding(false));

        var installed = fixture.Execute(["project", "install-manifest", "sample", "--from", fixture.Hand]);

        Assert.Equal(1, installed.Exit);
        Assert.StartsWith("Refused: --from differs from the current learned manifest at byte offset ", Assert.Single(installed.Lines));
        Assert.False(File.Exists(fixture.Live));
        Assert.Empty(Directory.GetFiles(fixture.ModelDirectory, "*.bak-*"));
        Assert.Empty(Directory.GetFiles(fixture.ModelDirectory, "*.tmp"));
    }

    private sealed class Fixture : IDisposable
    {
        private ProjectOnboardingFixture Home { get; } = new("no-solution");
        private ProjectOnboardingFixture Source { get; } = new("no-solution");
        private ProjectOnboardingFixture Scratch { get; } = new("no-solution");
        private OrchestratorProjectRegistry Registry { get; }
        public string ModelDirectory { get; }
        public string Live => Path.Combine(ModelDirectory, "acceptance-manifest.json");
        public string Emitted => Path.Combine(Scratch.Root, "learned.json");
        public string Hand => Path.Combine(Scratch.Root, "hand.json");

        public Fixture()
        {
            Registry = new(Path.Combine(Home.Root, "registry"), Path.Combine(Home.Root, "data"));
            ModelDirectory = Registry.CreateProject("sample", Source.Root).ResolveWorkspace().OrchestratorDirectory;
            Directory.CreateDirectory(ModelDirectory);
            File.WriteAllText(Path.Combine(ModelDirectory, "project-model.json"), ProjectModelJson.Serialize(Model()),
                new UTF8Encoding(false));
        }

        public void RecordAnswers()
        {
            foreach (var answer in Answers())
            {
                var result = Execute(["project", "answer", "sample", answer.FactKey, answer.Value]);
                Assert.Equal(0, result.Exit);
            }
        }

        public (int Exit, string[] Lines) Execute(string[] parts)
        {
            using var output = new StringWriter();
            var exit = ProjectCliCommand.Execute(parts, Registry, Home.Root, null, output);
            return (exit, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }

        public void Dispose()
        {
            Scratch.Dispose();
            Source.Dispose();
            Home.Dispose();
        }
    }

    private static ProjectModel Model(string coreRunner = "go") => new(ProjectModel.CurrentSchemaVersion, ".",
        [
            new("cmd/tool", Fact("cmd/tool"), Fact("cmd/tool"), Fact<bool?>(true)),
            new("internal/core", Fact("internal/core"), Fact("internal/core"), Fact<bool?>(true)),
            new("internal/util", Fact("internal/util"), Fact("internal/util"), Fact<bool?>(false))
        ], [],
        [
            new("cmd/tool", Fact("go test"), Fact("go")),
            new("internal/core", Fact("go test"), Fact(coreRunner))
        ], [],
        [
            new("cmd/tool", Fact("go build -o NUL ./cmd/tool"), Fact("go test ./cmd/tool")),
            new("internal/core", Fact("go build ./internal/core"), Fact("go test ./internal/core")),
            new("internal/util", Fact("go build ./internal/util"), null)
        ], [],
        [new("cmd/tool", null, Fact(140d)), new("internal/core", null, Fact(30d))],
        [new("cmd/tool", "go-test-builds-binary", Fact("go-test-builds-binary"))],
        Stack: ProjectStack.Go);

    private static ProjectFact<T> Fact<T>(T value) =>
        new(value, new FactSource(measurementReference: "fixture"), FactConfidence.High);

    private static OwnerAnswer[] Answers() =>
    [
        Answer("engine/timeouts/buildServerShutdownMinutes", "3"),
        Answer("engine/timeouts/discoveryMinutes", "4")
    ];

    private static OwnerAnswer Answer(string key, string value) =>
        new(key, value, "owner choice", new DateTimeOffset(2026, 10, 11, 12, 34, 56, TimeSpan.Zero));

    private static string HandManifest => ExpectedManifest.Replace("\r\n", "\n") + "\n";

    private const string ExpectedManifest = """
        {
          "version": 1,
          "engine": {
            "maxConcurrentShards": 1,
            "enforceStructuralCoverage": false,
            "timeouts": {
              "defaultMinutes": 7,
              "buildServerShutdownMinutes": 3,
              "discoveryMinutes": 4
            }
          },
          "checks": [
            {
              "name": "go build",
              "type": "command",
              "command": "go",
              "arguments": [
                "build",
                "-buildvcs=false",
                "./..."
              ]
            },
            {
              "name": "go vet",
              "type": "command",
              "command": "go",
              "arguments": [
                "vet",
                "./..."
              ]
            },
            {
              "name": "go test",
              "type": "command",
              "command": "go",
              "arguments": [
                "test",
                "-count=1",
                "./..."
              ]
            }
          ],
          "forbiddenChangedPathGlobs": []
        }
        """;
}
