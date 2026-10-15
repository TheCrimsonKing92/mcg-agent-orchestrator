using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Text;

// Pure narration and wrapping with no shared terminal or store state.
public sealed class OwnerActivityGoalListExplainTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly string[] Ids = ["11111111", "22222222", "33333333"];
    private static readonly string[] Titles = [
        "UI: Keep every listed goal readable in the activity explanation",
        "Ops: Show each long goal title with its identifying prefix",
        "Db: Preserve the full description of the third listed goal"];

    [Theory]
    [InlineData("landing")]
    [InlineData("joint")]
    [InlineData("main")]
    public void MultiGoalDetailListsEachGoalAndPreservesRowAndSpans(string kind)
    {
        var item = MultiGoal(kind);
        var count = kind == "joint" ? 3 : 2;
        var list = string.Join(", ", Titles.Take(count).Select((title, i) =>
            kind == "landing" ? title + " (" + Ids[i] + ")" : title));
        var suffix = kind == "joint" ? ": failed (Check.Joint failed); awaiting the conductor's next step" :
            kind == "main" ? ": Check.Main" : "";
        var lead = kind == "landing" ? "Landed together: " : kind == "joint" ? "Joint test run for " : "Main broke after landing ";
        var time = $"{item.Timestamp.ToLocalTime():HH:mm:ss} ";
        var row = time + (kind == "joint" ? "11111111 " : "") + lead + list + suffix;
        Assert.Equal(row, OwnerActivityNarrator.Line(item));
        var spans = OwnerActivityNarrator.LineSpans(item).Titles;
        Assert.Equal(count, spans.Count);
        for (var i = 0; i < count; i++)
            Assert.Equal(Titles[i], row.Substring(spans[i].Start, spans[i].Length));

        var text = OwnerActivityNarrator.Explain(item);
        var lines = text.Split('\n');
        Assert.Equal(count + 4, lines.Length);
        Assert.Equal("What happened: " + time + lead + count + " goals" + suffix, lines[0]);
        Assert.Equal(Ids.Take(count), item.GoalPrefixes);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal("  " + Ids[i] + " " + Titles[i], lines[i + 1]);
            Assert.Equal(1, text.Split(Titles[i], StringSplitOptions.None).Length - 1);
        }
        Assert.Equal("Why: " + item.Why, lines[count + 1]);
        Assert.Equal("What happens next: " + item.Next, lines[count + 2]);
        Assert.Equal("Do you need to act: No. No owner question is listed in DECISIONS for this event.", lines[count + 3]);
    }

    [Fact]
    public void NarrowLandingDetailKeepsEachGoalInItsOwnParagraph()
    {
        var page = new OwnerConsoleTextPage(OwnerActivityNarrator.Explain(MultiGoal("landing")), 40, 50);
        var header = page.Lines.ToList().FindIndex(line => line.StartsWith("What happened:", StringComparison.Ordinal));
        var sources = Ids.Take(2).Select(id =>
        {
            var index = page.Lines.ToList().FindIndex(line => line.StartsWith("  " + id + " ", StringComparison.Ordinal));
            Assert.True(index >= 0);
            Assert.NotEqual(page.SourceLines[header], page.SourceLines[index]);
            return page.SourceLines[index];
        }).ToArray();
        Assert.NotEqual(sources[0], sources[1]);
        Assert.All(page.Lines, line => Assert.True(line.GetColumns() <= 40));
    }

    [Theory]
    [InlineData("landing")]
    [InlineData("acceptance")]
    [InlineData("main")]
    public void SingleGoalDetailsKeepFourLinesAndProseReason(string kind)
    {
        OwnerConductEvent[] events = kind == "acceptance"
            ? [new(At, "acceptance", Ids[0], "ACCEPTANCE result=failed tests=Check.One")]
            : [new(At, "loop-relaunch", Ids[0], "LOOP_RELAUNCH_SCHEDULED tick=7")];
        if (kind == "main") events = [.. events, new(At.AddSeconds(1), "canary-gate", null, "CANARY_GATE result=failed tests=Check.Main")];
        var item = OwnerActivityNarrator.Narrate(events, _ => Titles[0])[0];
        var lines = OwnerActivityNarrator.Explain(item).Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Null(item.GoalPrefixes);
        Assert.Equal("What happened: " + OwnerActivityNarrator.Line(item), lines[0]);
        if (kind == "landing") Assert.Contains("Landed: " + Titles[0] + " (11111111)", lines[0]);
        Assert.Equal("Why: " + item.Why, lines[1]);
        Assert.DoesNotContain('\n', item.Why);
    }

    [Fact]
    public void AuthorResolutionCapitalizesOnlyTheActSentence()
    {
        var answeredAt = At.AddSeconds(5);
        var items = OwnerActivityNarrator.Narrate([
            new(At, "goal-escalation", Ids[0], "ESCALATED reason=Resolve the spec clarification before dispatching planner work"),
            new(answeredAt, "goal-lifecycle", Ids[0], "HumanInputReceived resolution-verb=answer resolution-actor=author")], _ => Titles[0]);
        var item = Assert.Single(items, value => value.Kind == "goal-escalation");
        var lines = OwnerActivityNarrator.Explain(item).Split('\n');
        Assert.Equal($"Do you need to act: No. Resolved at {answeredAt.ToLocalTime():HH:mm:ss} by the Author.", lines[^1]);
        Assert.Contains($"resolved at {answeredAt.ToLocalTime():HH:mm:ss} by the Author", lines[^2]);
    }

    [Fact]
    public void LiveDecisionActSentenceStartsWithOpen()
    {
        var question = new OwnerQuestion("q1", Ids[0], OwnerQuestionKind.HumanInput, "Continue?");
        var item = Assert.Single(OwnerActivityNarrator.Narrate([], _ => Titles[0],
            attention: [new(question, At)]));
        var decision = new OwnerConsoleDecision("q1", 1, Ids[0], Ids[0], OwnerQuestionKind.HumanInput,
            "Continue?", "Continue?", null, null, null);
        var lines = OwnerActivityNarrator.Explain(item, [decision]).Split('\n');
        Assert.Equal("Do you need to act: Yes. Open DECISIONS row [1] 11111111 HumanInput: Continue?", lines[^1]);
    }

    private static OwnerConsoleActivityItem MultiGoal(string kind)
    {
        OwnerConductEvent[] events = kind == "joint"
            ? [new(At, "acceptance-cohort", Ids[0], "ACCEPTANCE_COHORT outcome=failed members=11111111,22222222,33333333 tests=Check.Joint")]
            : [new(At, "loop-relaunch", Ids[0], "LOOP_RELAUNCH_SCHEDULED tick=7"),
                new(At, "loop-relaunch", Ids[1], "LOOP_RELAUNCH_SCHEDULED tick=7")];
        if (kind == "main") events = [.. events, new(At.AddSeconds(1), "canary-gate", null, "CANARY_GATE result=failed tests=Check.Main")];
        return OwnerActivityNarrator.Narrate(events, id => id is null ? "This work" : Titles[Array.IndexOf(Ids, id)])[0];
    }
}
