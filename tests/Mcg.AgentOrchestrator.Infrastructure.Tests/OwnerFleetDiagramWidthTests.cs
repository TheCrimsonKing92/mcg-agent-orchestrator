using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Text;

// Pure model fixtures; safe to run in parallel.
public sealed class OwnerFleetDiagramWidthTests
{
    private const string Lane = "infrastructure tests: Goal lifecycle commands";

    [Theory]
    [InlineData(120)]
    [InlineData(60)]
    [InlineData(40)]
    public void LaneRowsWrapWithoutLosingText(int width)
    {
        var text = OwnerFleetDiagram.Render(Model(Lane), width);
        var rows = text.Split('\n');
        Assert.All(rows, row => Assert.Equal(width, row.GetColumns()));
        Assert.DoesNotContain('…', text);
        var first = Array.FindIndex(rows, row => row.Contains("infrastructure", StringComparison.Ordinal));
        Assert.True(first >= 0);
        var count = width == 120 ? 1 : 2;
        var laneRows = rows.Skip(first).Take(count).ToArray();
        Assert.Equal(Lane + " median 47.3s n=12", string.Join(" ", laneRows.Select(Content)));
        if (width == 120) Assert.Contains(Lane + " median 47.3s n=12", laneRows[0]);
        else Assert.StartsWith("│   ", laneRows[1]);
        Assert.Contains("abcdefghijklmnopqrst median 2.0s n=1", text);
    }

    [Fact]
    public void LongWordIsHardSplitWithoutCharacterLoss()
    {
        var word = new string('x', 60);
        var rows = OwnerFleetDiagram.Render(Model(word), 40).Split('\n');
        Assert.All(rows, row => Assert.Equal(40, row.GetColumns()));
        var first = Array.FindIndex(rows, row => row.Contains("xxxxxxxx", StringComparison.Ordinal));
        Assert.True(first >= 0);
        var laneRows = rows.Skip(first).TakeWhile(row => !row.StartsWith("│   solo", StringComparison.Ordinal)).ToArray();
        var parts = laneRows.Select(Content).ToArray();
        Assert.Equal(word + " median 47.3s n=12", parts[0] + string.Join(" ", parts.Skip(1)));
        Assert.DoesNotContain('…', string.Join('\n', laneRows));
    }

    [Fact]
    public void BelowMinimumStillThrows() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnerFleetDiagram.Render(Model(Lane), 39));

    private static string Content(string row) => row[2..^2].Trim();

    private static OwnerFleetViewModel Model(string lane) => OwnerFleetViewModelBuilder.Build(
        new([new("executor", 0, new Dictionary<string, int>(), null, null, [], null, null, 0, 0,
            [new(lane, 12, 47.3, 47.3, null, null), new("abcdefghijklmnopqrst", 1, 2, 2, null, null)])], 0, 0),
        true, TimeZoneInfo.Utc);
}
