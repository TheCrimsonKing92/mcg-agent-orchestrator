using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel safe: in-memory records, fixed dates and fixed durations.
public sealed class LaneReuseShadowReportTestsRuleSummary
{
    private static readonly DateTimeOffset Since = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Until = Since.AddDays(7);

    [Fact]
    public void PairedDecisions_AttributeSavingsAndExecutedRedMissesPerRule()
    {
        LaneReuseShadowRecord[] records =
        [
            new("paired", "attempt", Since, [
                Row("a", "would-reuse", "must-run", true, "RED", 1000),
                Row("b", "must-run", "would-reuse", true, "GREEN", 2000),
                Row("c", "would-reuse", "would-reuse", true, "RED", 3000) with
                { ShadowMiss = true, FlakeConfirmed = true, MissReason = "reference-green-execution-red", FailingClasses = ["C"] },
                Row("d", "would-reuse", "would-reuse", false, "RED", 9000),
                Row("e", "must-run", "would-reuse", true, "RED", null)]),
            new("legacy", "attempt", Since.AddDays(1), [
                new("legacy-reuse", "would-reuse", "unaffected", true, 4000, Verdict: "RED"),
                new("legacy-run", "must-run", "legacy-hold", true, 1000, Verdict: "GREEN")]),
            new("outside", "attempt", Until, [new("outside", "would-reuse", null, true, 100000)])
        ];
        var report = LaneReuseShadowReport.Build(records, Since, Until, 2, 3);
        Assert.Equal(new[] {
            new LaneReuseShadowRuleSummary("marker-v1", 5, 3, 0.6, 4, 2),
            new LaneReuseShadowRuleSummary("launch-contract-v2", 5, 4, 0.8, 5, 2)
        }, report.RuleSummaries);
        Assert.Equal(1, report.RecordsWithoutRuleV2);
        Assert.Equal(2, report.Gates);
        Assert.Equal(7, report.LaneRows);
        Assert.Equal(4, report.WouldReuseRows);
        Assert.Equal(11, report.ExecutedLaneSeconds);
        Assert.Equal(8, report.SavedLaneSeconds);
        Assert.Single(report.Misses);
        Assert.Equal(1, report.FlakeConfirmedMisses);

        var legacyOnly = records.Select(record => record with
        {
            Lanes = record.Lanes.Select(row => row with { RuleV2Decision = null, RuleV2Reason = null, Verdict = null }).ToArray()
        });
        var before = LaneReuseShadowReport.Build(legacyOnly, Since, Until, 2, 3);
        using var beforeJson = JsonDocument.Parse(JsonSerializer.Serialize(before));
        using var afterJson = JsonDocument.Parse(JsonSerializer.Serialize(report));
        foreach (var property in beforeJson.RootElement.EnumerateObject().Where(property =>
                     property.Name is not ("RuleSummaries" or "RecordsWithoutRuleV2")))
            Assert.Equal(property.Value.GetRawText(), afterJson.RootElement.GetProperty(property.Name).GetRawText());
    }

    [Fact]
    public void EmptyAndPartiallyPairedRecords_KeepZeroSharesAndRowPairing()
    {
        var empty = LaneReuseShadowReport.Build([], Since, Until);
        Assert.Equal(2, empty.RuleSummaries.Count);
        Assert.All(empty.RuleSummaries, rule =>
        {
            Assert.Equal(0, rule.PairedLaneRows);
            Assert.Equal(0, rule.WouldReuseShare);
            Assert.Equal(0, rule.SavedLaneSeconds);
            Assert.Equal(0, rule.Misses);
        });
        var partial = LaneReuseShadowReport.Build([new("partial", "attempt", Since, [
            Row("paired", "would-reuse", "would-reuse", true, "GREEN", 1000),
            new("unpaired", "would-reuse", null, true, 5000)])], Since, Until);
        Assert.Equal(1, partial.RecordsWithoutRuleV2);
        Assert.All(partial.RuleSummaries, rule =>
        {
            Assert.Equal(1, rule.PairedLaneRows);
            Assert.Equal(1, rule.WouldReuseRows);
            Assert.Equal(1, rule.WouldReuseShare);
            Assert.Equal(1, rule.SavedLaneSeconds);
            Assert.Equal(0, rule.Misses);
        });
    }

    private static LaneReuseShadowLaneRow Row(string lane, string v1, string v2, bool executed, string verdict, long? duration) =>
        new(lane, v1, v1 == "would-reuse" ? "unaffected" : "held", executed, duration,
            Verdict: verdict, RuleV2Decision: v2, RuleV2Reason: v2 == "would-reuse" ? "unaffected" : "held-v2");
}
