using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AssemblyTempRedirectStartupCostTests(ITestOutputHelper output)
{
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
            "reapAgeSelectMs=not-run reapBoundSelectMs=not-run reapOverlap=not-run " +
            "deleteAttempted=not-run deleteSucceeded=not-run deleteMs=not-run",
            diagnostic);
    }

    [Fact]
    public void BoundedReapSelectionUnionsOverlappingRulesAndKeepsOldestRoots()
    {
        var writes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["p10"] = new DateTime(2026, 8, 21, 3, 0, 0, DateTimeKind.Utc),
            ["p20"] = new DateTime(2026, 8, 21, 2, 0, 0, DateTimeKind.Utc),
            ["p30"] = new DateTime(2026, 8, 21, 1, 0, 0, DateTimeKind.Utc)
        };

        var selected = AssemblyTempRedirect.SelectBoundedReapRoots(
            ["p10", "p20"],
            ["p20", "p30"],
            name => writes[name],
            limit: 2);

        Assert.Equal(["p30", "p20"], selected);
        Assert.Equal(2, selected.Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
