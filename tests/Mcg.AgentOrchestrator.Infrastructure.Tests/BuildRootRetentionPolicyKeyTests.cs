using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class BuildRootRetentionPolicyKeyTests
{
    [Fact]
    public void KeyDefaultsOffParsesValidatesAndIsDocumented()
    {
        foreach (var preset in new[] { ConductorAutonomyPolicy.Conservative,
                     ConductorAutonomyPolicy.Permissive, ConductorAutonomyPolicy.Manual })
            Assert.Equal(0, preset.BuildRootRetentionDays);
        var json = ConductorAutonomyPolicy.Conservative.ToJson();
        Assert.Contains("\"buildRootRetentionDays\": 0", json);
        var absent = string.Join(Environment.NewLine, json.Split('\n')
            .Where(line => !line.Contains("\"buildRootRetentionDays\"", StringComparison.Ordinal)));
        Assert.Equal(0, ConductorAutonomyPolicy.ParseJson(absent).BuildRootRetentionDays);
        var node = JsonNode.Parse(json)!.AsObject();
        node["buildRootRetentionDays"] = null;
        Assert.Equal(0, ConductorAutonomyPolicy.ParseJson(node.ToJsonString()).BuildRootRetentionDays);
        Assert.Equal(7, ConductorAutonomyPolicy.ParseJson(
            (ConductorAutonomyPolicy.Conservative with { BuildRootRetentionDays = 7 }).ToJson()).BuildRootRetentionDays);
        foreach (var value in new[] { "366", "-1", "\"7\"", "7.5" })
        {
            node["buildRootRetentionDays"] = JsonNode.Parse(value);
            var error = Assert.Throws<FormatException>(() => ConductorAutonomyPolicy.ParseJson(node.ToJsonString()));
            Assert.Contains("buildRootRetentionDays", error.Message);
            if (value is "366" or "-1")
                Assert.Contains($"buildRootRetentionDays must be between 0 and 365 (got {value}).", error.Message);
            else
                Assert.Contains("must be an integer.", error.Message);
        }
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "operator-runbook.md"));
        Assert.Contains("buildRootRetentionDays", text);
        Assert.Contains("SWEEP_BUILD_ROOT_RETENTION", text);
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") => VerifiedRepositoryRoot.Find(source);
}
