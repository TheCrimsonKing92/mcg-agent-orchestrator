using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AssemblyTempRedirectTests
{
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
        var fileSystem = new RecordingTempRootFileSystem();
        var labeler = new RecordingIntegrityLabeler(setResult: true);

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
    }

    [Theory]
    [InlineData(false, false, "Label")]
    [InlineData(true, true, "Write")]
    public void WindowsPreparationFailureFallsBackWithTypedReason(
        bool setResult,
        bool failPostLabelWrite,
        string expectedReason)
    {
        var fileSystem = new RecordingTempRootFileSystem(
            failWrites: failPostLabelWrite ? ["preferred"] : []);
        var labeler = new RecordingIntegrityLabeler(setResult);

        var selection = Select(
            [Candidate("preferred", requiresLowLabel: true), Candidate("fallback")],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Equal("fallback", selection.SelectedRoot);
        Assert.Equal(expectedReason, Assert.Single(Assert.Single(selection.Rejections).Reasons).ToString());
        Assert.Equal("preferred", Assert.Single(labeler.SetCalls).Path);
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
            failWrites: ["write-root"],
            failCleanup: ["cleanup-root"]);
        var labeler = new RecordingIntegrityLabeler(setResult: false);

        var selection = Select(
            [
                Candidate("create-root"),
                Candidate("label-root", requiresLowLabel: true),
                Candidate("write-root"),
                Candidate("cleanup-root")
            ],
            isWindows: true,
            fileSystem,
            labeler);

        Assert.Null(selection.SelectedRoot);
        Assert.Equal(
            "assembly-temp-redirect selected=<none> rejected=" +
            "create-root:create|label-root:label|write-root:write|cleanup-root:cleanup",
            AssemblyTempRedirect.FormatDiagnostic(selection));
        Assert.Contains(Path.Combine("cleanup-root", "probe", "probe.tmp"), fileSystem.DeleteFileAttempts);
        Assert.Contains(Path.Combine("cleanup-root", "probe"), fileSystem.DeleteDirectoryAttempts);
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
        IEnumerable<string>? failCleanup = null) : ITempRootFileSystem
    {
        private readonly HashSet<string> directories = [];
        private readonly HashSet<string> files = [];
        private readonly HashSet<string> createFailures = new(failCreates ?? []);
        private readonly HashSet<string> writeFailures = new(failWrites ?? []);
        private readonly HashSet<string> cleanupFailures = new(failCleanup ?? []);

        internal List<string> RootAttempts { get; } = [];

        internal List<string> DeleteFileAttempts { get; } = [];

        internal List<string> DeleteDirectoryAttempts { get; } = [];

        public bool DirectoryExists(string path) => directories.Contains(path);

        public void CreateDirectory(string path)
        {
            if (string.Equals(Path.GetFileName(path), "probe", StringComparison.Ordinal))
            {
                var root = Path.GetDirectoryName(path)!;
                directories.Add(path);
                if (writeFailures.Contains(root))
                {
                    throw new IOException("simulated probe-directory write denial");
                }

                return;
            }

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
            if (cleanupFailures.Contains(root) || files.Any(file => Path.GetDirectoryName(file) == path))
            {
                throw new IOException("simulated probe-directory cleanup denial");
            }

            directories.Remove(path);
        }
    }

    private sealed class RecordingIntegrityLabeler(bool setResult = true) : IWorkerIntegrityLabeler
    {
        private readonly Dictionary<string, IntegrityLabelState> states = [];

        internal List<string> QueryCalls { get; } = [];

        internal List<(string Path, string Level, bool Recursive)> SetCalls { get; } = [];

        public IntegrityLabelState Query(string path)
        {
            QueryCalls.Add(path);
            return states.GetValueOrDefault(
                path,
                new IntegrityLabelState(Exists: true, Low: false, Inheritable: false));
        }

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            SetCalls.Add((path, level, recursive));
            if (setResult)
            {
                states[path] = new IntegrityLabelState(Exists: true, Low: true, Inheritable: true);
            }

            return setResult;
        }
    }
}
