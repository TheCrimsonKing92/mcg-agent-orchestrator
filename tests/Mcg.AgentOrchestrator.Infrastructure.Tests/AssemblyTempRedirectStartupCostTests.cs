using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AssemblyTempRedirectStartupCostTests(ITestOutputHelper output)
{
    [Fact]
    public void StartupSweepUsesOneSelectionBatchAndOneBoundaryBatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sharedRoot = Path.Combine(Path.GetTempPath(), $"startup-batch-{Guid.NewGuid():N}");
        var selectedRoot = AssemblyTempRedirect.BuildProcessTempRoot(sharedRoot, Environment.ProcessId);
        var firstPid = 0x1010;
        var secondPid = 0x2020;
        var firstRoot = AssemblyTempRedirect.BuildProcessTempRoot(sharedRoot, firstPid);
        var secondRoot = AssemblyTempRedirect.BuildProcessTempRoot(sharedRoot, secondPid);
        var reads = new List<int[]>();
        var deleted = new List<string>();
        Directory.CreateDirectory(selectedRoot);
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        File.WriteAllText(AssemblyTempRedirect.RootLeasePath(firstRoot), "exited-owner");
        File.WriteAllText(AssemblyTempRedirect.RootLeasePath(secondRoot), "live-owner");
        try
        {
            AssemblyTempRedirect.ReapOrphanedRoots(
                selectedRoot,
                new TempRootStartupTimings(),
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
                    return TempRootDeleteOutcome.Deleted(path, readOnlyAttributesCleared: 0);
                },
                writeReceipt: null);

            Assert.Equal(2, reads.Count);
            Assert.Equal([firstPid, secondPid], reads[0]);
            Assert.Equal([firstPid, secondPid], reads[1]);
            Assert.Equal([firstRoot], deleted, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Fact]
    public void CurrentHostPublishesInitializerTimingReceipt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var receipt = AssemblyTempRedirect.StartupTimingDiagnostic;

        Assert.False(string.IsNullOrWhiteSpace(receipt));
        Assert.StartsWith("assembly-temp-redirect-timing ", receipt, StringComparison.Ordinal);
        Assert.Contains("totalMs=", receipt, StringComparison.Ordinal);
        Assert.Contains("reapSiblingCount=", receipt, StringComparison.Ordinal);
        Assert.Contains("deleteMs=", receipt, StringComparison.Ordinal);
        Assert.Contains("deleteFailed=", receipt, StringComparison.Ordinal);
        output.WriteLine(receipt);
    }

    [Fact]
    public void TimingDiagnosticDistinguishesSkippedLabelAndReapPhases()
    {
        var timings = new TempRootStartupTimings
        {
            TotalElapsedMilliseconds = 7
        };

        var diagnostic = AssemblyTempRedirect.FormatTimingDiagnostic(timings);

        Assert.Equal(
            "assembly-temp-redirect-timing totalMs=7 createMs=0 labelQueryMs=0 labelQueryCount=0 " +
            "labelSetMs=not-run labelSetCompleted=not-run labelSetTimedOut=not-run " +
            "labelSetExitCode=not-run labelSetSucceeded=not-run labelSetFailure=not-run " +
            "probeWriteMs=0 probeCleanupMs=0 reap=not-run reapEnumerateMs=not-run " +
            "reapSiblingCount=not-run reapPidSnapshotMs=not-run reapOrphanSelectMs=not-run " +
            "reapBoundSelectMs=not-run " +
            "deleteAttempted=not-run deleteSucceeded=not-run deleteMs=not-run " +
            "deleteDeleted=not-run deleteAlreadyAbsent=not-run deleteRetainedLiveOwner=not-run " +
            "deleteFailed=not-run " +
            "deleteFailureKinds=not-run deleteFirstFailure=not-run " +
            "deleteReadOnlyCleared=not-run",
            diagnostic);
    }

    [Fact]
    public void BoundedReapRecordsEachDeleteOutcomeClassification()
    {
        var statuses = new[]
        {
            TempRootDeleteStatus.Deleted,
            TempRootDeleteStatus.AlreadyAbsent,
            TempRootDeleteStatus.Failed
        };
        var nextStatus = 0;
        var timings = new TempRootStartupTimings { ReapRan = true };
        var receipts = new List<string>();

        var outcomes = AssemblyTempRedirect.ReapBoundedRoots(
            "shared",
            [Revalidated("p1"), Revalidated("p2"), Revalidated("p3")],
            path =>
            {
                var status = statuses[nextStatus++];
                return new TempRootDeleteOutcome(
                    path,
                    status,
                    status == TempRootDeleteStatus.Failed ? "InjectedFailure" : null);
            },
            _ => new MemoryStream(),
            timings,
            receipts.Add);

        Assert.Equal(statuses, outcomes.Select(outcome => outcome.Status));
        Assert.Equal(3, timings.ReapDeleteAttempted);
        Assert.Equal(1, timings.ReapDeleteDeleted);
        Assert.Equal(1, timings.ReapDeleteAlreadyAbsent);
        Assert.Equal(1, timings.ReapDeleteFailed);
        Assert.Equal(
            timings.ReapDeleteAttempted,
            timings.ReapDeleteDeleted + timings.ReapDeleteAlreadyAbsent + timings.ReapDeleteFailed);
        Assert.Equal(3, receipts.Count);
        Assert.Contains($"actorPid={Environment.ProcessId}", receipts[0], StringComparison.Ordinal);
        Assert.Contains("candidatePid=1", receipts[0], StringComparison.Ordinal);
        Assert.Contains("observedStatus=Exited", receipts[0], StringComparison.Ordinal);
        Assert.Contains("deleteStatus=Deleted", receipts[0], StringComparison.Ordinal);
        Assert.Contains("deleteStatus=AlreadyAbsent", receipts[1], StringComparison.Ordinal);
        Assert.Contains("deleteStatus=Failed", receipts[2], StringComparison.Ordinal);
        Assert.Contains("exceptionType=InjectedFailure", receipts[2], StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedReapRequiresExclusiveOwnerLeaseBeforeDeletingSiblingRoot()
    {
        var sharedRoot = Path.Combine(Path.GetTempPath(), $"mcg-reap-lease-{Guid.NewGuid():N}");
        var siblingRoot = AssemblyTempRedirect.BuildProcessTempRoot(sharedRoot, 0x2a);
        var leasePath = AssemblyTempRedirect.RootLeasePath(siblingRoot);
        Directory.CreateDirectory(siblingRoot);
        File.WriteAllText(Path.Combine(siblingRoot, "repository.marker"), "owned");
        var deleteCalls = 0;
        var retainedTimings = new TempRootStartupTimings { ReapRan = true };
        var retainedReceipts = new List<string>();
        try
        {
            using (var ownerLease = new FileStream(
                       leasePath,
                       FileMode.OpenOrCreate,
                       FileAccess.ReadWrite,
                       FileShare.Read))
            {
                var retained = Assert.Single(AssemblyTempRedirect.ReapBoundedRoots(
                    sharedRoot,
                    [Revalidated(Path.GetFileName(siblingRoot))],
                    path =>
                    {
                        deleteCalls++;
                        return AssemblyTempRedirect.DeleteTree(path);
                    },
                    AssemblyTempRedirect.TryAcquireDeletionLease,
                    retainedTimings,
                    retainedReceipts.Add));

                Assert.Equal(TempRootDeleteStatus.RetainedLiveOwner, retained.Status);
                Assert.Equal(0, deleteCalls);
                Assert.Equal(0, retainedTimings.ReapDeleteAttempted);
                Assert.Equal(1, retainedTimings.ReapDeleteRetainedLiveOwner);
                Assert.Contains(
                    "deleteStatus=RetainedLiveOwner",
                    Assert.Single(retainedReceipts),
                    StringComparison.Ordinal);
                Assert.True(File.Exists(Path.Combine(siblingRoot, "repository.marker")));
            }

            var deleted = Assert.Single(AssemblyTempRedirect.ReapBoundedRoots(
                sharedRoot,
                [Revalidated(Path.GetFileName(siblingRoot))],
                path =>
                {
                    deleteCalls++;
                    return AssemblyTempRedirect.DeleteTree(path);
                },
                AssemblyTempRedirect.TryAcquireDeletionLease,
                new TempRootStartupTimings { ReapRan = true },
                writeReceipt: null));

            Assert.Equal(TempRootDeleteStatus.Deleted, deleted.Status);
            Assert.Equal(1, deleteCalls);
            Assert.False(Directory.Exists(siblingRoot));
            Assert.False(File.Exists(leasePath));
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Fact]
    public void MissingRootIsAlreadyAbsentAndDoesNotIncrementFailureCount()
    {
        var sharedRoot = Path.Combine(Path.GetTempPath(), $"mcg-reap-absent-{Guid.NewGuid():N}");
        var timings = new TempRootStartupTimings { ReapRan = true };

        var outcome = Assert.Single(AssemblyTempRedirect.ReapBoundedRoots(
            sharedRoot,
            [Revalidated("p1")],
            AssemblyTempRedirect.DeleteTree,
            _ => new MemoryStream(),
            timings,
            writeReceipt: null));
        var diagnostic = AssemblyTempRedirect.FormatTimingDiagnostic(timings);

        Assert.Equal(TempRootDeleteStatus.AlreadyAbsent, outcome.Status);
        Assert.Equal(1, timings.ReapDeleteAlreadyAbsent);
        Assert.Equal(0, timings.ReapDeleteFailed);
        Assert.Contains("deleteAlreadyAbsent=1", diagnostic, StringComparison.Ordinal);
        Assert.Contains("deleteFailed=0", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalBackstopDeletesReadOnlyRootAndRecordsSuccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sharedRoot = Path.Combine(Path.GetTempPath(), $"mcg-reap-readonly-{Guid.NewGuid():N}");
        var root = AssemblyTempRedirect.BuildProcessTempRoot(sharedRoot, int.MaxValue);
        var nested = Directory.CreateDirectory(Path.Combine(root, "repository", ".git", "objects")).FullName;
        var readOnlyFile = Path.Combine(nested, "object");
        File.WriteAllText(readOnlyFile, "fixture");
        File.SetAttributes(readOnlyFile, File.GetAttributes(readOnlyFile) | FileAttributes.ReadOnly);
        var timings = new TempRootStartupTimings { ReapRan = true };
        var receipts = new List<string>();

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Directory.Delete(root, recursive: true));

            var outcome = Assert.Single(AssemblyTempRedirect.ReapBoundedRoots(
                sharedRoot,
                [Revalidated(Path.GetFileName(root))],
                AssemblyTempRedirect.DeleteTree,
                _ => new MemoryStream(),
                timings,
                receipts.Add));

            Assert.Equal(TempRootDeleteStatus.Deleted, outcome.Status);
            Assert.False(Directory.Exists(root));
            Assert.Equal(1, timings.ReapDeleteDeleted);
            Assert.Equal(0, timings.ReapDeleteFailed);
            Assert.True(timings.DeleteReadOnlyAttributesCleared > 0);
            var receipt = Assert.Single(receipts);
            Assert.Contains(
                $"readOnlyCleared={outcome.ReadOnlyAttributesCleared}",
                receipt,
                StringComparison.Ordinal);
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }
    }

    [Fact]
    public void ThrowingDeleteSeamSurfacesExceptionTypeAndFailingLeaf()
    {
        var timings = new TempRootStartupTimings { ReapRan = true };

        var outcome = Assert.Single(AssemblyTempRedirect.ReapBoundedRoots(
            "shared",
            [Revalidated("p2")],
            _ => throw new UnauthorizedAccessException("denied"),
            _ => new MemoryStream(),
            timings,
            writeReceipt: null));
        var diagnostic = AssemblyTempRedirect.FormatTimingDiagnostic(timings);

        Assert.Equal(TempRootDeleteStatus.Failed, outcome.Status);
        Assert.Equal(nameof(UnauthorizedAccessException), outcome.ExceptionType);
        Assert.Contains(
            "deleteFailureKinds=UnauthorizedAccessException:1",
            diagnostic,
            StringComparison.Ordinal);
        Assert.Contains(
            "deleteFirstFailure=p2:UnauthorizedAccessException",
            diagnostic,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupHousekeepingCompletesWhenDeleteSeamThrows()
    {
        var sharedRoot = Path.Combine(Path.GetTempPath(), $"mcg-reap-safety-{Guid.NewGuid():N}");
        var selectedRoot = AssemblyTempRedirect.BuildProcessTempRoot(sharedRoot, Environment.ProcessId);
        var abandonedRoot = AssemblyTempRedirect.BuildProcessTempRoot(sharedRoot, int.MaxValue);
        Directory.CreateDirectory(abandonedRoot);
        File.WriteAllText(AssemblyTempRedirect.RootLeasePath(abandonedRoot), "exited-owner");
        var timings = new TempRootStartupTimings();
        var completed = false;
        string? timingReceipt = null;
        var reapReceipts = new List<string>();

        try
        {
            timingReceipt = AssemblyTempRedirect.RunStartupHousekeeping(
                selectedRoot,
                timings,
                System.Diagnostics.Stopwatch.StartNew(),
                _ => throw new UnauthorizedAccessException("denied"),
                reapReceipts.Add);
            completed = true;
        }
        finally
        {
            _ = TempRootJanitor.DeleteTree(sharedRoot);
        }

        Assert.True(completed, "Startup housekeeping did not return after the delete seam threw.");
        Assert.NotNull(timingReceipt);
        Assert.StartsWith("assembly-temp-redirect-timing ", timingReceipt, StringComparison.Ordinal);
        Assert.Contains("deleteFailed=1", timingReceipt, StringComparison.Ordinal);
        var reapReceipt = Assert.Single(reapReceipts);
        Assert.Contains("candidatePid=2147483647", reapReceipt, StringComparison.Ordinal);
        Assert.Contains("observedStatus=Exited", reapReceipt, StringComparison.Ordinal);
        Assert.Contains("deleteStatus=Failed", reapReceipt, StringComparison.Ordinal);
        Assert.Contains("exceptionType=UnauthorizedAccessException", reapReceipt, StringComparison.Ordinal);
    }

    [Fact]
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

        var receipt = AssemblyTempRedirect.FormatReapReceipt(
            actorProcessId: 11,
            candidateProcessId: 42,
            path: "C:\\temp root\\p2a\"",
            observation: observation,
            deleteStatus: "Failed",
            exceptionType: "Injected Failure",
            failurePath: "C:\\temp root\\leaf \"x\"",
            readOnlyAttributesCleared: 3);

        Assert.Equal(
            "assembly-temp-reaper actorPid=11 candidatePid=42 " +
            "path=\"C:\\temp root\\p2a\\\"\" " +
            "observedPid=42 observedStatus=Exited observedName=\"former host\" " +
            "observedStartedAt=\"2026-08-30T12:00:00.0000000+00:00\" " +
            "observedExecutablePath=\"C:\\Program Files\\test\\\"host.exe\" " +
            "deleteStatus=Failed exceptionType=Injected_Failure " +
            "failurePath=\"C:\\temp root\\leaf \\\"x\\\"\" readOnlyCleared=3",
            receipt);
    }

    [Fact]
    public void BoundedReapSelectionDeduplicatesAndKeepsOldestRoots()
    {
        var writes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["p10"] = new DateTime(2026, 8, 21, 3, 0, 0, DateTimeKind.Utc),
            ["p20"] = new DateTime(2026, 8, 21, 2, 0, 0, DateTimeKind.Utc),
            ["p30"] = new DateTime(2026, 8, 21, 1, 0, 0, DateTimeKind.Utc)
        };

        var selected = AssemblyTempRedirect.SelectBoundedReapRoots(
            ["p10", "p20", "p20", "p30"],
            name => writes[name],
            limit: 2);

        Assert.Equal(["p30", "p20"], selected);
        Assert.Equal(2, selected.Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
            CommandLine: "testhost startup-batch-control",
            ProcessInspectionStatus.Available);

    private static RevalidatedTempRoot Revalidated(string name)
    {
        var processId = Convert.ToInt32(name[1..], 16);
        return new RevalidatedTempRoot(name, processId, ExitedProcess(processId));
    }

    [Fact]
    public void TimedOutLabelSetterFallsBackAndReportsTypedDeadlineOutcome()
    {
        var timings = new TempRootStartupTimings();
        var fileSystem = new SuccessfulTempRootFileSystem();
        var labeler = new TimedOutIntegrityLabeler();
        var candidates = new[]
        {
            new TempRootCandidate("preferred", RequiresLowLabel: true),
            new TempRootCandidate("fallback", RequiresLowLabel: false)
        };

        var selection = AssemblyTempRedirect.SelectWritableRoot(
            candidates,
            candidate => AssemblyTempRedirect.TryPrepareRoot(
                candidate,
                isWindows: true,
                fileSystem,
                labeler,
                probeDirectoryName: "probe",
                timings));

        Assert.Equal("fallback", selection.SelectedRoot);
        Assert.Equal(TempRootRejectionReason.Label, Assert.Single(Assert.Single(selection.Rejections).Reasons));
        Assert.True(timings.LabelSetElapsedMilliseconds.HasValue);
        Assert.Equal(120_000L, timings.LabelSetElapsedMilliseconds.GetValueOrDefault());
        var outcome = Assert.IsType<IntegrityLabelSetOutcome>(timings.LabelSetOutcome);
        Assert.False(outcome.Completed);
        Assert.True(outcome.TimedOut);
        Assert.Null(outcome.ExitCode);
        Assert.False(outcome.Succeeded);
        Assert.Equal("timeout", outcome.FailureKind);
    }

    [Fact]
    public void ReturnedFalseLabelSetterFallsBackAndReportsCompletedOutcome()
    {
        var timings = new TempRootStartupTimings();
        var fileSystem = new SuccessfulTempRootFileSystem();
        var candidates = new[]
        {
            new TempRootCandidate("preferred", RequiresLowLabel: true),
            new TempRootCandidate("fallback", RequiresLowLabel: false)
        };

        var selection = AssemblyTempRedirect.SelectWritableRoot(
            candidates,
            candidate => AssemblyTempRedirect.TryPrepareRoot(
                candidate,
                isWindows: true,
                fileSystem,
                new ReturnedFalseIntegrityLabeler(),
                probeDirectoryName: "probe",
                timings));

        Assert.Equal("fallback", selection.SelectedRoot);
        Assert.Equal(TempRootRejectionReason.Label, Assert.Single(Assert.Single(selection.Rejections).Reasons));
        Assert.True(timings.LabelSetElapsedMilliseconds.HasValue);
        Assert.True(timings.LabelSetElapsedMilliseconds.GetValueOrDefault() >= 0);
        var outcome = Assert.IsType<IntegrityLabelSetOutcome>(timings.LabelSetOutcome);
        Assert.True(outcome.Completed);
        Assert.False(outcome.TimedOut);
        Assert.Null(outcome.ExitCode);
        Assert.False(outcome.Succeeded);
        Assert.Equal("returned-false", outcome.FailureKind);
        Assert.True(outcome.ElapsedMilliseconds >= 0);
    }

    [Fact]
    public void CurrentProcessTempRootRetainsLowLabelAndExclusiveWrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Environment.GetEnvironmentVariable("TMP");
        Assert.False(string.IsNullOrWhiteSpace(tempRoot));
        Assert.Equal(tempRoot, Environment.GetEnvironmentVariable("TEMP"), ignoreCase: true);
        var requiredTempRoot = tempRoot!;

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var lowAncestor = string.IsNullOrWhiteSpace(localAppData)
            ? null
            : Path.Combine(localAppData, "Temp", "Low");
        if (lowAncestor is not null && IsUnder(requiredTempRoot, lowAncestor))
        {
            var label = new IcaclsIntegrityLabeler().Query(requiredTempRoot);
            Assert.True(
                label.Exists && label.Low && label.Inheritable,
                $"Expected TMP '{requiredTempRoot}' to carry an inheritable Low integrity label.");
        }
        else
        {
            Assert.Contains(
                Path.Combine(AppContext.BaseDirectory, ".test-tmp"),
                requiredTempRoot,
                StringComparison.OrdinalIgnoreCase);
        }

        var probePath = Path.Combine(requiredTempRoot, $"startup-cost-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using var probe = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            probe.WriteByte(0x2a);
            probe.Flush(flushToDisk: true);
            probe.Position = 0;
            Assert.Equal(0x2a, probe.ReadByte());
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    private static bool IsUnder(string candidate, string ancestor)
    {
        var normalizedCandidate = Path.GetFullPath(candidate);
        var normalizedAncestor = Path.GetFullPath(ancestor)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedAncestor, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SuccessfulTempRootFileSystem : ITempRootFileSystem
    {
        public void CreateDirectory(string path)
        {
        }

        public void CreateProbeFile(string path)
        {
        }

        public void DeleteFile(string path)
        {
        }

        public void DeleteDirectory(string path)
        {
        }
    }

    private sealed class TimedOutIntegrityLabeler :
        IWorkerIntegrityLabeler,
        IWorkerIntegrityLabelerDiagnostics
    {
        public IntegrityLabelSetOutcome? LastSetOutcome { get; private set; }

        public IntegrityLabelState Query(string path) =>
            new(Exists: true, Low: false, Inheritable: false);

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            LastSetOutcome = new IntegrityLabelSetOutcome(
                Completed: false,
                TimedOut: true,
                ExitCode: null,
                Succeeded: false,
                FailureKind: "timeout",
                ElapsedMilliseconds: 120_000);
            return false;
        }
    }

    private sealed class ReturnedFalseIntegrityLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) =>
            new(Exists: true, Low: false, Inheritable: false);

        public bool SetIntegrity(string path, string level, bool recursive) => false;
    }
}
