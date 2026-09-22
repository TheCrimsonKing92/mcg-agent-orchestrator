public sealed class AssemblyTempRedirectTests
{
    [Fact]
    public void CandidateRootsAreProcessOwnedAndNeverSelectSharedParent()
    {
        var candidates = AssemblyTempRedirect.EnumerateCandidateRoots().ToArray();

        Assert.NotEmpty(candidates);
        Assert.All(
            candidates,
            candidate =>
            {
                Assert.Equal($"p{Environment.ProcessId:x}", Path.GetFileName(candidate.Path));
                Assert.NotEqual("mcg-tests", Path.GetFileName(candidate.Path));
                Assert.NotEqual(".test-tmp", Path.GetFileName(candidate.Path));
            });
        Assert.Contains(candidates, candidate => candidate.Path.Contains("mcg-tests", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, candidate => candidate.Path.Contains(".test-tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SkipsCandidateThatCannotCreateFiles()
    {
        var fileSystem = new RecordingTempRootFileSystem(failWrites: ["existing-but-denied"]);
        var labeler = new RecordingIntegrityLabeler();

        var selection = Select(
            [Candidate("existing-but-denied"), Candidate("writable-fallback")],
            isWindows: false,
            fileSystem,
            labeler);

        Assert.Equal("writable-fallback", selection.SelectedRoot);
        Assert.Equal(["existing-but-denied", "writable-fallback"], fileSystem.RootAttempts);
        Assert.Equal(TempRootRejectionReason.Write, Assert.Single(Assert.Single(selection.Rejections).Reasons));
    }

    [Fact]
    public void WindowsAppliesAndVerifiesLowLabelOnExactCandidateBeforeSelection()
    {
        var events = new List<string>();
        var fileSystem = new RecordingTempRootFileSystem(events: events);
        var labeler = new RecordingIntegrityLabeler(setResult: true, events: events);

        var selection = Select(
            [Candidate("preferred", requiresLowLabel: true), Candidate("fallback")],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Equal("preferred", selection.SelectedRoot);
        Assert.Equal(["preferred", "preferred"], labeler.QueryCalls);
        var setCall = Assert.Single(labeler.SetCalls);
        Assert.Equal(("preferred", AssemblyTempRedirect.LowInheritableLevel, false), setCall);
        Assert.Equal(["preferred"], fileSystem.RootAttempts);
        Assert.Equal(
            [
                "create:preferred",
                "label-query:preferred",
                "label-set:preferred",
                "label-query:preferred",
                "create-probe:preferred",
                "write:preferred",
                "delete-file:preferred",
                "delete-directory:preferred"
            ],
            events);
    }

    [Theory]
    [InlineData(false, true, false, "Label")]
    [InlineData(true, false, false, "Label")]
    [InlineData(true, true, true, "Write")]
    public void WindowsPreparationFailureFallsBackWithTypedReason(
        bool setResult,
        bool verifyAfterSet,
        bool failPostLabelWrite,
        string expectedReason)
    {
        var fileSystem = new RecordingTempRootFileSystem(
            failWrites: failPostLabelWrite ? ["preferred"] : []);
        var labeler = new RecordingIntegrityLabeler(setResult, verifyAfterSet);

        var selection = Select(
            [Candidate("preferred", requiresLowLabel: true), Candidate("fallback")],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Equal("fallback", selection.SelectedRoot);
        Assert.Equal(expectedReason, Assert.Single(Assert.Single(selection.Rejections).Reasons).ToString());
        Assert.Equal("preferred", Assert.Single(labeler.SetCalls).Path);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void WindowsLabelerExceptionFallsBackWithTypedLabelReason(
        bool throwOnQuery,
        bool throwOnSet)
    {
        var fileSystem = new RecordingTempRootFileSystem();
        var labeler = new RecordingIntegrityLabeler(
            throwOnQuery: throwOnQuery,
            throwOnSet: throwOnSet);

        var selection = Select(
            [Candidate("preferred", requiresLowLabel: true), Candidate("fallback")],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Equal("fallback", selection.SelectedRoot);
        Assert.Equal(TempRootRejectionReason.Label, Assert.Single(Assert.Single(selection.Rejections).Reasons));
        Assert.Equal("preferred", Assert.Single(labeler.QueryCalls));
        Assert.Equal(throwOnSet ? ["preferred"] : [], labeler.SetCalls.Select(call => call.Path));
    }

    [Fact]
    public void WindowsAlreadyLowInheritableCandidateSkipsLabelApplication()
    {
        var fileSystem = new RecordingTempRootFileSystem();
        var labeler = new RecordingIntegrityLabeler(alreadyLow: ["preferred"]);

        var selection = Select(
            [Candidate("preferred", requiresLowLabel: true), Candidate("fallback")],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Equal("preferred", selection.SelectedRoot);
        Assert.Equal(["preferred"], labeler.QueryCalls);
        Assert.Empty(labeler.SetCalls);
        Assert.Equal(["preferred"], fileSystem.RootAttempts);
    }

    [Fact]
    public void NonWindowsRetainsDeterministicProbeFallbackWithoutLabelTooling()
    {
        var fileSystem = new RecordingTempRootFileSystem(failWrites: ["preferred"]);
        var labeler = new RecordingIntegrityLabeler(setResult: true);

        var selection = Select(
            [Candidate("preferred", requiresLowLabel: true), Candidate("fallback", requiresLowLabel: true)],
            isWindows: false,
            fileSystem,
            labeler);

        Assert.Equal("fallback", selection.SelectedRoot);
        Assert.Equal(["preferred", "fallback"], fileSystem.RootAttempts);
        Assert.Empty(labeler.QueryCalls);
        Assert.Empty(labeler.SetCalls);
    }

    [Fact]
    public void DiagnosticDistinguishesAllBoundedRejectionReasonsAndCleanupPreventsSelection()
    {
        var fileSystem = new RecordingTempRootFileSystem(
            failCreates: ["create-root"],
            failWrites: ["write-root", "write-cleanup-root"],
            failCleanup: ["cleanup-root", "write-cleanup-root"]);
        var labeler = new RecordingIntegrityLabeler(setResult: false);

        var selection = Select(
            [
                Candidate("create-root"),
                Candidate("label-root", requiresLowLabel: true),
                Candidate("write-root"),
                Candidate("cleanup-root"),
                Candidate("write-cleanup-root")
            ],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Null(selection.SelectedRoot);
        Assert.Equal(
            "assembly-temp-redirect selected=<none> rejected=" +
            "create-root:create|label-root:label|write-root:write|cleanup-root:cleanup|" +
            "write-cleanup-root:write+cleanup",
            AssemblyTempRedirect.FormatDiagnostic(selection));
        Assert.Contains(Path.Combine("cleanup-root", "probe", "probe.tmp"), fileSystem.DeleteFileAttempts);
        Assert.Contains(Path.Combine("cleanup-root", "probe"), fileSystem.DeleteDirectoryAttempts);
    }

    [Fact]
    public void DiagnosticNamesSelectedFallbackAfterPriorRejection()
    {
        var selection = Select(
            [Candidate("rejected-root"), Candidate("selected-root")],
            isWindows: false,
            new RecordingTempRootFileSystem(failWrites: ["rejected-root"]),
            new RecordingIntegrityLabeler());

        Assert.Equal(
            "assembly-temp-redirect selected=selected-root rejected=rejected-root:write",
            AssemblyTempRedirect.FormatDiagnostic(selection));
    }

    private static TempRootSelectionResult Select(
        IEnumerable<TempRootCandidate> candidates,
        bool isWindows,
        RecordingTempRootFileSystem fileSystem,
        RecordingIntegrityLabeler labeler) =>
        AssemblyTempRedirect.SelectWritableRoot(
            candidates,
            candidate => AssemblyTempRedirect.TryPrepareRoot(
                candidate,
                isWindows,
                fileSystem,
                labeler,
                probeDirectoryName: "probe"));

    private static TempRootCandidate Candidate(string path, bool requiresLowLabel = false) =>
        new(path, requiresLowLabel);

    private sealed class RecordingTempRootFileSystem(
        IEnumerable<string>? failCreates = null,
        IEnumerable<string>? failWrites = null,
        IEnumerable<string>? failCleanup = null,
        ICollection<string>? events = null) : ITempRootFileSystem
    {
        private readonly HashSet<string> directories = [];
        private readonly HashSet<string> files = [];
        private readonly HashSet<string> createFailures = new(failCreates ?? []);
        private readonly HashSet<string> writeFailures = new(failWrites ?? []);
        private readonly HashSet<string> cleanupFailures = new(failCleanup ?? []);

        internal List<string> RootAttempts { get; } = [];

        internal List<string> DeleteFileAttempts { get; } = [];

        internal List<string> DeleteDirectoryAttempts { get; } = [];

        public void CreateDirectory(string path)
        {
            if (string.Equals(Path.GetFileName(path), "probe", StringComparison.Ordinal))
            {
                var root = Path.GetDirectoryName(path)!;
                events?.Add($"create-probe:{root}");
                directories.Add(path);
                if (writeFailures.Contains(root))
                {
                    throw new IOException("simulated probe-directory write denial");
                }

                return;
            }

            events?.Add($"create:{path}");
            RootAttempts.Add(path);
            if (createFailures.Contains(path))
            {
                throw new IOException("simulated candidate creation denial");
            }

            directories.Add(path);
        }

        public void CreateProbeFile(string path)
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
            events?.Add($"write:{root}");
            files.Add(path);
            if (writeFailures.Contains(root))
            {
                throw new IOException("simulated probe-file write denial");
            }
        }

        public void DeleteFile(string path)
        {
            DeleteFileAttempts.Add(path);
            var root = Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
            events?.Add($"delete-file:{root}");
            if (cleanupFailures.Contains(root))
            {
                throw new IOException("simulated probe-file cleanup denial");
            }

            files.Remove(path);
        }

        public void DeleteDirectory(string path)
        {
            DeleteDirectoryAttempts.Add(path);
            var root = Path.GetDirectoryName(path)!;
            events?.Add($"delete-directory:{root}");
            if (cleanupFailures.Contains(root) || files.Any(file => Path.GetDirectoryName(file) == path))
            {
                throw new IOException("simulated probe-directory cleanup denial");
            }

            directories.Remove(path);
        }
    }

    private sealed class RecordingIntegrityLabeler(
        bool setResult = true,
        bool verifyAfterSet = true,
        ICollection<string>? events = null,
        IEnumerable<string>? alreadyLow = null,
        bool throwOnQuery = false,
        bool throwOnSet = false) : ITempRootIntegrityLabeler
    {
        private readonly Dictionary<string, TempRootIntegrityLabelState> states =
            (alreadyLow ?? []).ToDictionary(
                path => path,
                _ => new TempRootIntegrityLabelState(Exists: true, Low: true, Inheritable: true));

        internal List<string> QueryCalls { get; } = [];

        internal List<(string Path, string Level, bool Recursive)> SetCalls { get; } = [];

        public TempRootIntegrityLabelState Query(string path)
        {
            events?.Add($"label-query:{path}");
            QueryCalls.Add(path);
            if (throwOnQuery)
            {
                throw new InvalidOperationException("simulated label query failure");
            }

            return states.GetValueOrDefault(
                path,
                new TempRootIntegrityLabelState(Exists: true, Low: false, Inheritable: false));
        }

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            events?.Add($"label-set:{path}");
            SetCalls.Add((path, level, recursive));
            if (throwOnSet)
            {
                throw new InvalidOperationException("simulated label application failure");
            }

            if (setResult && verifyAfterSet)
            {
                states[path] = new TempRootIntegrityLabelState(Exists: true, Low: true, Inheritable: true);
            }

            return setResult;
        }
    }
}
