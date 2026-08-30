using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AssemblyTempRedirectTests
{
    [Xunit.Fact]
    public void StartupSweepUsesOneSelectionBatchAndOneBoundaryBatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sharedRoot = Path.Combine(Path.GetTempPath(), $"dashboard-startup-batch-{Guid.NewGuid():N}");
        var selectedRoot = TempRootJanitor.BuildOwnedRootPath(sharedRoot, Environment.ProcessId);
        var firstPid = 0x3030;
        var secondPid = 0x4040;
        var firstRoot = TempRootJanitor.BuildOwnedRootPath(sharedRoot, firstPid);
        var secondRoot = TempRootJanitor.BuildOwnedRootPath(sharedRoot, secondPid);
        var reads = new List<int[]>();
        var deleted = new List<string>();
        Directory.CreateDirectory(selectedRoot);
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        try
        {
            AssemblyTempRedirect.ReapOrphanedRoots(
                selectedRoot,
                ids =>
                {
                    var requested = ids!.Order().ToArray();
                    reads.Add(requested);
                    return WindowsNativeProcessInspection.ProcessInspectionResult.Success(
                        requested.ToDictionary(
                            processId => processId,
                            processId => reads.Count == 1 || processId == firstPid
                                ? ExitedProcess(processId)
                                : AvailableProcess(processId)));
                },
                deleted.Add);

            Xunit.Assert.Equal(2, reads.Count);
            Xunit.Assert.Equal([firstPid, secondPid], reads[0]);
            Xunit.Assert.Equal([firstPid, secondPid], reads[1]);
            Xunit.Assert.Equal([firstRoot], deleted, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    private static ProcessInspectionRecord ExitedProcess(int processId) =>
        new(
            processId,
            ParentProcessId: 0,
            Name: string.Empty,
            ExecutablePath: null,
            StartedAt: null,
            CommandLine: null,
            ProcessInspectionStatus.Exited);

    private static ProcessInspectionRecord AvailableProcess(int processId) =>
        new(
            processId,
            ParentProcessId: 100,
            Name: "testhost",
            ExecutablePath: @"C:\host\testhost.exe",
            StartedAt: DateTimeOffset.Parse("2026-08-30T12:00:00Z"),
            CommandLine: "testhost dashboard-startup-batch-control",
            ProcessInspectionStatus.Available);
}
