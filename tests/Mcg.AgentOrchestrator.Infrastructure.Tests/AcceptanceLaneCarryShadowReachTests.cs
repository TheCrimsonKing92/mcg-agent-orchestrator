using Xunit;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: all reach inputs are in memory.
public sealed class AcceptanceLaneCarryShadowReachTests
{
    [Theory]
    [InlineData("src/App/App.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("global.json")]
    [InlineData("repo.sln")]
    public void ProjectChanges_ReachEveryTier(string path) => AssertReach([path], "", true, true, true);

    [Fact]
    public void OwnTestSource_ReachEveryTier() =>
        AssertReach(["tests/Lane.cs"], "", true, true, true, ["tests/Lane.cs"]);

    [Fact]
    public void DeclaredTypeMention_ReachEveryTier() =>
        AssertReach(["src/Changed.cs"], "new Widget()", true, true, true,
            sources: new() { ["src/Changed.cs"] = "public class Widget {}" });

    [Theory]
    [InlineData("WidgetFactory widget", false)]
    [InlineData("EnumerateFiles WidgetFactory", true)]
    public void UnmentionedType_OnlyMarkerLaneConservative(string text, bool conservative) =>
        AssertReach(["src/Changed.cs"], text, false, false, conservative,
            sources: new() { ["src/Changed.cs"] = "public class Widget {}" });

    [Fact]
    public void SecondHop_ReachesMidAndConservativeOnly() =>
        AssertReach(["src/Changed.cs"], "new Consumer()", false, true, true,
            sources: new()
            {
                ["src/Changed.cs"] = "public class Widget {}",
                ["src/Consumer.cs"] = "public class Consumer { Widget value; }",
                ["src/Third.cs"] = "public class Third { Consumer value; }"
            });

    [Fact]
    public void ThirdHop_DoesNotReach() =>
        AssertReach(["src/Changed.cs"], "new Third()", false, false, false,
            sources: new()
            {
                ["src/Changed.cs"] = "public class Widget {}",
                ["src/Consumer.cs"] = "public class Consumer { Widget value; }",
                ["src/Third.cs"] = "public class Third { Consumer value; }"
            });

    [Theory]
    [InlineData("plain lane", false)]
    [InlineData("EnumerateFiles", true)]
    public void UnmentionedDoc_OnlyMarkerLaneConservative(string text, bool conservative) =>
        AssertReach(["docs/unmentioned-page.md"], text, false, false, conservative);

    [Theory]
    [InlineData("ProcessStartInfo")]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    [InlineData("EnumerateFiles")]
    [InlineData("GetFiles")]
    public void Marker_IsReachedByScriptChange(string marker) =>
        AssertReach(["scripts/unrelated.ps1"], marker, false, false, true);

    [Fact]
    public void NonCsBaseName_IsOrdinalSubstring() =>
        AssertReach(["docs/operator-runbook.md"], "docs/operator-runbook.md", true, true, true);

    [Theory]
    [InlineData("SharedHelper", true)]
    [InlineData("SharedHelperExtra", false)]
    [InlineData("sharedhelper", false)]
    public void TestHelper_BaseNameIsIdentifier(string text, bool reached) =>
        AssertReach(["tests/SharedHelper.cs"], text, reached, reached, reached);

    [Fact]
    public void DeletedSource_FallsBackToFileName() =>
        AssertReach(["src/Removed.cs"], "Removed", true, true, true);

    [Theory]
    [InlineData("record Thing;")]
    [InlineData("record struct Thing;")]
    [InlineData("struct Thing {}")]
    [InlineData("interface Thing {}")]
    [InlineData("enum Thing {}")]
    [InlineData("class Thing<T> {}")]
    public void DeclarationKinds_UseSimpleTypeIdentifier(string source) =>
        AssertReach(["src/Changed.cs"], "Thing", true, true, true,
            sources: new() { ["src/Changed.cs"] = source });

    [Fact]
    public void UnresolvedLane_AlwaysReached()
    {
        var changes = AcceptanceLaneCarryShadowReach.Prepare(["data/unrelated.bin"],
            new Dictionary<string, AcceptanceLaneCarryShadowReach.Source>());
        Assert.Equal(new(true, true, true), AcceptanceLaneCarryShadowReach.Evaluate(changes, [], "", unresolved: true));
    }

    private static void AssertReach(string[] changed, string text, bool optimistic, bool mid, bool conservative,
        string[]? lanePaths = null, Dictionary<string, string>? sources = null)
    {
        var index = (sources ?? []).ToDictionary(pair => pair.Key,
            pair => AcceptanceLaneCarryShadowReach.IndexSource(pair.Value), StringComparer.Ordinal);
        var changes = AcceptanceLaneCarryShadowReach.Prepare(changed, index);
        Assert.Equal(new(optimistic, mid, conservative),
            AcceptanceLaneCarryShadowReach.Evaluate(changes, lanePaths ?? [], text));
    }
}
