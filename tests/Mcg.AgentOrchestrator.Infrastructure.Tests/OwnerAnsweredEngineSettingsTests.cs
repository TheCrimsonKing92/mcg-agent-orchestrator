using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: overlay and comparison use only invocation-owned in-memory inputs.
public sealed class OwnerAnsweredEngineSettingsTests
{
    private const string Reason = "owner answer recorded 2026-10-11T12:34:56Z";

    [Fact]
    public void Overlay_MissingDefaultTimeout_AppliesOwnerValueAndRemovesQuestion()
    {
        var learned = Settings(null);
        var result = OwnerAnsweredEngineSettings.Overlay(learned,
            Applied(("engine/timeouts/defaultMinutes", "20")));
        Assert.Equal(20, result.DefaultTimeoutMinutes);
        Assert.Equal(Reason, result.DefaultTimeoutReason);
        Assert.Equal(new[] { "engine/timeouts/buildServerShutdownMinutes", "engine/timeouts/discoveryMinutes" },
            result.OwnerQuestions.Select(question => question.FactKey));
        Assert.Null(learned.DefaultTimeoutMinutes);
        Assert.Equal(3, learned.OwnerQuestions.Count);
    }

    [Fact]
    public void Overlay_DerivedDefaultTimeout_PreservesMeasuredValueAndReason()
    {
        var learned = Settings(10);
        var result = OwnerAnsweredEngineSettings.Overlay(learned,
            Applied(("engine/timeouts/defaultMinutes", "20")));
        Assert.Same(learned, result);
        Assert.Equal(10, result.DefaultTimeoutMinutes);
        Assert.Equal("measured timeout", result.DefaultTimeoutReason);
    }

    [Fact]
    public void Overlay_OtherTimeouts_FillsPropertiesAndRemovesOnlyAnsweredQuestions()
    {
        var learned = Settings(null);
        var result = OwnerAnsweredEngineSettings.Overlay(learned, Applied(
            ("engine/timeouts/buildServerShutdownMinutes", "1"), ("engine/timeouts/discoveryMinutes", "3")));
        Assert.Equal(1, result.BuildServerShutdownMinutes);
        Assert.Equal(3, result.DiscoveryMinutes);
        Assert.Equal(Reason, result.BuildServerShutdownReason);
        Assert.Equal(Reason, result.DiscoveryReason);
        Assert.Equal("engine/timeouts/defaultMinutes", Assert.Single(result.OwnerQuestions).FactKey);
        Assert.Null(learned.BuildServerShutdownMinutes);
        Assert.Null(learned.DiscoveryMinutes);
    }

    [Fact]
    public void Overlay_NoAnswers_ReturnsOriginalSettings()
    {
        var learned = Settings(null);
        Assert.Same(learned, OwnerAnsweredEngineSettings.Overlay(learned, new([], [], [])));
    }

    [Theory]
    [InlineData("buildServerShutdownMinutes", "engine/timeouts/buildServerShutdownMinutes")]
    [InlineData("discoveryMinutes", "engine/timeouts/discoveryMinutes")]
    public void Compare_OwnerTimeoutMatch_UsesOwnerReason(string property, string key)
    {
        var learned = OwnerAnsweredEngineSettings.Overlay(Settings(10), Applied((key, "3")));
        var fact = Assert.Single(ManifestEngineSettingsComparer.Compare(Manifest(property, 3), learned)
            .Where(fact => fact.Fact == "engine.timeouts." + property));
        Assert.Equal(ManifestFactResultKind.Match, fact.Kind);
        Assert.Equal("3", fact.Hand);
        Assert.Equal("3", fact.Learned);
        Assert.Equal("Agrees with the learned model: " + Reason, fact.Reason);
    }

    [Theory]
    [InlineData("buildServerShutdownMinutes", "engine/timeouts/buildServerShutdownMinutes")]
    [InlineData("discoveryMinutes", "engine/timeouts/discoveryMinutes")]
    public void Compare_OwnerTimeoutDifference_NamesBothValues(string property, string key)
    {
        var learned = OwnerAnsweredEngineSettings.Overlay(Settings(10), Applied((key, "3")));
        var fact = Assert.Single(ManifestEngineSettingsComparer.Compare(Manifest(property, 2), learned)
            .Where(fact => fact.Fact == "engine.timeouts." + property));
        Assert.Equal(ManifestFactResultKind.DiffersFromHand, fact.Kind);
        Assert.Equal("2", fact.Hand);
        Assert.Equal("3", fact.Learned);
        Assert.Equal("The learned model derives 3: " + Reason + ". The hand manifest declares 2.", fact.Reason);
    }

    [Theory]
    [InlineData(1, 2, ManifestFactResultKind.DiffersFromGateDefault)]
    [InlineData(25, 25, ManifestFactResultKind.Match)]
    public void Compare_NoOwnerTimeouts_PreservesGateEntries(int shutdown, int discovery,
        ManifestFactResultKind kind)
    {
        var json = JsonSerializer.Serialize(new
        {
            engine = new { timeouts = new { buildServerShutdownMinutes = shutdown, discoveryMinutes = discovery } }
        });
        var original = ManifestEngineSettingsComparer.Compare(json).Where(IsOwnerTimeout).ToArray();
        var learned = Settings(10);
        Assert.Null(learned.BuildServerShutdownMinutes);
        Assert.Null(learned.DiscoveryMinutes);
        var facts = ManifestEngineSettingsComparer.Compare(json, learned).Where(IsOwnerTimeout).ToArray();
        Assert.Equal(original, facts);
        Assert.Equal(2, facts.Length);
        Assert.All(facts, fact =>
        {
            Assert.Equal(kind, fact.Kind);
            Assert.Equal("25", fact.Learned);
            Assert.Equal(kind == ManifestFactResultKind.Match ? "Agrees with the gate default."
                : "The learned value comes from the gate's undeclared default.", fact.Reason);
        });
    }

    private static bool IsOwnerTimeout(ManifestFactComparison fact) => fact.Fact is
        "engine.timeouts.buildServerShutdownMinutes" or "engine.timeouts.discoveryMinutes";
    private static string Manifest(string property, int value) =>
        JsonSerializer.Serialize(new { engine = new { timeouts = new Dictionary<string, int> { [property] = value } } });
    private static LearnedEngineSettings Settings(int? timeout) => new(1, "shards", timeout,
        "measured timeout", new[] { "engine/timeouts/defaultMinutes", "engine/timeouts/buildServerShutdownMinutes",
            "engine/timeouts/discoveryMinutes" }.Select(key => new ProjectOwnerQuestion(key, "question", Source())).ToArray());
    private static FactSource Source() => new(measurementReference: "fixture");
    private static AppliedOwnerAnswers Applied(params (string Key, string Value)[] answers) => new([], answers.Select(answer =>
        new AppliedOwnerAnswers.AnsweredFact(answer.Key,
            new ProjectFact<string>(answer.Value, new FactSource(measurementReference: "owner-answer:" + answer.Key),
                FactConfidence.High), Reason)).ToArray(), []);
}
