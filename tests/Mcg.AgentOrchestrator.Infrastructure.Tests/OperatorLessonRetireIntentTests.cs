using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorLessonRetireIntentTests
{
    [Fact]
    public async Task RetirementAppendsHistoryAndRejectsUnknownOrRepeatedId()
    {
        using var fixture = new OperatorLessonHarness();
        File.WriteAllText(Path.Combine(fixture.Root, "proof.txt"), "proof");
        var record = await fixture.Record(["operator-evidence:proof.txt"]);
        fixture.Tick();
        var unknown = await fixture.Retire("missing");
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.IntentStore.GetAsync(unknown.Id))!.Status);
        Assert.Null(Assert.Single(fixture.LessonStore.List()).RetiredAt);

        fixture.Now = fixture.Now.AddHours(1);
        var retirement = await fixture.Retire(record.Id, "Rule corrected");
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.IntentStore.GetAsync(retirement.Id))!.Status);
        Assert.Empty(fixture.LessonStore.List());
        var retired = Assert.Single(fixture.LessonStore.List(includeRetired: true));
        Assert.Equal(record.Id, retired.Id);
        Assert.Equal("Rule corrected", retired.RetireReason);
        Assert.Equal("tester", retired.RetiredBy);
        Assert.Equal(fixture.Now, retired.RetiredAt);

        var repeated = await fixture.Retire(record.Id);
        fixture.Tick();
        Assert.Equal(OperatorIntentStatus.Rejected, (await fixture.IntentStore.GetAsync(repeated.Id))!.Status);
        Assert.Equal("Rule corrected", Assert.Single(fixture.LessonStore.List(includeRetired: true)).RetireReason);
    }
}
