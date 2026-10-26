using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Text;

// Parallel-safe: immutable inputs and pure formatting, no terminal or store state.
public sealed class OwnerConsoleEpicFormatterTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    public void NarrowListKeepsCountsAndShortensTitles(int width)
    {
        var row = Row(new string('T', 150));
        var model = new OwnerConsoleEpicViewModel(OwnerConsoleEpicWindow.Day, null, [row]);
        var lines = OwnerConsoleEpicFormatter.ListLines(model, width, row.Epic.Id);

        Assert.All(lines, line => Assert.True(line.Length <= width, line));
        Assert.EndsWith("…", lines[0]);
        Assert.Equal("> " + new string('T', width - 3) + "…", lines[0]);
        Assert.Equal("landed 1 · in flight 3 (1 verifying, 2 active) · failed 1 · backlog 2 open / 1 done",
            string.Join(" ", lines.Skip(1).Where(line => line.Length > 0)));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    public void DetailPreservesPlanAndStageAtNarrowWidths(int width)
    {
        const string plan = "Purpose: build an owner console with every phase visible.\nNext: review the complete plan together.";
        var epic = Row(new string('T', 150)).Epic with { Description = plan };
        var detail = new OwnerConsoleEpicViewModel.Detail(epic,
            [new("12345678-goal", new string('G', 100), "Developer")], [],
            [new("87654321-fail", "Failure", "Tester", "External dependency returned a rejected response.")], []);
        var lines = OwnerConsoleEpicFormatter.DetailLines(detail, width);

        Assert.All(lines, line => Assert.True(line.Length <= width, line));
        Assert.Contains(lines, line => line.StartsWith("12345678  ", StringComparison.Ordinal) && line.EndsWith("  Developer", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains('…') && line.EndsWith("  Developer", StringComparison.Ordinal));
        var planLines = lines.Skip(2).TakeWhile(line => line != "Epic plan:").Where(line => line.Length > 0);
        Assert.Equal(plan.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
            string.Join(" ", planLines).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("External dependency returned a rejected response.", string.Join(" ", lines));
    }

    [Fact]
    public void EmptyEpicsAndMissingPlansHaveExplicitText()
    {
        var model = new OwnerConsoleEpicViewModel(OwnerConsoleEpicWindow.AllTime, null, []);
        Assert.Equal(["No epics are defined yet. Create one with epic-add."], OwnerConsoleEpicFormatter.ListLines(model, 100, null));
        var row = Row("Empty");
        Assert.Contains("(no plan of record)", OwnerConsoleEpicFormatter.DetailLines(new(row.Epic, [], [], [], []), 100));
    }

    [Fact]
    public void VeryNarrowDetailKeepsStageAndLongPlanTokens()
    {
        const string token = "abcdefghijklmnopqrstuvwxyz";
        var detail = new OwnerConsoleEpicViewModel.Detail(Row("Epic").Epic with { Description = token },
            [new("12345678-id", "Title", "Developer")], [], [], []);
        var lines = OwnerConsoleEpicFormatter.DetailLines(detail, 5);
        Assert.All(lines, line => Assert.True(line.Length <= 5, line));
        Assert.Contains(token, string.Concat(lines));
        Assert.Contains("Developer", string.Concat(lines));
    }

    [Fact]
    public void WideUnicodeUsesTerminalColumnsAndPreservesPlanRunes()
    {
        var title = string.Concat(Enumerable.Repeat("界🌍", 30));
        var row = Row(title);
        var model = new OwnerConsoleEpicViewModel(OwnerConsoleEpicWindow.Day, null, [row]);
        var lines = OwnerConsoleEpicFormatter.ListLines(model, 40, row.Epic.Id);
        Assert.All(lines, line => Assert.True(line.GetColumns() <= 40, line));
        Assert.EndsWith("…", lines[0]);
        var planLines = OwnerConsoleEpicFormatter.Wrap(title, 40).ToArray();
        Assert.All(planLines, line => Assert.True(line.GetColumns() <= 40, line));
        Assert.Equal(title, string.Concat(planLines));
    }

    private static EpicProgressRollup Row(string title)
    {
        var now = DateTimeOffset.Parse("2026-10-08T22:00:00Z");
        var epic = new PortfolioEpic("epic", title, null, now, "test", now, "test");
        return new(epic, null, [], [], 0, 0, 2, 0, 0, 9, now, 1, 8, 0, 0, 2, 1,
            WindowFailedCount: 1, WindowLandedCount: 1);
    }
}
