using Mcg.AgentOrchestrator.Infrastructure;

public sealed class WindowsNativeProcessInspectionTests
{
    [Xunit.Fact]
    public void ProductionShapedPidReuseFixturePrunesOlderStaleBranchesAboveAndBelowWorkerPid()
    {
        const int wrapperPid = 37_252;
        const int workerPid = 22_628;
        const int falsePidAboveWorker = 22_664;
        const int falsePidBelowWorker = 12_000;
        const int realChildPid = 30_000;
        var wrapperStartedAt = DateTimeOffset.Parse("2026-08-30T05:48:32Z");
        WindowsNativeProcessInspection.ProcessInspectionSeed[] seeds =
        [
            new(wrapperPid, 1, "wrapper"),
            new(workerPid, wrapperPid, "worker"),
            new(realChildPid, workerPid, "real-child"),
            new(falsePidAboveWorker, workerPid, "false-above"),
            new(22_665, falsePidAboveWorker, "false-above-child"),
            new(falsePidBelowWorker, workerPid, "false-below"),
            new(11_999, falsePidBelowWorker, "false-below-child")
        ];
        var starts = new Dictionary<int, DateTimeOffset>
        {
            [wrapperPid] = wrapperStartedAt,
            [workerPid] = wrapperStartedAt.AddSeconds(1),
            [realChildPid] = wrapperStartedAt.AddSeconds(2),
            [falsePidAboveWorker] = wrapperStartedAt.AddDays(-3),
            [22_665] = wrapperStartedAt.AddDays(-3).AddSeconds(1),
            [falsePidBelowWorker] = wrapperStartedAt.AddDays(-3),
            [11_999] = wrapperStartedAt.AddDays(-3).AddSeconds(1)
        };

        var descendants = WindowsNativeProcessInspection.ListIdentityBoundDescendantProcessIds(
            wrapperPid,
            () => WindowsNativeProcessInspection.ProcessEnumerationResult.Success(seeds),
            seed => new ProcessInspectionRecord(
                seed.ProcessId,
                seed.ParentProcessId,
                seed.Name,
                Path.Combine("fixture", seed.Name + ".exe"),
                starts[seed.ProcessId],
                seed.Name,
                ProcessInspectionStatus.Available));

        Assert.Equal<int>([workerPid, realChildPid], descendants);
        Assert.DoesNotContain(falsePidAboveWorker, descendants);
        Assert.DoesNotContain(falsePidBelowWorker, descendants);
        Assert.DoesNotContain(22_665, descendants);
        Assert.DoesNotContain(11_999, descendants);
    }

    [Xunit.Fact]
    public void IdentityBoundDescendantsRejectUnreadableEdgeWithoutTreatingItAsOwned()
    {
        WindowsNativeProcessInspection.ProcessInspectionSeed[] seeds =
        [
            new(100, 1, "worker"),
            new(101, 100, "unreadable-child")
        ];

        var descendants = WindowsNativeProcessInspection.ListIdentityBoundDescendantProcessIds(
            100,
            () => WindowsNativeProcessInspection.ProcessEnumerationResult.Success(seeds),
            seed => new ProcessInspectionRecord(
                seed.ProcessId,
                seed.ParentProcessId,
                seed.Name,
                seed.ProcessId == 100 ? @"C:\workers\worker.exe" : null,
                seed.ProcessId == 100 ? DateTimeOffset.Parse("2026-08-30T05:48:33Z") : null,
                null,
                seed.ProcessId == 100 ? ProcessInspectionStatus.Available : ProcessInspectionStatus.AccessDenied));

        Assert.Empty(descendants);
    }

    [Xunit.Fact]
    public void ConservativeRefusalDescendantsRetainUnreadableOrphanButPruneOlderStaleBranch()
    {
        const int exitedWrapperPid = 6_001;
        const int unreadableChildPid = 6_102;
        const int laterGrandchildPid = 6_103;
        const int olderFalseChildPid = 6_200;
        var wrapperStartedAt = DateTimeOffset.Parse("2026-08-30T05:48:32Z");
        WindowsNativeProcessInspection.ProcessInspectionSeed[] seeds =
        [
            // The wrapper is intentionally absent: this is the post-cancellation snapshot.
            new(unreadableChildPid, exitedWrapperPid, "unreadable-child"),
            new(laterGrandchildPid, unreadableChildPid, "later-grandchild"),
            new(olderFalseChildPid, exitedWrapperPid, "older-false-child")
        ];

        var candidates = WindowsNativeProcessInspection.ListConservativeDescendantProcessIdsForRefusal(
            exitedWrapperPid,
            wrapperStartedAt,
            () => WindowsNativeProcessInspection.ProcessEnumerationResult.Success(seeds),
            seed => seed.ProcessId switch
            {
                unreadableChildPid => new ProcessInspectionRecord(
                    seed.ProcessId,
                    seed.ParentProcessId,
                    seed.Name,
                    null,
                    null,
                    null,
                    ProcessInspectionStatus.AccessDenied),
                laterGrandchildPid => new ProcessInspectionRecord(
                    seed.ProcessId,
                    seed.ParentProcessId,
                    seed.Name,
                    Path.Combine("fixture", "later-grandchild.exe"),
                    wrapperStartedAt.AddSeconds(2),
                    seed.Name,
                    ProcessInspectionStatus.Available),
                olderFalseChildPid => new ProcessInspectionRecord(
                    seed.ProcessId,
                    seed.ParentProcessId,
                    seed.Name,
                    Path.Combine("fixture", "older-false-child.exe"),
                    wrapperStartedAt.AddDays(-3),
                    seed.Name,
                    ProcessInspectionStatus.Available),
                _ => throw new InvalidOperationException($"Unexpected fixture pid {seed.ProcessId}.")
            });

        Assert.Equal<int>([unreadableChildPid, laterGrandchildPid], candidates);
        Assert.DoesNotContain(olderFalseChildPid, candidates);
    }

    [Xunit.Fact]
    public void TerminateIfMatches_RecycledIdentityDoesNotTerminateHandle()
    {
        var expected = AvailableRecord(DateTimeOffset.Parse("2026-08-26T12:00:00Z"));
        var calls = new List<string>();
        var terminated = WindowsNativeProcessInspection.TryTerminateIfMatches(
            expected,
            processId =>
            {
                calls.Add($"open:{processId}");
                return new WindowsNativeProcessInspection.ProcessOpenResult(new IntPtr(17), 0);
            },
            handle =>
            {
                calls.Add($"read:{handle}");
                return new WindowsNativeProcessInspection.OpenedProcessReadResult(
                    expected.ParentProcessId,
                    expected.ExecutablePath,
                    expected.StartedAt!.Value.AddSeconds(1),
                    expected.CommandLine,
                    ProcessInspectionStatus.Available);
            },
            handle =>
            {
                calls.Add($"terminate:{handle}");
                return true;
            },
            handle => calls.Add($"close:{handle}"));

        Assert.False(terminated);
        Assert.Equal(["open:17", "read:17", "close:17"], calls);
    }

    [Xunit.Fact]
    public void TerminateIfMatches_ExactIdentityTerminatesSameOpenedHandle()
    {
        var expected = AvailableRecord(DateTimeOffset.Parse("2026-08-26T12:00:00Z"));
        var calls = new List<string>();
        var terminated = WindowsNativeProcessInspection.TryTerminateIfMatches(
            expected,
            processId =>
            {
                calls.Add($"open:{processId}");
                return new WindowsNativeProcessInspection.ProcessOpenResult(new IntPtr(23), 0);
            },
            handle =>
            {
                calls.Add($"read:{handle}");
                return new WindowsNativeProcessInspection.OpenedProcessReadResult(
                    expected.ParentProcessId,
                    expected.ExecutablePath,
                    expected.StartedAt,
                    expected.CommandLine,
                    ProcessInspectionStatus.Available);
            },
            handle =>
            {
                calls.Add($"terminate:{handle}");
                return true;
            },
            handle => calls.Add($"close:{handle}"));

        Assert.True(terminated);
        Assert.Equal(["open:17", "read:23", "terminate:23", "close:23"], calls);
    }

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
                return WindowsNativeProcessInspection.ProcessEnumerationResult.Success(
                [
                    new WindowsNativeProcessInspection.ProcessInspectionSeed(41, 1, "ignored"),
                    new WindowsNativeProcessInspection.ProcessInspectionSeed(42, 1, "worker")
                ]);
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

        var record = Assert.Single(records.Records).Value;
        Assert.Null(records.Failure);
        Assert.Equal(1, enumerationCount);
        Assert.Equal(1, readCount);
        Assert.Equal(42, record.ProcessId);
        Assert.Equal(ProcessInspectionStatus.AccessDenied, record.Status);
        Assert.Null(record.CommandLine);
    }

    [Xunit.Fact]
    public void ReadRequested_AccessDeniedProcess_RetainsEnumeratedParent()
    {
        var records = WindowsNativeProcessInspection.ReadRequested(
            [42],
            () => WindowsNativeProcessInspection.ProcessEnumerationResult.Success(
            [
                new WindowsNativeProcessInspection.ProcessInspectionSeed(42, 7, "worker")
            ]),
            seed => new ProcessInspectionRecord(
                seed.ProcessId,
                seed.ParentProcessId,
                seed.Name,
                null,
                null,
                null,
                ProcessInspectionStatus.AccessDenied));

        var record = Assert.Single(records.Records).Value;
        Assert.Equal(7, record.ParentProcessId);
        Assert.Equal(ProcessInspectionStatus.AccessDenied, record.Status);
    }

    [Xunit.Fact]
    public void ReadParentProcessId_EnumerationFailure_RemainsTypedUnavailable()
    {
        var failure = new ProcessInspectionFailure(
            ProcessInspectionStatus.NativeFailure,
            24,
            "CreateToolhelp32Snapshot");

        var result = WindowsNativeProcessInspection.ReadParentProcessId(
            42,
            () => WindowsNativeProcessInspection.ProcessEnumerationResult.Failed(failure));

        Assert.Null(result.ParentProcessId);
        Assert.Equal(ProcessInspectionStatus.NativeFailure, result.Status);
        Assert.Same(failure, result.Failure);
    }

    [Xunit.Fact]
    public void ReadParentProcessId_UsesToolhelpParentWithoutOpeningTarget()
    {
        var result = WindowsNativeProcessInspection.ReadParentProcessId(
            42,
            () => WindowsNativeProcessInspection.ProcessEnumerationResult.Success(
            [
                new WindowsNativeProcessInspection.ProcessInspectionSeed(42, 7, "worker")
            ]));

        Assert.Equal(7, result.ParentProcessId);
        Assert.Equal(ProcessInspectionStatus.Available, result.Status);
        Assert.Null(result.Failure);
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

    [Xunit.Fact]
    public void ReadOne_OpenedHandle_BindsIdentityAndCommand()
    {
        var calls = new List<string>();
        var handle = new IntPtr(17);
        var startedAt = DateTimeOffset.Parse("2026-08-26T12:00:00Z");

        var record = WindowsNativeProcessInspection.ReadOne(
            new WindowsNativeProcessInspection.ProcessInspectionSeed(42, 3, "worker"),
            processId =>
            {
                calls.Add($"open:{processId}");
                return new WindowsNativeProcessInspection.ProcessOpenResult(handle, 0);
            },
            openedHandle =>
            {
                calls.Add($"read:{openedHandle}");
                return new WindowsNativeProcessInspection.OpenedProcessReadResult(
                    7,
                    @"C:\workers\worker.exe",
                    startedAt,
                    "worker.exe --run",
                    ProcessInspectionStatus.Available);
            },
            openedHandle => calls.Add($"close:{openedHandle}"));

        Assert.Equal(["open:42", "read:17", "close:17"], calls);
        Assert.Equal(7, record.ParentProcessId);
        Assert.Equal(startedAt, record.StartedAt);
        Assert.Equal(@"C:\workers\worker.exe", record.ExecutablePath);
        Assert.Equal("worker.exe --run", record.CommandLine);
        Assert.Equal(ProcessInspectionStatus.Available, record.Status);
    }

    [Xunit.Fact]
    public void ReadOne_SeedNameChanged_ReturnsDeadOrRecycled()
    {
        var record = WindowsNativeProcessInspection.ReadOne(
            new WindowsNativeProcessInspection.ProcessInspectionSeed(42, 3, "worker"),
            _ => new WindowsNativeProcessInspection.ProcessOpenResult(new IntPtr(17), 0),
            _ => new WindowsNativeProcessInspection.OpenedProcessReadResult(
                7,
                @"C:\workers\replacement.exe",
                DateTimeOffset.Parse("2026-08-26T12:00:00Z"),
                "replacement.exe --run",
                ProcessInspectionStatus.Available),
            _ => { });

        Assert.Equal(ProcessInspectionStatus.DeadOrRecycled, record.Status);
        Assert.Null(record.CommandLine);
    }

    [Xunit.Fact]
    public void ReadOne_ExitDuringRead_ClosesHandleAndReturnsExited()
    {
        var closed = false;
        var record = WindowsNativeProcessInspection.ReadOne(
            new WindowsNativeProcessInspection.ProcessInspectionSeed(42, 3, "worker"),
            _ => new WindowsNativeProcessInspection.ProcessOpenResult(new IntPtr(17), 0),
            _ => new WindowsNativeProcessInspection.OpenedProcessReadResult(
                7,
                @"C:\workers\worker.exe",
                null,
                null,
                ProcessInspectionStatus.Exited),
            _ => closed = true);

        Assert.True(closed);
        Assert.Equal(ProcessInspectionStatus.Exited, record.Status);
        Assert.Null(record.CommandLine);
    }

    [Xunit.Fact]
    public void ReadByNames_Candidates_EnumeratesOnce()
    {
        var enumerations = 0;
        var reads = 0;
        var records = WindowsNativeProcessInspection.ReadByNames(
            new HashSet<string>(["dotnet", "testhost"], StringComparer.OrdinalIgnoreCase),
            () =>
            {
                enumerations++;
                return WindowsNativeProcessInspection.ProcessEnumerationResult.Success(
                [
                    new WindowsNativeProcessInspection.ProcessInspectionSeed(1, 0, "dotnet"),
                    new WindowsNativeProcessInspection.ProcessInspectionSeed(2, 0, "unrelated"),
                    new WindowsNativeProcessInspection.ProcessInspectionSeed(3, 0, "testhost")
                ]);
            },
            seed =>
            {
                reads++;
                return new ProcessInspectionRecord(
                    seed.ProcessId,
                    seed.ParentProcessId,
                    seed.Name,
                    null,
                    null,
                    seed.Name,
                    ProcessInspectionStatus.Available);
            });

        Assert.Equal(1, enumerations);
        Assert.Equal(2, reads);
        Assert.Null(records.Failure);
        Assert.Equal([1, 3], records.Records.Keys.Order().ToArray());
    }

    [Xunit.Fact]
    public void EnumerateProcesses_CreateSnapshotFailure_ReturnsTypedNativeFailure()
    {
        var readCalled = false;
        var closeCalled = false;

        var result = WindowsNativeProcessInspection.EnumerateProcesses(
            () => new IntPtr(-1),
            _ =>
            {
                readCalled = true;
                return WindowsNativeProcessInspection.ProcessEnumerationResult.Success([]);
            },
            _ => closeCalled = true,
            () => 24);

        Assert.Empty(result.Processes);
        var failure = Assert.IsType<ProcessInspectionFailure>(result.Failure);
        Assert.Equal(ProcessInspectionStatus.NativeFailure, failure.Status);
        Assert.Equal(24, failure.NativeError);
        Assert.Equal("CreateToolhelp32Snapshot", failure.Operation);
        Assert.False(readCalled);
        Assert.False(closeCalled);
    }

    [Xunit.Fact]
    public void EnumerateProcesses_SnapshotReadFailure_ClosesHandleAndRetainsCause()
    {
        var closedHandle = IntPtr.Zero;
        var expectedFailure = new ProcessInspectionFailure(
            ProcessInspectionStatus.NativeFailure,
            299,
            "Process32First");

        var result = WindowsNativeProcessInspection.EnumerateProcesses(
            () => new IntPtr(17),
            _ => WindowsNativeProcessInspection.ProcessEnumerationResult.Failed(expectedFailure),
            handle => closedHandle = handle,
            () => 0);

        Assert.Empty(result.Processes);
        Assert.Same(expectedFailure, result.Failure);
        Assert.Equal(new IntPtr(17), closedHandle);
    }

    [Xunit.Fact]
    public void ReadProcessSnapshot_NextFailure_ReturnsTypedNativeFailure()
    {
        var first = new WindowsNativeProcessInspection.ProcessInspectionSeed(17, 1, "dotnet");

        var result = WindowsNativeProcessInspection.ReadProcessSnapshot(
            first,
            () => (null, 5));

        Assert.Empty(result.Processes);
        var failure = Assert.IsType<ProcessInspectionFailure>(result.Failure);
        Assert.Equal(ProcessInspectionStatus.NativeFailure, failure.Status);
        Assert.Equal(5, failure.NativeError);
        Assert.Equal("Process32Next", failure.Operation);
    }

    [Xunit.Fact]
    public void ReadProcessSnapshot_NoMoreFiles_ReturnsCompleteSnapshot()
    {
        var first = new WindowsNativeProcessInspection.ProcessInspectionSeed(17, 1, "dotnet");

        var result = WindowsNativeProcessInspection.ReadProcessSnapshot(
            first,
            () => (null, 18));

        Assert.Null(result.Failure);
        var process = Assert.Single(result.Processes);
        Assert.Same(first, process);
    }

    [Xunit.Fact]
    public void ReadByNames_EnumerationFailure_DoesNotBecomeEmptySuccess()
    {
        var readCount = 0;
        var expectedFailure = new ProcessInspectionFailure(
            ProcessInspectionStatus.NativeFailure,
            24,
            "CreateToolhelp32Snapshot");

        var result = WindowsNativeProcessInspection.ReadByNames(
            new HashSet<string>(["dotnet"], StringComparer.OrdinalIgnoreCase),
            () => WindowsNativeProcessInspection.ProcessEnumerationResult.Failed(expectedFailure),
            _ =>
            {
                readCount++;
                throw new InvalidOperationException("Enumeration failure must stop per-process reads.");
            });

        Assert.Empty(result.Records);
        Assert.Same(expectedFailure, result.Failure);
        Assert.Equal(0, readCount);
    }

    private static ProcessInspectionRecord AvailableRecord(DateTimeOffset startedAt) =>
        new(
            17,
            1,
            "MSBuild",
            @"C:\Program Files\dotnet\sdk\MSBuild.exe",
            startedAt,
            @"MSBuild.exe C:\repo\project.csproj",
            ProcessInspectionStatus.Available);
}
