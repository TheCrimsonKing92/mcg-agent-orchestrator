using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoVerificationKindTests
{
    [Fact]
    public void GoModelEmitsTheGoCommandChecksByteForByte()
    {
        var result = LearnedAcceptanceManifest.Build(Model(), Answers());

        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        Assert.Equal(Encoding.UTF8.GetBytes(HandManifest), Encoding.UTF8.GetBytes(result.Json));
        var reversed = LearnedAcceptanceManifest.Build(Model(), Answers().Reverse().ToArray());
        Assert.Empty(reversed.Blockers);
        Assert.NotNull(reversed.Json);
        Assert.Equal(Encoding.UTF8.GetBytes(result.Json), Encoding.UTF8.GetBytes(reversed.Json));
    }

    [Fact]
    public void GoDeriverMarksGoChecksAndBlocksOtherRunners()
    {
        var (checks, unresolved) = LearnedTestCheckDeriver.Derive(Model());

        Assert.Empty(unresolved);
        Assert.Equal(new[] { "cmd/tool", "internal/core" }, checks.Select(check => check.ProjectPath));
        Assert.All(checks, check =>
        {
            Assert.Equal("go", check.Runner);
            Assert.Equal(LearnedTestCheckKind.GoTest, check.Kind);
            Assert.Equal(FactConfidence.High, check.Confidence);
            Assert.Equal("fixture", check.Source.MeasurementReference);
        });

        var result = LearnedAcceptanceManifest.Build(Model("VSTest"), []);
        Assert.Null(result.Json);
        Assert.Equal(new[]
        {
            "commands/internal/core/test", "engine/timeouts/buildServerShutdownMinutes", "engine/timeouts/discoveryMinutes"
        }, result.Blockers.Select(blocker => blocker.FactKey));
        Assert.Equal("no confident go runner for internal/core", result.Blockers[0].Reason);
    }

    [Fact]
    public void DotnetModelStillRefusesTheGoRunner()
    {
        var model = Model() with { Stack = ProjectStack.Dotnet };
        Assert.Equal(2, LearnedTestCheckDeriver.Derive(model with { Stack = ProjectStack.Go }).Checks.Count);
        var (checks, unresolved) = LearnedTestCheckDeriver.Derive(model);

        Assert.Empty(checks);
        Assert.Equal(new[] { "cmd/tool", "internal/core" }, unresolved.Select(unit => unit.UnitId));
        var result = LearnedAcceptanceManifest.Build(model, Answers());
        Assert.Null(result.Json);
        Assert.Equal(new[]
        {
            "no confident VSTest or MTP runner for cmd/tool",
            "no confident VSTest or MTP runner for internal/core"
        }, result.Blockers.Select(blocker => blocker.Reason));
    }

    [Fact]
    public void MeasurerAdmitsOnlyDotnetAndGo()
    {
        Assert.Equal("go", ProcessUnitCommandMeasurer.MeasurableTool("go test ./internal/core"));
        Assert.Equal("go", ProcessUnitCommandMeasurer.MeasurableTool("go build -o NUL ./cmd/tool"));
        Assert.Equal("dotnet", ProcessUnitCommandMeasurer.MeasurableTool("dotnet build src/A/A.csproj"));
        foreach (var command in new[] { "gofmt -l .", "go.exe test ./...", "npm test", "" })
            Assert.Null(ProcessUnitCommandMeasurer.MeasurableTool(command));

        using var root = new ProjectOnboardingFixture("no-solution");
        var result = new ProcessUnitCommandMeasurer().Measure(root.Root, "internal/core", UnitCommandKinds.Test, "npm test");
        Assert.Null(result.Seconds);
        Assert.Equal("Only learned dotnet or go commands can be measured.", result.Failure);
    }

    [Theory]
    [InlineData("  \"go\" test \"./a b\"", "go")]
    [InlineData("\"dotnet\" build \"src/A B/A.csproj\"", "dotnet")]
    [InlineData("\"go test\" ./...", null)]
    [InlineData("Go test ./...", null)]
    public void ToolAdmissionUsesQuoteAwareExactWords(string command, string? expected) =>
        Assert.Equal(expected, ProcessUnitCommandMeasurer.MeasurableTool(command));

    [Fact]
    public void ToolAdmissionRejectsUnmatchedQuotes()
    {
        Assert.Throws<ArgumentException>(() => ProcessUnitCommandMeasurer.MeasurableTool("go test \""));
    }

    [Theory]
    [InlineData(FactConfidence.High, true)]
    [InlineData(FactConfidence.Medium, true)]
    [InlineData(FactConfidence.Low, false)]
    public void GoRunnerUsesTheExistingConfidenceThreshold(FactConfidence confidence, bool accepted)
    {
        var model = Model();
        model = model with
        {
            TestSetups = model.TestSetups.Select(setup => setup with
            {
                Runner = new ProjectFact<string>("go", setup.Runner.Source, confidence)
            }).ToArray()
        };

        var result = LearnedTestCheckDeriver.Derive(model);
        Assert.Equal(accepted ? 2 : 0, result.Checks.Count);
        Assert.Equal(accepted ? 0 : 2, result.Unresolved.Count);
    }

    [Fact]
    public void GoWithoutTestUnitsStillEmitsTheRepositoryChecks()
    {
        var model = Model() with { Units = [], TestSetups = [], Commands = [], Measurements = [], Hazards = [] };

        var result = LearnedAcceptanceManifest.Build(model, [.. Answers(), Answer("engine/timeouts/defaultMinutes", "7")]);

        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        using var manifest = JsonDocument.Parse(result.Json);
        Assert.Equal(new[] { "go build", "go vet", "go test" },
            manifest.RootElement.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("name").GetString()));
    }

    [Fact]
    public void GoSkipsPerProjectNamesTimeoutsAndMtpLayout()
    {
        var model = Model() with
        {
            Units =
            [
                new("a/Same.csproj", Fact("Same"), Fact("a/Same.csproj"), Fact<bool?>(true)),
                new("b/Same.csproj", Fact("Same"), Fact("b/Same.csproj"), Fact<bool?>(true))
            ],
            TestSetups =
            [
                new("a/Same.csproj", Fact("go test"), Fact("go")),
                new("b/Same.csproj", Fact("go test"), Fact("go"))
            ]
        };

        var result = LearnedAcceptanceManifest.Build(model,
            [.. Answers(), Answer("commands/a/Same.csproj/timeoutMinutes", "9")],
            new Dictionary<string, string> { ["a/Same.csproj"] = "layout refusal" });

        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        Assert.Equal(Encoding.UTF8.GetBytes(HandManifest), Encoding.UTF8.GetBytes(result.Json));
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
