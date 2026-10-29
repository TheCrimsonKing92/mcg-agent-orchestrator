using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: pure event detail parsing with no shared state or I/O.
public sealed class OwnerEventFieldsTests
{
    [Theory]
    [InlineData("kind=ask-owner question=Retry the build now? agent=Planner", "question", "Retry the build now?")]
    [InlineData("kind=ask-owner question=Retry the build now? agent=Planner", "reason", null)]
    [InlineData("kind=ask-owner question=Retry the build now? agent=Planner", "kind", "ask-owner")]
    [InlineData("attempt=1 role=Tester attempt=2", "attempt", "1")]
    public void Field_DetailWithSpacesMissingLeadingOrRepeatedKey_ReturnsExactValue(
        string detail, string name, string? expected)
    {
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "", null, detail);

        Assert.Equal(expected, OwnerEventFields.Field(item, name));
    }

    [Theory]
    [InlineData("STATE_LOG_DIVERGENCE lost=1 lost=1", "lost", false)]
    [InlineData("STATE_LOG_DIVERGENCE lost=many", "lost", false)]
    [InlineData("STATE_LOG_DIVERGENCE lost=2 repeated=0", "lost", true)]
    [InlineData("STATE_LOG_DIVERGENCE lost=2 repeated=0", "repeated", false)]
    public void Positive_DuplicateNonNumericPositiveOrZeroValue_ReturnsExactFlag(
        string detail, string name, bool expected)
    {
        var item = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "", null, detail);

        Assert.Equal(expected, OwnerEventFields.Positive(item, name));
    }
}
