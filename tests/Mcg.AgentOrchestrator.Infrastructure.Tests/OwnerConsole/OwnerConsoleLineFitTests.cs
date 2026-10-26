using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Text;

// Pure fitter/narrator cases are parallel-safe; the view case owns an isolated scene.
public sealed class OwnerConsoleLineFitTests
{
    [Fact]
    public async Task ActivityAndBoardFitWideNarrowAndResizeFromTheFullModel()
    {
        using var scene = new OwnerConsoleActivityOutcomeTests.Scene();
        var title = "Make owner console activity understandable " + new string('x', 60);
        scene.Harness.AddGoal("11111111", title, AgentRole.Developer);
        var time = scene.Harness.Clock.GetUtcNow();
        await scene.Render([
            new(time, "goal-lifecycle", "11111111", "TaskCompleted role=Developer"),
            new(time.AddSeconds(1), "goal-lifecycle", "11111111", "TaskFailed role=Planner rejection=missing tools.json")]);
        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 200, 5);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 200, 8);
        Assert.All(scene.View.ActivityLines, line => { Assert.Contains(title, line); Assert.DoesNotContain("…", line); });
        Assert.Equal(title, scene.View.BoardTable.Rows[0]["TITLE"]);
        scene.View.ActivityPane.SelectedItem = 1;

        // Frame/viewport changes drive the production resize handlers, without a second model build.
        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 80, 5);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 80, 8);
        Assert.All(scene.View.ActivityLines, line =>
        {
            Assert.True(line.GetColumns() <= 80);
            Assert.Contains("11111111", line);
            Assert.Contains("…", line);
        });
        Assert.Contains($"{time.ToLocalTime():HH:mm:ss}", scene.View.ActivityLines[1]);
        Assert.Contains("Developer passed", scene.View.ActivityLines[1]);
        Assert.Contains($"{time.AddSeconds(1).ToLocalTime():HH:mm:ss}", scene.View.ActivityLines[0]);
        Assert.Contains("sent", scene.View.ActivityLines[0]);
        Assert.Contains("back: plan rejected: missing tools.json", scene.View.ActivityLines[0]);
        Assert.Equal(1, scene.View.ActivityPane.SelectedItem);
        var boardTitle = Assert.IsType<string>(scene.View.BoardTable.Rows[0]["TITLE"]);
        Assert.Contains("…", boardTitle);
        Assert.Equal(scene.View.BoardPane.Style.GetColumnStyleIfAny(2)!.MaxWidth, boardTitle.GetColumns());
        Assert.Equal(title, scene.Controller.Model!.Board[0].Title);
        Assert.All(scene.Controller.Model.Activity, item => Assert.Contains(title, item.Phrase));

        scene.View.ActivityPane.Frame = new Rectangle(0, 0, 200, 5);
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 200, 8);
        Assert.All(scene.View.ActivityLines, line => Assert.Contains(title, line));
        Assert.Equal(title, scene.View.BoardTable.Rows[0]["TITLE"]);
    }

    [Fact]
    public void FittingCountsTerminalCellsAndKeepsTheOutcomeAfterAWideUnicodeTitle()
    {
        var title = string.Concat(Enumerable.Repeat("界", 40));
        const string lead = "12:34:56 11111111 Developer passed ";
        var line = lead + title;
        var fit = OwnerConsoleLineFitter.Fit(line, new([new(lead.Length, title.Length, 0)]), 80);
        Assert.Equal(80, fit.GetColumns());
        Assert.StartsWith("12:34:56 11111111 Developer passed ", fit);
        Assert.EndsWith("…", fit);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void LandedTogetherKeepsEveryLabelAndIdWithReadableTitles(int width)
    {
        string[] ids = ["11111111", "22222222", "33333333"];
        string[] labels = ["UI:", "Ops:", "Db:"];
        var titles = labels.Select((label, i) => label + " " + new string((char)('a' + i), 90 + i * 20)).ToArray();
        var item = Landed(ids, titles);
        var full = OwnerActivityNarrator.Line(item);
        Assert.True(full.GetColumns() > width);
        var fit = Fit(item, width);
        Assert.True(fit.GetColumns() <= width);
        Assert.StartsWith("Landed together: ", fit[9..]);
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.Contains(labels[i], fit);
            Assert.Contains("(" + ids[i] + ")", fit);
            var segment = LandingTitle(fit, labels[i], ids[i]);
            if (width == 80) Assert.Equal(labels[i] + " …", segment);
            else
            {
                Assert.True(segment.GetColumns() >= 16);
                Assert.NotEqual(labels[i] + " …", segment);
                Assert.True(segment.Length < titles[i].Length);
            }
        }
        Assert.Equal(full, OwnerActivityNarrator.Line(item));
        Assert.Equal(titles, item.Spans!.Titles.Select(span => item.Phrase.Substring(span.Start, span.Length)));
    }

    [Fact]
    public void JointTestRunKeepsAllLabelsAndPassedAtNarrowWidth()
    {
        string[] titles = ["UI: " + new string('a', 120), "Ops: " + new string('b', 100), "Db: " + new string('c', 80)];
        var item = Joint(titles);
        var fit = Fit(item, 80);
        Assert.True(fit.GetColumns() <= 80);
        Assert.EndsWith(": passed", fit);
        var entries = fit[(fit.IndexOf("Joint test run for ", StringComparison.Ordinal) + "Joint test run for ".Length)..^8].Split(", ");
        Assert.Equal(3, entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            Assert.StartsWith(titles[i].Split(' ')[0], entries[i]);
            Assert.NotEqual("…", entries[i]);
            Assert.True(entries[i].GetColumns() >= 16 || entries[i] == titles[i].Split(' ')[0] + " …");
        }
        // Prefix-only fallback starts at the last goal, rather than collapsing the first.
        Assert.True(entries[0].GetColumns() >= 16);
        Assert.Equal("Ops: …", entries[1]);
        Assert.Equal("Db: …", entries[2]);
    }

    [Fact]
    public void UnlabeledTitleKeepsItsFloorBeforeEntryElision()
    {
        const string noLabel = "What this means names the goal once and describes its entire progress";
        var item = Joint(["UI: " + new string('a', 120), "Db: " + new string('b', 100), noLabel]);
        var fit = Fit(item, 80);
        Assert.True(fit.GetColumns() <= 80);
        Assert.EndsWith(": passed", fit);
        Assert.Contains("UI:", fit);
        Assert.Contains("Db:", fit);
        Assert.EndsWith(", What this means…: passed", fit);
        Assert.Equal(0, item.Spans!.Titles[2].PrefixLength);
    }

    [Fact]
    public void RenderedBackticksAndRewrittenWordsUsePositionsAndKeepLaterEntry()
    {
        const string raw = "Queries: serve `status [<goal>] --tasks-only` as a read-only receipt query with more detail";
        var item = Landed(["2ccd209d", "e70f1a44"], [raw, "UI: " + new string('x', 100)]);
        var spans = OwnerActivityNarrator.LineSpans(item);
        var full = OwnerActivityNarrator.Line(item);
        var first = spans.Titles[0];
        var rendered = full.Substring(first.Start, first.Length);
        Assert.Contains("`status [<goal>] --tasks-only`", rendered);
        Assert.Contains("proof", rendered);
        Assert.DoesNotContain("receipt", rendered);
        Assert.Equal(item.Phrase.Substring(item.Spans!.Titles[0].Start, first.Length), rendered);
        var fit = Fit(item, 165);
        Assert.True(fit.GetColumns() <= 165);
        Assert.Contains("`status [<goal>] --tasks-only`", fit);
        Assert.Contains("… (2ccd209d), UI:", fit);
        Assert.EndsWith("… (e70f1a44)", fit);
        Assert.True(LandingTitle(fit, "UI:", "e70f1a44").Length < 104);
    }

    [Fact]
    public void ProportionalSharesClampRedistributeAndBreakRoundingTiesByPosition()
    {
        var line = new string('a', 100) + ", " + new string('b', 50) + ", " + new string('c', 20);
        var spans = new OwnerConsoleLineSpans([new(0, 100, 0), new(102, 50, 0), new(154, 20, 0)]);
        var entries = OwnerConsoleLineFitter.Fit(line, spans, 140).Split(", ");
        Assert.Equal(new[] { 80, 40, 16 }, entries.Select(entry => entry.GetColumns()));
        entries = OwnerConsoleLineFitter.Fit(line, spans, 80).Split(", ");
        Assert.Equal(new[] { 39, 21, 16 }, entries.Select(entry => entry.GetColumns()));

        line = new string('a', 50) + ", " + new string('b', 50) + ", " + new string('c', 50);
        spans = new([new(0, 50, 0), new(52, 50, 0), new(104, 50, 0)]);
        entries = OwnerConsoleLineFitter.Fit(line, spans, 149).Split(", ");
        Assert.Equal(new[] { 48, 48, 49 }, entries.Select(entry => entry.GetColumns()));
    }

    [Fact]
    public void IdenticalRenderedTitlesHaveDistinctSpans()
    {
        var title = "UI: " + new string('x', 100);
        var item = Joint([title, title, title]);
        var fit = Fit(item, 120);
        Assert.True(fit.GetColumns() <= 120);
        Assert.EndsWith(": passed", fit);
        var entries = fit[(fit.IndexOf("Joint test run for ", StringComparison.Ordinal) + "Joint test run for ".Length)..^8].Split(", ");
        Assert.Equal(3, entries.Length);
        Assert.All(entries, entry => { Assert.StartsWith("UI:", entry); Assert.EndsWith("…", entry); });
        Assert.True(entries.Max(entry => entry.Length) - entries.Min(entry => entry.Length) <= 1);
    }

    [Fact]
    public void WholeEntryElisionAndFinalCutProtectTheOutcome()
    {
        var item = Joint(["UI: " + new string('a', 120), "Ops: " + new string('b', 100), "Db: " + new string('c', 80)]);
        var fit = Fit(item, 62);
        Assert.True(fit.GetColumns() <= 62);
        Assert.Contains("UI: …, Ops: …, …: passed", fit);
        Assert.DoesNotContain("Db:", fit);
        for (var width = 1; width <= 50; width++)
        {
            fit = Fit(item, width);
            Assert.True(fit.GetColumns() <= width);
            if (width >= "passed".Length) Assert.EndsWith("passed", fit);
        }
        var failed = Joint(["UI: " + new string('a', 120), "Db: " + new string('b', 100)], "failed");
        fit = Fit(failed, 50);
        Assert.True(fit.GetColumns() <= 50);
        Assert.Contains("failed", fit);
    }

    [Fact]
    public void FittingPreservesShortTitlesLongLabelsAndAlreadyFittingLines()
    {
        var item = Joint(["UI: short", "An exceptionally long label: " + new string('a', 100), new string('b', 100)]);
        var full = OwnerActivityNarrator.Line(item);
        Assert.Equal(full, Fit(item, full.GetColumns()));
        Assert.Equal(full, Fit(item, 0));
        var fit = Fit(item, 110);
        Assert.True(fit.GetColumns() <= 110);
        Assert.Contains("UI: short, An exceptionally long label:", fit);
        Assert.EndsWith(": passed", fit);
        var single = Landed(["11111111"], ["UI: short"]);
        Assert.Equal(OwnerActivityNarrator.Line(single), Fit(single, 80));
    }

    [Fact]
    public void InvalidTitleSpansFallBackWithoutThrowingOrLosingAValidOutcome()
    {
        const string line = "Some lengthy description: passed";
        var outcome = line.Length - "passed".Length;
        OwnerConsoleTitleSpan[][] bad = [[new(-1, 5, 0)], [new(0, 500, 0)], [new(0, 5, 6)],
            [new(4, 5, 0), new(3, 4, 0)], [new(0, 8, 0), new(4, 8, 0)], [new(outcome, 6, 0)]];
        foreach (var titles in bad)
        {
            var fit = OwnerConsoleLineFitter.Fit(line, new(titles, outcome, 6), 12);
            Assert.True(fit.GetColumns() <= 12);
            Assert.EndsWith("passed", fit);
        }
        Assert.Equal(OwnerConsoleLineFitter.Cut(line, 12), OwnerConsoleLineFitter.Fit(line, OwnerConsoleLineSpans.None, 12));
    }

    [Fact]
    public void WideTitlesKeepTheReadableFloorUntilWholeEntryElision()
    {
        var title = new string('界', 40);
        var line = string.Join(", ", title, title, title);
        var spans = new OwnerConsoleLineSpans([new(0, 40, 0), new(42, 40, 0), new(84, 40, 0)]);
        var entries = OwnerConsoleLineFitter.Fit(line, spans, 54).Split(", ");
        Assert.Equal(new[] { 17, 17, 1 }, entries.Select(entry => entry.GetColumns()));
        Assert.Equal("…", entries[2]);
    }

    [Theory]
    [InlineData("goal-lifecycle", "TaskDispatched role=Developer")]
    [InlineData("goal-lifecycle", "TaskFailed role=Planner rejection=missing tools.json")]
    [InlineData("acceptance", "ACCEPTANCE result=passed")]
    [InlineData("acceptance", "ACCEPTANCE result=failed")]
    [InlineData("goal-stalled", "GOAL_STALLED repeatedForSeconds=300 blocker=waiting_for_approval")]
    [InlineData("state-log-divergence", "STATE_LOG_DIVERGENCE lost=1 repeated=0")]
    public void SingleGoalNarrationMarksTheRenderedTitleWithoutChangingFullText(string kind, string detail)
    {
        var title = "UI: `status [<goal>]` " + new string('x', 100);
        var item = Assert.Single(OwnerActivityNarrator.Narrate([new(DateTimeOffset.UnixEpoch, kind, "11111111", detail)], _ => title));
        var full = OwnerActivityNarrator.Line(item);
        var spans = OwnerActivityNarrator.LineSpans(item);
        var span = Assert.Single(spans.Titles);
        Assert.Equal(title, full.Substring(span.Start, span.Length));
        Assert.Equal(3, span.PrefixLength);
        var fit = Fit(item, 160);
        Assert.True(fit.GetColumns() <= 160);
        Assert.Contains("UI:", fit);
        if (kind == "acceptance")
        {
            var outcome = detail.EndsWith("passed", StringComparison.Ordinal) ? "passed" : "failed";
            Assert.Equal(outcome, full.Substring(spans.OutcomeStart, spans.OutcomeLength));
            Assert.Contains(outcome, fit);
        }
        Assert.Equal(full, OwnerActivityNarrator.Line(item));
        Assert.Equal(title, item.GoalTitle);
    }

    [Fact]
    public void CanaryNarrationCarriesTheLastTrainSpansIncludingRepeatedNames()
    {
        var title = "UI: " + new string('x', 100);
        var items = OwnerActivityNarrator.Narrate([
            new(DateTimeOffset.UnixEpoch, "loop-relaunch", "11111111", "LOOP_RELAUNCH_SCHEDULED tick=7"),
            new(DateTimeOffset.UnixEpoch, "loop-relaunch", "22222222", "LOOP_RELAUNCH_SCHEDULED tick=7"),
            new(DateTimeOffset.UnixEpoch.AddSeconds(1), "canary-gate", null, "CANARY_GATE result=failed tests=Check.Main")], _ => title);
        var item = Assert.Single(items, value => value.Kind == "canary-gate");
        var spans = OwnerActivityNarrator.LineSpans(item);
        var full = OwnerActivityNarrator.Line(item);
        Assert.Equal(2, spans.Titles.Count);
        Assert.All(spans.Titles, span => Assert.Equal(title, full.Substring(span.Start, span.Length)));
        var fit = Fit(item, 80);
        Assert.True(fit.GetColumns() <= 80);
        Assert.EndsWith(": Check.Main", fit);
        Assert.Equal(2, fit.Split("UI:").Length - 1);
    }

    private static string Fit(OwnerConsoleActivityItem item, int width) =>
        OwnerConsoleLineFitter.Fit(OwnerActivityNarrator.Line(item), OwnerActivityNarrator.LineSpans(item), width);

    private static OwnerConsoleActivityItem Landed(string[] ids, string[] titles) => Assert.Single(
        OwnerActivityNarrator.Narrate(ids.Select(id => new OwnerConductEvent(DateTimeOffset.UnixEpoch,
            "loop-relaunch", id, "LOOP_RELAUNCH_SCHEDULED tick=7")), id => titles[Array.IndexOf(ids, id!)]));

    private static OwnerConsoleActivityItem Joint(string[] titles, string outcome = "passed")
    {
        var ids = Enumerable.Range(1, titles.Length).Select(i => new string((char)('0' + i), 8)).ToArray();
        return Assert.Single(OwnerActivityNarrator.Narrate([new(DateTimeOffset.UnixEpoch, "acceptance-cohort", ids[0],
            "ACCEPTANCE_COHORT members=" + string.Join(",", ids) + " outcome=" + outcome)], id => titles[Array.IndexOf(ids, id!)]));
    }

    private static string LandingTitle(string line, string label, string id)
    {
        var start = line.IndexOf(label, StringComparison.Ordinal);
        var end = line.IndexOf(" (" + id + ")", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return line[start..end];
    }
}
