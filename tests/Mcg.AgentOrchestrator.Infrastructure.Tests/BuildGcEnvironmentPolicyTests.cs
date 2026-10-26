using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each policy file has an invocation-owned temporary root.
public sealed class BuildGcEnvironmentPolicyTests
{
    [Fact]
    public void Presets_DefaultControls_LeaveBothVariablesOff()
    {
        foreach (var policy in new[] { ConductorAutonomyPolicy.Conservative,
                     ConductorAutonomyPolicy.Permissive, ConductorAutonomyPolicy.Manual })
            AssertDefaults(policy);
    }

    [Fact]
    public void Parse_AbsentOrNullKeys_UsesDefaults()
    {
        var json = ConductorAutonomyPolicy.Conservative.ToJson();
        var absent = string.Join(Environment.NewLine, json.Split('\n')
            .Where(line => !line.Contains("\"buildGc", StringComparison.Ordinal)));
        AssertDefaults(ConductorAutonomyPolicy.ParseJson(absent));
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (var key in new[] { "buildGcConserveMemory", "buildGcConserveMemoryLevel", "buildGcHeapCount" })
            node[key] = null;
        AssertDefaults(ConductorAutonomyPolicy.ParseJson(node.ToJsonString()));
    }

    [Fact]
    public void Serialization_DefaultsAndOverrides_RoundTripAndTriggerReload()
    {
        var baseline = ConductorAutonomyPolicy.Conservative;
        var json = baseline.ToJson();
        Assert.Contains("\"buildGcConserveMemory\": false", json);
        Assert.Contains("\"buildGcConserveMemoryLevel\": 5", json);
        Assert.Contains("\"buildGcHeapCount\": 0", json);
        var changed = baseline with
        { BuildGcConserveMemory = true, BuildGcConserveMemoryLevel = 3, BuildGcHeapCount = 16 };
        var parsed = ConductorAutonomyPolicy.ParseJson(changed.ToJson());
        Assert.True(parsed.BuildGcConserveMemory);
        Assert.Equal(3, parsed.BuildGcConserveMemoryLevel);
        Assert.Equal(16, parsed.BuildGcHeapCount);
        Assert.NotEqual(json, (baseline with { BuildGcConserveMemory = true }).ToJson());
        Assert.NotEqual(json, (baseline with { BuildGcConserveMemoryLevel = 3 }).ToJson());
        Assert.NotEqual(json, (baseline with { BuildGcHeapCount = 4 }).ToJson());
    }

    [Theory]
    [InlineData("buildGcConserveMemory", "1")]
    [InlineData("buildGcConserveMemoryLevel", "10")]
    [InlineData("buildGcConserveMemoryLevel", "-1")]
    [InlineData("buildGcConserveMemoryLevel", "\"5\"")]
    [InlineData("buildGcConserveMemoryLevel", "5.5")]
    [InlineData("buildGcHeapCount", "65")]
    [InlineData("buildGcHeapCount", "-1")]
    [InlineData("buildGcHeapCount", "\"4\"")]
    [InlineData("buildGcHeapCount", "4.5")]
    public void Parse_InvalidValue_RejectsWithKey(string key, string value)
    {
        var node = JsonNode.Parse(ConductorAutonomyPolicy.Conservative.ToJson())!.AsObject();
        node[key] = JsonNode.Parse(value);
        var error = Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(node.ToJsonString()));
        Assert.Contains(key, error.Message);
        if (key == "buildGcConserveMemory")
            Assert.Contains("buildGcConserveMemory must be a boolean", error.Message);
        if (value is "-1" or "10" or "65")
            Assert.Contains($"{key} must be between 0 and {(key == "buildGcHeapCount" ? 64 : 9)} (got {value}).", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    public void Parse_HeapBoundary_AcceptsCount(int count)
    {
        var policy = ConductorAutonomyPolicy.Conservative with { BuildGcHeapCount = count };
        Assert.Equal(count, ConductorAutonomyPolicy.ParseJson(policy.ToJson()).BuildGcHeapCount);
    }

    [Theory]
    [InlineData(false, 5, 0, null, null)]
    [InlineData(true, 5, 0, "5", null)]
    [InlineData(true, 0, 0, "0", null)]
    [InlineData(false, 5, 4, null, "4")]
    [InlineData(false, 5, 10, null, "A")]
    [InlineData(false, 5, 16, null, "10")]
    [InlineData(false, 5, 64, null, "40")]
    [InlineData(true, 3, 16, "3", "10")]
    public void For_IndependentSettings_EmitsExactMap(bool conserve, int level, int heaps,
        string? expectedConserve, string? expectedHeaps)
    {
        var actual = BuildGcEnvironment.For(ConductorAutonomyPolicy.Conservative with
        { BuildGcConserveMemory = conserve, BuildGcConserveMemoryLevel = level, BuildGcHeapCount = heaps });
        var expected = new Dictionary<string, string>();
        if (expectedConserve is not null) expected["DOTNET_GCConserveMemory"] = expectedConserve;
        if (expectedHeaps is not null) expected["DOTNET_GCHeapCount"] = expectedHeaps;
        Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Json_InvalidOrAbsent_ReturnsEmpty(string? json) =>
        Assert.Empty(BuildGcEnvironment.ForPolicyJson(json));

    [Fact]
    public void StateFile_RewrittenPolicy_IsReadOnEveryCall()
    {
        var root = Path.Combine(Path.GetTempPath(), $"r-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var state = Path.Combine(root, "state.db");
            var path = Path.Combine(root, "conductor-policy.json");
            Assert.Empty(BuildGcEnvironment.ForStatePath(state));
            File.WriteAllText(path, "not json");
            Assert.Empty(BuildGcEnvironment.ForStatePath(state));
            File.WriteAllText(path, ConductorAutonomyPolicy.Conservative.ToJson());
            Assert.Empty(BuildGcEnvironment.ForStatePath(state));
            File.WriteAllText(path, (ConductorAutonomyPolicy.Conservative with
            { BuildGcConserveMemory = true, BuildGcConserveMemoryLevel = 3, BuildGcHeapCount = 16 }).ToJson());
            var enabled = BuildGcEnvironment.ForStatePath(state);
            Assert.Equal(2, enabled.Count);
            Assert.Equal("3", enabled["DOTNET_GCConserveMemory"]);
            Assert.Equal("10", enabled["DOTNET_GCHeapCount"]);
            File.WriteAllText(path, ConductorAutonomyPolicy.Conservative.ToJson());
            Assert.Empty(BuildGcEnvironment.ForStatePath(state));
            Assert.Empty(BuildGcEnvironment.ForStatePath("\0"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Apply_EnabledEntries_OverwriteWithoutRemovingOtherEntries()
    {
        var environment = new Dictionary<string, string?>
        { ["DOTNET_GCConserveMemory"] = "7", ["DOTNET_GCHeapCount"] = "2", ["keep"] = "value" };
        BuildGcEnvironment.Apply(environment, BuildGcEnvironment.For(ConductorAutonomyPolicy.Conservative));
        Assert.Equal("7", environment["DOTNET_GCConserveMemory"]);
        Assert.Equal("2", environment["DOTNET_GCHeapCount"]);
        BuildGcEnvironment.Apply(environment, BuildGcEnvironment.For(ConductorAutonomyPolicy.Conservative with
        { BuildGcConserveMemory = true, BuildGcConserveMemoryLevel = 3, BuildGcHeapCount = 16 }));
        Assert.Equal(3, environment.Count);
        Assert.Equal("3", environment["DOTNET_GCConserveMemory"]);
        Assert.Equal("10", environment["DOTNET_GCHeapCount"]);
        Assert.Equal("value", environment["keep"]);
    }

    [Fact]
    public void Runbook_Controls_AreDocumented()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "operator-runbook.md"));
        foreach (var token in new[] { "buildGcConserveMemory", "buildGcConserveMemoryLevel", "buildGcHeapCount",
                     "DOTNET_GCConserveMemory", "DOTNET_GCHeapCount" })
            Assert.Contains(token, text);
    }

    private static void AssertDefaults(ConductorAutonomyPolicy policy)
    {
        Assert.False(policy.BuildGcConserveMemory);
        Assert.Equal(5, policy.BuildGcConserveMemoryLevel);
        Assert.Equal(0, policy.BuildGcHeapCount);
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") => VerifiedRepositoryRoot.Find(source);
}
