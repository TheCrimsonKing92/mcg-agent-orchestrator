using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Text;

// Pure display-column operations; safe to run in parallel.
public sealed class OwnerFleetLaneWrapTests
{
    [Theory]
    [InlineData("short lane", 10)]
    [InlineData("  solo 2.0s conc -", 30)]
    [InlineData("", 4)]
    public void FittingLineIsUnchanged(string text, int columns) =>
        Assert.Equal(text, Assert.Single(OwnerFleetLaneWrap.Wrap(text, columns)));

    [Fact]
    public void OneColumnOverBreaksAtLastFittingSpace() =>
        Assert.Equal(new[] { "alpha", "  beta" }, OwnerFleetLaneWrap.Wrap("alpha beta", 9));

    [Fact]
    public void SpaceAtBoundaryDoesNotSplitFittingWord() =>
        Assert.Equal(new[] { "alpha beta", "  gamma" }, OwnerFleetLaneWrap.Wrap("alpha beta gamma", 10));

    [Fact]
    public void WideRunesRemainWholeWithinColumnBudget()
    {
        const string text = "界界界界界界界";
        var rows = OwnerFleetLaneWrap.Wrap(text, 7);
        Assert.True(rows.Count > 1);
        Assert.All(rows, row => Assert.InRange(row.GetColumns(), 1, 7));
        Assert.Equal(text, string.Concat(rows.Select(row => row.TrimStart())));
    }

    [Fact]
    public void WrappingPreservesWordsAndSoloIndent()
    {
        const string text = "  solo 47.3s conc 123.4s";
        var rows = OwnerFleetLaneWrap.Wrap(text, 12);
        Assert.All(rows, row => Assert.StartsWith("  ", row));
        Assert.Equal(text.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            string.Join(" ", rows.Select(row => row.TrimStart())).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void InvalidBudgetFailsLoudly(int columns) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnerFleetLaneWrap.Wrap("lane", columns));
}
