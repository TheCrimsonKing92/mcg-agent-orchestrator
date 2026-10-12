using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: readers and overlays use invocation-owned in-memory facts.
public sealed class OwnerAnsweredCheckTimeoutsTests
{
    private const string Unit = "tests/A/A.csproj";
    private const string Key = "commands/" + Unit + "/timeoutMinutes";

    [Fact]
    public void Read_BackslashUnit_UsesOrdinalSlashPathAndReason()
    {
        var result = OwnerAnsweredCheckTimeouts.Read(new([], [Answer(
            "commands/tests\\A\\A.csproj/timeoutMinutes", "025", "chosen timeout")], []));
        Assert.Single(result);
        Assert.Equal((25, "chosen timeout"), result[Unit]);
        Assert.False(result.ContainsKey("tests/a/A.csproj"));
    }

    [Fact]
    public void Read_RepeatedKey_LastListEntryWinsIncludingReason()
    {
        var applied = new AppliedOwnerAnswers([], [Answer(Key, "25", "first"), Answer(Key, "30", "later")], []);
        var result = OwnerAnsweredCheckTimeouts.Read(applied);
        Assert.Single(result);
        Assert.Equal((30, "later"), result[Unit]);
        Assert.Equal((25, "first"), OwnerAnsweredCheckTimeouts.Read(applied with
        {
            AnsweredFacts = applied.AnsweredFacts.Reverse().ToArray()
        })[Unit]);
    }

    [Fact]
    public void Read_EngineAndStaleAnswers_ReturnsEmptyMap()
    {
        var applied = new AppliedOwnerAnswers([], [Answer(OwnerAnswerValidator.DiscoveryKey, "3", "engine")],
            [new(new OwnerAnswer(Key, "25", null, DateTimeOffset.UnixEpoch), OwnerAnswerApplier.NotLiveReason)]);
        Assert.Empty(OwnerAnsweredCheckTimeouts.Read(applied));
    }

    [Fact]
    public void Overlay_OnlyUnitTimeout_PreservesGlobalFieldsAndQuestions()
    {
        var learned = new LearnedEngineSettings(2, "shards", 10, "measured",
            [new(OwnerAnswerValidator.DiscoveryKey, "question", Source())])
        {
            BuildServerShutdownMinutes = 3, BuildServerShutdownReason = "shutdown",
            DiscoveryMinutes = 4, DiscoveryReason = "discovery"
        };
        var result = OwnerAnsweredEngineSettings.Overlay(learned, new([], [Answer(Key, "25", "unit")], []));
        Assert.Equal((25, "unit"), result.CheckTimeouts[Unit]);
        Assert.Single(result.CheckTimeouts);
        Assert.Equal(learned, result with { CheckTimeouts = learned.CheckTimeouts });
        Assert.Same(learned.OwnerQuestions, result.OwnerQuestions);
        Assert.Empty(learned.CheckTimeouts);
    }

    [Fact]
    public void Settings_DefaultCheckTimeouts_PreservesRecordEquality()
    {
        ProjectOwnerQuestion[] questions = [];
        var first = new LearnedEngineSettings(2, "shards", 10, "measured", questions);
        var second = new LearnedEngineSettings(2, "shards", 10, "measured", questions);
        Assert.Empty(first.CheckTimeouts);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    private static FactSource Source() => new(measurementReference: "fixture");
    private static AppliedOwnerAnswers.AnsweredFact Answer(string key, string value, string reason) =>
        new(key, new ProjectFact<string>(value, Source(), FactConfidence.High), reason);
}
