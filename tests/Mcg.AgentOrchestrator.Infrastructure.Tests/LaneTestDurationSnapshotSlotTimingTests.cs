using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its TRX and sidecar directory.
public sealed class LaneTestDurationSnapshotSlotTimingTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("snapshot-slot-").FullName;

    [Fact]
    public void Read_ThreeTests_SubtractsWaitFloorsAndPreservesUnmatched()
    {
        var trx = WriteTrx();
        File.WriteAllLines(LaneSlotTimingReader.PathFor(trx),
        [
            """{"test_name":"Example.Tests.Queued","wait_ms":1250.5,"held_ms":2000.25,"slot_count":2,"process_id":1}""",
            """{"test_name":"Example.Tests.Floored","wait_ms":4000,"held_ms":5.5,"slot_count":2,"process_id":1}"""
        ]);
        var snapshot = LaneTestDurationSnapshot.Read([trx]);

        Assert.Null(snapshot.UnavailableReason);
        Assert.Null(snapshot.SlotTimingUnavailableReason);
        Assert.Equal(2749.5, snapshot.Tests["Example.Tests.Queued"]);
        // The existing snapshot retains only durations >= 1000 ms. A zero result must
        // therefore disappear, rather than preserving its original 3000 ms TRX value.
        Assert.False(snapshot.Tests.ContainsKey("Example.Tests.Floored"));
        Assert.Equal(0, LaneSlotTimingReader.Subtract(3000, 4000));
        Assert.Equal(5000, snapshot.Tests["Example.Tests.Unmatched"]);
        Assert.Equal(2, snapshot.Tests.Count);
        Assert.Equal(new LaneSlotTimingReader.LaneSlotTotals(5250.5, 2005.75, 2), snapshot.SlotTiming);

        File.Delete(LaneSlotTimingReader.PathFor(trx));
        var control = LaneTestDurationSnapshot.Read([trx]);
        Assert.Equal(4000, control.Tests["Example.Tests.Queued"]);
        Assert.Equal(3000, control.Tests["Example.Tests.Floored"]);
        Assert.Equal(5000, control.Tests["Example.Tests.Unmatched"]);
        Assert.Null(control.SlotTiming);
    }

    [Fact]
    public void Read_TheoryUses_SumsWaitBeforeCorrectingAggregatedDuration()
    {
        var trx = Path.Combine(root, "theory.trx");
        File.WriteAllText(trx, """
            <TestRun><Results>
              <UnitTestResult testName="Example.Tests.Theory(1)" outcome="Passed" duration="00:00:03" />
              <UnitTestResult testName="Example.Tests.Theory(2)" outcome="Passed" duration="00:00:04" />
            </Results></TestRun>
            """);
        File.WriteAllLines(LaneSlotTimingReader.PathFor(trx),
        [
            """{"test_name":"Example.Tests.Theory(1)","wait_ms":1000,"held_ms":3,"slot_count":2,"process_id":1}""",
            """{"test_name":"Example.Tests.Theory(2)","wait_ms":2000,"held_ms":4,"slot_count":2,"process_id":1}"""
        ]);
        var snapshot = LaneTestDurationSnapshot.Read([trx, trx]);
        Assert.Equal(4000, Assert.Single(snapshot.Tests).Value);
        Assert.Equal("Example.Tests.Theory", Assert.Single(snapshot.Tests).Key);
        Assert.Equal(new LaneSlotTimingReader.LaneSlotTotals(3000, 7, 2), snapshot.SlotTiming);
    }

    private string WriteTrx()
    {
        var path = Path.Combine(root, "lane.trx");
        File.WriteAllText(path, """
            <TestRun><Results>
              <UnitTestResult testName="Example.Tests.Queued" outcome="Passed" duration="00:00:04" />
              <UnitTestResult testName="Example.Tests.Floored" outcome="Passed" duration="00:00:03" />
              <UnitTestResult testName="Example.Tests.Unmatched" outcome="Passed" duration="00:00:05" />
            </Results></TestRun>
            """);
        return path;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
