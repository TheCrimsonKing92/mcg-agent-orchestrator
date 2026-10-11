using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ManifestEngineSettingsComparerTests
{
    private const string Project = "tests/NetHealth.App.Tests/NetHealth.App.Tests.csproj";
    private const string NetHealthManifest = """
        {
          "engine": {
            "slotCount": 1,
            "maxConcurrentShards": 2,
            "enforceStructuralCoverage": false,
            "timeouts": { "defaultMinutes": 10, "buildServerShutdownMinutes": 1, "discoveryMinutes": 2 }
          },
          "forbiddenChangedPathGlobs": [],
          "checks": [{ "type": "dotnet-test", "project": "tests/NetHealth.App.Tests/NetHealth.App.Tests.csproj",
                       "arguments": ["--verbosity", "minimal"] }]
        }
        """;

    [Fact]
    public void Compare_NetHealthManifest_ExposesOverridesAndForwardedVstestArgumentsInOrder()
    {
        var facts = ManifestEngineSettingsComparer.Compare(NetHealthManifest);
        var defaults = new AcceptanceGateEngineSettings();
        AssertFact(facts, "engine.maxConcurrentShards", ManifestFactResultKind.DiffersFromGateDefault, "2", "1");
        AssertFact(facts, "engine.timeouts.defaultMinutes", ManifestFactResultKind.DiffersFromGateDefault,
            "10", Minutes(defaults.ResolveCheckTimeout(null)));
        AssertFact(facts, "engine.timeouts.buildServerShutdownMinutes", ManifestFactResultKind.DiffersFromGateDefault,
            "1", Minutes(defaults.ResolveBuildServerShutdownTimeout()));
        AssertFact(facts, "engine.timeouts.discoveryMinutes", ManifestFactResultKind.DiffersFromGateDefault,
            "2", Minutes(defaults.ResolveDiscoveryTimeout()));
        AssertFact(facts, "engine.enforceStructuralCoverage", ManifestFactResultKind.Match, "false", "false");
        AssertFact(facts, "engine.slotCount", ManifestFactResultKind.NotConsumed, "1", "none");
        AssertFact(facts, "forbiddenChangedPathGlobs", ManifestFactResultKind.Match, "none", "none");
        AssertFact(facts, $"check[{Project}].filter", ManifestFactResultKind.Match, "none", "none");
        AssertFact(facts, $"check[{Project}].timeoutMinutes", ManifestFactResultKind.Match, "none", "none");
        AssertFact(facts, $"check[{Project}].advisory", ManifestFactResultKind.Match, "false", "false");
        AssertFact(facts, $"check[{Project}].estimatedSerialSeconds", ManifestFactResultKind.Match, "0", "0");
        AssertFact(facts, $"check[{Project}].arguments", ManifestFactResultKind.Match, "--verbosity minimal", "none");
        Assert.Equal(new[]
        {
            "engine.maxConcurrentShards", "engine.enforceStructuralCoverage", "engine.timeouts.defaultMinutes",
            "engine.timeouts.buildServerShutdownMinutes", "engine.timeouts.discoveryMinutes", "engine.slotCount",
            "forbiddenChangedPathGlobs", $"check[{Project}].filter", $"check[{Project}].timeoutMinutes",
            $"check[{Project}].advisory", $"check[{Project}].estimatedSerialSeconds", $"check[{Project}].arguments"
        }, facts.Select(fact => fact.Fact));
    }

    [Fact]
    public void Compare_MtpCheck_ReportsDroppedVerbositySeparatelyFromForwardedArguments()
    {
        var facts = ManifestEngineSettingsComparer.Compare(NetHealthManifest.Replace(
            "\"arguments\":", "\"runner\": \"mtp\", \"arguments\":"));
        AssertFact(facts, $"check[{Project}].arguments", ManifestFactResultKind.Match, "none", "none");
        AssertFact(facts, $"check[{Project}].arguments", ManifestFactResultKind.NotConsumed, "--verbosity minimal", "none");
        Assert.Contains("ignores", facts.Last().Reason);
    }

    [Fact]
    public void Compare_UnknownEngineProperties_UsesSettingsPropertiesAndJsonIgnore()
    {
        var facts = ManifestEngineSettingsComparer.Compare("""
            { "engine": { "inventedKey": "declared", "MAXCONCURRENTSHARDS": 3,
                          "timeouts": {}, "declaredDotnetTestChecks": [] } }
            """);
        AssertFact(facts, "engine.inventedKey", ManifestFactResultKind.NotConsumed, "declared", "none");
        AssertFact(facts, "engine.declaredDotnetTestChecks", ManifestFactResultKind.NotConsumed, "[]", "none");
        Assert.DoesNotContain(facts, fact => fact.Kind == ManifestFactResultKind.NotConsumed &&
            (fact.Fact.Contains("MAXCONCURRENTSHARDS") || fact.Fact == "engine.timeouts"));
    }

    [Fact]
    public void Compare_DeclaredCheckValuesAndGlobs_ReportsDifferences()
    {
        var facts = ManifestEngineSettingsComparer.Compare("""
            { "forbiddenChangedPathGlobs": [" obj/** ", " bin/** "],
              "checks": [{ "type": "dotnet-test", "project": "sample.csproj", "timeoutMinutes": 30,
                           "advisory": true, "estimatedSerialSeconds": 12,
                           "arguments": ["--filter", "Category=Unit"] }] }
            """);
        AssertFact(facts, "forbiddenChangedPathGlobs", ManifestFactResultKind.DiffersFromGateDefault, "obj/**,bin/**", "none");
        AssertFact(facts, "check[sample.csproj].filter", ManifestFactResultKind.DiffersFromGateDefault, "Category=Unit", "none");
        AssertFact(facts, "check[sample.csproj].timeoutMinutes", ManifestFactResultKind.DiffersFromGateDefault, "30", "none");
        AssertFact(facts, "check[sample.csproj].advisory", ManifestFactResultKind.DiffersFromGateDefault, "true", "false");
        AssertFact(facts, "check[sample.csproj].estimatedSerialSeconds", ManifestFactResultKind.DiffersFromGateDefault, "12", "0");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"engine\":{\"maxConcurrentShards\":1},\"checks\":[]}")]
    [InlineData("{\"checks\":[{\"type\":\"command\",\"arguments\":[\"--filter\",\"X\"]}]}")]
    public void Compare_NoDotnetTestDeclarations_ReportsOnlySixMatchingDefaultFacts(string manifest)
    {
        var facts = ManifestEngineSettingsComparer.Compare(manifest);
        Assert.Equal(6, facts.Count);
        Assert.All(facts, fact => Assert.Equal(ManifestFactResultKind.Match, fact.Kind));
        Assert.DoesNotContain(facts, fact => fact.Fact.StartsWith("check[", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Compare_NonpositiveTimeouts_ResolvesThroughGateDefaults(int timeout)
    {
        var manifest = JsonSerializer.Serialize(new { engine = new { timeouts = new
            { defaultMinutes = timeout, buildServerShutdownMinutes = timeout, discoveryMinutes = timeout } } });
        var facts = ManifestEngineSettingsComparer.Compare(manifest);
        Assert.All(facts, fact => Assert.Equal(ManifestFactResultKind.Match, fact.Kind));
        Assert.Equal(Minutes(new AcceptanceGateEngineSettings().ResolveCheckTimeout(null)),
            facts.Single(fact => fact.Fact == "engine.timeouts.defaultMinutes").Hand);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("null")]
    public void Compare_NonObjectRoot_ThrowsJsonException(string manifest) =>
        Assert.Throws<JsonException>(() => ManifestEngineSettingsComparer.Compare(manifest));

    [Fact]
    public void Compare_MtpRepeatedTokensAndOptionValues_PreservesDroppedTokenIdentity()
    {
        var facts = ManifestEngineSettingsComparer.Compare("""
            { "checks": [{ "type": "dotnet-test", "project": "sample.csproj", "runner": "mtp",
                           "arguments": ["--verbosity", "minimal", "minimal", "--logger", "minimal",
                                         "--filter", "X", "--verbosity", "--filter", "kept"] }] }
            """);
        AssertFact(facts, "check[sample.csproj].filter", ManifestFactResultKind.DiffersFromGateDefault, "X", "none");
        AssertFact(facts, "check[sample.csproj].arguments", ManifestFactResultKind.Match, "minimal kept", "none");
        AssertFact(facts, "check[sample.csproj].arguments", ManifestFactResultKind.NotConsumed,
            "--verbosity minimal --logger minimal --verbosity --filter", "none");
    }

    [Theory]
    [InlineData("vstest")]
    [InlineData("mtp")]
    public void Compare_FilterSpellingsAndRepeatedFilters_FollowsGateExtractor(string runner)
    {
        var facts = ManifestEngineSettingsComparer.Compare(JsonSerializer.Serialize(new { checks = new[]
        {
            new { type = "dotnet-test", project = "sample.csproj", runner,
                arguments = new[] { "--filter", "first", "--filter=inline", "--filter", "last" } }
        } }));
        AssertFact(facts, "check[sample.csproj].filter", ManifestFactResultKind.DiffersFromGateDefault, "last", "none");
        AssertFact(facts, "check[sample.csproj].arguments", ManifestFactResultKind.Match, "--filter=inline", "none");
        Assert.DoesNotContain(facts, fact => fact.Kind == ManifestFactResultKind.NotConsumed);
    }

    private static string Minutes(TimeSpan timeout) => timeout.TotalMinutes.ToString(CultureInfo.InvariantCulture);

    private static void AssertFact(IReadOnlyList<ManifestFactComparison> facts, string name,
        ManifestFactResultKind kind, string hand, string learned)
    {
        var fact = Assert.Single(facts, fact => fact.Fact == name && fact.Kind == kind);
        Assert.Equal(hand, fact.Hand);
        Assert.Equal(learned, fact.Learned);
        Assert.False(string.IsNullOrWhiteSpace(fact.Reason));
    }
}
