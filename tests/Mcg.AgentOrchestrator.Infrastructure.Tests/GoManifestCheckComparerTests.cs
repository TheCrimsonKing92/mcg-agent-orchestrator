using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoManifestCheckComparerTests
{
    [Fact]
    public void HandGoManifestMatchesEveryLearnedCheck()
    {
        var manifest = JsonNode.Parse(HandManifest)!;
        var checks = manifest["checks"]!.AsArray();
        checks.Add(JsonNode.Parse("""{"name":"whitespace","type":"command","command":"git","arguments":["diff","--check"]}"""));
        checks.Add(JsonNode.Parse("""{"name":"dotnet","type":"dotnet-test","runner":"vstest","project":"cmd/tool"}"""));

        var result = ManifestCheckComparer.Compare(Model(), manifest.ToJsonString());

        Assert.Equal(new[] { "go build", "go test", "go vet" }, result.Select(row => row.ProjectPath));
        Assert.All(result, row =>
        {
            Assert.Equal(ManifestCheckResultKind.Match, row.Kind);
            Assert.Equal(row.LearnedRunner, row.ManifestRunner);
            Assert.Empty(row.Reason);
        });
        Assert.Equal(new[] { "go build -buildvcs=false ./...", "go test -count=1 ./...", "go vet ./..." },
            result.Select(row => row.LearnedRunner));
        var model = Model();
        Assert.Equal(result, ManifestCheckComparer.Compare(model, manifest.ToJsonString(),
            OwnerAnswerApplier.Apply(model, Answers())));
    }

    [Fact]
    public void ChangedMissingAndExtraGoChecksAreReported()
    {
        var manifest = JsonNode.Parse(HandManifest)!;
        var checks = manifest["checks"]!.AsArray();
        checks[2]!["arguments"] = new JsonArray("test", "./...");
        checks.RemoveAt(1);
        checks.Add(JsonNode.Parse("""{"type":"command","command":"go","arguments":["generate","./..."]}"""));
        var json = manifest.ToJsonString();

        var result = ManifestCheckComparer.Compare(Model(), json);

        Assert.Equal(new[] { "go build", "go generate", "go test", "go vet" }, result.Select(row => row.ProjectPath));
        Assert.Equal(new[]
        {
            ManifestCheckResultKind.Match, ManifestCheckResultKind.MissingFromLearned,
            ManifestCheckResultKind.RunnerDiffers, ManifestCheckResultKind.MissingFromManifest
        }, result.Select(row => row.Kind));
        Assert.Equal("No learned go check runs go generate.", result[1].Reason);
        Assert.Null(result[1].LearnedRunner);
        Assert.Equal("go generate ./...", result[1].ManifestRunner);
        Assert.Equal("go test -count=1 ./...", result[2].LearnedRunner);
        Assert.Equal("go test ./...", result[2].ManifestRunner);
        Assert.Equal("The learned and manifest go arguments differ.", result[2].Reason);
        Assert.Equal("No manifest go command check runs go vet.", result[3].Reason);
        Assert.Equal("go vet ./...", result[3].LearnedRunner);
        Assert.Null(result[3].ManifestRunner);

        var unresolved = ManifestCheckComparer.Compare(Model("VSTest"), json);
        Assert.Equal(result, unresolved.Take(4));
        var pending = unresolved[4];
        Assert.Equal(5, unresolved.Count);
        Assert.Equal("internal/core", pending.ProjectPath);
        Assert.Equal(ManifestCheckResultKind.Unresolved, pending.Kind);
        Assert.Equal("Runner requires owner answer: commands/internal/core/test", pending.Reason);
        Assert.Null(pending.LearnedRunner);
        Assert.Null(pending.ManifestRunner);

        var model = Model("VSTest");
        Assert.Equal(unresolved, ManifestCheckComparer.Compare(model, json, OwnerAnswerApplier.Apply(model, Answers())));
    }

    [Fact]
    public void DotnetModelStillIgnoresGoCommandChecks()
    {
        var model = Model() with { Stack = ProjectStack.Dotnet };
        Assert.Equal(3, ManifestCheckComparer.Compare(model with { Stack = ProjectStack.Go }, HandManifest).Count);

        Assert.Empty(ManifestCheckComparer.Compare(model, HandManifest));
        Assert.Empty(ManifestCheckComparer.Compare(model, HandManifest, OwnerAnswerApplier.Apply(model, Answers())));
        var derived = LearnedTestCheckDeriver.Derive(Model());
        var byList = ManifestCheckComparer.Compare(derived.Checks, derived.Unresolved, HandManifest);
        Assert.Equal(new[] { "cmd/tool", "internal/core" }, byList.Select(row => row.ProjectPath));
        Assert.All(byList, row => Assert.Equal(ManifestCheckResultKind.MissingFromManifest, row.Kind));
    }

    [Theory]
    [InlineData("""{"command":"go","arguments":["vet","./..."]}""", ManifestCheckResultKind.Match)]
    [InlineData("""{"TYPE":"CoMmAnD","COMMAND":"go","ARGUMENTS":["vet","./..."]}""", ManifestCheckResultKind.Match)]
    [InlineData("""{"type":null,"command":"go","arguments":["vet","./..."]}""", ManifestCheckResultKind.MissingFromManifest)]
    [InlineData("""{"type":"dotnet-test","command":"go","arguments":["vet","./..."]}""", ManifestCheckResultKind.MissingFromManifest)]
    [InlineData("""{"type":"command","command":"Go","arguments":["vet","./..."]}""", ManifestCheckResultKind.MissingFromManifest)]
    [InlineData("""{"command":"go"}""", ManifestCheckResultKind.MissingFromManifest)]
    [InlineData("""{"command":"go","arguments":null}""", ManifestCheckResultKind.MissingFromManifest)]
    [InlineData("""{"command":"go","arguments":"vet ./..."}""", ManifestCheckResultKind.MissingFromManifest)]
    [InlineData("""{"command":"go","arguments":[]}""", ManifestCheckResultKind.MissingFromManifest)]
    [InlineData("""{"command":"go","arguments":["","vet"]}""", ManifestCheckResultKind.MissingFromManifest)]
    [InlineData("""{"command":"go","arguments":["vet",7]}""", ManifestCheckResultKind.MissingFromManifest)]
    [InlineData("""{"command":"go","arguments":["vet",null]}""", ManifestCheckResultKind.MissingFromManifest)]
    public void OnlyUsableExactGoCommandsParticipate(string check, ManifestCheckResultKind expected)
    {
        var result = ManifestCheckComparer.Compare(Model(), "{\"checks\":[" + check + "]}");

        Assert.Equal(3, result.Count);
        Assert.Equal(expected, result.Single(row => row.ProjectPath == "go vet").Kind);
    }

    [Fact]
    public void FirstUsableDeclarationWinsAndArgumentsAreOrdinal()
    {
        const string manifest = """
            {"checks":[
              {"command":"go","arguments":["test",null]},
              {"command":"go","arguments":["test","-count=1","./..."]},
              {"command":"go","arguments":["test","./..."]},
              {"command":"go","arguments":["build","-buildvcs=false","./...","extra"]},
              {"command":"go","arguments":["vet","./..."]},
              {"command":"go","arguments":["Vet","./..."]}
            ]}
            """;

        var result = ManifestCheckComparer.Compare(Model(), manifest);

        Assert.Equal(new[] { "go Vet", "go build", "go test", "go vet" }, result.Select(row => row.ProjectPath));
        Assert.Equal(ManifestCheckResultKind.MissingFromLearned, result[0].Kind);
        Assert.Equal(ManifestCheckResultKind.RunnerDiffers, result[1].Kind);
        Assert.Equal("go build -buildvcs=false ./... extra", result[1].ManifestRunner);
        Assert.Equal(ManifestCheckResultKind.Match, result[2].Kind);
        Assert.Equal("go test -count=1 ./...", result[2].ManifestRunner);
        Assert.Equal(ManifestCheckResultKind.Match, result[3].Kind);
    }

    [Fact]
    public void MissingChecksAndUnresolvedUnitsAreReportedTogether()
    {
        var result = ManifestCheckComparer.Compare(Model("VSTest"), "{}");

        Assert.Equal(new[] { "go build", "go test", "go vet", "internal/core" }, result.Select(row => row.ProjectPath));
        Assert.All(result.Take(3), row => Assert.Equal(ManifestCheckResultKind.MissingFromManifest, row.Kind));
        Assert.Equal(ManifestCheckResultKind.Unresolved, result[3].Kind);
    }

    [Theory]
    [InlineData("[]", "The manifest must be an object.")]
    [InlineData("""{"checks":null}""", "Manifest checks must be an array.")]
    [InlineData("""{"checks":[null]}""", "Each manifest check must be an object.")]
    public void InvalidEnvelopesKeepTheExistingComparerErrors(string json, string expected)
    {
        Assert.Equal(expected, Assert.Throws<JsonException>(() => ManifestCheckComparer.Compare(Model(), json)).Message);
        Assert.Equal(expected, Assert.Throws<JsonException>(() =>
            ManifestCheckComparer.Compare(Model() with { Stack = ProjectStack.Dotnet }, json)).Message);
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
