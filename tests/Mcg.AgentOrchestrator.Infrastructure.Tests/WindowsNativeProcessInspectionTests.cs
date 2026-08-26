using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WindowsNativeProcessInspectionTests
{
    [Xunit.Fact]
    public void Read_UnavailableProcess_RetainsTypedRecord()
    {
        var enumerationCount = 0;
        var readCount = 0;

        var records = WindowsNativeProcessInspection.Read(
            [42],
            () =>
            {
                enumerationCount++;
                return
                [
                    new WindowsNativeProcessInspection.ProcessInspectionSeed(41, 1, "ignored"),
                    new WindowsNativeProcessInspection.ProcessInspectionSeed(42, 1, "worker")
                ];
            },
            seed =>
            {
                readCount++;
                return new ProcessInspectionRecord(
                    seed.ProcessId,
                    seed.ParentProcessId,
                    seed.Name,
                    null,
                    null,
                    null,
                    ProcessInspectionStatus.AccessDenied);
            });

        var record = Assert.Single(records).Value;
        Assert.Equal(1, enumerationCount);
        Assert.Equal(1, readCount);
        Assert.Equal(42, record.ProcessId);
        Assert.Equal(ProcessInspectionStatus.AccessDenied, record.Status);
        Assert.Null(record.CommandLine);
    }

    [Xunit.Fact]
    public void GetMemoryLayout_Wow64Target_Uses32BitOffsets()
    {
        var layout = WindowsNativeProcessInspection.GetMemoryLayout(targetIsWow64: true);

        Assert.Equal(4, layout.PointerSize);
        Assert.Equal(0x10, layout.ProcessParametersOffset);
        Assert.Equal(0x40, layout.CommandLineOffset);
    }

    [Xunit.Fact]
    public void Snapshot_UnavailableProcess_DoesNotBecomeReadableOrAbsent()
    {
        var snapshot = new ProcessCommandLineSnapshot(new Dictionary<int, ProcessInspectionRecord>
        {
            [73] = new(73, 7, "worker", null, null, null, ProcessInspectionStatus.PartialRead)
        });

        Assert.Empty(snapshot.Read([73]));
        Assert.True(snapshot.TryGetRecord(73, out var record));
        Assert.Equal(ProcessInspectionStatus.PartialRead, record.Status);
    }
}
