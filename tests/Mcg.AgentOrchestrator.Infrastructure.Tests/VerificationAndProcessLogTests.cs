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

    [Xunit.Fact(DisplayName = "RefreshLatestProcess_tolerates_locked_stdout_when_process_is_running")]
    public void RefreshLatestProcessToleratesLockedStdoutWhenProcessIsRunning()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Refresh with locked stdout running");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);

    using var lockedStdout = new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.None);

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => true);
    var result = runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.True(result.IsRunning);
    Assert.True(task.LastProcess!.IsRunning);
}

    [Xunit.Fact(DisplayName = "RefreshLatestProcess_tolerates_locked_stdout_when_process_has_exited")]
    public void RefreshLatestProcessToleratesLockedStdoutWhenProcessHasExited()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Refresh with locked stdout exited");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);

    using var lockedStdout = new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.None);

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
    var result = runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.False(result.IsRunning);
    Assert.Equal(0, result.ExitCode);
    Assert.True(task.LastVerification is not null);
    Assert.True(task.LastVerification!.StandardOutput.Contains(stdoutPath, StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "RefreshLatestProcess_tolerates_locked_stderr_when_process_is_running")]
    public void RefreshLatestProcessToleratesLockedStderrWhenProcessIsRunning()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Refresh with locked stderr running");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);

    using var lockedStderr = new FileStream(stderrPath, FileMode.Create, FileAccess.Write, FileShare.None);

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => true);
    var result = runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.True(result.IsRunning);
    Assert.True(task.LastProcess!.IsRunning);
}
}

