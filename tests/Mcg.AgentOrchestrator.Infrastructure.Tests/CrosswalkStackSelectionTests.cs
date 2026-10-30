using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = LuaProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: unique filesystem roots and injected process responses, no ambient state.
public sealed class CrosswalkStackSelectionTests
{
    [Theory]
    [InlineData("jvm", "gradle")]
    [InlineData("future-stack", "future-runner")]
    public void Select_ListedSuite_EmitsAndComparesOneRootCheck(string stack, string framework)
    {
        using var fixture = new Fixture();
        using var crosswalk = new FakeCrosswalkProcess(stack, framework);
        crosswalk.ListTests("module:Suite#first", "module:Suite#second");

        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader);

        Assert.IsType<CrosswalkProjectDiscoveryAdapter>(adapter);
        var model = adapter.Discover(fixture.Root);
        Assert.Equal(ProjectStack.Crosswalk, model.Stack);
        Assert.Equal(stack, Fact(model, "crosswalk-stack"));
        Assert.Equal(framework, Fact(model, "crosswalk-test-framework"));
        Assert.Equal("2", Fact(model, "crosswalk-test-count"));
        Assert.Equal("crosswalk-test .", Fact(model, "crosswalk-test-command"));
        using var evidence = JsonDocument.Parse(Fact(model, "crosswalk-evidence"));
        var item = Assert.Single(evidence.RootElement.EnumerateArray());
        Assert.Equal("build.file", item.GetProperty("path").GetString());
        Assert.Equal("suite declaration", item.GetProperty("reason").GetString());
        Assert.Empty(model.OwnerQuestions);

        var result = LearnedAcceptanceManifest.Build(model, Fixture.Answers());

        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        using var manifest = JsonDocument.Parse(result.Json);
        var check = Assert.Single(manifest.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal("crosswalk-test .", check.GetProperty("name").GetString());
        Assert.Equal("crosswalk-test", check.GetProperty("type").GetString());
        Assert.Equal(".", check.GetProperty("project").GetString());
        Assert.Empty(check.GetProperty("arguments").EnumerateArray());
        Assert.False(check.TryGetProperty("command", out _));
        Assert.False(manifest.RootElement.GetProperty("engine").TryGetProperty("mtpInvocations", out _));
        Assert.Equal(ManifestCheckResultKind.Match, Assert.Single(ManifestCheckComparer.Compare(model, result.Json)).Kind);
        Assert.Equal(ManifestCheckResultKind.Match, Assert.Single(ManifestCheckComparer.Compare(model, result.Json,
            OwnerAnswerApplier.Apply(model, Fixture.Answers()))).Kind);
        Assert.Equal(1, crosswalk.VersionCalls);
        Assert.Equal(2, crosswalk.Calls.Count);
        Assert.Contains(" detect ", crosswalk.Calls[0].Command, StringComparison.Ordinal);
        Assert.Contains(" list ", crosswalk.Calls[1].Command, StringComparison.Ordinal);
        Assert.All(crosswalk.Calls, call =>
        {
            Assert.Equal(crosswalk.Timeout, call.Timeout);
            Assert.Equal(fixture.Root, call.WorkingDirectory);
            Assert.Contains(crosswalk.Binary, call.Command, StringComparison.Ordinal);
        });

        var roundTrip = ProjectModelJson.Deserialize(ProjectModelJson.Serialize(model));
        Assert.Equal(ProjectStack.Crosswalk, roundTrip.Stack);
        fixture.Write("home/project-model.json", ProjectModelJson.Serialize(roundTrip));
        Assert.Equal(Toolchain.Unknown, ProjectStackResolver.Resolve(fixture.Root, Path.Combine(fixture.Root, "home"), shadow: null));
    }

    [Fact]
    public void Select_Lua_UsesExistingAdapterWithoutListing()
    {
        using var fixture = new Fixture();
        fixture.Write(".busted");
        using var crosswalk = new FakeCrosswalkProcess("lua", "busted");

        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader);

        Assert.IsType<LuaProjectDiscoveryAdapter>(adapter);
        Assert.Equal(ProjectModelJson.Serialize(new LuaProjectDiscoveryAdapter().Discover(fixture.Root)),
            ProjectModelJson.Serialize(adapter.Discover(fixture.Root)));
        Assert.Contains(" detect ", Assert.Single(crosswalk.Calls).Command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown", "not-run")]
    [InlineData("empty", "no-tests")]
    [InlineData("exit", "exit-nonzero: exit=1")]
    [InlineData("unsupported", "exit-2: unavailable")]
    [InlineData("malformed", "invalid-json")]
    [InlineData("timeout", "timeout")]
    public void Select_UnavailableSuite_RecordsDeclaredFallbackFacts(string scenario, string outcome)
    {
        using var fixture = new Fixture();
        var stack = scenario == "unknown" ? "unknown" : "jvm";
        using var crosswalk = new FakeCrosswalkProcess(stack);
        crosswalk.ListTests("partial-result");
        crosswalk.ListResult = scenario switch
        {
            "exit" => crosswalk.ListResult with { ExitCode = 1, StandardError = "failure" },
            "unsupported" => new(2, "[]", "unavailable"),
            "malformed" => new(0, "{}", ""),
            "timeout" => new(-1, "[]", "", TimedOut: true),
            _ => new(0, "[]", "")
        };

        var adapter = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader);

        Assert.IsType<DeclaredProjectDiscoveryAdapter>(adapter);
        var model = adapter.Discover(fixture.Root);
        Assert.Equal(ProjectStack.Declared, model.Stack);
        Assert.Equal(stack, Fact(model, "crosswalk-stack"));
        Assert.Equal(outcome, Fact(model, "crosswalk-list-outcome"));
        Assert.Equal(new DeclaredProjectDiscoveryAdapter().Discover(fixture.Root).OwnerQuestions, model.OwnerQuestions);
        Assert.Equal(scenario == "unknown" ? 1 : 2, crosswalk.Calls.Count);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("timeout")]
    [InlineData("capture")]
    [InlineData("malformed")]
    [InlineData("empty-stack")]
    [InlineData("invalid-evidence")]
    [InlineData("start")]
    public void Select_DetectFailure_ThrowsWithToolPath(string failure)
    {
        using var fixture = new Fixture();
        using var crosswalk = new FakeCrosswalkProcess();
        crosswalk.DetectResult = failure switch
        {
            "exit" => new(1, "", "failed"),
            "timeout" => new(-1, "", "", TimedOut: true),
            "capture" => crosswalk.DetectResult with { CaptureLimited = true },
            "empty-stack" => new(0, """{"stack":"","evidence":[]}""", ""),
            "invalid-evidence" => new(0, """{"stack":"jvm","evidence":[{}]}""", ""),
            "malformed" => new(0, "not json", ""),
            _ => crosswalk.DetectResult
        };
        if (failure == "start") crosswalk.DetectException = new IOException("injected start failure");

        var error = Assert.Throws<InvalidOperationException>(() => ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader));

        Assert.Contains(crosswalk.Binary, error.Message, StringComparison.Ordinal);
        Assert.Single(crosswalk.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("project")]
    [InlineData("arguments")]
    [InlineData("type")]
    public void Compare_ChangedInvocation_DoesNotMatch(string change)
    {
        using var fixture = new Fixture();
        using var crosswalk = new FakeCrosswalkProcess("jvm");
        crosswalk.ListTests("one");
        var model = ProjectDiscoveryAdapterSelector.Select(fixture.Root, crosswalk.Reader).Discover(fixture.Root);
        var manifest = JsonNode.Parse(LearnedAcceptanceManifest.Build(model, Fixture.Answers()).Json!)!;
        var checks = manifest["checks"]!.AsArray();
        switch (change)
        {
            case "missing": checks.Clear(); break;
            case "extra": checks.Add(JsonNode.Parse("""{"name":"extra","type":"command","command":"other"}""")); break;
            case "duplicate": checks.Add(checks[0]!.DeepClone()); break;
            case "project": checks[0]!["project"] = "subproject"; break;
            case "arguments": checks[0]!["arguments"] = new JsonArray("--id", "one"); break;
            case "type": checks[0]!["type"] = "command"; break;
        }
        Assert.Contains(ManifestCheckComparer.Compare(model, manifest.ToJsonString()), result => result.Kind != ManifestCheckResultKind.Match);
    }

    private static string Fact(ProjectModel model, string name) =>
        Assert.Single(model.EnvironmentNeeds, need => need.Name == name).Value.Value;
}
