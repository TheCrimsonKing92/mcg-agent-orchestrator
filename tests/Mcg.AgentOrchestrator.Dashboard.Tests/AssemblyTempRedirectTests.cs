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
        var receipts = new List<string>();
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
                path =>
                {
                    deleted.Add(path);
                    return new TempRootJanitorDeleteResult(
                        path,
                        TempRootJanitorDeleteStatus.Deleted,
                        ExceptionType: null,
                        FailurePath: null,
                        ReadOnlyAttributesCleared: 0);
                },
                receipts.Add);

            Xunit.Assert.Equal(2, reads.Count);
            Xunit.Assert.Equal([firstPid, secondPid], reads[0]);
            Xunit.Assert.Equal([firstPid, secondPid], reads[1]);
            Xunit.Assert.Equal([firstRoot], deleted, StringComparer.OrdinalIgnoreCase);
            var receipt = Xunit.Assert.Single(receipts);
            Xunit.Assert.Contains($"actorPid={Environment.ProcessId}", receipt, StringComparison.Ordinal);
            Xunit.Assert.Contains($"candidatePid={firstPid}", receipt, StringComparison.Ordinal);
            Xunit.Assert.Contains("observedStatus=Exited", receipt, StringComparison.Ordinal);
            Xunit.Assert.Contains("deleteStatus=Deleted", receipt, StringComparison.Ordinal);
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Xunit.Fact]
    public void StartupSweepRecordsTypedReceiptWhenDeleteSeamRemovesSharedParent()
    {
        var sharedRoot = Path.Combine(Path.GetTempPath(), $"dashboard-parent-loss-{Guid.NewGuid():N}");
        var selectedRoot = TempRootJanitor.BuildOwnedRootPath(sharedRoot, Environment.ProcessId);
        var abandonedPid = 0x3030;
        var livePid = 0x4040;
        var abandonedRoot = TempRootJanitor.BuildOwnedRootPath(sharedRoot, abandonedPid);
        var liveRoot = TempRootJanitor.BuildOwnedRootPath(sharedRoot, livePid);
        var currentStartedAt = DateTimeOffset.Parse("2026-09-03T12:00:00Z");
        var liveStartedAt = DateTimeOffset.Parse("2026-09-03T12:00:01Z");
        var typedReceipts = new List<(string SharedRoot, TempRootApparatusDestroyedOwner[] Owners)>();
        Directory.CreateDirectory(selectedRoot);
        Directory.CreateDirectory(abandonedRoot);
        Directory.CreateDirectory(liveRoot);
        WriteOwnerLease(selectedRoot, Environment.ProcessId, currentStartedAt);
        WriteOwnerLease(liveRoot, livePid, liveStartedAt);

        try
        {
            AssemblyTempRedirect.ReapOrphanedRoots(
                selectedRoot,
                ids => WindowsNativeProcessInspection.ProcessInspectionResult.Success(
                    ids!.ToDictionary(
                        processId => processId,
                        processId => processId == livePid
                            ? AvailableProcess(processId) with { StartedAt = liveStartedAt }
                            : ExitedProcess(processId))),
                path =>
                {
                    _ = TempRootJanitor.DeleteTree(sharedRoot);
                    return new TempRootJanitorDeleteResult(
                        path,
                        TempRootJanitorDeleteStatus.Deleted,
                        ExceptionType: null,
                        FailurePath: null,
                        ReadOnlyAttributesCleared: 0);
                },
                writeReceipt: null,
                (root, owners) => typedReceipts.Add((root, owners.ToArray())));

            var receipt = Xunit.Assert.Single(typedReceipts);
            Xunit.Assert.Equal(sharedRoot, receipt.SharedRoot, StringComparer.OrdinalIgnoreCase);
            Xunit.Assert.Equal(
                new[] { Environment.ProcessId, livePid }.Order(),
                receipt.Owners.Select(owner => owner.OwnerProcessId).Order());
            var currentOwner = Xunit.Assert.Single(
                receipt.Owners,
                owner => owner.OwnerProcessId == Environment.ProcessId);
            Xunit.Assert.Equal(currentStartedAt, currentOwner.OwnerStartedAt);
            Xunit.Assert.Equal(selectedRoot, currentOwner.OwnedRootPath, StringComparer.OrdinalIgnoreCase);
            var liveOwner = Xunit.Assert.Single(
                receipt.Owners,
                owner => owner.OwnerProcessId == livePid);
            Xunit.Assert.Equal(liveStartedAt, liveOwner.OwnerStartedAt);
            Xunit.Assert.Equal(liveRoot, liveOwner.OwnedRootPath, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Xunit.Fact]
    public void ReapReceiptPinsEveryDiagnosticFieldAndEscaping()
    {
        var observation = new ProcessInspectionRecord(
            ProcessId: 42,
            ParentProcessId: 7,
            Name: "former host",
            ExecutablePath: "C:\\Program Files\\test\"host.exe",
            StartedAt: DateTimeOffset.Parse("2026-08-30T12:00:00Z"),
            CommandLine: null,
            ProcessInspectionStatus.Exited);
        var outcome = new TempRootJanitorDeleteResult(
            Path: "C:\\temp root\\p2a\"",
            Status: TempRootJanitorDeleteStatus.Failed,
            ExceptionType: "Injected Failure",
            FailurePath: "C:\\temp root\\leaf \"x\"",
            ReadOnlyAttributesCleared: 3);

        var receipt = AssemblyTempRedirect.FormatReapReceipt(
            actorProcessId: 11,
            candidateProcessId: 42,
            path: outcome.Path,
            observation: observation,
            outcome: outcome);

        Xunit.Assert.Equal(
            "assembly-temp-reaper actorPid=11 candidatePid=42 " +
            "path=\"C:\\temp root\\p2a\\\"\" " +
            "observedPid=42 observedStatus=Exited observedName=\"former host\" " +
            "observedStartedAt=\"2026-08-30T12:00:00.0000000+00:00\" " +
            "observedExecutablePath=\"C:\\Program Files\\test\\\"host.exe\" " +
            "deleteStatus=Failed exceptionType=Injected_Failure " +
            "failurePath=\"C:\\temp root\\leaf \\\"x\\\"\" readOnlyCleared=3",
            receipt);
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

    private static void WriteOwnerLease(string rootPath, int processId, DateTimeOffset startedAt) =>
        File.WriteAllText(
            AssemblyTempRedirect.RootLeasePath(rootPath),
            $"pid={processId};startedAt={startedAt:O};path=testhost");
}
