using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AssemblyTempRootCleanupHolderDiagnosticsTests
{
    [Fact]
    public async Task FailedRemovalNamesMatchingProcessAndPreservesCleanupFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "cleanup-holder-root");
        var command = $"powershell -Command Get-Content '{root}'";
        var snapshot = Snapshot(
            Process(424242, 31337, "powershell.exe", command),
            Process(535353, 31338, "unrelated-sentinel.exe", "unrelated-holder-sentinel"));

        var exception = await FailedCleanup(root, () => snapshot);

        Assert.StartsWith($"Assembly temp-root cleanup failed for '{root}'", exception.Message);
        Assert.Contains("hresult=0x80070020, attempts=6", exception.Message);
        Assert.Contains($"at '{Path.Combine(root, "held")}'", exception.Message);
        Assert.Contains("pid=424242 parent=31337 image=powershell.exe cmd=" + command, exception.Message);
        Assert.DoesNotContain("535353", exception.Message);
        Assert.DoesNotContain("unrelated-holder-sentinel", exception.Message);
    }

    [Fact]
    public async Task FailedRemovalReportsSnapshotExceptionWithoutReplacingOriginalFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "cleanup-holder-root");
        var exception = await FailedCleanup(root, () => throw new IOException("snapshot denied\r\nsecond line"));

        Assert.StartsWith($"Assembly temp-root cleanup failed for '{root}'", exception.Message);
        Assert.Contains("in use", exception.Message);
        Assert.Contains("process snapshot unavailable: IOException: snapshot denied  second line", exception.Message);
    }

    [Fact]
    public async Task FailedRemovalExplicitlyReportsNoMatchingProcesses()
    {
        var root = Path.Combine(Path.GetTempPath(), "cleanup-holder-root");
        var exception = await FailedCleanup(root, () => ProcessCommandLineSnapshot.Empty);

        Assert.Contains($"no live process command line or executable path contains '{root}'", exception.Message);
    }

    [Fact]
    public async Task FailedRemovalListsAllMatchesIncludingExecutablePathsAndPartialSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cleanup-holder-root");
        var records = new[]
        {
            Process(Environment.ProcessId, 10, "current.exe", root),
            Process(424242, 11, "path-holder.exe", "path-only") with
            {
                ExecutablePath = Path.Combine(root, "worker.exe").ToUpperInvariant().Replace('\\', '/')
            },
            Process(535353, 12, "exited.exe", root) with { Status = ProcessInspectionStatus.Exited }
        };
        var snapshot = new ProcessCommandLineSnapshot(records.ToDictionary(process => process.ProcessId),
            new ProcessInspectionFailure(ProcessInspectionStatus.AccessDenied, 5, "inspect-process"));

        var exception = await FailedCleanup(root, () => snapshot);

        Assert.Contains($"pid={Environment.ProcessId} parent=10 image=current.exe cmd={root}", exception.Message);
        Assert.Contains("pid=424242 parent=11 image=path-holder.exe cmd=path-only", exception.Message);
        Assert.DoesNotContain("image=exited.exe", exception.Message);
        Assert.Contains("process snapshot incomplete: status=AccessDenied, operation=inspect-process, native_error=5", exception.Message);
    }

    private static async Task<InvalidOperationException> FailedCleanup(
        string root, Func<ProcessCommandLineSnapshot> snapshot)
    {
        var fixture = new AssemblyTempRootCleanupFixture(
            () => TempRootDeleteOutcome.Failure(root, "IOException", Path.Combine(root, "held"),
                0, "in use", unchecked((int)0x80070020), 6),
            TextWriter.Null, snapshot);
        return await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DisposeAsync().AsTask());
    }

    private static ProcessInspectionRecord Process(int pid, int parentPid, string image, string command) =>
        new(pid, parentPid, image, null, null, command, ProcessInspectionStatus.Available);

    private static ProcessCommandLineSnapshot Snapshot(params ProcessInspectionRecord[] records) =>
        new(records.ToDictionary(process => process.ProcessId));
}
