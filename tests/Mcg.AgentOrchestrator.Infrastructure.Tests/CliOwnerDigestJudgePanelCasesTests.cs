using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using static OwnerDigestJudgePanelFixture;

public sealed class CliOwnerDigestJudgePanelCasesTests
{
    [Fact]
    public async Task ListsThreeCasesAndTheirObservedResolutionsInTriggerOrder()
    {
        using var fixture = await CreateAsync();
        await fixture.AddGoalAsync(GoalE);
        await fixture.AddGoalAsync(GoalF, [Retry(GoalF, 2.5)]);
        await fixture.AddGoalAsync(GoalG);
        fixture.AddCase(GoalE, 3, new Call("sol", "no-action"), new Call("sonnet", "ask-owner", Owner: "Operator"));
        fixture.AddCase(GoalF, 2, new Call("sol"), new Call("sonnet", "repair"));
        fixture.AddCase(GoalG, 1, new Call("sol", "request-evidence"), new Call("sonnet", "ask-owner"));
        fixture.Intent(GoalE, "adjudicate", 4, "close");
        var panelHash = SHA256.HashData(File.ReadAllBytes(fixture.PanelPath));
        var intentsHash = SHA256.HashData(File.ReadAllBytes(fixture.IntentsPath));

        var text = fixture.Run(rounds: true);

        Assert.Equal($"eeeeeeee | PreReviewEvidence | {Start.AddHours(3):O} | completed | sol=valid no-action/Developer | sonnet=valid ask-owner/Operator | operator-adjudicate-close",
            CaseLine(text, GoalE));
        Assert.Equal($"ffffffff | PreReviewEvidence | {Start.AddHours(2):O} | completed | sol=valid retry/Developer | sonnet=valid repair/Developer | developer-retry-by-conductor",
            CaseLine(text, GoalF));
        Assert.Equal($"gggggggg | PreReviewEvidence | {Start.AddHours(1):O} | completed | sol=valid request-evidence/Developer | sonnet=valid ask-owner/Developer | unresolved",
            CaseLine(text, GoalG));
        var lines = text.Split(Environment.NewLine).Where(line => line.Contains(" | PreReviewEvidence |", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { CaseLine(text, GoalG), CaseLine(text, GoalF), CaseLine(text, GoalE) }, lines);
        Assert.Equal(2, text.Split(ConductorJudgePanelPacketBuilder.AuthorityBoundary, StringSplitOptions.None).Length);
        using var json = JsonDocument.Parse(fixture.Run(json: true));
        var section = json.RootElement.GetProperty("judgePanel");
        Assert.Equal(ConductorJudgePanelPacketBuilder.AuthorityBoundary, section.GetProperty("authorityBoundary").GetString());
        Assert.Equal(new[] { "unresolved", "developer-retry-by-conductor", "operator-adjudicate-close" },
            section.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("resolution").GetString()).ToArray());
        var judges = section.GetProperty("cases")[2].GetProperty("judges");
        Assert.Equal("valid", judges[0].GetProperty("outcome").GetString());
        Assert.Equal("no-action", judges[0].GetProperty("nextActionKind").GetString());
        Assert.Equal("Developer", judges[0].GetProperty("nextActionOwner").GetString());
        Assert.Equal(panelHash, SHA256.HashData(File.ReadAllBytes(fixture.PanelPath)));
        Assert.Equal(intentsHash, SHA256.HashData(File.ReadAllBytes(fixture.IntentsPath)));
    }

    [Fact]
    public async Task IntentsBeforeOrAtTriggerAndOtherGoalsCannotResolveCase()
    {
        using var fixture = await CreateAsync();
        await fixture.AddGoalAsync(GoalE);
        fixture.AddCase(GoalE, 2, new Call("sol"), new Call("sonnet"));
        fixture.Intent(GoalE, "adjudicate", 1, "close");
        fixture.Intent(GoalE, "adjudicate", 2, "close");
        fixture.Intent(GoalF, "adjudicate", 3, "close");
        fixture.Intent(GoalE, "retry", 3, status: "Pending");
        fixture.Intent(GoalE, "retry", 3, status: "Rejected");
        fixture.Intent(GoalE, "progress", 3);

        Assert.EndsWith(" | unresolved", CaseLine(fixture.Run(), GoalE));

        fixture.Intent(GoalE, "answer", 4);
        Assert.EndsWith(" | operator-answer", CaseLine(fixture.Run(), GoalE));
    }

    [Fact]
    public async Task OperatorRetryOwnsPairedTimelineEventButNotLaterDispatchInterval()
    {
        using var fixture = await CreateAsync();
        await fixture.AddGoalAsync(GoalE, [Retry(GoalE, 2)], [2.5]);
        await fixture.AddGoalAsync(GoalF, [Retry(GoalF, 2)], [2.5]);
        fixture.AddCase(GoalE, 1, new Call("sol"), new Call("sonnet"));
        fixture.AddCase(GoalF, 1, new Call("sol"), new Call("sonnet"));
        fixture.Intent(GoalE, "retry", 2.25);
        fixture.Intent(GoalF, "retry", 3);

        var text = fixture.Run();
        Assert.EndsWith(" | operator-retry-developer", CaseLine(text, GoalE));
        Assert.EndsWith(" | developer-retry-by-conductor", CaseLine(text, GoalF));
    }

    [Theory]
    [InlineData(2, "landed")]
    [InlineData(3, "operator-adjudicate-close")]
    [InlineData(4, "operator-adjudicate-close")]
    public async Task EarliestSourceWinsAndOperatorIntentWinsExactTie(double landingHour, string expected)
    {
        using var fixture = await CreateAsync();
        await fixture.AddGoalAsync(GoalE);
        fixture.AddCase(GoalE, 1, new Call("sol", "no-action"), new Call("sonnet", "no-action"));
        fixture.Intent(GoalE, "adjudicate", 3, "close");
        fixture.Land(GoalE, landingHour);

        Assert.EndsWith(" | " + expected, CaseLine(fixture.Run(), GoalE));
    }

    [Fact]
    public async Task TimelineAtTriggerIsExcludedAndLaterCancellationResolvesGoal()
    {
        using var fixture = await CreateAsync();
        await fixture.AddGoalAsync(GoalE,
            [Retry(GoalE, 2), new(GoalE, null, ProgressKind.GoalCancelled, "cancel receipt", Start.AddHours(4))]);
        fixture.AddCase(GoalE, 2, new Call("sol"), new Call("sonnet"));
        Assert.EndsWith(" | cancelled", CaseLine(fixture.Run(), GoalE));
    }

    [Fact]
    public async Task MissingTriggerIsCountedAndOwnerCannotInjectColumnsOrLines()
    {
        using var fixture = await CreateAsync();
        await fixture.AddGoalAsync(GoalE);
        await fixture.AddGoalAsync(GoalF);
        fixture.AddCase(GoalE, 1, new Call("sol", Owner: "Developer|\nOperator"), new Call("sonnet"));
        fixture.AddCase(GoalF, 2, false, new Call("sol"), new Call("sonnet"));
        var text = fixture.Run();
        Assert.Contains("sol=valid retry/Developer/ Operator", CaseLine(text, GoalE));
        Assert.Contains("Panel cases without a recorded trigger time: 1", text);
        Assert.DoesNotContain("ffffffff | PreReviewEvidence |", text);
    }
}
