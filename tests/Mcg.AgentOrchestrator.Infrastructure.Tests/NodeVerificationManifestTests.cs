using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using Fixture = NodeProjectDiscoveryAdapterTests.Fixture;

// Parallel-safe: each test owns a file-only fixture and derives the manifest in memory.
public sealed class NodeVerificationManifestTests
{
    [Fact]
    public void Build_NpmVitestTypeScript_EmitsExactlyThreeOrderedChecks()
    {
        using var fixture = new Fixture();
        var model = fixture.Discover();

        var result = LearnedAcceptanceManifest.Build(model, Fixture.Answers());

        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        var manifest = JsonNode.Parse(result.Json)!;
        var expected = JsonNode.Parse("""
            [
              {"name":"npm ci","type":"command","command":"npm","arguments":["ci"]},
              {"name":"npx tsc --noEmit","type":"command","command":"npx","arguments":["tsc","--noEmit"]},
              {"name":"crosswalk-test .","type":"crosswalk-test","project":".","arguments":[]}
            ]
            """);
        Assert.True(JsonNode.DeepEquals(expected, manifest["checks"]), manifest["checks"]!.ToJsonString());
        Assert.Equal(1, manifest["engine"]!["maxConcurrentShards"]!.GetValue<int>());
        Assert.False(manifest["engine"]!.AsObject().ContainsKey("mtpInvocations"));
        Assert.DoesNotContain(manifest["checks"]!.AsArray(), check =>
            check!["arguments"]!.AsArray().Any(argument => argument!.GetValue<string>() is "test" or "build" or "run" or "vite"));
        // Registry reachability remains an owner follow-up even when the manifest can be emitted.
        Assert.Contains(model.OwnerQuestions, question => question.FactKey == "environment/node-registry-access");
        Assert.DoesNotContain(result.Blockers, blocker => blocker.FactKey == "environment/node-registry-access");
    }

    [Fact]
    public void Build_NoTypeScriptConfig_EmitsInstallThenWholeSuiteCrosswalk()
    {
        using var fixture = new Fixture(hasTsconfig: false);
        var model = fixture.Discover();

        var result = LearnedAcceptanceManifest.Build(model, Fixture.Answers());

        Assert.Empty(result.Blockers);
        Assert.NotNull(result.Json);
        var manifest = JsonNode.Parse(result.Json)!;
        var expected = JsonNode.Parse("""
            [
              {"name":"npm ci","type":"command","command":"npm","arguments":["ci"]},
              {"name":"crosswalk-test .","type":"crosswalk-test","project":".","arguments":[]}
            ]
            """);
        Assert.True(JsonNode.DeepEquals(expected, manifest["checks"]), manifest["checks"]!.ToJsonString());
        Assert.Equal("absent: no TypeScript type-check, because tsconfig.json is absent at the root",
            Assert.Single(model.EnvironmentNeeds, need => need.Name == "node-typescript-config").Value.Value);
        Assert.DoesNotContain(model.OwnerQuestions, question => question.FactKey.Contains("typescript", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{}", "package-lock.json", "environment/node-test-framework")]
    [InlineData("{\"devDependencies\":{\"jest\":\"30\"}}", "package-lock.json", "environment/node-test-framework")]
    [InlineData("{\"devDependencies\":{\"vitest\":\"4\",\"jest\":\"30\"}}", "package-lock.json", "environment/node-test-framework")]
    [InlineData("{\"devDependencies\":{\"vitest\":\"4\"}}", "pnpm-lock.yaml", "environment/node-package-manager")]
    [InlineData("{\"devDependencies\":{\"vitest\":\"4\"}}", "yarn.lock", "environment/node-package-manager")]
    [InlineData("{\"devDependencies\":{\"vitest\":\"4\"}}", "", "environment/node-package-manager")]
    [InlineData("{\"devDependencies\":{\"vitest\":\"4\"}}", "package-lock.json|yarn.lock", "environment/node-package-manager")]
    [InlineData("{bad", "package-lock.json", "discovery/files/package.json/parse")]
    public void Build_UnsupportedDiscovery_ReturnsOwnerQuestionBlocker(
        string package, string lockfiles, string key)
    {
        using var fixture = new Fixture(package, lockfiles: lockfiles.Split('|', StringSplitOptions.RemoveEmptyEntries));
        var model = fixture.Discover();
        var question = Assert.Single(model.OwnerQuestions, item => item.FactKey == key);

        var result = LearnedAcceptanceManifest.Build(model, Fixture.Answers());

        Assert.Null(result.Json);
        var blocker = Assert.Single(result.Blockers, item => item.FactKey == key);
        Assert.Equal(question.Question, blocker.Reason);
        Assert.DoesNotContain(result.Blockers, item => item.FactKey.StartsWith("engine/", StringComparison.Ordinal));
    }
}
