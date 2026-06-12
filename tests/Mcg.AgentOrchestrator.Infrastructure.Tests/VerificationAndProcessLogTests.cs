using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Mcg.AgentOrchestrator.App.Rendering;
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
    Assert.False(logs.Heartbeat.IsAvailable);
    Assert.Equal("missing", logs.Heartbeat.UnavailableReason);
}

    [Xunit.Fact(DisplayName = "ProcessLogReader_reads_dispatch_heartbeat_status")]
    public void ProcessLogReaderReadsDispatchHeartbeatStatus()
{
    var root = CreateTempDirectory();
    var exit = Path.Combine(root, "worker.exit.txt");
    var process = CreateProcessRecord(root, exit);
    var heartbeatPath = BackgroundDispatchRunner.GetHeartbeatPath(process);
    File.WriteAllText(heartbeatPath, """
{"pid":123,"childPid":456,"state":"running","lastObservedAt":"2026-06-12T20:00:10Z","lastProgressAt":"2026-06-12T20:00:00Z","stdoutBytes":42,"stderrBytes":7}
""");

    var heartbeat = ProcessLogReader.ReadHeartbeat(process, DateTimeOffset.Parse("2026-06-12T20:00:30Z"));

    Assert.True(heartbeat.IsAvailable);
    Assert.Equal(heartbeatPath, heartbeat.Path);
    Assert.True(heartbeat.UnavailableReason is null);
    Assert.Equal(123, heartbeat.ProcessId);
    Assert.Equal(456, heartbeat.ChildProcessId);
    Assert.Equal("running", heartbeat.State);
    Assert.Equal(TimeSpan.FromSeconds(20), heartbeat.HeartbeatAge);
    Assert.Equal(TimeSpan.FromSeconds(30), heartbeat.IdleDuration);
    Assert.Equal(42, heartbeat.StandardOutputBytes);
    Assert.Equal(7, heartbeat.StandardErrorBytes);
}

    [Xunit.Fact(DisplayName = "ProcessLogReader_degrades_missing_or_invalid_heartbeat_to_unavailable")]
    public void ProcessLogReaderDegradesMissingOrInvalidHeartbeatToUnavailable()
{
    var root = CreateTempDirectory();
    var missingProcess = CreateProcessRecord(root, Path.Combine(root, "missing.exit.txt"));
    var missing = ProcessLogReader.ReadHeartbeat(missingProcess);

    Assert.False(missing.IsAvailable);
    Assert.Equal("missing", missing.UnavailableReason);
    Assert.Equal(BackgroundDispatchRunner.GetHeartbeatPath(missingProcess), missing.Path);

    var invalidProcess = CreateProcessRecord(root, Path.Combine(root, "invalid.exit.txt"));
    File.WriteAllText(BackgroundDispatchRunner.GetHeartbeatPath(invalidProcess), "{not-json");

    var invalid = ProcessLogReader.ReadHeartbeat(invalidProcess);

    Assert.False(invalid.IsAvailable);
    Assert.Equal("invalid", invalid.UnavailableReason);
    Assert.Equal(BackgroundDispatchRunner.GetHeartbeatPath(invalidProcess), invalid.Path);
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

    [Xunit.Fact(DisplayName = "RefreshLatestProcess_populates_log_paths_in_verification_record")]
    public void RefreshLatestProcessPopulatesLogPathsInVerificationRecord()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Refresh populates log paths");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdoutPath, "stdout content");
    File.WriteAllText(stderrPath, "stderr content");
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
    runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.True(task.LastVerification is not null);
    Assert.Equal(stdoutPath, task.LastVerification!.StandardOutputPath);
    Assert.Equal(stderrPath, task.LastVerification.StandardErrorPath);
}

    [Xunit.Fact(DisplayName = "CreateVerificationLog_truncates_long_output_at_2000_chars")]
    public void CreateVerificationLogTruncatesLongOutputAt2000Chars()
{
    var longText = new string('x', 5000);

    var preview = OutputTextPreview.CreateVerificationLog(longText);

    Assert.True(preview.IsTruncated);
    Assert.Equal(5000, preview.OriginalLength);
    Assert.True(preview.Text.Length < 3000);
    Assert.True(preview.Text.Contains("[truncated", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "CreateVerificationLog_includes_log_path_in_truncation_marker")]
    public void CreateVerificationLogIncludesLogPathInTruncationMarker()
{
    var longText = new string('x', 5000);
    var logPath = @"C:\logs\test.err.log";

    var preview = OutputTextPreview.CreateVerificationLog(longText, logPath);

    Assert.True(preview.IsTruncated);
    Assert.True(preview.Text.Contains(logPath, StringComparison.Ordinal));
    Assert.True(preview.Text.Contains("full log:", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "CreateVerificationLog_does_not_truncate_short_output")]
    public void CreateVerificationLogDoesNotTruncateShortOutput()
{
    var shortText = "Build succeeded. 0 warnings.";

    var preview = OutputTextPreview.CreateVerificationLog(shortText);

    Assert.False(preview.IsTruncated);
    Assert.Equal(shortText, preview.Text);
}

    [Xunit.Fact(DisplayName = "RecordCompletedProcess_reaps_build_daemons_referencing_working_directory")]
    public void RecordCompletedProcessReapsBuildDaemonsReferencingWorkingDirectory()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "worktree");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Reap build daemons on completion");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", workingDirectory, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "codex exec", workingDirectory, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);
    var killedPids = new List<int>();
    var runner = new BackgroundDispatchRunner(
        isStillRunning: _ => false,
        findBuildDaemons: _ => [(42716, "VBCSCompiler", Path.Combine(workingDirectory, "obj", "debug", "Assembly.dll"))],
        tryKillBuildDaemon: pid => { killedPids.Add(pid); return true; });

    runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.True(killedPids.Count == 1 && killedPids[0] == 42716);
    Assert.True(task.LastVerification!.StandardError.Contains("42716", StringComparison.Ordinal));
    Assert.True(task.LastVerification.StandardError.Contains("VBCSCompiler", StringComparison.Ordinal));
    Assert.True(task.LastVerification.StandardError.Contains("Reaped", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "RecordCompletedProcess_leaves_non_matching_processes_alone")]
    public void RecordCompletedProcessLeavesNonMatchingProcessesAlone()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "worktree");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("No-match build daemons");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", workingDirectory, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "codex exec", workingDirectory, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);
    var killedPids = new List<int>();
    var runner = new BackgroundDispatchRunner(
        isStillRunning: _ => false,
        findBuildDaemons: _ => [],
        tryKillBuildDaemon: pid => { killedPids.Add(pid); return true; });

    runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.True(killedPids.Count == 0);
    Assert.True(task.LastVerification is not null);
    Assert.False(task.LastVerification!.StandardError.Contains("Reaped", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "RecordCompletedProcess_kill_failure_degrades_to_note_not_dispatch_failure")]
    public void RecordCompletedProcessKillFailureDegradestoNoteNotDispatchFailure()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "worktree");
    Directory.CreateDirectory(workingDirectory);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Kill failure does not fail dispatch");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Planner);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", workingDirectory, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "codex exec", workingDirectory, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);
    var runner = new BackgroundDispatchRunner(
        isStillRunning: _ => false,
        findBuildDaemons: _ => [(56656, "MSBuild", null)],
        tryKillBuildDaemon: _ => false);

    runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.True(task.LastVerification.StandardError.Contains("56656", StringComparison.Ordinal));
    Assert.True(task.LastVerification.StandardError.Contains("failed", StringComparison.OrdinalIgnoreCase));
}

    [Xunit.Fact(DisplayName = "RecordCompletedProcess_skips_daemon_reaping_for_local_dispatches")]
    public void RecordCompletedProcessSkipsDaemonReapingForLocalDispatches()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Local dispatch skips daemon reaping");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "local-cmd", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "local-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);
    var findCalled = false;
    var runner = new BackgroundDispatchRunner(
        isStillRunning: _ => false,
        findBuildDaemons: _ => { findCalled = true; return []; },
        tryKillBuildDaemon: _ => true);

    runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.False(findCalled);
}

private static TaskProcessRecord CreateProcessRecord(string root, string exitPath)
{
    return new TaskProcessRecord(
        123,
        "fake-cmd",
        root,
        Path.Combine(root, "worker.out.log"),
        Path.Combine(root, "worker.err.log"),
        exitPath,
        DateTimeOffset.Parse("2026-06-12T19:59:00Z"),
        null,
        null);
}
}

