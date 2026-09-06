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
{"pid":123,"childPid":456,"ownedPids":[123,789],"ownedProcessIdentities":[{"processId":789,"startedAt":"2026-06-12T19:59:59Z","imagePath":"C:\\workers\\worker.exe"}],"state":"running","lastObservedAt":"2026-06-12T20:00:10Z","lastProgressAt":"2026-06-12T20:00:00Z","stdoutBytes":42,"stderrBytes":7,"providerSessionId":"codex-session-123","worktreeHeadSha":"abc123","dirtyStateHash":"dirty-hash"}
""");

    var heartbeat = ProcessLogReader.ReadHeartbeat(process, DateTimeOffset.Parse("2026-06-12T20:00:30Z"));

    Assert.True(heartbeat.IsAvailable);
    Assert.Equal(heartbeatPath, heartbeat.Path);
    Assert.True(heartbeat.UnavailableReason is null);
    Assert.Equal(123, heartbeat.ProcessId);
    Assert.Equal(456, heartbeat.ChildProcessId);
    Assert.Equal<int>([123, 789], heartbeat.OwnedProcessIds);
    Assert.Equal(789, Assert.Single(heartbeat.OwnedProcessIdentities).ProcessId);
    Assert.Equal("running", heartbeat.State);
    Assert.Equal(TimeSpan.FromSeconds(20), heartbeat.HeartbeatAge);
    Assert.Equal(TimeSpan.FromSeconds(30), heartbeat.IdleDuration);
    Assert.Equal(42, heartbeat.StandardOutputBytes);
    Assert.Equal(7, heartbeat.StandardErrorBytes);
    Assert.Equal("codex-session-123", heartbeat.ProviderSessionId);
    Assert.Equal("abc123", heartbeat.WorktreeHeadSha);
    Assert.Equal("dirty-hash", heartbeat.DirtyStateHash);
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
    WriteHeartbeat(processRecord, pid: 999999, childPid: null, ownedPids: [999999], state: "running");

    using var lockedStdout = new FileStream(stdoutPath, FileMode.Create, FileAccess.Write, FileShare.None);

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => true, readProcessIdentity: ReadTestIdentity);
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
    WriteHeartbeat(processRecord, pid: 999999, childPid: null, ownedPids: [999999], state: "running");

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
    WriteHeartbeat(processRecord, pid: 999999, childPid: null, ownedPids: [999999], state: "running");

    using var lockedStderr = new FileStream(stderrPath, FileMode.Create, FileAccess.Write, FileShare.None);

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => true, readProcessIdentity: ReadTestIdentity);
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
    WriteHeartbeat(processRecord, pid: 111, childPid: 222, ownedPids: [111, 222], state: "exiting");

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
    runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.True(task.LastVerification is not null);
    Assert.Equal(stdoutPath, task.LastVerification!.StandardOutputPath);
    Assert.Equal(stderrPath, task.LastVerification.StandardErrorPath);
}

    [Xunit.Fact(DisplayName = "RefreshLatestProcess_bounds_large_worker_logs_in_verification_record")]
    public void RefreshLatestProcessBoundsLargeWorkerLogsInVerificationRecord()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var reviewTask = new TaskSpec(TaskId.New(), "Review worker output", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Refresh bounds large worker logs", [reviewTask]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    var stdoutHead = new string('O', VerificationTextBounds.PreviewHeadChars);
    var stdoutMiddle = "STDOUT-MIDDLE-SHOULD-NOT-BE-RETAINED" + new string('M', 2_000_000);
    var stdoutTail = new string('T', VerificationTextBounds.PreviewTailChars);
    var stderrHead = new string('R', VerificationTextBounds.PreviewHeadChars);
    var stderrMiddle = "STDERR-MIDDLE-SHOULD-NOT-BE-RETAINED" + new string('N', 2_000_000);
    var stderrTail = new string('E', VerificationTextBounds.PreviewTailChars);
    File.WriteAllText(stdoutPath, stdoutHead + stdoutMiddle + stdoutTail);
    File.WriteAllText(stderrPath, stderrHead + stderrMiddle + stderrTail);
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, reviewTask.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, reviewTask.Id, processRecord);

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
    runner.RefreshLatestProcess(kernel, goal.Id, reviewTask.Id);

    Assert.True(reviewTask.LastVerification is not null);
    Assert.True(reviewTask.LastVerification!.StandardOutput.Length <= 20_000);
    Assert.True(reviewTask.LastVerification.StandardError.Length <= 20_000);
    Assert.Equal(BackgroundDispatchRunner.ReadBoundedBestEffort(stdoutPath), reviewTask.LastVerification.StandardOutput);
    Assert.StartsWith(stdoutHead, reviewTask.LastVerification.StandardOutput, StringComparison.Ordinal);
    Assert.EndsWith(stdoutTail, reviewTask.LastVerification.StandardOutput, StringComparison.Ordinal);
    Assert.StartsWith(stderrHead, reviewTask.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains(stderrTail, reviewTask.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.DoesNotContain("STDOUT-MIDDLE-SHOULD-NOT-BE-RETAINED", reviewTask.LastVerification.StandardOutput, StringComparison.Ordinal);
    Assert.DoesNotContain("STDERR-MIDDLE-SHOULD-NOT-BE-RETAINED", reviewTask.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains(stdoutPath, reviewTask.LastVerification.StandardOutput, StringComparison.Ordinal);
    Assert.Contains(stderrPath, reviewTask.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.True(new FileInfo(stdoutPath).Length > 1_000_000);
    Assert.True(new FileInfo(stderrPath).Length > 1_000_000);
}

    [Xunit.Fact(DisplayName = "RefreshLatestProcess_keeps_worker_result_present_when_the_block_opens_inside_the_head_preview")]
    public void RefreshLatestProcessKeepsWorkerResultPresentWhenTheBlockOpensInsideTheHeadPreview()
{
    // Shape measured from goal 0b81147a's reviewer log: 16,690 chars total with the WORKER_RESULT block
    // opening at byte 3,153 - i.e. INSIDE the 8,192-char head-preview window. The head prefix and the
    // decision content each charged those in-window block lines to the same 20,000-char budget, so the
    // retention overflowed by roughly 1,100 chars at the END of the block. model_fit, skills and confidence
    // live there and are REQUIRED fields, so the parser reported the block absent, WorkerResultPresent went
    // false, MergedReviewFindings was never set, and the convergence brief announced "merged finding state
    // was EMPTY" while twelve valid findings sat in the log.
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var reviewTask = new TaskSpec(TaskId.New(), "Review worker output", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Keep worker result present", [reviewTask]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");

    var findingsJson =
        "findings: [{\"stable_id\":\"budget-straddling-finding\",\"state\":\"open\",\"severity\":\"blocking\"," +
        "\"category\":\"correctness\",\"location\":{\"file\":\"src/Some/File.cs\",\"region\":\"SomeMethod\"}," +
        "\"description\":\"" + new string('D', 6_500) + "\"}]";
    var stdout =
        new string('R', 3_100) + Environment.NewLine +          // prose review before the block, as in production
        "WORKER_RESULT" + Environment.NewLine +
        "files: src/Some/File.cs" + Environment.NewLine +
        "commands: dotnet test" + Environment.NewLine +
        "tests: not-run - Reviewer role" + Environment.NewLine +
        new string('B', 5_500) + Environment.NewLine +          // in-block prose inside the head-preview window
        findingsJson + Environment.NewLine +
        "touched_anchors: []" + Environment.NewLine +
        "blockers: P1 something is wrong" + Environment.NewLine +
        "model_fit: Anthropic/claude-opus-5 - adequate" + Environment.NewLine +
        "skills: none" + Environment.NewLine +
        "confidence: high" + Environment.NewLine +
        "END_WORKER_RESULT";
    File.WriteAllText(stdoutPath, stdout);
    File.WriteAllText(stderrPath, string.Empty);
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, reviewTask.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, reviewTask.Id, processRecord);

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
    runner.RefreshLatestProcess(kernel, goal.Id, reviewTask.Id);

    // The trailing REQUIRED fields must survive the retention budget, or the whole block reads as absent.
    Assert.True(
        reviewTask.LastVerification!.WorkerResultPresent,
        "WORKER_RESULT must still be detected when the block opens inside the head-preview window");
    Assert.DoesNotContain("decision-text-truncated", reviewTask.LastVerification.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact]
    public void RefreshLatestProcessUsesCompleteStdoutForAnOverCapWorkerResult()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var reviewTask = new TaskSpec(TaskId.New(), "Review worker output", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Use complete stdout for worker-result decisions", [reviewTask]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var priorOutput =
            "WORKER_RESULT:" + Environment.NewLine +
            "files: none" + Environment.NewLine +
            "commands: prior review" + Environment.NewLine +
            "tests: pass - prior review passed" + Environment.NewLine +
            "findings: [{\"stable_id\":\"over-cap-finding\",\"state\":\"open\",\"severity\":\"blocking\"," +
            "\"category\":\"operator-owned\",\"location\":{\"file\":\"src/Some/File.cs\",\"region\":\"SomeMethod\"}," +
            "\"description\":\"Prior category must be replaced.\"}]" + Environment.NewLine +
            "touched_anchors: []" + Environment.NewLine +
            "blockers: P1 prior category" + Environment.NewLine +
            "model_fit: Anthropic/claude-opus-5 - adequate" + Environment.NewLine +
            "skills: none" + Environment.NewLine +
            "confidence: high" + Environment.NewLine +
            "END_WORKER_RESULT";
        kernel.RecordTaskDispatch(goal.Id, reviewTask.Id, new TaskDispatchRecord("local", "prior-review", root, DateTimeOffset.UtcNow));
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            reviewTask.Id,
            new TaskVerificationRecord(
                "prior-review",
                root,
                1,
                priorOutput,
                string.Empty,
                DateTimeOffset.UtcNow,
                WorkerResultPresent: true,
                FullStandardOutput: priorOutput));
        Assert.Equal(FindingCategory.OperatorOwned, Assert.Single(reviewTask.LastVerification!.MergedReviewFindings!).Category);
        kernel.RetryTask(goal.Id, reviewTask.Id, "Reviewer corrected the finding category.");
        var stdoutPath = Path.Combine(root, "out.log");
        var stderrPath = Path.Combine(root, "err.log");
        var exitPath = Path.Combine(root, "exit.txt");
        var childExitPath = Path.Combine(root, "child-exit.json");
        var stdout =
            "WORKER_RESULT:" + Environment.NewLine +
            "files: src/Some/File.cs" + Environment.NewLine +
            "commands: focused review" + Environment.NewLine +
            "tests: pass - focused review passed" + Environment.NewLine +
            "findings: [{\"stable_id\":\"over-cap-finding\",\"state\":\"open\",\"severity\":\"blocking\"," +
            "\"category\":\"correctness\",\"location\":{\"file\":\"src/Some/File.cs\",\"region\":\"SomeMethod\"}," +
            "\"description\":\"" + new string('D', 20_100) + "\"}]" + Environment.NewLine +
            "touched_anchors: []" + Environment.NewLine +
            "blockers: none" + Environment.NewLine +
            "model_fit: Anthropic/claude-opus-5 - adequate" + Environment.NewLine +
            "skills: none" + Environment.NewLine +
            "confidence: high" + Environment.NewLine +
            "END_WORKER_RESULT";
        File.WriteAllText(stdoutPath, stdout);
        File.WriteAllText(stderrPath, string.Empty);
        File.WriteAllText(exitPath, "1");
        File.WriteAllText(
            childExitPath,
            System.Text.Json.JsonSerializer.Serialize(
                new DispatchProcessHost.DispatchChildExitRecord(888888, 0, DateTimeOffset.UtcNow),
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        using var activeWriter = new FileStream(
            stdoutPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        kernel.RecordTaskDispatch(goal.Id, reviewTask.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            reviewTask.Id,
            new TaskProcessRecord(
                999999,
                "fake-cmd",
                root,
                stdoutPath,
                stderrPath,
                exitPath,
                DateTimeOffset.UtcNow,
                null,
                null,
                ChildExitRecordPath: childExitPath));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        runner.RefreshLatestProcess(kernel, goal.Id, reviewTask.Id);

        Assert.True(stdout.Length > VerificationTextBounds.MaxRetainedChars);
        Assert.True(
            reviewTask.LastVerification!.WorkerResultPresent,
            "The complete stdout contains a valid WORKER_RESULT, so a capped decision snapshot must not make it absent");
        var finding = Assert.Single(reviewTask.LastVerification.MergedReviewFindings!);
        Assert.Equal("over-cap-finding", finding.StableId);
        Assert.Equal(FindingCategory.Correctness, finding.Category);
        Assert.True(reviewTask.LastVerification.ReconciledToSuccess);
        Assert.Equal(0, reviewTask.LastVerification.ExitCode);
        Assert.True(reviewTask.LastVerification.StandardOutput.Length <= VerificationTextBounds.MaxRetainedChars);
        Assert.Contains(
            $"decision-text-truncated source=stdout dropped_chars={stdout.Length - VerificationTextBounds.MaxRetainedChars}",
            reviewTask.LastVerification.StandardError,
            StringComparison.Ordinal);
        Assert.Contains(
            $"dropped_scope=selected-decision-content retained_chars={VerificationTextBounds.MaxRetainedChars} " +
            $"cap={VerificationTextBounds.MaxRetainedChars}",
            reviewTask.LastVerification.StandardError,
            StringComparison.Ordinal);
        Assert.Contains($"complete_artifact='{stdoutPath}'", reviewTask.LastVerification.StandardError, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RefreshLatestProcessPreservesWorkerResultInTheBoundedFallbackSnapshot()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var reviewTask = new TaskSpec(TaskId.New(), "Review worker output", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Preserve worker result in bounded snapshot", [reviewTask]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var stdoutPath = Path.Combine(root, "out.log");
        var stderrPath = Path.Combine(root, "err.log");
        var exitPath = Path.Combine(root, "exit.txt");
        var decisionProse = string.Join(
            Environment.NewLine,
            Enumerable.Repeat("tests: pass - verbose decision-significant prose " + new string('P', 100), 150));
        var stdout = decisionProse + Environment.NewLine +
            "WORKER_RESULT:" + Environment.NewLine +
            "files: none" + Environment.NewLine +
            "commands: focused review" + Environment.NewLine +
            "tests: pass - focused review passed" + Environment.NewLine +
            "blockers: none" + Environment.NewLine +
            "model_fit: Anthropic/claude-opus-5 - adequate" + Environment.NewLine +
            "skills: none" + Environment.NewLine +
            "confidence: high" + Environment.NewLine +
            "END_WORKER_RESULT";
        File.WriteAllText(stdoutPath, stdout);
        File.WriteAllText(stderrPath, string.Empty);
        File.WriteAllText(exitPath, "0");
        kernel.RecordTaskDispatch(goal.Id, reviewTask.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            reviewTask.Id,
            new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null));
        Stream OpenAndRemoveStdout(string path)
        {
            var content = File.ReadAllBytes(path);
            if (string.Equals(path, stdoutPath, StringComparison.Ordinal))
            {
                File.Delete(path);
            }
            return new MemoryStream(content, writable: false);
        }

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false, openLogReadStream: OpenAndRemoveStdout);
        runner.RefreshLatestProcess(kernel, goal.Id, reviewTask.Id);

        Assert.False(File.Exists(stdoutPath));
        Assert.True(
            reviewTask.LastVerification!.WorkerResultPresent,
            "When the complete artifact becomes unavailable, the bounded fallback must retain the contractual block");
        Assert.Contains("decision-text-truncated source=stdout", reviewTask.LastVerification.StandardError, StringComparison.Ordinal);
        Assert.Contains(
            $"retained_chars={VerificationTextBounds.MaxRetainedChars} cap={VerificationTextBounds.MaxRetainedChars}",
            reviewTask.LastVerification.StandardError,
            StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void RefreshLatestProcessRejectsAbsentOrMalformedOverCapWorkerResults(bool malformedBlock)
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var reviewTask = new TaskSpec(TaskId.New(), "Review worker output", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Reject invalid over-cap worker result", [reviewTask]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var stdoutPath = Path.Combine(root, "out.log");
        var stderrPath = Path.Combine(root, "err.log");
        var exitPath = Path.Combine(root, "exit.txt");
        var stdout = malformedBlock
            ? "WORKER_RESULT:" + Environment.NewLine +
              "files: none" + Environment.NewLine +
              "commands: focused review" + Environment.NewLine +
              "tests: pass - " + new string('T', 20_100) + Environment.NewLine +
              "blockers: none" + Environment.NewLine +
              "model_fit: Anthropic/claude-opus-5 - adequate" + Environment.NewLine +
              "skills: none" + Environment.NewLine +
              "END_WORKER_RESULT"
            : string.Join(
                Environment.NewLine,
                Enumerable.Repeat("tests: pass - no worker-result block " + new string('T', 100), 200));
        File.WriteAllText(stdoutPath, stdout);
        File.WriteAllText(stderrPath, string.Empty);
        File.WriteAllText(exitPath, "0");
        kernel.RecordTaskDispatch(goal.Id, reviewTask.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            reviewTask.Id,
            new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        runner.RefreshLatestProcess(kernel, goal.Id, reviewTask.Id);

        Assert.True(stdout.Length > VerificationTextBounds.MaxRetainedChars);
        Assert.False(reviewTask.LastVerification!.WorkerResultPresent);
        Assert.Null(reviewTask.LastVerification.MergedReviewFindings);
    }

    [Xunit.Fact]
    public void RefreshLatestProcessCompleteStdoutPreservesBareCarriageReturnMarkers()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var reviewTask = new TaskSpec(TaskId.New(), "Review worker output", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Read bare carriage return worker result", [reviewTask]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var stdoutPath = Path.Combine(root, "out.log");
        var stderrPath = Path.Combine(root, "err.log");
        var exitPath = Path.Combine(root, "exit.txt");
        File.WriteAllText(
            stdoutPath,
            "prefix\rWORKER_RESULT:\rfiles: none\rcommands: focused review\rtests: pass - focused review passed" +
            "\rblockers: none\rmodel_fit: test/test - adequate\rskills: none\rconfidence: high\rEND_WORKER_RESULT\r");
        File.WriteAllText(stderrPath, string.Empty);
        File.WriteAllText(exitPath, "0");
        kernel.RecordTaskDispatch(goal.Id, reviewTask.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            reviewTask.Id,
            new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null));

        var outcome = new BackgroundDispatchRunner(isStillRunning: _ => false)
            .ReconcileLatestProcess(kernel, goal.Id, reviewTask.Id);

        Assert.True(outcome.Verification!.WorkerResultPresent);
    }

    [Xunit.Fact(DisplayName = "RefreshLatestProcess_ignores_prompt_human_input_and_upstream_blocker_in_stderr")]
    public void RefreshLatestProcessIgnoresPromptHumanInputAndUpstreamBlockerInStderr()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Review worker output", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Use only current worker output", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        const string question = "Confirm the authoritative retry floor?";
        const string upstreamBlocker = "exact-blocker - operator decision required";
        var request = kernel.RequestHumanInputDeduplicated(
            goal.Id,
            task.Id,
            question,
            questionFingerprint: HumanInputRequest.BuildQuestionFingerprint(question),
            blockerFingerprint: HumanInputRequest.BuildWorkerResultBlockerFingerprint(
                task.Id,
                task.RequiredRole,
                question,
                upstreamBlocker)).Request;
        kernel.SubmitHumanInput(request.Id, "Use five seconds.");
        var stdoutPath = Path.Combine(root, "out.log");
        var stderrPath = Path.Combine(root, "err.log");
        var exitPath = Path.Combine(root, "exit.txt");
        File.WriteAllText(stdoutPath, $$"""
            WORKER_RESULT:
            files: none
            commands: focused review
            tests: pass - focused review passed
            blockers: none
            model_fit: OpenAI/{{AgentCatalog.OpenAiSolSubscriptionModelAlias}} - adequate
            skills: none
            confidence: high
            END_WORKER_RESULT
            """);
        File.WriteAllText(stderrPath, $$"""
            Prompt context from an upstream role:
            WORKER_RESULT:
            files: none
            commands: none
            tests: deferred - awaiting operator
            blockers: {{upstreamBlocker}}
            model_fit: OpenAI/{{AgentCatalog.OpenAiSolSubscriptionModelAlias}} - adequate
            skills: none
            confidence: high
            END_WORKER_RESULT
            HUMAN_INPUT: {{question}}
            """);
        File.WriteAllText(exitPath, "0");
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.True(task.LastVerification!.WorkerResultPresent);
        Assert.Null(task.LastVerification.HumanInputQuestion);
        Assert.Equal(0, request.SuppressionCount);
        Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed);
    }

    [Xunit.Fact]
    public void Tick_driven_apparatus_holds_do_not_increment_answered_suppression_streak()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Recover an indeterminate dispatch", AgentRole.Developer);
        var goal = kernel.CreateGoal("Keep recovery observations out of the duplicate-input guard", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var process = new TaskProcessRecord(
            999999,
            "fake-cmd",
            root,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "exit.txt"),
            DateTimeOffset.UtcNow,
            null,
            null);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        var decision = new DispatchRecoveryDecision(
            DispatchRecoveryAction.Hold,
            "hold",
            process.ExitCodePath,
            "exit artifact unreadable",
            "exit-artifact-invalid");
        var outcome = new DispatchRefreshOutcome(process, null, RecoveryDecision: decision);

        for (var observation = 0; observation < 3; observation++)
        {
            BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);
        }

        var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        kernel.SubmitHumanInput(request.Id, "Artifact repaired.");
        for (var observation = 0; observation < 3; observation++)
        {
            BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);
        }

        Assert.Equal(0, request.SuppressionCount);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.DuplicateHumanInputSuppressed);
        var failure = Assert.Single(goal.Timeline, item =>
            item.TaskId == task.Id &&
            item.Kind == ProgressKind.TaskFailed &&
            item.Message.Contains(request.Id.Value[..8], StringComparison.Ordinal));
        Assert.Contains("3 observations after answered recovery request", failure.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void RefreshLatestProcess_recognizes_worker_result_file_without_stderr_result()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Review worker output", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Recognize worker result artifact", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var stdoutPath = Path.Combine(root, "out.log");
        var stderrPath = Path.Combine(root, "err.log");
        var exitPath = Path.Combine(root, "exit.txt");
        File.WriteAllText(stdoutPath, "Worker wrote the structured result to its result artifact.");
        File.WriteAllText(stderrPath, "Prompt context only; no worker result here.");
        File.WriteAllText(exitPath, "0");
        File.WriteAllText(Path.Combine(root, "WORKER_RESULT.md"), $"""
            WORKER_RESULT:
            files: none
            commands: focused review
            tests: pass - focused review passed
            commit: none
            blockers: none
            model_fit: OpenAI/{AgentCatalog.OpenAiSolSubscriptionModelAlias} - adequate
            skills: none
            confidence: high
            END_WORKER_RESULT
            """);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null));

        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
        runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.True(task.LastVerification!.WorkerResultPresent);
    }

    [Xunit.Fact(DisplayName = "RefreshLatestProcess_pauses_for_middle_log_human_input_before_retained_excerpt")]
    public void RefreshLatestProcessPausesForMiddleLogHumanInputBeforeRetainedExcerpt()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var reviewTask = new TaskSpec(TaskId.New(), "Review worker output", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Pause from full worker log", [reviewTask]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    var stdout =
        new string('O', VerificationTextBounds.PreviewHeadChars) +
        Environment.NewLine +
        string.Join(
            Environment.NewLine,
            Enumerable.Repeat("tests: pass - decision prefill " + new string('M', 100), 120)) +
        Environment.NewLine +
        "HUMAN_INPUT: Which branch should I modify?" +
        Environment.NewLine +
        new string('N', 2_000) +
        Environment.NewLine +
        new string('T', VerificationTextBounds.PreviewTailChars);
    Assert.True(stdout.Length > VerificationTextBounds.MaxRetainedChars);
    File.WriteAllText(stdoutPath, stdout);
    File.WriteAllText(stderrPath, string.Empty);
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, reviewTask.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "fake-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, reviewTask.Id, processRecord);

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
    runner.RefreshLatestProcess(kernel, goal.Id, reviewTask.Id);

    var request = kernel.GetPendingHumanInput(goal.Id).Single();
    Assert.Equal(WorkTaskStatus.WaitingForHuman, reviewTask.Status);
    Assert.Equal("Which branch should I modify?", request.Question);
    Assert.False(reviewTask.LastVerification!.WorkerResultPresent);
    Assert.True(reviewTask.LastVerification!.StandardOutput.Length <= VerificationTextBounds.MaxRetainedChars);
    Assert.DoesNotContain("HUMAN_INPUT:", reviewTask.LastVerification.StandardOutput, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "RefreshLatestProcess_classifies_middle_log_verification_before_retained_excerpt")]
    public void RefreshLatestProcessClassifiesMiddleLogVerificationBeforeRetainedExcerpt()
{
    var worktree = LandingExecutorTests.CreateGitRepository();
    var logRoot = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var testerTask = new TaskSpec(TaskId.New(), "Verify existing behavior", AgentRole.Tester);
    var goal = kernel.CreateGoal("Classify full log before bounding", [testerTask]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var stdoutPath = Path.Combine(logRoot, "tester.out.log");
    var stderrPath = Path.Combine(logRoot, "tester.err.log");
    var exitPath = Path.Combine(logRoot, "tester.exit.txt");
    var middleEvidence = "test run successful";
    var stdout =
        new string('H', VerificationTextBounds.PreviewHeadChars) +
        Environment.NewLine +
        middleEvidence +
        Environment.NewLine +
        new string('M', 2_000_000) +
        new string('T', VerificationTextBounds.PreviewTailChars);
    File.WriteAllText(stdoutPath, stdout);
    File.WriteAllText(stderrPath, string.Empty);
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, testerTask.Id, new TaskDispatchRecord("codex-cli", "codex exec", worktree, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "codex exec", worktree, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, testerTask.Id, processRecord);

    var runner = new BackgroundDispatchRunner(
        isStillRunning: _ => false,
        findBuildDaemons: _ => []);
    var refreshed = runner.RefreshLatestProcess(kernel, goal.Id, testerTask.Id);

    Assert.Equal(0, refreshed.ExitCode);
    Assert.Equal(WorkTaskStatus.Completed, testerTask.Status);
    Assert.True(testerTask.LastVerification!.StandardOutput.Length <= 20_000);
    Assert.DoesNotContain(middleEvidence, testerTask.LastVerification.StandardOutput, StringComparison.Ordinal);
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

    [Xunit.Fact]
    public void CompletedNonLocalDispatch_ExitArtifactPrecedesScopedCleanup()
{
    var root = CreateTempDirectory();
    var workingDirectory = Path.Combine(root, "worktree");
    var siblingDirectory = Path.Combine(root, "sibling-worktree");
    Directory.CreateDirectory(workingDirectory);
    Directory.CreateDirectory(siblingDirectory);
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
    using var ownedKilled = new ManualResetEventSlim();
    using var siblingKilled = new ManualResetEventSlim();
    string? cleanupScope = null;
    var fixtures = new[]
    {
        (ProcessId: 42716, Name: "VBCSCompiler", CommandLine: Path.Combine(workingDirectory, "obj", "debug", "Assembly.dll")),
        (ProcessId: 42717, Name: "VBCSCompiler", CommandLine: Path.Combine(siblingDirectory, "obj", "debug", "Sibling.dll"))
    };
    var runner = new BackgroundDispatchRunner(
        isStillRunning: _ => false,
        findBuildDaemons: directory =>
        {
            cleanupScope = directory;
            Assert.True(File.Exists(exitPath), "The native exit artifact must exist before scoped cleanup starts.");
            return fixtures
                .Where(fixture => WorktreeBuildDaemonReaper.ShouldReap(directory, fixture.CommandLine))
                .Select(fixture => new ProcessInspectionRecord(
                    fixture.ProcessId,
                    1,
                    fixture.Name,
                    $"C:\\tools\\{fixture.Name}.exe",
                    DateTimeOffset.UnixEpoch,
                    fixture.CommandLine,
                    ProcessInspectionStatus.Available))
                .ToArray();
        },
        tryKillBuildDaemon: process =>
        {
            var pid = process.ProcessId;
            if (pid == 42716) ownedKilled.Set();
            if (pid == 42717) siblingKilled.Set();
            return true;
        });

    runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(Path.GetFullPath(workingDirectory), Path.GetFullPath(cleanupScope!));
    Assert.True(ownedKilled.IsSet);
    Assert.False(siblingKilled.IsSet);
    Assert.True(task.LastVerification!.StandardError.Contains("42716", StringComparison.Ordinal));
    Assert.DoesNotContain("42717", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.True(task.LastVerification.StandardError.Contains("VBCSCompiler", StringComparison.Ordinal));
    Assert.True(task.LastVerification.StandardError.Contains("Reaped", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "RefreshLatestProcess_holds_live_tracked_owned_pids_when_exit_file_exists")]
    public void RefreshLatestProcessHoldsLiveTrackedOwnedPidsWhenExitFileExists()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Refresh holds live tracked pids");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdoutPath, "done");
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(
        111,
        "fake-cmd",
        root,
        stdoutPath,
        stderrPath,
        exitPath,
        DateTimeOffset.UtcNow,
        null,
        null,
        OwnedProcessIds: [111, 222]);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);
    WriteHeartbeat(processRecord, pid: 111, childPid: 222, ownedPids: [111, 222], state: "exiting");
    var killed = new List<int>();
    var running = new HashSet<int> { 111, 222 };

    var runner = new BackgroundDispatchRunner(
        isStillRunning: pid => running.Contains(pid),
        tryKillOwnedProcess: pid => { killed.Add(pid); running.Remove(pid); return true; },
        readProcessIdentity: ReadTestIdentity);

    var refreshed = runner.RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Empty(killed);
    Assert.True(refreshed.IsRunning);
    Assert.True(task.LastProcess!.IsRunning);
}

    [Xunit.Fact(DisplayName = "Build_daemon_reaping_requires_command_line_ownership_evidence")]
    public void BuildDaemonReapingRequiresCommandLineOwnershipEvidence()
{
    var root = CreateTempDirectory();
    var worktree = Path.Combine(root, "worktree");
    Directory.CreateDirectory(worktree);

    Assert.False(WorktreeBuildDaemonReaper.ShouldReap(worktree, null));
    Assert.False(WorktreeBuildDaemonReaper.ShouldReap(worktree, ""));
    Assert.False(WorktreeBuildDaemonReaper.ShouldReap(worktree, "VBCSCompiler.exe -shared"));
    Assert.True(WorktreeBuildDaemonReaper.ShouldReap(
        worktree,
        $"VBCSCompiler.exe -keepalive \"{Path.Combine(worktree, "obj", "Debug", "Core.dll")}\""));
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
        tryKillBuildDaemon: process => { killedPids.Add(process.ProcessId); return true; });

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
    var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Researcher);
    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdoutPath, WorkerDispatchTestSupport.ResearcherContractFixture());
    File.WriteAllText(stderrPath, string.Empty);
    File.WriteAllText(exitPath, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec", workingDirectory, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "codex exec", workingDirectory, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);
    var runner = new BackgroundDispatchRunner(
        isStillRunning: _ => false,
        findBuildDaemons: _ =>
        [
            new ProcessInspectionRecord(
                56656,
                1,
                "MSBuild",
                "C:\\tools\\MSBuild.exe",
                DateTimeOffset.UnixEpoch,
                null,
                ProcessInspectionStatus.Available)
        ],
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

    [Xunit.Fact(DisplayName = "SweepExitedProcesses_auto_reconciles_exited_process_without_refresh_dispatch_call")]
    public void SweepExitedProcessesAutoReconcilesExitedProcessWithoutRefreshDispatchCall()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "local-worker task", AgentRole.Developer);
    var goal = kernel.CreateGoal("Sweep reconcile", [task]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var workTask = goal.Tasks.Single();

    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdoutPath, "Worker completed successfully.");

    kernel.RecordTaskDispatch(goal.Id, workTask.Id, new TaskDispatchRecord("local", "local-cmd", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "local-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, workTask.Id, processRecord);

    Assert.True(workTask.LastProcess!.IsRunning);

    File.WriteAllText(exitPath, "0");

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
    var swept = runner.SweepExitedProcesses(kernel);

    Assert.Equal(1, swept);
    Assert.False(workTask.LastProcess!.IsRunning);
}

    [Xunit.Fact(DisplayName = "SweepExitedProcesses_is_idempotent_second_sweep_returns_zero")]
    public void SweepExitedProcessesIsIdempotentSecondSweepReturnsZero()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "local-worker task idempotent", AgentRole.Developer);
    var goal = kernel.CreateGoal("Sweep idempotent", [task]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var workTask = goal.Tasks.Single();

    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdoutPath, "Worker completed successfully.");
    File.WriteAllText(exitPath, "0");

    kernel.RecordTaskDispatch(goal.Id, workTask.Id, new TaskDispatchRecord("local", "local-cmd", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(999999, "local-cmd", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, workTask.Id, processRecord);

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
    var firstSwept = runner.SweepExitedProcesses(kernel);
    var secondSwept = runner.SweepExitedProcesses(kernel);

    Assert.Equal(1, firstSwept);
    Assert.Equal(0, secondSwept);
}

    [Xunit.Fact(DisplayName = "SweepExitedProcesses_is_idempotent_after_recoverable_usage_limit_clears_latest_verification")]
    public void SweepExitedProcessesIsIdempotentAfterRecoverableUsageLimitClearsLatestVerification()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "subscription-worker task idempotent", AgentRole.Developer);
    var goal = kernel.CreateGoal("Sweep usage limit idempotent", [task]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var workTask = goal.Tasks.Single();

    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdoutPath, string.Empty);
    File.WriteAllText(stderrPath, "ERROR: You've hit your usage limit. Please try again in 42s.");
    File.WriteAllText(exitPath, "1");

    kernel.RecordTaskDispatch(
        goal.Id,
        workTask.Id,
        new TaskDispatchRecord(
            "codex-cli",
            "codex exec prompt",
            root,
            DateTimeOffset.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var processRecord = new TaskProcessRecord(999999, "codex exec prompt", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, workTask.Id, processRecord);

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
    var firstSwept = runner.SweepExitedProcesses(kernel);
    var secondSwept = runner.SweepExitedProcesses(kernel);

    Assert.Equal(1, firstSwept);
    Assert.Equal(0, secondSwept);
    Assert.Null(workTask.LastVerification);
    Assert.Single(workTask.VerificationHistory);
    Assert.Equal(1, DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(workTask));
}

    [Xunit.Fact(DisplayName = "SweepExitedProcesses_reconciles_verified_task_with_dead_heartbeat_pids_and_exit_artifact")]
    public void SweepExitedProcessesReconcilesVerifiedTaskWithDeadHeartbeatPidsAndExitArtifact()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "already verified worker task", AgentRole.Developer);
    var goal = kernel.CreateGoal("Sweep verified terminal dispatch", [task]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var workTask = goal.Tasks.Single();

    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdoutPath, "Worker had already completed.");
    File.WriteAllText(stderrPath, string.Empty);
    File.WriteAllText(exitPath, "1");

    kernel.RecordTaskDispatch(goal.Id, workTask.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(111, "codex exec prompt", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, workTask.Id, processRecord);
    kernel.RecordTaskVerification(goal.Id, workTask.Id, new TaskVerificationRecord("manual", root, 0, "already accepted", string.Empty, DateTimeOffset.UtcNow));
    WriteHeartbeat(processRecord, pid: 32640, childPid: 32641, ownedPids: [32642], state: "exiting");

    var runner = new BackgroundDispatchRunner(isStillRunning: _ => false);
    var swept = runner.SweepExitedProcesses(kernel);
    var secondSwept = runner.SweepExitedProcesses(kernel);

    Assert.Equal(1, swept);
    Assert.Equal(0, secondSwept);
    Assert.False(workTask.LastProcess!.IsRunning);
    Assert.Equal(1, workTask.LastProcess.ExitCode);
    Assert.NotNull(workTask.LastVerification);
    Assert.Equal(0, workTask.LastVerification!.ExitCode);
    Assert.Single(workTask.VerificationHistory);
}

    [Xunit.Fact(DisplayName = "SweepExitedProcesses_does_not_reap_live_heartbeat_pid_when_exit_artifact_exists")]
    public void SweepExitedProcessesDoesNotReapLiveHeartbeatPidWhenExitArtifactExists()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "live heartbeat worker task", AgentRole.Developer);
    var goal = kernel.CreateGoal("Sweep live heartbeat dispatch", [task]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var workTask = goal.Tasks.Single();

    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdoutPath, "Worker still alive.");
    File.WriteAllText(exitPath, "0");

    kernel.RecordTaskDispatch(goal.Id, workTask.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(111, "codex exec prompt", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, workTask.Id, processRecord);
    WriteHeartbeat(processRecord, pid: 32640, childPid: 32641, ownedPids: [], state: "exiting");

    var killed = new List<int>();
    var runner = new BackgroundDispatchRunner(
        isStillRunning: pid => pid == 32641,
        tryKillOwnedProcess: pid => { killed.Add(pid); return true; },
        readProcessIdentity: ReadTestIdentity);
    var swept = runner.SweepExitedProcesses(kernel);

    Assert.Equal(0, swept);
    Assert.Empty(killed);
    Assert.True(workTask.LastProcess!.IsRunning);
    Assert.Null(workTask.LastVerification);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_holds_no_exit_dispatch_when_heartbeat_child_pid_is_live")]
    public void BackgroundDispatchRunnerReconcileHoldsNoExitDispatchWhenHeartbeatChildPidIsLive()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "live child heartbeat worker task", AgentRole.Developer);
    var goal = kernel.CreateGoal("Hold live child heartbeat dispatch", [task]);
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var workTask = goal.Tasks.Single();

    var stdoutPath = Path.Combine(root, "out.log");
    var stderrPath = Path.Combine(root, "err.log");
    var exitPath = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdoutPath, "Worker still alive.");

    kernel.RecordTaskDispatch(goal.Id, workTask.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, DateTimeOffset.UtcNow));
    var processRecord = new TaskProcessRecord(111, "codex exec prompt", root, stdoutPath, stderrPath, exitPath, DateTimeOffset.UtcNow, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, workTask.Id, processRecord);
    WriteHeartbeat(processRecord, pid: 32640, childPid: 32641, ownedPids: [], state: "running");

    var killed = new List<int>();
    var runner = new BackgroundDispatchRunner(
        isStillRunning: pid => pid == 32641,
        tryKillOwnedProcess: pid => { killed.Add(pid); return true; },
        readProcessIdentity: ReadTestIdentity);
    runner.RefreshLatestProcess(kernel, goal.Id, workTask.Id);

    Assert.Empty(killed);
    Assert.True(workTask.LastProcess!.IsRunning);
    Assert.Null(workTask.LastProcess.ExitCode);
    Assert.Null(workTask.LastVerification);
    Assert.Empty(workTask.VerificationHistory);
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

private static void WriteHeartbeat(
    TaskProcessRecord process,
    int pid,
    int? childPid,
    IReadOnlyList<int> ownedPids,
    string state)
{
    var observedAt = DateTimeOffset.UtcNow;
    var identityPids = ownedPids
        .Concat(childPid is > 0 ? [childPid.Value] : [])
        .Concat(pid > 0 ? [pid] : [])
        .Distinct()
        .ToArray();
    var identities = string.Join(",", identityPids.Select(identityPid =>
        $$"""{"processId":{{identityPid}},"startedAt":"2026-07-19T11:58:00Z","imagePath":"C:\\workers\\worker-{{identityPid}}.exe"}"""));
    var heartbeat = $$"""
{"pid":{{pid}},"childPid":{{(childPid is null ? "null" : childPid.Value.ToString())}},"ownedPids":[{{string.Join(",", ownedPids)}}],"ownedProcessIdentities":[{{identities}}],"state":"{{state}}","lastObservedAt":"{{observedAt:O}}","lastProgressAt":"{{observedAt:O}}","stdoutBytes":42,"stderrBytes":0}
""";
    File.WriteAllText(BackgroundDispatchRunner.GetHeartbeatPath(process), heartbeat);
}

private static (DateTimeOffset StartedAt, string ImagePath)? ReadTestIdentity(int processId) =>
    (DateTimeOffset.Parse("2026-07-19T11:58:00Z"), $@"C:\workers\worker-{processId}.exe");
}

