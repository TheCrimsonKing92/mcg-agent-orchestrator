using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: fixed local instants and pure formatting; no shared mutable state.
public sealed class OwnerConsoleActivityFilterTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  Developer started  ", "Developer started")]
    public void Normalize_TrimsAndKeepsInnerSpaces(string? text, string? expected) =>
        Assert.Equal(expected, OwnerConsoleActivityFilter.Normalize(text));

    [Theory]
    [InlineData("AbCd1234", "abc", true)]
    [InlineData("AbCd1234", "abcd1234-0000-0000", true)]
    [InlineData("AbCd1234", "developer", true)]
    [InlineData("AbCd1234", "DEVELOPER STARTED", true)]
    [InlineData("AbCd1234", "started", true)]
    [InlineData("", "987654321", false)]
    [InlineData("abc", "abc-000000000", false)]
    [InlineData("AbCd1234", "zzzz", false)]
    public void Matches_PrefixLongIdAndLine_UseOrdinalIgnoreCase(string goal, string filter, bool expected)
    {
        var item = Item(18, goal) with { Phrase = "Developer started" };
        Assert.Equal(expected, OwnerConsoleActivityFilter.Matches(item, filter));
    }

    [Fact]
    public void Build_EmptyFilters_KeepEveryOriginalRowField()
    {
        OwnerConsoleActivityItem[] items = [Item(18, "22222222"), Item(17, "11111111")];
        var original = OwnerConsoleActivityRows.Build(items);
        foreach (var filter in new string?[] { null, "", "  " })
        {
            var actual = OwnerConsoleActivityRows.Build(items, filter);
            Assert.Equal(original.Count, actual.Count);
            for (var index = 0; index < original.Count; index++) Assert.Equal(original[index], actual[index]);
        }
    }

    [Fact]
    public void Build_Filter_KeepsOriginalIndicesKeysAndOnlyMatchingDates()
    {
        var duplicate = Item(18, "22222222");
        OwnerConsoleActivityItem[] items =
            [Item(19, "11111111"), duplicate, duplicate, Item(17, "33333333"), Item(16, "22222222")];
        var keys = OwnerConsoleActivityFollow.Keys(items);
        var rows = OwnerConsoleActivityRows.Build(items, "  2222  ");
        Assert.Equal(new[] { "-- Sun 2026-10-18 --", OwnerActivityNarrator.Line(items[1]),
            OwnerActivityNarrator.Line(items[2]), "-- Fri 2026-10-16 --", OwnerActivityNarrator.Line(items[4]) },
            rows.Select(row => row.Text));
        Assert.Equal(new int?[] { null, 1, 2, null, 4 }, rows.Select(row => row.ItemIndex));
        Assert.Equal(new[] { keys[1], keys[2], keys[4] }, rows.Where(row => row.ItemIndex is not null).Select(row => row.Key));
        Assert.NotEqual(keys[1], keys[2]);

        var sameDate = OwnerConsoleActivityRows.Build(items[..4], "2222");
        Assert.Equal(new int?[] { 1, 2 }, sameDate.Select(row => row.ItemIndex));
        Assert.Equal(new[] { keys[1], keys[2] }, sameDate.Select(row => row.Key));
        Assert.DoesNotContain(sameDate, row => row.Key.Kind == "day-separator");
    }

    [Fact]
    public void Build_NoMatch_HasOneUnmappedFilterEmptyRow()
    {
        var row = Assert.Single(OwnerConsoleActivityRows.Build([Item(18, "22222222")], "zzzz"));
        Assert.Equal("No ACTIVITY row matches filter \"zzzz\"; type :filter to clear it.", row.Text);
        Assert.Equal("filter-empty", row.Key.Kind);
        Assert.Null(row.ItemIndex);
    }

    [Fact]
    public void Notices_AndCommandText_EmbedFilterVerbatim()
    {
        Assert.Equal("ACTIVITY filter \"a \"b\": 2 of 6 rows", OwnerConsoleActivityFilter.AppliedNotice("a \"b", 2, 6));
        Assert.Equal("ACTIVITY filter cleared", OwnerConsoleActivityFilter.ClearedNotice);
        Assert.Equal("Enter: no ACTIVITY row matches filter \"zzzz\"; type :filter to clear it",
            OwnerConsoleActivityFilter.EmptyEnterNotice("zzzz"));
        Assert.Equal("filter ", OwnerConsoleActivityFilter.CommandText(null));
        Assert.Equal("filter 2222", OwnerConsoleActivityFilter.CommandText("2222"));
    }

    [Theory]
    [InlineData("2222", true, 100, "ACTIVITY · filter \"2222\" · following · 3 above · 4 below")]
    [InlineData("2222", true, 30, "ACTIVITY /2222 follow ^3 v4")]
    [InlineData("2222", true, 14, "A… /2222 follow ^3 v4")]
    [InlineData("2222", false, 100, "ACTIVITY · filter \"2222\" · inspecting · 3 above · 4 below")]
    [InlineData("2222", false, 30, "ACTIVITY /2222 inspect ^3 v4")]
    [InlineData("2222", false, 14, "A… /2222 inspect ^3 v4")]
    [InlineData("abcdefghijklmnopqrst", true, 100, "ACTIVITY · filter \"abcdefghijklmnopqrst\" · following · 3 above · 4 below")]
    [InlineData("abcdefghijklmnopqrst", true, 30, "AC… /abcdefghijk… follow ^3 v4")]
    [InlineData("abcdefghijklmnopqrst", true, 14, "A… /abcdefghijk… follow ^3 v4")]
    [InlineData("abcdefghijklmnopqrst", false, 100, "ACTIVITY · filter \"abcdefghijklmnopqrst\" · inspecting · 3 above · 4 below")]
    [InlineData("abcdefghijklmnopqrst", false, 30, "A… /abcdefghijk… inspect ^3 v4")]
    [InlineData("abcdefghijklmnopqrst", false, 14, "A… /abcdefghijk… inspect ^3 v4")]
    public void Title_FilterAndMode_UsesExactLongShortAndCutForms(
        string filter, bool following, int columns, string expected)
    {
        var title = OwnerConsoleActivityTitle.Title(new(3, 4), following, columns, filter);
        Assert.Equal(expected, title);
        Assert.Contains(following ? "follow" : "inspect", title);
    }

    [Theory]
    [InlineData(0, 0, true, 100, "ACTIVITY · following")]
    [InlineData(0, 0, false, 100, "ACTIVITY · inspecting")]
    [InlineData(0, 0, true, 20, "ACTIVITY · following")]
    [InlineData(0, 0, true, 19, "ACTIVITY follow")]
    [InlineData(0, 0, false, 20, "ACTIVITY inspect")]
    [InlineData(0, 40, true, 100, "ACTIVITY · following · 40 below")]
    [InlineData(12, 40, false, 43, "ACTIVITY · inspecting · 12 above · 40 below")]
    [InlineData(12, 40, true, 42, "ACTIVITY · following · 12 above · 40 below")]
    [InlineData(12, 40, false, 42, "ACTIVITY inspect ^12 v40")]
    [InlineData(12, 40, false, 24, "ACTIVITY inspect ^12 v40")]
    [InlineData(12, 40, false, 23, "ACTIVI… inspect ^12 v40")]
    [InlineData(12, 40, true, 10, "A… follow ^12 v40")]
    [InlineData(12, 40, true, 0, "ACTIVITY · following · 12 above · 40 below")]
    [InlineData(12, 40, true, -4, "ACTIVITY · following · 12 above · 40 below")]
    public void Title_NullFilter_KeepsEveryExistingTheoryRow(
        int above, int below, bool following, int columns, string expected) =>
        Assert.Equal(expected, OwnerConsoleActivityTitle.Title(new(above, below), following, columns, filter: null));

    [Fact]
    public void Registry_Filter_IsGlobalAndAloneMayOwnSlash()
    {
        Assert.Empty(OwnerConsoleBindings.Validate(OwnerConsoleBindings.All));
        foreach (var pane in Enum.GetValues<OwnerConsolePane>())
            Assert.Same(OwnerConsoleBindings.Filter, OwnerConsoleBindings.ForKey('/', pane));
        Assert.Same(OwnerConsoleBindings.Filter, OwnerConsoleBindings.ForLine(":filter 2222"));
        var other = OwnerConsoleBindings.Filter with { Command = "other", Usage = "other" };
        Assert.EndsWith(" is reserved for 'filter'.", Assert.Single(OwnerConsoleBindings.Validate([other])));
        Assert.Contains("follow | [/] filter <text> | [l] landings", OwnerConsoleBindings.CommandPrompt);
    }

    private static OwnerConsoleActivityItem Item(int day, string goal)
    {
        var local = new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Unspecified);
        return new(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)),
            "acceptance", "outcome", goal, "result=passed", Phrase: "Search passed");
    }
}
