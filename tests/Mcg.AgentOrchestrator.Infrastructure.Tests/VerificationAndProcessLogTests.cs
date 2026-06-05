using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

public sealed class VerificationAndProcessLogTests
{
    [Xunit.Fact(DisplayName = "ManualVerificationRecorder_creates_pass_and_fail_records")]
    public void ManualVerificationRecorderCreatesPassAndFailRecords()
{
    var completedAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");

    var passed = ManualVerificationRecorder.Create(true, "Looks correct.", "C:\\repo", completedAt);
    var failed = ManualVerificationRecorder.Create(false, "Missing retry test.", "C:\\repo", completedAt);

    Assert.Equal("manual-verification passed", passed.Command);
    Assert.Equal(0, passed.ExitCode);
    Assert.Equal("Looks correct.", passed.StandardOutput);
    Assert.True(passed.Succeeded);
    Assert.Equal("manual-verification failed", failed.Command);
    Assert.Equal(1, failed.ExitCode);
    Assert.Equal("Missing retry test.", failed.StandardError);
    Assert.False(failed.Succeeded);
}
    [Xunit.Fact(DisplayName = "ProcessLogReader_reads_available_process_logs")]
    public void ProcessLogReaderReadsAvailableProcessLogs()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdout, "hello stdout");
    File.WriteAllText(stderr, "hello stderr");
    File.WriteAllText(exit, "0");
    var process = new TaskProcessRecord(
        123,
        "dotnet test",
        root,
        stdout,
        stderr,
        exit,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        0);

    var logs = ProcessLogReader.Read(process);

    Assert.Equal("hello stdout", logs.StandardOutput);
    Assert.Equal("hello stderr", logs.StandardError);
    Assert.Equal("0", logs.ExitCode);
}
}

