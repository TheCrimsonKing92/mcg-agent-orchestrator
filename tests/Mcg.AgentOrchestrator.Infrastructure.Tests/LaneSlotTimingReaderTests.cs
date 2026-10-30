using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its timing files and never changes the process environment.
public sealed class LaneSlotTimingReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "lane-slot-reader-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Read_InvalidLines_RetainsValidRowsAndReportsOneSkippedCount()
    {
        var trx = Write("mixed", Row("Example.Tests.Method(1)", 10.25, 20),
            "{", "null", Row("Example.Tests.Method", -1, 3), Row("", 1, 2),
            Row("Example.Tests.Method", 1, -2),
            "{\"test_name\":\"Example.Tests.Method\",\"wait_ms\":\"bad\",\"held_ms\":2,\"slot_count\":2,\"process_id\":1}",
            Row("Example.Tests.Method(2)", 5.5, 30));

        var result = LaneSlotTimingReader.Read([trx]);

        Assert.Equal(15.75, result.Totals!.WaitMs);
        Assert.Equal(50, result.Totals.HeldMs);
        Assert.Equal(15.75, result.WaitFor(trx, "Example.Tests.Method"));
        Assert.Equal(2, result.Totals.SlotCount);
        Assert.Contains("6 invalid lines", result.UnavailableReason);
        Assert.Contains(LaneSlotTimingReader.PathFor(trx), result.UnavailableReason);
        Assert.Single(result.UnavailableReason!.Split("; "));
    }

    [Fact]
    public void Read_InconsistentCounts_UsesLargestAndReportsDistinctValues()
    {
        var trx = Write("counts", Row("Example.Tests.Method", 1, 2, 4),
            Row("Example.Tests.Method", 3, 4, 2), Row("Example.Tests.Method", 5, 6, 4));

        var result = LaneSlotTimingReader.Read([trx]);

        Assert.Equal(new LaneSlotTimingReader.LaneSlotTotals(9, 12, 4), result.Totals);
        Assert.Equal("slot-timing-inconsistent-counts:2,4", result.UnavailableReason);
    }

    [Fact]
    public void Read_UnreadableFile_DiscardsItsRowsAndPreservesOtherFiles()
    {
        var lockedTrx = Write("locked", Row("Example.Tests.Locked", 1, 2));
        var goodTrx = Write("good", Row("Example.Tests.Good", 3, 4));
        using var locked = new FileStream(LaneSlotTimingReader.PathFor(lockedTrx), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);

        var result = LaneSlotTimingReader.Read([lockedTrx, goodTrx]);

        Assert.Equal(new LaneSlotTimingReader.LaneSlotTotals(3, 4, 2), result.Totals);
        Assert.Equal(0, result.WaitFor(lockedTrx, "Example.Tests.Locked"));
        Assert.Contains("slot-timing-unreadable:", result.UnavailableReason);
        Assert.Contains(LaneSlotTimingReader.PathFor(lockedTrx), result.UnavailableReason);
        Assert.Contains("IOException", result.UnavailableReason);
    }

    [Fact]
    public void Read_MissingEmptyAndInvalidFiles_ExposeNoTotals()
    {
        Directory.CreateDirectory(root);
        var missing = Path.Combine(root, "missing.trx");
        var absent = LaneSlotTimingReader.Read([missing]);
        Assert.Null(absent.Totals);
        Assert.Contains(LaneSlotTimingReader.PathFor(missing), absent.UnavailableReason);
        Assert.Contains("FileNotFoundException", absent.UnavailableReason);

        var empty = LaneSlotTimingReader.Read([Write("empty")]);
        Assert.Null(empty.Totals);
        Assert.Null(empty.UnavailableReason);

        var invalid = LaneSlotTimingReader.Read([Write("invalid", "{}")]);
        Assert.Null(invalid.Totals);
        Assert.Contains("1 invalid lines", invalid.UnavailableReason);
    }

    [Fact]
    public void Read_DuplicatePaths_KeepsRepeatedUsesAndCountsUnknownIdentity()
    {
        var trx = Write("duplicate", Row("Example.Tests.Method", 2, 3),
            Row("Example.Tests.Method", 2, 3), Row("<unknown>", 4, 5));

        var result = LaneSlotTimingReader.Read([trx, trx]);

        Assert.Equal(new LaneSlotTimingReader.LaneSlotTotals(8, 11, 2), result.Totals);
        Assert.Equal(4, result.WaitFor(trx, "Example.Tests.Method"));
        Assert.Equal(0, result.WaitFor(trx, "Example.Tests.Unmatched"));
        Assert.Single(result.WaitsByFile);
        Assert.Null(result.UnavailableReason);
        Assert.Equal(0, LaneSlotTimingReader.Subtract(3, 4));
    }

    [Fact]
    public void Read_ValidZeroValues_StillExposesMeasuredCapacity()
    {
        var result = LaneSlotTimingReader.Read([Write("zero", Row("Example.Tests.Method", 0, 0))]);
        Assert.Equal(new LaneSlotTimingReader.LaneSlotTotals(0, 0, 2), result.Totals);
        Assert.Null(result.UnavailableReason);
    }

    private string Write(string name, params string[] rows)
    {
        Directory.CreateDirectory(root);
        var trx = Path.Combine(root, name + ".trx");
        File.WriteAllLines(LaneSlotTimingReader.PathFor(trx), rows);
        return trx;
    }

    private static string Row(string name, double wait, double held, int count = 2) => JsonSerializer.Serialize(new
    {
        test_name = name, wait_ms = wait, held_ms = held, slot_count = count, process_id = Environment.ProcessId
    });

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
