using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns a fixture copy and answers; discovery never starts processes.
public sealed class DeclaredProjectOnboardingTests
{
    private const string TestCommand = "node --test --test-reporter=junit --test-reporter-destination=reports/junit.xml";

    [Fact]
    public void NoMarker_AnswersEmitWholeSuiteCheck_AndComparisonMatches()
    {
        using var fixture = new ProjectOnboardingFixture("declared-no-marker");
        ProjectOnboardingFixture.SeedRemoteDefault(fixture.Root);
        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root);
        Assert.IsType<DeclaredProjectDiscoveryAdapter>(adapter);
        var model = adapter.Discover(fixture.Root);
        Assert.Equal(ProjectStack.Declared, model.Stack);
        Assert.Equal([DeclaredProjectDiscoveryAdapter.BuildQuestion, DeclaredProjectDiscoveryAdapter.ReportsQuestion,
            DeclaredProjectDiscoveryAdapter.TestQuestion], model.OwnerQuestions.Select(question => question.FactKey));
        var emitted = LearnedAcceptanceManifest.Build(model, Answers());
        Assert.Empty(emitted.Blockers);
        using var document = JsonDocument.Parse(emitted.Json!);
        var check = Assert.Single(document.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal("declared-test", check.GetProperty("name").GetString());
        Assert.Equal("junit-command", check.GetProperty("type").GetString());
        Assert.Equal("node", check.GetProperty("command").GetString());
        Assert.Equal(".", check.GetProperty("project").GetString());
        Assert.Equal(["--test", "--test-reporter=junit", "--test-reporter-destination=reports/junit.xml"],
            check.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["reports/*.xml"], check.GetProperty("reports").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(ManifestCheckResultKind.Match,
            Assert.Single(ManifestCheckComparer.Compare(model, emitted.Json!, OwnerAnswerApplier.Apply(model, Answers()))).Kind);
        Assert.All(ManifestCheckComparer.Compare(model, emitted.Json!), comparison => Assert.Equal(ManifestCheckResultKind.Unresolved, comparison.Kind));
        Assert.Contains("\"declared\"", ProjectModelJson.Serialize(model));
    }

    [Fact]
    public void UnansweredDeclarations_BlockEmission_WithQuestionKeys()
    {
        using var fixture = new ProjectOnboardingFixture("declared-no-marker");
        ProjectOnboardingFixture.SeedRemoteDefault(fixture.Root);
        var model = new DeclaredProjectDiscoveryAdapter().Discover(fixture.Root);
        var emitted = LearnedAcceptanceManifest.Build(model, Answers().Where(answer => !answer.FactKey.StartsWith("commands/", StringComparison.Ordinal)).ToArray());
        Assert.Null(emitted.Json);
        Assert.Equal(model.OwnerQuestions.Select(question => question.FactKey).Order(StringComparer.Ordinal),
            emitted.Blockers.Select(blocker => blocker.FactKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PersistedDeclaredStack_ResolvesWithoutToolchainAdapter()
    {
        using var fixture = new ProjectOnboardingFixture("declared-no-marker");
        var home = Path.Combine(fixture.Root, "home");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "project-model.json"), "{\"stack\":\"declared\"}");
        Assert.Equal(Toolchain.Unknown, ProjectStackResolver.Resolve(fixture.Root, home, shadow: null));
    }

    [Fact]
    public void BuildAndReports_EmitDefaults_AndCompareEffectiveInvocations()
    {
        using var fixture = new ProjectOnboardingFixture("declared-no-marker");
        ProjectOnboardingFixture.SeedRemoteDefault(fixture.Root);
        var model = new DeclaredProjectDiscoveryAdapter().Discover(fixture.Root);
        var answers = Answers(" compiler  build ", "reports/*.xml,more/**/*.xml");
        var emitted = LearnedAcceptanceManifest.Build(model, answers);
        var manifest = JsonNode.Parse(emitted.Json!)!;
        var checks = manifest["checks"]!.AsArray();
        Assert.Equal(2, checks.Count);
        Assert.Equal("declared-build", checks[0]!["name"]!.GetValue<string>());
        Assert.Equal("compiler", checks[0]!["command"]!.GetValue<string>());
        Assert.Equal("command", checks[0]!["type"]!.GetValue<string>());
        Assert.False(checks[0]!.AsObject().ContainsKey("project"));
        Assert.Equal("build", Assert.Single(checks[0]!["arguments"]!.AsArray())!.GetValue<string>());
        var applied = OwnerAnswerApplier.Apply(model, answers);
        checks[1]!.AsObject().Remove("project");
        checks[1]!["reports"] = new JsonArray("more/**/*.xml", "reports/*.xml");
        Assert.All(ManifestCheckComparer.Compare(model, manifest.ToJsonString(), applied), result => Assert.Equal(ManifestCheckResultKind.Match, result.Kind));
        foreach (var field in new[] { "type", "name", "command", "project", "arguments", "reports" })
        {
            var changed = JsonNode.Parse(manifest.ToJsonString())!;
            changed["checks"]![1]![field] = field is "arguments" or "reports" ? new JsonArray("different") : JsonValue.Create("different");
            Assert.Contains(ManifestCheckComparer.Compare(model, changed.ToJsonString(), applied), result => result.Kind != ManifestCheckResultKind.Match);
        }
    }

    [Theory]
    [InlineData("commands/declared/test", "node --test | tee")]
    [InlineData("commands/declared/test", "")]
    [InlineData("commands/declared/test", "   ")]
    [InlineData("commands/declared/build", "echo ; next")]
    [InlineData("commands/declared/test", "echo & next")]
    [InlineData("commands/declared/test", "echo < input")]
    [InlineData("commands/declared/test", "echo > output")]
    [InlineData("commands/declared/test", "echo (value)")]
    [InlineData("commands/declared/test", "echo $value")]
    [InlineData("commands/declared/test", "echo `value")]
    [InlineData("commands/declared/test", "echo 'value'")]
    [InlineData("commands/declared/test", "echo \"value\"")]
    [InlineData("commands/declared/reports", "../x/*.xml")]
    [InlineData("commands/declared/reports", "x\\..\\*.xml")]
    [InlineData("commands/declared/reports", "C:\\reports\\*.xml")]
    [InlineData("commands/declared/reports", "/reports/*.xml")]
    [InlineData("commands/declared/reports", "\\\\host\\reports\\*.xml")]
    [InlineData("commands/declared/reports", "reports/*.xml,")]
    [InlineData("commands/declared/reports", "none")]
    public void InvalidAnswer_RefusesUnsafeOrEmptyDeclarations(string key, string value) =>
        Assert.Throws<ArgumentException>(() => OwnerAnswerValidator.Validate(key, value));

    [Theory]
    [InlineData("commands/declared/build", " none ", "none")]
    [InlineData("commands/declared/test", "none", "none")]
    [InlineData("commands/declared/test", " tool  arg ", "tool arg")]
    [InlineData("commands/declared/reports", " reports\\*.xml , more/**/*.xml ", "reports/*.xml,more/**/*.xml")]
    public void ValidAnswer_NormalizesLiteralTokens(string key, string value, string expected) =>
        Assert.Equal(expected, OwnerAnswerValidator.Validate(key, value));

    [Theory]
    [InlineData("solution", ProjectStack.Dotnet, "Sample.sln")]
    [InlineData("go-module", ProjectStack.Go, "go.mod")]
    [InlineData("declared-no-marker", ProjectStack.Node, "package.json")]
    [InlineData("declared-no-marker", ProjectStack.Lua, ".busted")]
    public void RecognizedMarkers_KeepExistingDetection(string name, ProjectStack stack, string marker)
    {
        using var fixture = new ProjectOnboardingFixture(name);
        if (stack is ProjectStack.Node or ProjectStack.Lua) File.WriteAllText(Path.Combine(fixture.Root, marker), "{}");
        var detection = ProjectDiscoveryAdapterSelector.Detect(fixture.Root);
        Assert.Equal(stack, detection.Stack);
        Assert.Equal(marker, detection.Marker);
        Assert.Null(detection.Refusal);
    }

    private static OwnerAnswer[] Answers(string build = "none", string reports = "reports/*.xml") =>
    [
        new(OwnerAnswerValidator.DefaultTimeoutKey, "5", "fixture", DateTimeOffset.UnixEpoch),
        new(OwnerAnswerValidator.BuildServerShutdownKey, "3", "fixture", DateTimeOffset.UnixEpoch),
        new(OwnerAnswerValidator.DiscoveryKey, "4", "fixture", DateTimeOffset.UnixEpoch),
        new(DeclaredProjectDiscoveryAdapter.BuildQuestion, build, "fixture", DateTimeOffset.UnixEpoch),
        new(DeclaredProjectDiscoveryAdapter.TestQuestion, TestCommand, "fixture", DateTimeOffset.UnixEpoch),
        new(DeclaredProjectDiscoveryAdapter.ReportsQuestion, reports, "fixture", DateTimeOffset.UnixEpoch)
    ];
}
