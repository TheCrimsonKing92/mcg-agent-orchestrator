using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AssemblyTempRootCleanupHolderReportTests
{
    [Fact]
    public async Task FailedCleanup_WritesHolderBeforeThrow()
    {
        var root = Root();
        using var writer = new StringWriter();
        var fixture = Fixture(root, writer);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DisposeAsync().AsTask());

        var line = Assert.Single(writer.ToString().Split(Environment.NewLine)
            .Where(value => value.StartsWith("assembly-temp-cleanup-holder pid=424242 ", StringComparison.Ordinal)));
        Assert.Contains("match=cmd", line);
        Assert.Contains("parent_pid=31337 parent_live=false", line);
        Assert.StartsWith("Assembly temp-root cleanup failed for", exception.Message);
        Assert.Contains("pid=424242", exception.Message);
    }

    [Fact]
    public async Task FailedCleanup_HolderWriterThrows_PreservesCleanupException()
    {
        var fixture = Fixture(Root(), new HolderThrowingWriter());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DisposeAsync().AsTask());
        Assert.StartsWith("Assembly temp-root cleanup failed for", exception.Message);
        Assert.Contains("pid=424242", exception.Message);
        Assert.Contains("hresult=0x80070020, attempts=6", exception.Message);
    }

    [Fact]
    public void Describe_CwdInsideRoot_ExcludesSiblingAndCountsDeniedRead()
    {
        var root = Root();
        var nested = Path.Combine(root, "nested");
        var report = AssemblyTempRootCleanupHolderDiagnostics.Describe(root,
            () => Snapshot(Process(1), Process(2), Process(3)), pid => pid switch
            {
                1 => new(nested, ProcessCurrentDirectoryStatus.Available),
                2 => new(root + "-sibling", ProcessCurrentDirectoryStatus.Available),
                _ => new(null, ProcessCurrentDirectoryStatus.AccessDenied, 5)
            });

        var holder = Assert.Single(report.Holders);
        Assert.Equal(1, holder.ProcessId);
        Assert.Equal("cwd", holder.Match);
        Assert.Equal(nested, holder.CurrentDirectory);
        Assert.DoesNotContain(report.DiagnosticLines, line => line.Contains("pid=2 ", StringComparison.Ordinal));
        Assert.Equal(1, report.UnreadableCurrentDirectoryCount);
        Assert.Contains("assembly-temp-cleanup-holder summary snapshot=complete unreadable_cwd_count=1", report.DiagnosticLines);
    }

    [Fact]
    public void Describe_RootItself_CwdWinsAndParentIsLive()
    {
        var root = Root();
        var report = AssemblyTempRootCleanupHolderDiagnostics.Describe(root,
            () => Snapshot(Process(1, root) with { ParentProcessId = 2, ExecutablePath = root }, Process(2)),
            pid => new(pid == 1 ? root + Path.DirectorySeparatorChar : Path.GetTempPath(), ProcessCurrentDirectoryStatus.Available));

        var holder = Assert.Single(report.Holders);
        Assert.Equal("cwd", holder.Match);
        Assert.Equal("true", holder.ParentLive);
        Assert.Contains($"pid=1 parent=2 image=shell.exe cmd={root}", report.MessageText);
    }

    [Fact]
    public void Describe_ReaderThrows_ReportsUnreadableWithoutThrowing()
    {
        var report = AssemblyTempRootCleanupHolderDiagnostics.Describe(Root(),
            () => Snapshot(Process(1)), _ => throw new IOException("denied"));
        Assert.Empty(report.Holders);
        Assert.Equal(1, report.UnreadableCurrentDirectoryCount);
        Assert.StartsWith("assembly-temp-cleanup-holder none root=", report.DiagnosticLines[0]);
    }

    [Fact]
    public void Describe_IncompleteSnapshot_ReportsUnknownParentAndBoundsCommand()
    {
        var root = Root();
        var command = root + "\r\n" + new string('x', 700);
        var report = AssemblyTempRootCleanupHolderDiagnostics.Describe(root,
            () => new ProcessCommandLineSnapshot(Snapshot(Process(1, command)).Records,
                new ProcessInspectionFailure(ProcessInspectionStatus.AccessDenied, 5, "inspect-process")),
            _ => new(null, ProcessCurrentDirectoryStatus.AccessDenied));

        var line = report.DiagnosticLines[0];
        Assert.Equal("unknown", Assert.Single(report.Holders).ParentLive);
        Assert.Contains("match=cmd cwd=none", line);
        Assert.Contains("...\" cmd_truncated=true", line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain(new string('x', 512), line);
        Assert.Contains("assembly-temp-cleanup-holder summary snapshot=incomplete unreadable_cwd_count=1", report.DiagnosticLines);
    }

    [Fact]
    public void Describe_SnapshotThrows_ReportsUnavailable()
    {
        var report = AssemblyTempRootCleanupHolderDiagnostics.Describe(Root(),
            () => throw new IOException("snapshot denied"));
        Assert.Contains("process snapshot unavailable: IOException: snapshot denied", report.MessageText);
        Assert.Contains("assembly-temp-cleanup-holder summary snapshot=unavailable unreadable_cwd_count=0", report.DiagnosticLines);
    }

    private static AssemblyTempRootCleanupFixture Fixture(string root, TextWriter writer) =>
        new(() => TempRootDeleteOutcome.Failure(root, "IOException", Path.Combine(root, "held"),
            0, "in use", unchecked((int)0x80070020), 6), writer,
            () => Snapshot(Process(424242, root) with { ParentProcessId = 31337 }),
            _ => new(Path.GetTempPath(), ProcessCurrentDirectoryStatus.Available));

    private static string Root() => Path.Combine(Path.GetTempPath(), "holder-report-" + Guid.NewGuid().ToString("N"));
    private static ProcessInspectionRecord Process(int pid, string command = "shell -Command ready") =>
        new(pid, 0, "shell.exe", "shell.exe", null, command, ProcessInspectionStatus.Available);
    private static ProcessCommandLineSnapshot Snapshot(params ProcessInspectionRecord[] records) =>
        new(records.ToDictionary(process => process.ProcessId));

    private sealed class HolderThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            if (value?.StartsWith("assembly-temp-cleanup-holder", StringComparison.Ordinal) == true)
                throw new IOException("writer closed");
        }
    }
}
