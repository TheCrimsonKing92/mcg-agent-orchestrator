using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: pure event inputs and a fixed clock.
public sealed class OwnerTestImpactHeadroomActivityTests
{
    private const string Fields = "goal=11111111 candidate=c main=m bound=IndexedSourceFiles count=3200 cap=4000";

    [Fact]
    public void Narrate_HeadroomWarning_ShowsCountsAndOwnerAction()
    {
        var evt = Headroom(Fields);
        Assert.True(OwnerActivityNarrator.Maps(evt));
        Assert.True(OwnerConsoleStartupActivity.IsOperatorEvent(evt));
        var item = Assert.Single(OwnerActivityNarrator.Narrate([evt], _ => ""));
        Assert.Equal("Focused test selection near its limit: IndexedSourceFiles 3200 of 4000", item.Phrase);
        Assert.Equal("Above the limit, focused selection falls back to the full Infrastructure suite.", item.Why);
        Assert.Equal("Selection keeps working until a candidate passes the limit.", item.Next);
        Assert.Equal("Yes. Raise the limit or shrink the test closure before a candidate passes it.", item.Act);
    }

    [Theory]
    [InlineData("bound=IndexedSourceFiles cap=4000")]
    [InlineData("bound=IndexedSourceFiles count=3200")]
    [InlineData("count=3200 cap=4000")]
    [InlineData("bound=IndexedSourceFiles count=invalid cap=4000")]
    [InlineData("bound=IndexedSourceFiles count=3200 cap=invalid")]
    public void Narrate_MalformedHeadroom_PreservesBaselineItems(string fields)
    {
        var malformed = Headroom(fields);
        Assert.Empty(OwnerActivityNarrator.Narrate([malformed], _ => ""));
        OwnerConductEvent[] baseline =
        [
            new(DateTimeOffset.UnixEpoch, "goal-escalation", "22222222", "GOAL_ESCALATED reason=blocked"),
            new(DateTimeOffset.UnixEpoch.AddSeconds(1), "acceptance", "33333333", "result=passed")
        ];
        var expected = OwnerActivityNarrator.Narrate(baseline, _ => "fixture");
        Assert.Equal(2, expected.Count);
        var actual = OwnerActivityNarrator.Narrate([.. baseline, malformed], _ => "fixture");
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            // Narration creates fresh title lists; compare their values, not record list identity.
            Assert.Equal(expected[index] with { Spans = null }, actual[index] with { Spans = null });
            Assert.Equal(expected[index].Spans?.OutcomeStart, actual[index].Spans?.OutcomeStart);
            Assert.Equal(expected[index].Spans?.OutcomeLength, actual[index].Spans?.OutcomeLength);
            Assert.Equal(expected[index].Spans?.Titles.ToArray(), actual[index].Spans?.Titles.ToArray());
        }
    }

    private static OwnerConductEvent Headroom(string fields) => new(DateTimeOffset.UnixEpoch.AddSeconds(2),
        "test-impact-headroom-low", "11111111", "TEST_IMPACT_HEADROOM_LOW " + fields);
}
