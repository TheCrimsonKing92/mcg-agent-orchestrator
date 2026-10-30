// Parallel-safe: rendering immutable in-memory snapshots without shared state or I/O.
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleGateMemberLinesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cohort_Width80_ShowsBothTitleStartsOnSeparateLines(bool expanded)
    {
        var gate = Gate("cohort",
        [
            new("5d0d9fe7aabbccddeeff001122334455", "Convert five small ConsoleViews partials into named text view types (console taming slice 14, batch two)"),
            new("81b12e56aabbccddeeff001122334455", "Resolve gate test obligations from the pass a disjoint main advance carried forward")
        ]);

        var lines = Render(gate, 80, expanded);
        var members = MemberBlock(lines);

        Assert.Collection(members,
            line =>
            {
                Assert.StartsWith("  5d0d9fe7  ", line);
                Assert.Contains("Convert five small ConsoleViews", line);
            },
            line =>
            {
                Assert.StartsWith("  81b12e56  ", line);
                Assert.Contains("Resolve gate test obligations", line);
            });
        Assert.All(members, line =>
        {
            Assert.Equal(80, line.Length);
            Assert.EndsWith("…", line);
            Assert.DoesNotContain(" + ", line);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Train_FourMembers_PreservesInputOrderWithoutDroppingAny(bool expanded)
    {
        var gate = Gate("train",
        [
            new("dddddddd111111111111111111111111", "First member"),
            new("aaaaaaaa222222222222222222222222", "Second member"),
            new("cccccccc333333333333333333333333", "Third member"),
            new("bbbbbbbb444444444444444444444444", "Fourth member")
        ]);

        var lines = Render(gate, 80, expanded);

        Assert.Equal(new[]
        {
            "  dddddddd  First member",
            "  aaaaaaaa  Second member",
            "  cccccccc  Third member",
            "  bbbbbbbb  Fourth member"
        }, MemberBlock(lines));
        Assert.StartsWith(" x ", lines[5]);
    }

    [Theory]
    [InlineData(120, "11111111 Readable title")]
    [InlineData(20, "11111111 Readable t…")]
    public void Solo_NormalAndNarrowWidths_KeepOneFittedMemberLine(int width, string expected)
    {
        var lines = Render(Gate("solo", [new("11111111", "Readable title")]), width);

        Assert.Equal(expected, Assert.Single(MemberBlock(lines)));
    }

    [Theory]
    [InlineData("11111111aabbccddeeff001122334455", "Readable title",
        "11111111aabbccddeeff001122334455 Readable title")]
    [InlineData("11111111", "", "11111111")]
    public void Solo_FullIdOrEmptyTitle_PreservesOriginalContent(
        string id, string title, string expected)
    {
        var lines = Render(Gate("solo", [new(id, title)]), 120);

        Assert.Equal(expected, Assert.Single(MemberBlock(lines)));
    }

    [Theory]
    [InlineData("cohort", 2, "ALSO ON HOST  gate cohort aaaaaaaa+bbbbbbbb 7m")]
    [InlineData("train", 4, "ALSO ON HOST  gate train aaaaaaaa+bbbbbbbb+cccccccc+1 7m")]
    public void OtherGate_CohortOrTrain_KeepsExistingHostSummary(
        string kind, int count, string expected)
    {
        OwnerConsoleGateMember[] otherMembers =
        [
            new("aaaaaaaa111111111111111111111111", "Other first title"),
            new("bbbbbbbb222222222222222222222222", "Other second title"),
            new("cccccccc333333333333333333333333", "Other third title"),
            new("dddddddd444444444444444444444444", "Other fourth title")
        ];
        var other = Gate(kind, otherMembers.Take(count).ToArray()) with
        {
            Elapsed = TimeSpan.FromMinutes(7)
        };
        var gate = Gate("cohort", [new("11111111", "First"), new("22222222", "Second")]) with
        {
            OtherLiveGates = [other]
        };

        var lines = Render(gate, 120);

        Assert.Equal(2, MemberBlock(lines).Length);
        Assert.Equal(expected, Assert.Single(lines.Where(line => line.StartsWith("ALSO ON HOST"))));
        Assert.Equal(expected, lines[^1]);
    }

    [Fact]
    public void MultiMember_ShortIdEmptyTitleAndControlText_RenderSafely()
    {
        var lines = Render(Gate("cohort", [new("xy", ""), new("1234567890", "café\nnext")]), 80);

        Assert.Equal(new[] { "  xy", "  12345678  caf??next" }, MemberBlock(lines));
    }

    [Fact]
    public void EmptyGate_KeepsTheExistingBlankMemberLine()
    {
        var lines = Render(Gate("cohort", []), 80);

        Assert.Equal("", Assert.Single(MemberBlock(lines)));
    }

    private static OwnerConsoleGateProgress Gate(string kind, IReadOnlyList<OwnerConsoleGateMember> members) =>
        new(true, kind, members, DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(3), [], 0, 0, 0, 0);

    private static string[] Render(OwnerConsoleGateProgress gate, int width, bool expanded = false)
    {
        var lines = OwnerConsoleGateLayout.Lines(gate, width, expanded).ToArray();
        Assert.All(lines, line =>
        {
            Assert.True(line.Length <= width, line);
            Assert.All(line, c => Assert.True(c is >= ' ' and <= '~' || c == '…', line));
        });
        return lines;
    }

    private static string[] MemberBlock(string[] lines)
    {
        Assert.StartsWith("GATE ", lines[0]);
        var countsIndex = Array.FindIndex(lines, line => line.StartsWith(" x "));
        Assert.True(countsIndex > 1, "Expected a member block between the gate header and lane counts.");
        return lines[1..countsIndex];
    }
}
