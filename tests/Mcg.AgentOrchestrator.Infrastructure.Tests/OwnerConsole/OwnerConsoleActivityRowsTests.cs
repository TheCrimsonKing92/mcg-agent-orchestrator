using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: fixed input instants; no shared state or clock mutation.
public sealed class OwnerConsoleActivityRowsTests
{
    [Fact]
    public void Build_TwoLocalDates_LabelsEachDayAndPreservesItemIndices()
    {
        OwnerConsoleActivityItem[] items = [Item(18, 0, 1, 0), Item(18, 0, 0, 30), Item(17, 23, 59, 30)];

        var rows = OwnerConsoleActivityRows.Build(items);

        Assert.Equal(new[] { "-- Sun 2026-10-18 --", OwnerActivityNarrator.Line(items[0]),
            OwnerActivityNarrator.Line(items[1]), "-- Sat 2026-10-17 --", OwnerActivityNarrator.Line(items[2]) },
            rows.Select(row => row.Text));
        Assert.Equal(new int?[] { null, 0, 1, null, 2 }, rows.Select(row => row.ItemIndex));
    }

    [Fact]
    public void Build_ThreeLocalDates_KeepsDayOrderAndStableUniqueKeys()
    {
        OwnerConsoleActivityItem[] items = [Item(18, 12, 0, 0), Item(17, 12, 0, 0), Item(16, 12, 0, 0)];
        var rows = OwnerConsoleActivityRows.Build(items);
        var separators = rows.Where(row => row.ItemIndex is null).ToArray();

        Assert.Equal(new[] { "-- Sun 2026-10-18 --", "-- Sat 2026-10-17 --", "-- Fri 2026-10-16 --" },
            separators.Select(row => row.Text));
        Assert.All(separators, row => Assert.Equal("day-separator", row.Key.Kind));
        Assert.Equal(rows.Count, rows.Select(row => row.Key).Distinct().Count());
        var refreshed = OwnerConsoleActivityRows.Build([Item(18, 13, 0, 0), .. items]);
        Assert.Equal(separators.Select(row => row.Key),
            refreshed.Where(row => row.ItemIndex is null).Select(row => row.Key));
        Assert.Equal(refreshed.Count, refreshed.Select(row => row.Key).Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public void Build_SingleLocalDate_MatchesExistingLinesIndicesAndKeys(int count)
    {
        var latest = Item(18, 12, 0, 0);
        var items = Enumerable.Range(0, count)
            .Select(index => latest with { Timestamp = latest.Timestamp.AddSeconds(-index) }).ToArray();
        var keys = OwnerConsoleActivityFollow.Keys(items);

        var rows = OwnerConsoleActivityRows.Build(items);

        Assert.Equal(items.Length, rows.Count);
        for (var index = 0; index < items.Length; index++)
        {
            Assert.Equal(OwnerActivityNarrator.Line(items[index]), rows[index].Text);
            Assert.Equal(index, rows[index].ItemIndex);
            Assert.Equal(keys[index], rows[index].Key);
        }
        Assert.DoesNotContain(rows, row => row.Key.Kind == "day-separator");
    }

    private static OwnerConsoleActivityItem Item(int day, int hour, int minute, int second)
    {
        var local = new DateTime(2026, 10, day, hour, minute, second, DateTimeKind.Unspecified);
        return new(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)),
            "acceptance", "outcome", "11111111", "result=passed", Phrase: "Search passed");
    }
}
