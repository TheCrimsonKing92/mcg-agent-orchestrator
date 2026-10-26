// Parallel-safe: fixed models; no files, clock or process.
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class OwnerConsoleGateWhereLineTests
{
    private const string FullWhere = "WHERE   mcg-exec-1 3/3 [##########]   mcg-exec-2 2/2 [##########]   local 1/5 [##--------]";

    [Theory]
    [InlineData(120)]
    [InlineData(80)]
    public void RunningLanes_KnownCapacity_RenderOccupancyAndWrapWholeGroups(int width)
    {
        var expected = width == 120 ? new[] { FullWhere } :
            new[] { "WHERE   mcg-exec-1 3/3 [##########]   mcg-exec-2 2/2 [##########]", "        local 1/5 [##--------]" };
        Assert.Equal(expected, WhereLines(Render(Model(), width)));
    }

    [Fact]
    public void CompletedExecutor_KnownCapacity_RendersEmptyOccupancy()
    {
        var gate = Model();
        gate = gate with { Lanes = gate.Lanes.Select(row => row.Location == "mcg-exec-1"
            ? row with { State = OwnerConsoleGateLaneState.Done, Outcome = "passed" } : row).ToArray() };
        Assert.Equal(FullWhere.Replace("mcg-exec-1 3/3 [##########]", "mcg-exec-1 0/3 [----------]"),
            Assert.Single(WhereLines(Render(gate))));
    }

    [Fact]
    public void ExecutorWithoutRows_KnownCapacity_StillRendersZero()
    {
        var gate = Model();
        gate = gate with { Lanes = gate.Lanes.Where(row => row.Location != "mcg-exec-2").ToArray() };
        Assert.Equal(FullWhere.Replace("mcg-exec-2 2/2 [##########]", "mcg-exec-2 0/2 [----------]"),
            Assert.Single(WhereLines(Render(gate))));
    }

    [Fact]
    public void UnknownRemote_KnownCapacity_RendersBetweenExecutorsAndLocal()
    {
        var gate = Model();
        gate = gate with { Lanes = [.. gate.Lanes, Row("unknown", "remote")] };
        Assert.Equal(FullWhere.Replace("   local", "   remote 1 running   local"),
            Assert.Single(WhereLines(Render(gate))));
    }

    [Fact]
    public void ConfirmingAndBaseline_Running_DoNotOccupyExecutorSlots()
    {
        var gate = Model();
        gate = gate with { Lanes = [.. gate.Lanes,
            Row("confirming", "mcg-exec-1") with { IsAttribution = true },
            Row("check [trusted main baseline]", "mcg-exec-1")] };
        Assert.Equal(FullWhere, Assert.Single(WhereLines(Render(gate))));
    }

    [Fact]
    public void LocalOverBudget_RendersActualCountAndClampsBar()
    {
        var gate = Model() with { Lanes = Enumerable.Range(0, 7).Select(i => Row($"local {i}", $"local slot {i}")).ToArray() };
        Assert.Equal("WHERE   mcg-exec-1 0/3 [----------]   mcg-exec-2 0/2 [----------]   local 7/5 [##########]",
            Assert.Single(WhereLines(Render(gate))));
    }

    [Theory]
    [InlineData(false, 80, false)]
    [InlineData(false, 80, true)]
    [InlineData(false, 120, false)]
    [InlineData(false, 120, true)]
    [InlineData(true, 80, false)]
    [InlineData(true, 80, true)]
    [InlineData(true, 120, false)]
    [InlineData(true, 120, true)]
    public void CapacitySnapshot_AllLayouts_ChangesOnlyWhereLines(bool localOnly, int width, bool expanded)
    {
        var gate = Model();
        if (localOnly)
            gate = gate with { Capacity = new([], 5), Lanes = gate.Lanes.Where(row => row.Location.StartsWith("local")).ToArray() };
        var actual = Render(gate, width, expanded);
        var legacy = Render(gate with { Capacity = null }, width, expanded);
        var where = WhereLines(actual);
        var legacyWhere = WhereLines(legacy);
        Assert.NotEmpty(where);
        Assert.Single(legacyWhere);
        Assert.Equal(WithoutWhere(legacy), WithoutWhere(actual));
        Assert.Equal(where.Length - 1, actual.Length - legacy.Length);
        if (localOnly) Assert.Equal("WHERE   local 1/5 [##--------]", Assert.Single(where));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void NoCapacity_PreservesLegacyCompletionLineWithoutWrapping(int width)
    {
        const string legacy = "WHERE   mcg-exec-1 [----------]   mcg-exec-2 [###-------]   local 0 [----------]   local [##########]";
        Assert.Equal(width == 80 ? legacy[..79] + "…" : legacy,
            Assert.Single(WhereLines(Render(Model() with { Capacity = null }, width))));
    }

    [Fact]
    public void LocationGroups_UseConfigurationOrderAndOrdinalUnknownOrder()
    {
        var gate = Model() with
        {
            Capacity = new([new("second", 2), new("first", 1), new("second", 9)], 5),
            Lanes = [Row("z", "z"), Row("a", "a"), Row("slot", "local 2"), Row("exact", "local")]
        };
        Assert.Equal("WHERE   second 0/2 [----------]   first 0/1 [----------]   a 1 running   z 1 running   local 2/5 [####------]",
            Assert.Single(WhereLines(Render(gate))));
    }

    [Theory]
    [InlineData(0, false, "WHERE   local 0/0 [----------]")]
    [InlineData(0, true, "WHERE   local 1/0 [##########]")]
    [InlineData(-1, true, "WHERE   local 1/-1 [##########]")]
    public void NonpositiveCapacity_RendersWithoutDivisionByZero(int capacity, bool running, string expected)
    {
        var gate = Model() with { Capacity = new([], capacity), Lanes = running ? [Row("local", "local")] : [] };
        Assert.Equal(expected, Assert.Single(WhereLines(Render(gate))));
    }

    private static OwnerConsoleGateProgress Model()
    {
        var rows = Enumerable.Range(1, 3).Select(i => Row($"executor one {i}", "mcg-exec-1"))
            .Concat(Enumerable.Range(1, 2).Select(i => Row($"executor two {i}", "mcg-exec-2")))
            .Append(Row("local running", "local slot 0"))
            .Concat(Enumerable.Range(1, 2).Select(i => Row($"local done {i}", "local", OwnerConsoleGateLaneState.Done)))
            .Append(Row("remote done", "mcg-exec-2", OwnerConsoleGateLaneState.Done)).ToArray();
        return new(true, "solo", [new("ca798e7c", "First")], null, TimeSpan.FromMinutes(5), rows, 1d / 3, 60, 3, 9,
            Capacity: new([new("mcg-exec-1", 3), new("mcg-exec-2", 2)], 5));
    }

    private static OwnerConsoleGateLane Row(string name, string location,
        OwnerConsoleGateLaneState state = OwnerConsoleGateLaneState.Running) =>
        new(name, state, state == OwnerConsoleGateLaneState.Done ? "passed" : "unknown", location, TimeSpan.FromSeconds(10), 100, false);
    private static string[] Render(OwnerConsoleGateProgress gate, int width = 120, bool expanded = false) =>
        OwnerConsoleGateLayout.Lines(gate, width, expanded).ToArray();
    private static string[] WhereLines(string[] lines)
    {
        var start = Array.FindIndex(lines, line => line.StartsWith("WHERE   ", StringComparison.Ordinal));
        Assert.True(start >= 0, "WHERE line was not rendered");
        return lines.Skip(start).Take(1).Concat(lines.Skip(start + 1)
            .TakeWhile(line => line.StartsWith("        ", StringComparison.Ordinal))).ToArray();
    }
    private static string[] WithoutWhere(string[] lines)
    {
        var start = Array.FindIndex(lines, line => line.StartsWith("WHERE   ", StringComparison.Ordinal));
        return lines.Take(start).Concat(lines.Skip(start + WhereLines(lines).Length)).ToArray();
    }
}
