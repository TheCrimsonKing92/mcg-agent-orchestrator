using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: pure comparisons on invocation-owned settings and JSON.
public sealed class ManifestEngineSettingsLearnedComparerTests
{
    [Theory]
    [InlineData(2, ManifestFactResultKind.Match)]
    [InlineData(4, ManifestFactResultKind.DiffersFromHand)]
    public void Compare_LearnedShards_ReportsHandAgreementAndRule(int hand, ManifestFactResultKind kind)
    {
        var fact = ManifestEngineSettingsComparer.Compare(
            JsonSerializer.Serialize(new { engine = new { maxConcurrentShards = hand } }), Settings())
            .Single(fact => fact.Fact == "engine.maxConcurrentShards");
        Assert.Equal(kind, fact.Kind);
        Assert.Equal(hand.ToString(), fact.Hand);
        Assert.Equal("2", fact.Learned);
        Assert.Equal(hand == 2 ? "Agrees with the learned model: 3 test units, cap 2"
            : "The learned model derives 2: 3 test units, cap 2. The hand manifest declares 4.", fact.Reason);
    }

    [Theory]
    [InlineData(10, ManifestFactResultKind.Match)]
    [InlineData(25, ManifestFactResultKind.DiffersFromHand)]
    public void Compare_LearnedTimeout_ReportsHandAgreementAndRule(int hand, ManifestFactResultKind kind)
    {
        var fact = ManifestEngineSettingsComparer.Compare(
            JsonSerializer.Serialize(new { engine = new { timeouts = new { defaultMinutes = hand } } }), Settings())
            .Single(fact => fact.Fact == "engine.timeouts.defaultMinutes");
        Assert.Equal(kind, fact.Kind);
        Assert.Equal(hand.ToString(), fact.Hand);
        Assert.Equal("10", fact.Learned);
        Assert.Equal(hand == 10 ? "Agrees with the learned model: slow measured 200 seconds, multiplier 3, bounds 5..60"
            : "The learned model derives 10: slow measured 200 seconds, multiplier 3, bounds 5..60. The hand manifest declares 25.", fact.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(10)]
    public void Compare_UnlearnedFacts_RetainCompleteGateDefaultComparison(int? timeout)
    {
        const string manifest = """
            {"engine":{"slotCount":1,"maxConcurrentShards":4,
                "timeouts":{"defaultMinutes":10,"buildServerShutdownMinutes":1,"discoveryMinutes":2}},
             "checks":[{"type":"dotnet-test","project":"sample.csproj","arguments":["--verbosity","minimal"]}]}
            """;
        var baseline = ManifestEngineSettingsComparer.Compare(manifest);
        var learned = ManifestEngineSettingsComparer.Compare(manifest, Settings() with { DefaultTimeoutMinutes = timeout });
        var unlearned = baseline.Where(fact => fact.Fact != "engine.maxConcurrentShards" &&
            (timeout is null || fact.Fact != "engine.timeouts.defaultMinutes")).ToArray();
        Assert.Equal(unlearned, learned.Where(fact => fact.Fact != "engine.maxConcurrentShards" &&
            (timeout is null || fact.Fact != "engine.timeouts.defaultMinutes")).ToArray());
        Assert.Equal(ManifestFactResultKind.NotConsumed, learned.Single(fact => fact.Fact == "engine.slotCount").Kind);
    }

    [Fact]
    public void Compare_NoLearnedSettings_PreservesDefaultKindsValuesAndReasons()
    {
        const string manifest = """{"engine":{"slotCount":1,"maxConcurrentShards":2,"timeouts":{"defaultMinutes":10}}}""";
        var facts = ManifestEngineSettingsComparer.Compare(manifest);
        Assert.Equal(facts, ManifestEngineSettingsComparer.Compare(manifest, null));
        Assert.Equal(new[] { "engine.maxConcurrentShards", "engine.enforceStructuralCoverage", "engine.timeouts.defaultMinutes",
            "engine.timeouts.buildServerShutdownMinutes", "engine.timeouts.discoveryMinutes", "engine.slotCount", "forbiddenChangedPathGlobs" },
            facts.Select(fact => fact.Fact));
        Assert.Equal(new ManifestFactComparison("engine.maxConcurrentShards", ManifestFactResultKind.DiffersFromGateDefault,
            "2", "1", "The learned value comes from the gate's undeclared default."), facts[0]);
        Assert.All(facts.Where(fact => fact.Kind == ManifestFactResultKind.Match),
            fact => Assert.Equal("Agrees with the gate default.", fact.Reason));
    }

    private static LearnedEngineSettings Settings() =>
        new(2, "3 test units, cap 2", 10, "slow measured 200 seconds, multiplier 3, bounds 5..60", []);
}
