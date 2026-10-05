using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Core.Tests;

public sealed class LandingDenylistTests
{
    private static string Document(string id = "process-launch", string reason = "Observed", string[]? paths = null) =>
        JsonSerializer.Serialize(new { version = 1, rules = new[] { new { id, reason, paths = paths ?? ["src/*/Processes/WorkerProcessJobs*.cs"] } } });

    [Fact]
    public void MatchesOnlyDeclaredPathsIgnoringCaseAndNormalizingSeparators()
    {
        var denylist = LandingDenylist.Parse(Document(), "test");
        var match = Assert.Single(denylist.Match(["src/Mcg.AgentOrchestrator.Execution/Processes/WorkerProcessJobs.RegistryScope.cs"]));
        Assert.Equal("process-launch", match.RuleId);
        Assert.Empty(denylist.Match(["src/Mcg.AgentOrchestrator.Execution/Processes/TempRootJanitor.cs", "src/Mcg.AgentOrchestrator.Core/Domain/Goal.cs"]));
        var normalized = Assert.Single(denylist.Match([@"SRC\MCG.AgentOrchestrator.Execution\PROCESSES\WorkerProcessJobs.cs"]));
        Assert.Equal("process-launch", normalized.RuleId);
        Assert.Equal("SRC/MCG.AgentOrchestrator.Execution/PROCESSES/WorkerProcessJobs.cs", normalized.Path);
    }

    [Fact]
    public void DoubleStarMatchesZeroOrMoreWholeSegmentsAndSingleStarStaysWithinSegment()
    {
        var denylist = LandingDenylist.Parse(Document(paths: ["src/**/X.cs"]), "test");
        Assert.Equal(3, denylist.Match(["src/X.cs", "src/a/X.cs", "src/a/b/X.cs"]).Count);
        Assert.Empty(denylist.Match(["other/X.cs", "src/a/Y.cs"]));
        var single = LandingDenylist.Parse(Document(paths: ["src/*/X.cs"]), "test");
        Assert.Empty(single.Match(["src/X.cs", "src/a/b/X.cs"]));
    }

    [Fact]
    public void MatchesInRuleThenInputOrderWithoutDuplicatePairs()
    {
        var json = """{"version":1,"rules":[{"id":"first","reason":"one","paths":["src/**","src/*"]},{"id":"second","reason":"two","paths":["src/**"]}]}""";
        var matches = LandingDenylist.Parse(json, "test").Match(["src/b", "src/a", @"src\b"]);
        Assert.Equal(["first=src/b", "first=src/a", "second=src/b", "second=src/a"], matches.Select(m => $"{m.RuleId}={m.Path}"));
    }

    public static IEnumerable<object[]> InvalidDocuments()
    {
        yield return [Document().Replace("\"version\":1", "\"version\":2"), "version"];
        yield return ["{\"version\":1,\"rules\":[]}", "rules"];
        yield return [Document("Bad_Id"), "Bad_Id"];
        yield return [Document(reason: ""), "reason"];
        yield return [Document().Replace("\"reason\":\"Observed\",", ""), "reason"];
        yield return [Document(paths: []), "paths"];
        foreach (var path in new[] { "src/../X.cs", "/src/X.cs", "src//X.cs", "src/X.cs/", @"src\X.cs", "src/?.cs", "src/[X].cs", "src/{X}.cs", "src/a**/X.cs" })
            yield return [Document(paths: [path]), "process-launch"];
        var rule = "{\"id\":\"same\",\"reason\":\"r\",\"paths\":[\"src/**\"]}";
        yield return [$"{{\"version\":1,\"rules\":[{rule},{rule}]}}", "same"];
        yield return [Document().Replace("\"reason\":", "\"unknown\":true,\"reason\":"), "unknown"];
        yield return [Document().Replace("\"version\":1", "\"version\":1,\"version\":1"), "version"];
        yield return [Document().Replace("\"reason\":\"Observed\"", "\"reason\":\"Observed\",\"reason\":\"again\""), "reason"];
        yield return [Document().Replace("\"version\":1", "\"version\":\"one\""), "version"];
        yield return ["not JSON", "JSON"];
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void RefusesInvalidDocumentNamingOffendingFieldOrRule(string json, string field)
    {
        var exception = Assert.Throws<FormatException>(() => LandingDenylist.Parse(json, "fixture"));
        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
        Assert.Contains("fixture", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckedInAndBuiltInRulesAreIdenticalAndNameThemselves()
    {
        var json = File.ReadAllText(Path.Combine(VerifiedRepositoryRoot.Find(), "config", "landing-denylist.json"));
        var parsed = LandingDenylist.Parse(json, "candidate checkout");
        Assert.NotEmpty(parsed.Match(["config/landing-denylist.json"]));
        Assert.NotEmpty(LandingDenylist.BuiltInDefault.Match(["config/landing-denylist.json"]));
        Assert.Equal(JsonSerializer.Serialize(parsed.Rules), JsonSerializer.Serialize(LandingDenylist.BuiltInDefault.Rules));
    }
}
