using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: all inputs are immutable, invocation-owned snapshots.
public sealed class LearnedEngineSettingsDeriverTests
{
    [Theory]
    [InlineData(100, 5)]
    [InlineData(200, 10)]
    [InlineData(601, 31)]
    [InlineData(1500, 60)]
    [InlineData(double.MaxValue, 60)]
    public void Derive_MeasuredTest_UsesMultiplierCeilingAndBounds(double seconds, int expected)
    {
        var learned = LearnedEngineSettingsDeriver.Derive(Model([Unit("test")],
            [Command("test")], [Measurement("test", seconds)]));
        Assert.Equal(expected, learned.DefaultTimeoutMinutes);
        Assert.Contains("test", learned.DefaultTimeoutReason);
        Assert.Contains(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), learned.DefaultTimeoutReason);
        Assert.Contains("multiplier 3", learned.DefaultTimeoutReason);
        Assert.Contains("5..60", learned.DefaultTimeoutReason);
        AssertQuestions(learned, false);
    }

    [Fact]
    public void Derive_TwoMeasuredTests_UsesSlowestUnitAndNamesItsInputs()
    {
        var learned = LearnedEngineSettingsDeriver.Derive(Model([Unit("fast"), Unit("slow")],
            [Command("fast"), Command("slow")], [Measurement("fast", 120), Measurement("slow", 400)]));
        Assert.Equal(20, learned.DefaultTimeoutMinutes);
        Assert.Contains("slow measured 400 seconds", learned.DefaultTimeoutReason);
    }

    [Fact]
    public void Derive_TiedDurations_SelectsOrdinalUnitRegardlessOfInputOrder()
    {
        var learned = LearnedEngineSettingsDeriver.Derive(Model([Unit("z"), Unit("a")],
            [Command("z"), Command("a")], [Measurement("z", 400), Measurement("a", 400)]));
        Assert.Contains("unit a measured 400", learned.DefaultTimeoutReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Derive_MissingTestDuration_QuestionsOwnerAndIgnoresBuild(bool buildOnly)
    {
        UnitMeasurement[] measurements = buildOnly
            ? [new("unmeasured", Fact(99999d), null)] : [];
        var learned = LearnedEngineSettingsDeriver.Derive(Model([Unit("unmeasured")],
            [Command("unmeasured")], measurements));
        Assert.Null(learned.DefaultTimeoutMinutes);
        Assert.Contains("unmeasured", learned.DefaultTimeoutReason);
        AssertQuestions(learned, true);
        var question = learned.OwnerQuestions[0];
        Assert.Contains("Supply a default timeout", question.Question);
        Assert.Contains("project discover --measure test", question.Question);
    }

    [Fact]
    public void Derive_PartiallyMeasuredTests_NamesMissingUnitAndDerivesNoTimeout()
    {
        var learned = LearnedEngineSettingsDeriver.Derive(Model([Unit("measured"), Unit("missing")],
            [Command("measured"), Command("missing")], [Measurement("measured", 200)]));
        Assert.Null(learned.DefaultTimeoutMinutes);
        Assert.Equal("Missing measured test durations for units: missing", learned.DefaultTimeoutReason);
        AssertQuestions(learned, true);
    }

    [Fact]
    public void Derive_NoTestCommand_IgnoresDurationsAndQuestionsOwner()
    {
        var learned = LearnedEngineSettingsDeriver.Derive(Model([Unit("test")],
            [new("test", Fact("build"), null)], [Measurement("test", 200)]));
        Assert.Null(learned.DefaultTimeoutMinutes);
        Assert.Contains("No units have a test command", learned.DefaultTimeoutReason);
        AssertQuestions(learned, true);
    }

    [Fact]
    public void Derive_NonTestUnitWithTestCommand_StillUsesMeasuredDuration()
    {
        var learned = LearnedEngineSettingsDeriver.Derive(Model([Unit("tool", false)],
            [Command("tool")], [Measurement("tool", 200)]));
        Assert.Equal(10, learned.DefaultTimeoutMinutes);
        Assert.Contains("0 test units", learned.MaxConcurrentShardsReason);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(3, 0, 2)]
    [InlineData(3, 1, 1)]
    public void Derive_TestCountAndHazards_ControlShardCount(int count, int hazards, int expected)
    {
        var units = Enumerable.Range(0, count).Select(index => Unit($"test{index}"))
            .Append(Unit("app", false)).Append(Unit("unknown", null)).ToArray();
        var model = Model(units, [], []) with
        {
            Hazards = Enumerable.Range(0, hazards).Select(index =>
                new SharedStateHazard("test0", $"hazard{index}", Fact("shared"))).ToArray()
        };
        var learned = LearnedEngineSettingsDeriver.Derive(model);
        Assert.Equal(expected, learned.MaxConcurrentShards);
        if (hazards > 0) Assert.Contains($"{hazards} shared-state hazards", learned.MaxConcurrentShardsReason);
        else
        {
            Assert.Contains($"{count} test units", learned.MaxConcurrentShardsReason);
            Assert.Contains("cap 2", learned.MaxConcurrentShardsReason);
        }
        AssertQuestions(learned, true);
    }

    private static void AssertQuestions(LearnedEngineSettings learned, bool missing) =>
        Assert.Equal(missing
            ? new[] { "engine/timeouts/defaultMinutes", "engine/timeouts/buildServerShutdownMinutes", "engine/timeouts/discoveryMinutes" }
            : new[] { "engine/timeouts/buildServerShutdownMinutes", "engine/timeouts/discoveryMinutes" },
            learned.OwnerQuestions.Select(question => question.FactKey));

    private static ProjectModel Model(ProjectUnit[] units, UnitCommands[] commands, UnitMeasurement[] measurements) =>
        new(ProjectModel.CurrentSchemaVersion, ".", units, [], [], [], commands, [], measurements, []);

    private static ProjectFact<T> Fact<T>(T value) =>
        new(value, new FactSource(measurementReference: "fixture"), FactConfidence.High);
    private static ProjectUnit Unit(string id, bool? test = true) => new(id, Fact(id), Fact(id), Fact(test));
    private static UnitCommands Command(string id) => new(id, Fact("build"), Fact("test"));
    private static UnitMeasurement Measurement(string id, double seconds) => new(id, null, Fact(seconds));
}
