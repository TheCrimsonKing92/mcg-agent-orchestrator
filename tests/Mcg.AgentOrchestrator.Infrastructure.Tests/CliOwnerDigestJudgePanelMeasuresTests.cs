using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using static OwnerDigestJudgePanelFixture;

public sealed class CliOwnerDigestJudgePanelMeasuresTests
{
    [Fact]
    public async Task ReportsExactValidAgreementAndProvisionalFractions()
    {
        using var fixture = await CreateAsync();
        const string unresolved = "hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh";
        await fixture.AddGoalAsync(GoalE);
        await fixture.AddGoalAsync(GoalF, [Retry(GoalF, 3)]);
        await fixture.AddGoalAsync(GoalG);
        await fixture.AddGoalAsync(unresolved);
        fixture.AddCase(GoalE, 1, new Call("sol", "no-action"), new Call("sonnet", "no-action"));
        fixture.AddCase(GoalF, 1, new Call("sol"), new Call("sonnet", "repair"));
        fixture.AddCase(GoalG, 1, new Call("sol", null, PanelJudgeOutcome.InvalidOutput), new Call("sonnet"));
        fixture.AddCase(unresolved, 1, new Call("sol"), new Call("sonnet"));
        fixture.Intent(GoalE, "adjudicate", 2, "close");
        fixture.Intent(GoalG, "adjudicate", 2, "close");

        var text = fixture.Run();

        Assert.Equal(new[]
        {
            "Panel valid output | sol | 3/4 | rate=0.75",
            "Panel valid output | sonnet | 4/4 | rate=1",
            "Panel next_action agreement | 2/3 | share=0.667",
            "Panel provisional-match | sol | 2/2 | share=1",
            "Panel provisional-match | sonnet | 2/3 | share=0.667"
        }, RateLines(text));
        using var json = JsonDocument.Parse(fixture.Run(json: true));
        var section = json.RootElement.GetProperty("judgePanel");
        Assert.Equal("""
            [{"judge":"sol","numerator":3,"denominator":4,"rate":0.75},{"judge":"sonnet","numerator":4,"denominator":4,"rate":1}]
            """, section.GetProperty("validOutput").GetRawText());
        Assert.Equal(2, section.GetProperty("nextActionAgreement").GetProperty("numerator").GetInt32());
        Assert.Equal(3, section.GetProperty("nextActionAgreement").GetProperty("denominator").GetInt32());
        Assert.Equal(2, section.GetProperty("provisionalMatch")[0].GetProperty("denominator").GetInt32());
        Assert.Equal(2, section.GetProperty("provisionalMatch")[1].GetProperty("numerator").GetInt32());
        Assert.Equal(3, section.GetProperty("provisionalMatch")[1].GetProperty("denominator").GetInt32());
        Assert.DoesNotContain("confirmed", section.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkippedCallsAndMalformedValidAnswersCannotInflateAgreementOrMatch()
    {
        using var fixture = await CreateAsync();
        await fixture.AddGoalAsync(GoalE);
        await fixture.AddGoalAsync(GoalF);
        await fixture.AddGoalAsync(GoalG);
        fixture.AddCase(GoalE, 1, new Call("sol", null, PanelJudgeOutcome.InvalidOutput),
            new Call("sonnet", null, PanelJudgeOutcome.Skipped, Launched: false));
        fixture.AddCase(GoalF, 1, new Call("sol", AnswerOverride: "malformed"), new Call("sonnet"));
        fixture.AddCase(GoalG, 1, new Call("sol", null, PanelJudgeOutcome.TimedOut),
            new Call("sonnet", null, PanelJudgeOutcome.InvocationFailed));
        fixture.Intent(GoalF, "adjudicate", 2, "close");

        var text = fixture.Run();

        Assert.Equal(new[]
        {
            "Panel valid output | sol | 1/3 | rate=0.333",
            "Panel valid output | sonnet | 1/2 | rate=0.5",
            "Panel next_action agreement | 0/0 | share=n/a",
            "Panel provisional-match | sol | 0/0 | share=n/a",
            "Panel provisional-match | sonnet | 0/1 | share=0"
        }, RateLines(text));
        Assert.Contains("sol=valid | sonnet=valid retry/Developer", CaseLine(text, GoalF));
        Assert.Contains("sonnet=skipped", CaseLine(text, GoalE));
        Assert.Contains("sol=timed-out | sonnet=invocation-failed", CaseLine(text, GoalG));
    }

    [Fact]
    public async Task EmptyWindowPrintsNoRateLinesAndUsesHalfOpenTriggerWindow()
    {
        using var fixture = await CreateAsync();
        await fixture.AddGoalAsync(GoalE);
        fixture.AddCase(GoalE, -1, new Call("sol"), new Call("sonnet"));
        fixture.AddCase(GoalE, 24, new Call("sol"), new Call("sonnet"));
        var text = fixture.Run();
        Assert.Contains("Judge panel shadow cases: none in window", text);
        Assert.Empty(RateLines(text));
        using (var json = JsonDocument.Parse(fixture.Run(json: true)))
        {
            var section = json.RootElement.GetProperty("judgePanel");
            Assert.Empty(section.GetProperty("cases").EnumerateArray());
            Assert.False(section.TryGetProperty("validOutput", out _));
            Assert.False(section.TryGetProperty("nextActionAgreement", out _));
            Assert.False(section.TryGetProperty("provisionalMatch", out _));
        }

        fixture.AddCase(GoalE, 0, new Call("sol"), new Call("sonnet"));
        Assert.Contains($" | {Start:O} | completed |", CaseLine(fixture.Run(), GoalE));
    }

    [Theory]
    [InlineData("no-action", "close", true)]
    [InlineData("retry", "reopen-regate", true)]
    [InlineData("repair", "close", false)]
    [InlineData("request-evidence", "reopen-regate", true)]
    [InlineData("ask-owner", "route", true)]
    public async Task NamedMappingCoversEveryActionAndProducesExpectedMatch(string action, string shape, bool matches)
    {
        using var fixture = await CreateAsync();
        await fixture.AddGoalAsync(GoalE);
        fixture.AddCase(GoalE, 1, new Call("sol", action), new Call("sonnet", action));
        fixture.Intent(GoalE, "adjudicate", 2, shape);
        Assert.Equal(PanelV0Contract.ActionKinds.Order(StringComparer.Ordinal),
            CliOwnerDigestJudgePanel.ProvisionalMatchTable.Keys.Order(StringComparer.Ordinal));
        Assert.Contains($"Panel provisional-match | sol | {(matches ? "1/1 | share=1" : "0/1 | share=0")}", fixture.Run());
    }

    private static string[] RateLines(string text) => text.Split(Environment.NewLine)
        .Where(line => line.StartsWith("Panel valid output |", StringComparison.Ordinal) ||
            line.StartsWith("Panel next_action agreement |", StringComparison.Ordinal) ||
            line.StartsWith("Panel provisional-match |", StringComparison.Ordinal)).ToArray();
}
