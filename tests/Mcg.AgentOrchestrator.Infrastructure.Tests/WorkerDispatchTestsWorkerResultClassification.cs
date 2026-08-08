using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json;

[Xunit.Collection("EnvMutation")]
public sealed class WorkerDispatchTestsWorkerResultClassification : WorkerDispatchTestSupport
{
    [Xunit.Fact(DisplayName = "DispatchFailureClassifier_classifies_claude_401_as_provider_authentication_failure")]
    public void DispatchFailureClassifierClassifiesClaude401AsProviderAuthenticationFailure()
{
    var task = new TaskSpec(TaskId.New(), "Implement with Claude.", AgentRole.Developer);
    var verification = new TaskVerificationRecord(
        "claude --model claude-haiku-4-5 -p prompt",
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: Failed to authenticate: API Error 401 Unauthorized",
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"));

    var outcome = DispatchFailureClassifier.Classify(task, verification);

    Assert.Equal(DispatchOutcomeKind.ProviderAuthentication, outcome.Kind);
    Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
    Assert.True(outcome.EvidenceSummary.Contains("401", StringComparison.OrdinalIgnoreCase), outcome.EvidenceSummary);
}

    [Xunit.Fact(DisplayName = "DispatchFailureClassifier_uses_provider_failure_kind_from_worker_provider_parse_outcome")]
    public void DispatchFailureClassifierUsesProviderFailureKindFromWorkerProviderParseOutcome()
{
    var provider = WorkerProviderCatalog.Default().Resolve(ProviderKind.OpenAICodexCli);
    var failureKind = provider.ParseOutcome(new WorkerProviderOutcome(
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Try again later."));

    var outcome = DispatchFailureClassifier.ClassifyProviderFailure(
        failureKind,
        exitCode: 1,
        hasZeroByteOutput: true,
        evidenceSummary: "provider supplied typed failure");

    Assert.Equal(ProviderFailureKind.RateLimit, failureKind);
    Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
    Assert.Equal(RecoveryRecommendation.AutoRetry, outcome.RecoveryRecommendation);
}

    [Xunit.Fact(DisplayName = "WorkerProvider_parse_outcome_ignores_rate_limit_words_inside_WORKER_RESULT_blockers")]
    public void WorkerProviderParseOutcomeIgnoresRateLimitWordsInsideWorkerResultBlockers()
{
    var provider = WorkerProviderCatalog.Default().Resolve(ProviderKind.OpenAICodexCli);
    var stdout = string.Join(Environment.NewLine,
        "WORKER_RESULT:",
        "files: none",
        "commands: dotnet test --no-build",
        "tests: not-run",
        "commit: none",
        "blockers: API rate limit hit while running a local verification fixture",
        "model_fit: OpenAI/gpt-5.5 - adequate - dispatch",
        "skills: none",
        "confidence: medium",
        "END_WORKER_RESULT");

    var failureKind = provider.ParseOutcome(new WorkerProviderOutcome(1, stdout, string.Empty));

    Assert.Equal(ProviderFailureKind.Unknown, failureKind);
}

    [Xunit.Fact(DisplayName = "DispatchFailureClassifier_identifies_subscription_dispatch_from_typed_provider_identity")]
    public void DispatchFailureClassifierIdentifiesSubscriptionDispatchFromTypedProviderIdentity()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Exercise typed provider dispatch classification",
        [new TaskSpec(TaskId.New(), "Implement the change.", AgentRole.Developer)]);
    kernel.ActivateGoal(goal.Id, [SubscriptionDeveloperAgent()]);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord(
            WorkerProfileDispatcher.OpenAiSubscriptionProfileName,
            "opaque launcher command",
            "C:\\repo",
            DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var verification = new TaskVerificationRecord(
        "opaque launcher command",
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Try again later.",
        DateTimeOffset.Parse("2026-06-26T12:01:00Z"));

    var outcome = DispatchFailureClassifier.Classify(task, verification);

    Assert.Equal(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
}

    [Xunit.Fact(DisplayName = "DispatchFailureClassifier_does_not_infer_subscription_dispatch_from_command_text")]
    public void DispatchFailureClassifierDoesNotInferSubscriptionDispatchFromCommandText()
{
    var task = new TaskSpec(TaskId.New(), "Implement the change.", AgentRole.Developer);
    var verification = new TaskVerificationRecord(
        "codex-cli simulated command text",
        "C:\\repo",
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Try again later.",
        DateTimeOffset.Parse("2026-06-26T12:01:00Z"));

    var outcome = DispatchFailureClassifier.Classify(task, verification);

    Assert.NotEqual(DispatchOutcomeKind.RecoverableSubscriptionLimit, outcome.Kind);
}

    [Xunit.Fact(DisplayName = "WorkerProviderCatalog_try_resolve_profile_returns_typed_identity")]
    public void WorkerProviderCatalogTryResolveProfileReturnsTypedIdentity()
{
    var catalog = WorkerProviderCatalog.Default();

    var found = catalog.TryResolveProfile(WorkerProfileDispatcher.OpenAiSubscriptionProfileName, out var provider);

    Assert.True(found);
    Assert.Equal(ProviderKind.OpenAICodexCli, provider.Identity.Kind);
    Assert.False(provider.Capabilities.CanSelfCommit);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_rejects_duplicate_process_start")]
    public void BackgroundDispatchRunnerRejectsDuplicateProcessStart()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Avoid duplicate process start");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "Write-Output ok", root, DateTimeOffset.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(1234, "Write-Output ok", root, "out.log", "err.log", "exit.txt", DateTimeOffset.UtcNow, null, null));

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => new BackgroundDispatchRunner().StartLatestDispatch(kernel, goal.Id, task.Id, Path.Combine(root, "logs")));

    Assert.Contains("already has a dispatch process record", ex.Message, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refuses_process_start_when_disabled_for_environment")]
    public void BackgroundDispatchRunnerRefusesProcessStartWhenDisabledForEnvironment()
{
    var root = CreateTempDirectory();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Refuse disabled dispatch start");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "Write-Output should-not-run", root, DateTimeOffset.UtcNow));
    var runner = new BackgroundDispatchRunner(disableProcessStart: true);

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => runner.StartLatestDispatch(kernel, goal.Id, task.Id, Path.Combine(root, "logs")));

    Assert.Contains(BackgroundDispatchRunner.DisableDispatchStartVariable, ex.Message, StringComparison.Ordinal);
    Xunit.Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_registration_failure_stops_before_process_receipt")]
    public async Task BackgroundDispatchRunnerRegistrationFailureStopsBeforeProcessReceipt()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var root = CreateTempDirectory();
    var stateDbPath = Path.Combine(root, "state.db");
    var corruptRegistryPath = Path.Combine(root, "corrupt-spawn-registry.db");
    File.WriteAllBytes(corruptRegistryPath, []);
    Process? spawned = null;
    var spawnedProcessId = -1;
    try
    {
        WorkerProcessJobs.ConfigureRegistry(corruptRegistryPath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Surface worker registration failure");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(candidate => candidate.RequiredRole == AgentRole.Developer);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("local", "Write-Output ok", root, DateTimeOffset.UtcNow));
        _ = StateDbMigrations.EnsureUpToDate(stateDbPath);
        var repository = new SqliteOrchestratorStateRepository(stateDbPath);
        await repository.SaveAsync(kernel);
        var runner = new BackgroundDispatchRunner(
            disableProcessStart: false,
            startProcess: _ =>
            {
                spawned = Process.Start(new ProcessStartInfo
                {
                    FileName = WorkerShell.Executable,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }.WithArguments(
                    WorkerShell.BaseArguments().Concat([
                        "Start-Sleep -Seconds 9999"
                    ]))) ?? throw new InvalidOperationException("Failed to start registration-failure fixture.");
                spawnedProcessId = spawned.Id;
                return spawned;
            });

        DispatchProcessStartResult? startResult = null;
        var checkpointCount = 0;
        WorkTaskStatus? secondCheckpointStatus = null;
        bool? secondCheckpointHasProcess = null;
        await repository.TransactAsync((transactionKernel, _) =>
        {
            startResult = runner.TryStartLatestDispatch(
                transactionKernel,
                goal.Id,
                task.Id,
                Path.Combine(root, "logs"),
                (checkpointKernel, checkpointGoalId, checkpointTaskId, _) =>
                {
                    checkpointCount++;
                    if (checkpointCount == 2)
                    {
                        var checkpointTask = checkpointKernel.GetTask(checkpointGoalId, checkpointTaskId);
                        secondCheckpointStatus = checkpointTask.Status;
                        secondCheckpointHasProcess = checkpointTask.LastProcess is not null;
                    }
                });
            return Task.FromResult((true, true));
        });

        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        var restoredTask = restoredGoal.Tasks.Single(candidate => candidate.Id == task.Id);

        Assert.NotNull(startResult);
        Assert.Null(startResult.ProcessRecord);
        Assert.Equal(2, checkpointCount);
        Assert.Equal(WorkTaskStatus.Failed, secondCheckpointStatus);
        Assert.False(secondCheckpointHasProcess);
        Assert.Contains("worker-process-registration-failed", startResult.FailureReason, StringComparison.Ordinal);
        Assert.Contains("stage=durable-registry-write", startResult.FailureReason, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Failed, restoredTask.Status);
        Assert.Null(restoredTask.LastProcess);
        Assert.Contains(restoredGoal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains("worker-process-registration-failed", StringComparison.Ordinal));
        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                try
                {
                    using var candidate = Process.GetProcessById(spawnedProcessId);
                    return candidate.HasExited;
                }
                catch (ArgumentException)
                {
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            },
            TimeSpan.FromSeconds(5)));
    }
    finally
    {
        WorkerProcessJobs.ClearRegistryForTests();
        if (spawnedProcessId > 0)
        {
            try
            {
                using var candidate = Process.GetProcessById(spawnedProcessId);
                if (!candidate.HasExited)
                {
                    candidate.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The registration failure path should already have terminated the exact fixture.
            }
        }

        spawned?.Dispose();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_paid_worker_preflight_failure_has_zero_external_io")]
    public void BackgroundDispatchRunnerPaidWorkerPreflightFailureHasZeroExternalIo()
{
    var root = CreateTempDirectory();
    var logRoot = Path.Combine(root, "logs");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Refuse paid worker before external IO");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex-cli", "codex exec paid-worker-prompt", root, DateTimeOffset.UtcNow));
    var runner = new BackgroundDispatchRunner(disableProcessStart: true);

    var ex = Assert.ThrowsAny<InvalidOperationException>(
        () => runner.TryStartLatestDispatch(kernel, goal.Id, task.Id, logRoot));

    Assert.Contains(BackgroundDispatchRunner.DisableDispatchStartVariable, ex.Message, StringComparison.Ordinal);
    Assert.False(Directory.Exists(logRoot));
    Assert.Empty(Directory.GetFiles(root));
    Assert.Null(task.LastProcess);
    Assert.Null(task.LastDispatch!.WorktreeHeadSha);
    Assert.Null(task.LastDispatch.DirtyStateHash);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_cancel_kills_tracked_tree_and_allows_redispatch_without_paid_start")]
    public void BackgroundDispatchRunnerCancelKillsTrackedTreeAndAllowsRedispatchWithoutPaidStart()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.Parse("2026-06-21T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Stop worker tree and redispatch");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var dispatch = new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now);
    kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
    var process = new TaskProcessRecord(
        111,
        dispatch.Command,
        dispatch.WorkingDirectory,
        "out.log",
        "err.log",
        Path.Combine(root, "worker.exit.txt"),
        now,
        null,
        null,
        OwnedProcessIds: [111, 222]);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    File.WriteAllText(
        BackgroundDispatchRunner.GetHeartbeatPath(process),
        "{" +
        "\"pid\":111," +
        "\"childPid\":222," +
        "\"ownedPids\":[111,222]," +
        $"\"startedAt\":\"{now:O}\"," +
        $"\"lastObservedAt\":\"{now.AddSeconds(10):O}\"," +
        $"\"lastProgressAt\":\"{now.AddSeconds(10):O}\"," +
        "\"state\":\"running\"," +
        "\"stdoutBytes\":0," +
        "\"stderrBytes\":0," +
        "\"exitFileExists\":false," +
        "\"ownedCpuMs\":42" +
        "}");
    var running = new HashSet<int> { 111, 222 };
    var killed = new List<int>();
    var runner = new BackgroundDispatchRunner(
        new TestClock(now.AddMinutes(1)),
        isStillRunning: pid => running.Contains(pid),
        tryKillOwnedProcess: pid =>
        {
            killed.Add(pid);
            running.Remove(pid);
            return true;
        });

    var cancelled = runner.CancelLatestProcess(kernel, goal.Id, task.Id);
    kernel.RequeueInterruptedDispatch(goal.Id, task.Id, "Redispatch after stopped worker tree.");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt 2", root, now.AddMinutes(2)));

    Assert.True(killed.SequenceEqual([111, 222]));
    Assert.NotNull(cancelled.ResourceAccounting);
    Assert.Equal(42, cancelled.ResourceAccounting.CpuMilliseconds);
    Assert.True(cancelled.ResourceAccounting.Reaped);
    Assert.Equal("snapshot", cancelled.ResourceAccounting.AccountingSource);
    Assert.Contains(
        kernel.GetTimeline(goal.Id),
        evt => evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("RESOURCE ", StringComparison.Ordinal) &&
            evt.Message.Contains("cpu_ms=42", StringComparison.Ordinal) &&
            evt.Message.Contains("peak_mem_bytes=", StringComparison.Ordinal) &&
            evt.Message.Contains("io_bytes=", StringComparison.Ordinal) &&
            evt.Message.Contains("reaped=true", StringComparison.Ordinal));
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.Equal("codex exec prompt 2", task.LastDispatch!.Command);
    Xunit.Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_defers_future_subscription_retry_after_before_start")]
    public void BackgroundDispatchRunnerDefersFutureSubscriptionRetryAfterBeforeStart()
{
    var root = CreateTempDirectory();
    var now = DateTimeOffset.UtcNow;
    var retryAfter = now.AddHours(1);
    var command = "Write-Output should-not-run";
    var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
        [
            new GoalSnapshot(
                "goal-deferred",
                "Defer recorded dispatch start",
                GoalStatus.Active,
                [
                    new TaskSnapshot(
                        "task-deferred",
                        "Do work",
                        AgentRole.Developer,
                        WorkTaskStatus.Running,
                        "developer",
                        null,
                        null,
                        [
                            new TaskVerificationSnapshot(
                                command,
                                root,
                                1,
                                string.Empty,
                                $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryAfter:h:mm tt}.",
                                now)
                        ],
                        new TaskDispatchSnapshot("local", command, root, now),
                        null,
                        SubscriptionRetryAfter: retryAfter)
                ],
                [])
        ],
        []));
    var goal = kernel.Goals.Single();
    var task = goal.Tasks.Single();

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => new BackgroundDispatchRunner().StartLatestDispatch(kernel, goal.Id, task.Id, Path.Combine(root, "logs")));

    Assert.Contains("retry after", ex.Message, StringComparison.Ordinal);
    Assert.True(ex.Message.Contains(retryAfter.ToString("u"), StringComparison.Ordinal));
    Xunit.Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_non_local_dispatch_records_resource_accounting")]
    public void BackgroundDispatchRunnerNonLocalDispatchRecordsResourceAccounting()
{
    using var _ = ClearWorkerSandboxEnv();
    var root = CreateTempDirectory();
    var logs = Path.Combine(root, "logs");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Record worker dispatch resources");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "Write-Output $env:DOTNET_CLI_USE_MSBUILD_SERVER; Write-Output $env:MSBUILDDISABLENODEREUSE; Write-Output $env:UseSharedCompilation",
        root,
        DateTimeOffset.UtcNow));

    using var protectedPidScope = ClearProtectedPidEnvironment();
    var process = new BackgroundDispatchRunner().StartLatestDispatch(kernel, goal.Id, task.Id, logs);
    WaitForExitFile(process.ExitCodePath);
    new BackgroundDispatchRunner().RefreshLatestProcess(kernel, goal.Id, task.Id);

    // The exit file can appear before the final accounting snapshot lands, so under load the first
    // refresh may read null accounting. Re-poll briefly; the assertions below stay unchanged.
    // (Killed two full acceptance-gate attempts as a flake on 2026-07-25 before this wait.)
    var accountingDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (task.LastProcess!.ResourceAccounting is null && DateTimeOffset.UtcNow < accountingDeadline)
    {
        Thread.Sleep(100);
        new BackgroundDispatchRunner().RefreshLatestProcess(kernel, goal.Id, task.Id);
    }

    var refreshed = task.LastProcess!;
    var output = File.ReadAllLines(process.StandardOutputPath);
    Assert.True(File.Exists(BackgroundDispatchRunner.GetHeartbeatPath(process)));
    Assert.Equal(3, output.Length);
    Assert.Equal("0", output[0]);
    Assert.Equal("1", output[1]);
    Assert.Equal("false", output[2]);
    Assert.NotNull(refreshed.ResourceAccounting);
    Assert.True(refreshed.ResourceAccounting.CpuMilliseconds >= 0);
    Assert.True(refreshed.ResourceAccounting.PeakMemoryBytes > 0);
    Assert.True(refreshed.ResourceAccounting.IoBytes >= 0);
    Assert.Contains("RESOURCE ", task.LastVerification!.StandardError, StringComparison.Ordinal);
    Assert.Contains("cpu_ms=", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("peak_mem_bytes=", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("io_bytes=", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains(
        kernel.GetTimeline(goal.Id),
        evt => evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("RESOURCE ", StringComparison.Ordinal) &&
            evt.Message.Contains("cpu_ms=", StringComparison.Ordinal) &&
            evt.Message.Contains("peak_mem_bytes=", StringComparison.Ordinal) &&
            evt.Message.Contains("io_bytes=", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_completes_when_exit_file_exists_even_if_wrapper_is_running")]
    public void BackgroundDispatchRunnerRefreshCompletesWhenExitFileExistsEvenIfWrapperIsRunning()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-11T16:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Complete background process from exit file");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "done");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, clock.UtcNow, null, null));

    var completed = new BackgroundDispatchRunner(clock, isStillRunning: _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(0, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal("done", task.LastVerification!.StandardOutput);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_trusts_exit_zero_file_before_usage_limit_stderr")]
    public void BackgroundDispatchRunnerRefreshTrustsExitZeroFileBeforeUsageLimitStderr()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-07-05T04:30:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Complete background process from exit file despite usage text");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "WORKER_RESULT:\nfiles: src/Foo.cs\ntests: pass\nblockers: none\nEND_WORKER_RESULT");
    File.WriteAllText(stderr, "Rate limit reached for gpt-5.5. Please try again in 42s.");
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord(
            "codex-cli",
            "codex exec prompt",
            root,
            clock.UtcNow,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, clock.UtcNow, null, null));

    var completed = new BackgroundDispatchRunner(clock, isStillRunning: _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(0, completed.ExitCode);
    AssertExitCode(exit, 0);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.False(DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_completes_dead_exiting_worker_from_exit_file")]
    public void BackgroundDispatchRunnerReconcileCompletesDeadExitingWorkerFromExitFile()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-25T06:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Complete dead exiting worker from exit file");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "WORKER_RESULT:");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
    var process = new TaskProcessRecord(
        999999,
        "codex exec prompt",
        root,
        stdout,
        stderr,
        exit,
        now.AddMinutes(-5),
        null,
        null,
        OwnedProcessIds: [111, 222]);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(
        process,
        now.AddMinutes(-1),
        now.AddMinutes(-1),
        "exiting",
        14,
        0,
        childPid: null,
        ownedPids: [111, 222],
        exitFileExists: true);

    var outcome = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
        .ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.NotNull(outcome.Verification);
    Assert.Equal(0, outcome.ProcessRecord.ExitCode);
    Assert.Equal(clock.UtcNow, outcome.ProcessRecord.CompletedAt);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_holds_dead_worker_when_exit_file_is_unreadable")]
    public void BackgroundDispatchRunnerReconcileHoldsDeadWorkerWhenExitFileIsUnreadable()
    {
        var root = CreateTempDirectory();
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        var now = DateTimeOffset.Parse("2026-06-25T06:02:00Z");
        var clock = new TestClock(now);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Fail dead worker from unreadable exit file");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        File.WriteAllText(stdout, "worker output");
        File.WriteAllText(stderr, string.Empty);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
        var process = new TaskProcessRecord(
            999999,
            "codex exec prompt",
            root,
            stdout,
            stderr,
            exit,
            now.AddMinutes(-5),
            null,
            null,
            OwnedProcessIds: [111, 222]);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        using (var lockedExit = new FileStream(exit, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(lockedExit))
        {
            writer.Write("0");
            writer.Flush();

            var outcome = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
                .ReconcileLatestProcess(kernel, goal.Id, task.Id);

            Assert.Null(outcome.Verification);
            Assert.Null(outcome.ProcessRecord.ExitCode);
            Assert.Null(outcome.ProcessRecord.CompletedAt);
            Assert.Equal(DispatchRecoveryAction.Hold, outcome.RecoveryDecision!.Action);
            Assert.Contains("state=Unreadable", outcome.RecoveryDecision.Reason, StringComparison.Ordinal);
            Assert.Equal(WorkTaskStatus.Running, task.Status);
        }
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_sweep_completes_role_boundary_exited_worker_from_exit_file")]
    public void BackgroundDispatchRunnerSweepCompletesRoleBoundaryExitedWorkerFromExitFile()
    {
        var root = CreateTempDirectory();
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        var now = DateTimeOffset.Parse("2026-06-25T06:30:00Z");
        var clock = new TestClock(now);
        File.WriteAllText(stdout, "WORKER_RESULT:");
        File.WriteAllText(stderr, string.Empty);
        File.WriteAllText(exit, "0");

        var developerId = TaskId.New().Value;
        var testerId = TaskId.New().Value;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-role-boundary",
                    "Complete role boundary worker",
                    GoalStatus.Active,
                    [
                        new TaskSnapshot(
                            developerId,
                            "Developer task exited at role boundary",
                            AgentRole.Developer,
                            WorkTaskStatus.Assigned,
                            "agent-dev",
                            null,
                            null,
                            [],
                            new TaskDispatchSnapshot("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)),
                            new TaskProcessSnapshot(
                                999999,
                                "codex exec prompt",
                                root,
                                stdout,
                                stderr,
                                exit,
                                now.AddMinutes(-5),
                                now.AddMinutes(-1),
                                0,
                                OwnedProcessIds: [111, 222])),
                        new TaskSnapshot(
                            testerId,
                            "Tester task should be next",
                            AgentRole.Tester,
                            WorkTaskStatus.Assigned,
                            "agent-test",
                            null,
                            null,
                            [],
                            null,
                            null)
                    ],
                    [])
            ],
            []));
        var goal = kernel.Goals.Single();
        var developer = goal.Tasks.First(task => task.Id.Value == developerId);
        var tester = goal.Tasks.First(task => task.Id.Value == testerId);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, GoalLifecycle.ResolveState(goal, new GoalLifecycleFacts(WorkspaceExists: true)));

        var swept = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .SweepExitedProcesses(kernel);
        var nextActions = kernel.BuildNextActions(goal.Id).Items;

        Assert.Equal(1, swept);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(0, developer.LastVerification!.ExitCode);
        Assert.Equal(clock.UtcNow, developer.LastProcess!.CompletedAt);
        Assert.DoesNotContain(nextActions, action => action.TaskId == developer.Id);
        Assert.Contains(nextActions, action => action.TaskId == tester.Id && action.Kind == NextActionKind.RunAssignedTask);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_ignores_stale_child_pid_when_exit_file_exists")]
    public void BackgroundDispatchRunnerReconcileIgnoresStaleChildPidWhenExitFileExists()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-25T06:05:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Hold live child despite exit file");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "WORKER_RESULT:");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
    var process = new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, now.AddMinutes(-5), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(
        process,
        now.AddMinutes(-1),
        now.AddMinutes(-1),
        "running",
        14,
        0,
        childPid: 333,
        exitFileExists: true);

    var outcome = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
        .ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.NotNull(outcome.Verification);
    Assert.Equal(0, outcome.ProcessRecord.ExitCode);
    Assert.Equal(clock.UtcNow, outcome.ProcessRecord.CompletedAt);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_holds_exit_file_when_owned_pid_still_running")]
    public void BackgroundDispatchRunnerReconcileHoldsExitFileWhenOwnedPidStillRunning()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-25T06:10:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Hold live owned process despite exit file");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "WORKER_RESULT:");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
    var process = new TaskProcessRecord(
        999998,
        "codex exec prompt",
        root,
        stdout,
        stderr,
        exit,
        now.AddMinutes(-5),
        null,
        null,
        OwnedProcessIds: [444, 555]);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(
        process,
        now.AddMinutes(-1),
        now.AddMinutes(-1),
        "exiting",
        14,
        0,
        childPid: null,
        ownedPids: [444, 555],
        exitFileExists: true);

    var outcome = new BackgroundDispatchRunner(clock, isStillRunning: pid => pid == 444)
        .ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.Null(outcome.Verification);
    Assert.Null(outcome.ProcessRecord.ExitCode);
    Assert.Null(outcome.ProcessRecord.CompletedAt);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_fails_idle_codex_wrapper_after_final_output")]
    public void BackgroundDispatchRunnerRefreshFailsIdleCodexWrapperAfterFinalOutput()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    var now = DateTimeOffset.Parse("2026-06-11T16:10:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Detect hung codex wrapper");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "Implemented the change.");
    File.WriteAllText(stderr, "Tokens used: input=123 output=45");
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, now.AddMinutes(-5), null, null));

    var completed = new BackgroundDispatchRunner(clock, TimeSpan.FromMinutes(2), _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.True(File.Exists(exit));
    Assert.Contains("Implemented the change.", task.LastVerification!.StandardOutput, StringComparison.Ordinal);
    Assert.Contains("Tokens used", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("wrapper appears hung after codex final output", task.LastVerification.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_codex_hung_detection_ignores_final_output_beyond_decision_limit")]
    public void BackgroundDispatchRunnerCodexHungDetectionIgnoresFinalOutputBeyondDecisionLimit()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    var now = DateTimeOffset.Parse("2026-06-11T16:10:00Z");
    var significantPrefix = string.Join(
        Environment.NewLine,
        Enumerable.Repeat($"Model fit: {new string('x', 500)}", 50));
    File.WriteAllText(stdout, significantPrefix + Environment.NewLine + "Tokens used: input=123 output=45");
    File.WriteAllText(stderr, string.Empty);
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Preserve bounded codex final output detection");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
    kernel.RecordTaskProcessStarted(
        goal.Id,
        task.Id,
        new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, now.AddMinutes(-5), null, null));

    var outcome = new BackgroundDispatchRunner(new TestClock(now), TimeSpan.FromMinutes(2), _ => true)
        .ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.Null(outcome.Verification);
    Assert.Null(outcome.ProcessRecord.ExitCode);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_codex_idle_guard_skips_log_reads_before_timeout")]
    public void BackgroundDispatchRunnerCodexIdleGuardSkipsLogReadsBeforeTimeout()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    var now = DateTimeOffset.Parse("2026-06-11T16:10:00Z");
    File.WriteAllText(stdout, "Implemented the change.");
    File.WriteAllText(stderr, "Tokens used: input=123 output=45");
    File.SetLastWriteTimeUtc(stdout, now.AddSeconds(-10).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddSeconds(-10).UtcDateTime);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Guard log read before codex idle timeout");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-1)));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
        999999, "codex exec prompt", root, stdout, stderr, exit, now.AddMinutes(-1), null, null));
    var opens = 0;
    var runner = new BackgroundDispatchRunner(
        new TestClock(now),
        TimeSpan.FromMinutes(2),
        _ => true,
        openLogReadStream: path =>
        {
            opens++;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        });

    var outcome = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.Null(outcome.Verification);
    Assert.Equal(0, opens);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_memoizes_unchanged_active_process_logs")]
    public void BackgroundDispatchRunnerMemoizesUnchangedActiveProcessLogs()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    var now = DateTimeOffset.Parse("2026-06-11T16:10:00Z");
    File.WriteAllText(stdout, "still working");
    File.WriteAllText(stderr, "no final marker");
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Memoize unchanged active logs");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
        999999, "codex exec prompt", root, stdout, stderr, exit, now.AddMinutes(-5), null, null));
    var opens = 0;
    var runner = new BackgroundDispatchRunner(
        new TestClock(now),
        TimeSpan.FromMinutes(2),
        _ => true,
        openLogReadStream: path =>
        {
            opens++;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        });

    Assert.Null(runner.ReconcileLatestProcess(kernel, goal.Id, task.Id).Verification);
    Assert.Null(runner.ReconcileLatestProcess(kernel, goal.Id, task.Id).Verification);

    Assert.Equal(2, opens);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_retries_failed_unchanged_process_log_read")]
    public void BackgroundDispatchRunnerRetriesFailedUnchangedProcessLogRead()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    var now = DateTimeOffset.Parse("2026-06-11T16:10:00Z");
    File.WriteAllText(stdout, "Tokens used: input=123 output=45");
    File.WriteAllText(stderr, "no final marker");
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Retry locked unchanged log");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("codex-cli", "codex exec prompt", root, now.AddMinutes(-5)));
    kernel.RecordTaskProcessStarted(
        goal.Id,
        task.Id,
        new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, now.AddMinutes(-5), null, null));
    var stdoutAttempts = 0;
    var runner = new BackgroundDispatchRunner(
        new TestClock(now),
        TimeSpan.FromMinutes(2),
        _ => true,
        openLogReadStream: path =>
        {
            if (path == stdout && ++stdoutAttempts == 1)
            {
                throw new IOException("simulated transient lock");
            }

            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        });

    Assert.Null(runner.ReconcileLatestProcess(kernel, goal.Id, task.Id).Verification);
    var completed = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(2, stdoutAttempts);
    Assert.Equal(1, completed.ProcessRecord.ExitCode);
    Assert.Contains("wrapper appears hung after codex final output", completed.Verification!.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_bare_carriage_returns_preserve_worker_result_markers")]
    public void BackgroundDispatchRunnerBareCarriageReturnsPreserveWorkerResultMarkers()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    File.WriteAllText(
        stdout,
        "prefix\r" + WorkerResultBlock("none", "no changes", "pass - no changes")
            .Replace(Environment.NewLine, "\r", StringComparison.Ordinal) + "\r");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Read bare carriage return worker result");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);
    kernel.RecordTaskDispatch(
        goal.Id,
        task.Id,
        new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
    kernel.RecordTaskProcessStarted(
        goal.Id,
        task.Id,
        new TaskProcessRecord(999999, "fake-cmd", root, stdout, stderr, exit, DateTimeOffset.UtcNow, null, null));

    var outcome = new BackgroundDispatchRunner(isStillRunning: _ => false)
        .ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.True(outcome.Verification!.WorkerResultPresent);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_completion_reads_each_log_once_and_shares_payload_strings")]
    public void BackgroundDispatchRunnerCompletionReadsEachLogOnceAndSharesPayloadStrings()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    File.WriteAllText(stdout, "completed output");
    File.WriteAllText(stderr, "completed error");
    File.WriteAllText(exit, "0");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Read completion logs once");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "fake-cmd", root, DateTimeOffset.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
        999999, "fake-cmd", root, stdout, stderr, exit, DateTimeOffset.UtcNow, null, null));
    var opens = 0;
    var runner = new BackgroundDispatchRunner(
        isStillRunning: _ => false,
        openLogReadStream: path =>
        {
            opens++;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        });

    var outcome = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(2, opens);
    Assert.NotNull(outcome.Verification);
    Assert.NotNull(outcome.DiagnosticPayload);
    Assert.Same(outcome.Verification!.StandardOutput, outcome.DiagnosticPayload!.StandardOutput);
    Assert.Same(outcome.Verification.StandardError, outcome.DiagnosticPayload.StandardError);
}

    [Xunit.Theory(DisplayName = "BackgroundDispatchRunner_refresh_uses_provider_identity_for_codex_exit_file_behavior")]
    [Xunit.InlineData("codex-cli", "subscription worker prompt", true)]
    [Xunit.InlineData("claude-cli", "claude prompt", false)]
    [Xunit.InlineData("custom-agent", "codex exec prompt", false)]
    public void BackgroundDispatchRunnerRefreshUsesProviderIdentityForCodexExitFileBehavior(
        string workerProfileName,
        string command,
        bool expectedExitFile)
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "exit.txt");
    var now = DateTimeOffset.Parse("2026-06-11T16:10:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Detect profile-specific wrapper behavior");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "Implemented the change.");
    File.WriteAllText(stderr, "Tokens used: input=123 output=45");
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(workerProfileName, command, root, now.AddMinutes(-5)));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, command, root, stdout, stderr, exit, now.AddMinutes(-5), null, null));

    var refreshed = new BackgroundDispatchRunner(clock, TimeSpan.FromMinutes(2), _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(expectedExitFile, File.Exists(exit));
    if (expectedExitFile)
    {
        Assert.Equal(1, refreshed.ExitCode);
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Contains("wrapper appears hung after codex final output", task.LastVerification!.StandardError, StringComparison.Ordinal);
    }
    else
    {
        Assert.Null(refreshed.ExitCode);
        Assert.Equal(WorkTaskStatus.Running, task.Status);
        Assert.Null(task.LastVerification);
    }
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_fails_provider_neutral_stall_after_heartbeat_progress_timeout")]
    public void BackgroundDispatchRunnerRefreshFailsProviderNeutralStallAfterHeartbeatProgressTimeout()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Detect silent subscription stall");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-40)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-40), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 0, 0);

    var completed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            progressStallTimeout: TimeSpan.FromMinutes(10))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.True(File.Exists(exit));
    Assert.Contains("no observable progress", task.LastVerification!.StandardError, StringComparison.Ordinal);
    Assert.Contains("heartbeat state=running", task.LastVerification.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_progress_stall_idle_guard_skips_worktree_inspection")]
    public void BackgroundDispatchRunnerProgressStallIdleGuardSkipsWorktreeInspection()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    File.WriteAllText(stdout, "worker output");
    File.WriteAllText(stderr, string.Empty);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Skip worktree inspection before stall bound");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-5)));
    var process = new TaskProcessRecord(
        999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-5), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(process, now.AddMinutes(-1), now.AddMinutes(-1), "running", 12, 0, childPid: 4242);
    var inspections = 0;
    var runner = new BackgroundDispatchRunner(
        new TestClock(now),
        isStillRunning: _ => true,
        progressStallTimeout: TimeSpan.FromMinutes(10),
        beforeGoalWorktreeInspection: () => inspections++);

    var outcome = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);

    Assert.Null(outcome.Verification);
    Assert.Equal(0, inspections);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_cycle_reuses_worktree_inspection")]
    public void BackgroundDispatchRunnerRefreshCycleReusesWorktreeInspection()
{
    var root = CreateSeededDispatchRepository();
    try
    {
        var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Reuse worktree facts within one refresh cycle");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.AppendAllText(Path.Combine(worktree, "README.md"), Environment.NewLine + "dirty");
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        File.WriteAllText(stdout, "worker output");
        File.WriteAllText(stderr, string.Empty);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "claude-cli",
            "claude prompt",
            worktree,
            now.AddMinutes(-40)));
        var process = new TaskProcessRecord(
            999999, "claude prompt", worktree, stdout, stderr, exit, now.AddMinutes(-40), null, null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 12, 0, childPid: 4242);
        var inspections = 0;
        var runner = new BackgroundDispatchRunner(
            new TestClock(now),
            isStillRunning: _ => true,
            progressStallTimeout: TimeSpan.FromMinutes(10),
            beforeGoalWorktreeInspection: () => inspections++);

        Assert.Null(runner.ReconcileLatestProcess(kernel, goal.Id, task.Id).Verification);
        Assert.Null(runner.ReconcileLatestProcess(kernel, goal.Id, task.Id).Verification);
        Assert.Equal(1, inspections);

        runner.BeginRefreshCycle();
        Assert.Null(runner.ReconcileLatestProcess(kernel, goal.Id, task.Id).Verification);
        Assert.Equal(2, inspections);
    }
    finally
    {
        _ = GoalWorktrees.DeleteDirectory(root);
    }
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_completion_refreshes_cached_worktree_before_commit")]
    public void BackgroundDispatchRunnerCompletionRefreshesCachedWorktreeBeforeCommit()
{
    var root = CreateSeededDispatchRepository();
    try
    {
        var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Refresh worktree evidence at completion");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.AppendAllText(Path.Combine(worktree, "README.md"), Environment.NewLine + "first dirty change");
        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        File.WriteAllText(stdout, WorkerResultBlock("README.md, src/Second.cs", "implemented changes", "pass - focused"));
        File.WriteAllText(stderr, string.Empty);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("claude-cli", "claude prompt", worktree, now.AddMinutes(-40)));
        var process = new TaskProcessRecord(
            999999, "claude prompt", worktree, stdout, stderr, exit, now.AddMinutes(-40), null, null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 12, 0, childPid: 4242);
        var running = true;
        var runner = new BackgroundDispatchRunner(
            new TestClock(now),
            isStillRunning: _ => running,
            progressStallTimeout: TimeSpan.FromMinutes(10));

        Assert.Null(runner.ReconcileLatestProcess(kernel, goal.Id, task.Id).Verification);
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        File.WriteAllText(Path.Combine(worktree, "src", "Second.cs"), "internal sealed class Second;");
        File.WriteAllText(exit, "0");
        running = false;

        var completed = runner.ReconcileLatestProcess(kernel, goal.Id, task.Id);
        var status = GitCli.Run(worktree, "status", "--porcelain");

        Assert.Equal(0, completed.ProcessRecord.ExitCode);
        Assert.True(completed.Verification!.HasCommittedChanges);
        Assert.True(status.Succeeded);
        Assert.True(string.IsNullOrWhiteSpace(status.Output));
        Assert.True(File.Exists(Path.Combine(worktree, "src", "Second.cs")));
    }
    finally
    {
        _ = GoalWorktrees.DeleteDirectory(root);
    }
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_records_WORKER_RESULT_blocker_as_task_failure_without_subscription_retry")]
    public void BackgroundDispatchRunnerRefreshRecordsWorkerResultBlockerAsTaskFailureWithoutSubscriptionRetry()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-07-03T14:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Refresh structured blocker");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, string.Join(Environment.NewLine,
        "WORKER_RESULT:",
        "files: none",
        "commands: dotnet test --no-build",
        "tests: fail - timed out",
        "commit: none",
        "blockers: full Infrastructure no-build timed out at 214s",
        "model_fit: OpenAI/gpt-5.5 - adequate - dispatch",
        "skills: dotnet-windows-build-hygiene",
        "confidence: medium",
        "END_WORKER_RESULT"));
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "1");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec prompt",
        root,
        now.AddMinutes(-5),
        "OpenAI",
        "gpt-5.5",
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    var process = new TaskProcessRecord(999999, "codex exec prompt", root, stdout, stderr, exit, now.AddMinutes(-5), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

    var completed = new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, completed.ExitCode);
    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.NotNull(task.LastVerification);
    Assert.Equal(ProviderFailureKind.Unknown, task.LastVerification.ProviderFailureKind);
    Assert.Equal<DateTimeOffset?>(null, task.SubscriptionRetryAfter);
    Assert.False(DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskFailed &&
        evt.Message.Contains("full Infrastructure no-build timed out at 214s", StringComparison.Ordinal));
    Assert.DoesNotContain(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskRetried &&
        evt.Message.Contains("recoverable subscription usage limit", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_startup_hang_fast_path_fires_when_cpu_idle_and_no_output")]
    public void BackgroundDispatchRunnerStartupHangFastPathFiresWhenCpuIdleAndNoOutput()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Startup hang with idle cpu and no output");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Planner);
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-40)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-40), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 0, 0, ownedCpuMs: 0L, childPid: null);

    var completed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            startupHangTimeout: TimeSpan.FromMinutes(4))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.True(File.Exists(exit));
    Assert.Contains("never launched", task.LastVerification!.StandardError, StringComparison.Ordinal);
    Assert.Contains("ownedCpuMs=0", task.LastVerification.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_startup_hang_suppressed_when_child_alive_and_idle")]
    public void BackgroundDispatchRunnerStartupHangSuppressedWhenChildAliveAndIdle()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("API-bound worker: child alive but idle on the provider");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-10)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-10), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    // childPid set (tool launched) but ownedCpuMs idle + no output: an API-bound claude/codex -p worker
    // waiting on the provider. The tool WAS invoked, so the startup-hang fast-path must NOT fire.
    WriteHeartbeat(process, now.AddMinutes(-1), now.AddMinutes(-1), "running", 0, 0, ownedCpuMs: 0L, childPid: 4242);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            startupHangTimeout: TimeSpan.FromMinutes(4),
            progressStallTimeout: TimeSpan.FromMinutes(30))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_startup_hang_suppressed_when_cpu_above_epsilon")]
    public void BackgroundDispatchRunnerStartupHangSuppressedWhenCpuAboveEpsilon()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("No startup hang when cpu active");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-10)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-10), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    // ownedCpuMs=2000 > CpuIdleEpsilonMs=1000: worker is actively burning CPU, suppress fast-path
    WriteHeartbeat(process, now.AddMinutes(-1), now.AddMinutes(-1), "running", 0, 0, ownedCpuMs: 2000L);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            startupHangTimeout: TimeSpan.FromMinutes(4),
            progressStallTimeout: TimeSpan.FromMinutes(30))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_startup_hang_suppressed_when_ownedCpuMs_absent")]
    public void BackgroundDispatchRunnerStartupHangSuppressedWhenOwnedCpuMsAbsent()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("No startup hang for old-format heartbeat");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-10)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-10), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    // ownedCpuMs absent (old-format heartbeat, pre-cf59ce9e): suppress fast-path entirely
    WriteHeartbeat(process, now.AddMinutes(-1), now.AddMinutes(-1), "running", 0, 0, ownedCpuMs: null);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            startupHangTimeout: TimeSpan.FromMinutes(4),
            progressStallTimeout: TimeSpan.FromMinutes(30))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_startup_hang_suppressed_when_output_bytes_nonzero")]
    public void BackgroundDispatchRunnerStartupHangSuppressedWhenOutputBytesNonzero()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("No startup hang when output present");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "some output");
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-10)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-10), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    // stdoutBytes=11 nonzero: process communicated something, suppress fast-path
    WriteHeartbeat(process, now.AddMinutes(-1), now.AddMinutes(-1), "running", 11, 0, ownedCpuMs: 0L);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            startupHangTimeout: TimeSpan.FromMinutes(4),
            progressStallTimeout: TimeSpan.FromMinutes(30))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_keeps_recent_heartbeat_running")]
    public void BackgroundDispatchRunnerRefreshKeepsRecentHeartbeatRunning()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Keep active heartbeat running");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    File.WriteAllText(stdout, "thinking");
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", root, now.AddMinutes(-30)));
    var process = new TaskProcessRecord(999999, "claude prompt", root, stdout, stderr, exit, now.AddMinutes(-30), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(process, now.AddMinutes(-1), now.AddMinutes(-1), "running", 8, 0);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            progressStallTimeout: TimeSpan.FromMinutes(10))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
    Xunit.Assert.Null(task.LastVerification);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_auto_requeues_safe_stale_dispatch_corpse")]
    public void BackgroundDispatchRunnerReconcileAutoRequeuesSafeStaleDispatchCorpse()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-07-16T01:13:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Auto requeue safe stale dispatch");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var process = RecordStaleDeveloperDispatch(kernel, goal, task, worktree, root, now.AddMinutes(-40), "first");
    WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 0, 0, childPid: null, ownedCpuMs: 953);

    new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.Null(task.LastProcess);
    Assert.Null(task.LastDispatch);
    Assert.Null(task.LastVerification);
    Assert.Single(task.VerificationHistory, verification =>
        verification.StandardError.Contains("Dispatch recovery policy action='retry-stale'", StringComparison.Ordinal));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("StaleDispatchAutoRequeued", StringComparison.Ordinal) &&
        evt.Message.Contains("heartbeat_state=running", StringComparison.Ordinal) &&
        evt.Message.Contains("stdout_file_bytes=0", StringComparison.Ordinal) &&
        evt.Message.Contains("commits_after_dispatch=0", StringComparison.Ordinal) &&
        evt.Message.Contains("auto_requeue=1/2", StringComparison.Ordinal));
    Assert.Contains(kernel.BuildNextActions(goal.Id).Items, action =>
        action.TaskId == task.Id && action.Kind == NextActionKind.RunAssignedTask);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_preserves_interrupted_dispatch_with_dirty_worktree")]
    public void BackgroundDispatchRunnerReconcilePreservesInterruptedDispatchWithDirtyWorktree()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-07-16T01:13:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Escalate ambiguous stale dispatch");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "uncommitted work");
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var process = RecordStaleDeveloperDispatch(kernel, goal, task, worktree, root, now.AddMinutes(-40), "dirty");
    WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 0, 0, childPid: null, ownedCpuMs: 953);

    new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.NotNull(task.LastProcess);
    Assert.Equal(1, task.LastProcess!.ExitCode);
    Assert.NotNull(task.LastProcess.CompletedAt);
    Assert.Equal(DispatchExitArtifactOrigin.Synthetic, task.LastProcess.ExitArtifactOrigin);
    Assert.Equal("process missing with dirty worktree evidence", task.LastProcess.ExitArtifactReason);
    Assert.NotNull(task.LastVerification);
    Assert.True(File.Exists(Path.Combine(worktree, "dirty.txt")));
    Assert.True(DispatchExitArtifacts.TryRead(process.ExitCodePath, out var artifact));
    Assert.Equal(DispatchExitArtifactOrigin.Synthetic, artifact.Origin);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Message.Contains("InterruptedDispatchWorkPreserved", StringComparison.Ordinal));
    Assert.DoesNotContain(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Message.Contains("StaleDispatchAutoRequeued", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_fails_output_only_dispatch_corpse")]
    public void BackgroundDispatchRunnerReconcileFailsOutputOnlyDispatchCorpse()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-07-16T01:13:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Fail output-only stale dispatch");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var process = RecordStaleDeveloperDispatch(kernel, goal, task, worktree, root, now.AddMinutes(-40), "output-only");
    File.WriteAllText(process.StandardOutputPath, "partial provider output");
    WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 23, 0, childPid: null, ownedCpuMs: 953);

    new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastProcess!.ExitCode);
    Assert.NotNull(task.LastProcess.CompletedAt);
    Assert.Equal(DispatchExitArtifactOrigin.Synthetic, task.LastProcess.ExitArtifactOrigin);
    Assert.DoesNotContain("dirty worktree", task.LastProcess.ExitArtifactReason ?? string.Empty, StringComparison.Ordinal);
    Assert.DoesNotContain(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Message.Contains("InterruptedDispatchWorkPreserved", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_reconcile_escalates_third_safe_stale_dispatch_corpse")]
    public void BackgroundDispatchRunnerReconcileEscalatesThirdSafeStaleDispatchCorpse()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-07-16T01:13:00Z");
    var clock = new MutableClock(now);
    var kernel = new AgentOrchestratorKernel(clock);
    var goal = kernel.CreateGoal("Cap repeated safe stale dispatch");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    for (var attempt = 1; attempt <= 3; attempt++)
    {
        var process = RecordStaleDeveloperDispatch(
            kernel,
            goal,
            task,
            worktree,
            root,
            clock.UtcNow,
            $"attempt-{attempt}");
        WriteHeartbeat(process, clock.UtcNow.AddMinutes(-31), clock.UtcNow.AddMinutes(-31), "running", 0, 0, childPid: null, ownedCpuMs: 953);

        new BackgroundDispatchRunner(clock, isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);
        clock.Advance();
    }

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.NotNull(task.LastVerification);
    Assert.Contains("Dispatch recovery policy action='budget-exhausted'", task.LastVerification!.StandardError, StringComparison.Ordinal);
    Assert.Contains("stale-dispatch auto-requeue cap exhausted", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("auto_requeue=2/2", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Equal(2, task.VerificationHistory.Count(DispatchRecoveryPolicy.IsStaleDispatchRetryVerification));
    Assert.Equal(2, goal.Timeline.Count(evt =>
        evt.TaskId == task.Id &&
        evt.Message.Contains("StaleDispatchAutoRequeued", StringComparison.Ordinal)));
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Message.Contains("StaleDispatchAutoRequeueCapExhausted", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_refresh_keeps_stale_file_role_heartbeat_when_worktree_changed")]
    public void BackgroundDispatchRunnerRefreshKeepsStaleFileRoleHeartbeatWhenWorktreeChanged()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Do not kill worker after file progress");
    kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "progress");
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var stdout = Path.Combine(root, "out.log");
    var stderr = Path.Combine(root, "err.log");
    var exit = Path.Combine(root, "worker.exit.txt");
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", worktree, now.AddMinutes(-30)));
    var process = new TaskProcessRecord(999999, "claude prompt", worktree, stdout, stderr, exit, now.AddMinutes(-30), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    WriteHeartbeat(process, now.AddMinutes(-20), now.AddMinutes(-20), "running", 0, 0);

    var refreshed = new BackgroundDispatchRunner(
            clock,
            isStillRunning: _ => true,
            progressStallTimeout: TimeSpan.FromMinutes(10))
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(process, refreshed);
    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.False(File.Exists(exit));
    Xunit.Assert.Null(task.LastVerification);
}

    [Xunit.Theory(DisplayName = "BackgroundDispatchRunner_hung_wrappers_complete_read_only_roles_with_valid_worker_result")]
    [Xunit.InlineData(AgentRole.Planner, true)]
    [Xunit.InlineData(AgentRole.Planner, false)]
    [Xunit.InlineData(AgentRole.Researcher, true)]
    [Xunit.InlineData(AgentRole.Researcher, false)]
    [Xunit.InlineData(AgentRole.Reviewer, true)]
    [Xunit.InlineData(AgentRole.Reviewer, false)]
    public void BackgroundDispatchRunnerHungWrappersCompleteReadOnlyRolesWithValidWorkerResult(
        AgentRole role,
        bool codexWrapper)
    {
        var scenario = RunHungReadOnlyRoleScenario(
            role,
            codexWrapper,
            BuildSuccessfulReadOnlyOutput(role),
            childExitCode: role == AgentRole.Planner ? 0 : null);

        Assert.Equal(0, scenario.Outcome.ProcessRecord.ExitCode);
        Assert.Equal(0, scenario.Outcome.Verification!.ExitCode);
        Assert.Equal(WorkTaskStatus.Completed, scenario.Task.Status);
        AssertExitCode(scenario.Process.ExitCodePath, 0);
        Assert.Contains("wrapper appears hung", scenario.Task.LastVerification!.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("complete, non-blocked WORKER_RESULT", scenario.Task.LastVerification.StandardError, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "BackgroundDispatchRunner_hung_wrappers_fail_read_only_roles_without_valid_worker_result")]
    [Xunit.InlineData(true, "empty")]
    [Xunit.InlineData(false, "empty")]
    [Xunit.InlineData(true, "malformed")]
    [Xunit.InlineData(false, "malformed")]
    [Xunit.InlineData(true, "blocked")]
    [Xunit.InlineData(false, "blocked")]
    public void BackgroundDispatchRunnerHungWrappersFailReadOnlyRolesWithoutValidWorkerResult(
        bool codexWrapper,
        string evidenceKind)
    {
        var standardOutput = evidenceKind switch
        {
            "empty" => string.Empty,
            "malformed" => "WORKER_RESULT:\nblockers: none",
            "blocked" => WorkerResultBlock(
                "none",
                "inspection",
                "not-run - blocked before verification",
                blockers: "exact-blocker - required evidence is missing"),
            _ => throw new ArgumentOutOfRangeException(nameof(evidenceKind))
        };
        var scenario = RunHungReadOnlyRoleScenario(
            AgentRole.Planner,
            codexWrapper,
            standardOutput,
            childExitCode: 0);

        Assert.Equal(1, scenario.Outcome.ProcessRecord.ExitCode);
        Assert.Equal(1, scenario.Outcome.Verification!.ExitCode);
        Assert.Equal(0, scenario.Outcome.ProcessRecord.ChildExitCode);
        Assert.Equal(WorkTaskStatus.Failed, scenario.Task.Status);
        AssertExitCode(scenario.Process.ExitCodePath, 1);
        Assert.Contains("wrapper appears hung", scenario.Task.LastVerification!.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rescue denied", scenario.Task.LastVerification.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_read_only_hung_wrapper_rescue_is_dispatch_agnostic")]
    public void BackgroundDispatchRunnerReadOnlyHungWrapperRescueIsDispatchAgnostic()
    {
        var taskWithoutDispatch = new TaskSpec(TaskId.New(), "Plan the work.", AgentRole.Planner);

        Assert.True(BackgroundDispatchRunner.CanCompleteHungWrapperWithoutChangeEvidence(
            taskWithoutDispatch,
            hasPopulatedStandardOutput: true,
            hasSuccessfulWorkerResult: true,
            out var capabilityGapDiagnostic));
        Assert.Null(capabilityGapDiagnostic);

        var localScenario = RunHungReadOnlyRoleScenario(
            AgentRole.Reviewer,
            codexWrapper: false,
            BuildSuccessfulReadOnlyOutput(AgentRole.Reviewer),
            localDispatch: true);
        Assert.Equal(0, localScenario.Outcome.ProcessRecord.ExitCode);
        Assert.Equal(WorkTaskStatus.Completed, localScenario.Task.Status);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_unrecognized_role_capability_fails_closed")]
    public void BackgroundDispatchRunnerUnrecognizedRoleCapabilityFailsClosed()
    {
        var task = new TaskSpec(TaskId.New(), "Unknown role task.", (AgentRole)999);

        Assert.False(BackgroundDispatchRunner.CanCompleteHungWrapperWithoutChangeEvidence(
            task,
            hasPopulatedStandardOutput: true,
            hasSuccessfulWorkerResult: true,
            out var capabilityGapDiagnostic));
        Assert.Contains("HungWrapperUnrecognizedRoleCapability", capabilityGapDiagnostic, StringComparison.Ordinal);
    }

    [Xunit.Theory(DisplayName = "BackgroundDispatchRunner_hung_wrapper_completes_when_file_role_worktree_evidence_passes")]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void BackgroundDispatchRunnerHungWrapperCompletesWhenFileRoleWorktreeEvidencePasses(AgentRole role)
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var taskSpec = new TaskSpec(TaskId.New(), $"{role} task.", role);
    var goal = kernel.CreateGoal("Hung wrapper with evidence", [taskSpec]);
    var agent = new AgentDefinition(
        new AgentId($"codex-{role.ToString().ToLowerInvariant()}"),
        $"Codex {role}",
        role,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);

    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
    RunGit(worktree, ["add", "-A"], now.AddMinutes(-4));
    RunGit(worktree, ["commit", "-m", "Feature"], now.AddMinutes(-4));
    var head = ReadGit(worktree, ["rev-parse", "--short", "HEAD"]);

    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, "dev.out.log");
    var stderr = Path.Combine(logs, "dev.err.log");
    var exit = Path.Combine(logs, "dev.exit.txt");
    File.WriteAllText(stdout, "Implemented the change." + Environment.NewLine + WorkerResultBlock("feature.txt", "implemented feature", "Passed: 1", head));
    File.WriteAllText(stderr, "Tokens used: input=123 output=45");
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);

    var task = goal.Tasks.Single(t => t.RequiredRole == role);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", worktree, now.AddMinutes(-5)));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt", worktree, stdout, stderr, exit, now.AddMinutes(-5), null, null));

    var completed = new BackgroundDispatchRunner(clock, TimeSpan.FromMinutes(2), _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(0, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.True(File.Exists(exit));
    AssertExitCode(exit, 0);
    Assert.Contains("Wrapper process reaped", task.LastVerification!.StandardError, StringComparison.Ordinal);
    Assert.Contains("commits_after_dispatch=1", task.LastVerification.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_hung_claude_cli_wrapper_completes_when_worktree_evidence_passes")]
    public void BackgroundDispatchRunnerHungClaudeCliWrapperCompletesWhenWorktreeEvidencePasses()
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var taskSpec = new TaskSpec(TaskId.New(), "Developer task.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Hung claude-cli wrapper with evidence", [taskSpec]);
    var agent = new AgentDefinition(
        new AgentId("claude-developer"),
        "Claude Developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);

    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
    RunGit(worktree, ["add", "-A"], now.AddMinutes(-4));
    RunGit(worktree, ["commit", "-m", "Feature"], now.AddMinutes(-4));

    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, "dev.out.log");
    var stderr = Path.Combine(logs, "dev.err.log");
    var exit = Path.Combine(logs, "dev.exit.txt");
    File.WriteAllText(stdout, "Implemented the change.");
    File.WriteAllText(stderr, string.Empty);

    var task = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", "claude prompt", worktree, now.AddMinutes(-40)));
    var process = new TaskProcessRecord(999999, "claude prompt", worktree, stdout, stderr, exit, now.AddMinutes(-40), null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    // Heartbeat: childPid=null signals the worker has already exited; stalled progress
    // beyond postOutputIdleTimeout of 2 minutes triggers the hung-wrapper detector.
    WriteHeartbeat(process, now.AddMinutes(-31), now.AddMinutes(-31), "running", 0, 0, childPid: null);

    var completed = new BackgroundDispatchRunner(clock, TimeSpan.FromMinutes(2), _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(0, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.True(File.Exists(exit));
    AssertExitCode(exit, 0);
    Assert.Contains("Wrapper process reaped", task.LastVerification!.StandardError, StringComparison.Ordinal);
    Assert.Contains("commits_after_dispatch=1", task.LastVerification.StandardError, StringComparison.Ordinal);
}

    [Xunit.Theory(DisplayName = "BackgroundDispatchRunner_hung_wrapper_fails_when_file_role_worktree_evidence_missing")]
    [Xunit.InlineData(AgentRole.Developer)]
    [Xunit.InlineData(AgentRole.Tester)]
    public void BackgroundDispatchRunnerHungWrapperFailsWhenFileRoleWorktreeEvidenceMissing(AgentRole role)
{
    var root = CreateSeededDispatchRepository();
    var now = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var clock = new TestClock(now);
    var kernel = new AgentOrchestratorKernel();
    var taskSpec = new TaskSpec(TaskId.New(), $"{role} task.", role);
    var goal = kernel.CreateGoal("Hung wrapper without evidence", [taskSpec]);
    var agent = new AgentDefinition(
        new AgentId($"codex-{role.ToString().ToLowerInvariant()}"),
        $"Codex {role}",
        role,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);

    var worktree = GoalWorktrees.Ensure(root, goal.Id);

    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, "dev.out.log");
    var stderr = Path.Combine(logs, "dev.err.log");
    var exit = Path.Combine(logs, "dev.exit.txt");
    File.WriteAllText(
        stdout,
        "Completed role work." + Environment.NewLine +
        WorkerResultBlock("none", "focused verification", "pass - structured evidence", blockers: "none"));
    File.WriteAllText(stderr, "Tokens used: input=123 output=45");
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);

    var task = goal.Tasks.Single(t => t.RequiredRole == role);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt", worktree, now.AddMinutes(-5)));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "codex exec prompt", worktree, stdout, stderr, exit, now.AddMinutes(-5), null, null));

    var completed = new BackgroundDispatchRunner(clock, TimeSpan.FromMinutes(2), _ => true)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, completed.ExitCode);
    Assert.Equal(clock.UtcNow, completed.CompletedAt);
    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.True(File.Exists(exit));
    AssertExitCode(exit, 1);
    Assert.Contains("wrapper appears hung after codex final output", task.LastVerification!.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_developer_unchanged_dispatch_fails")]
    public void BackgroundDispatchRunnerDeveloperUnchangedDispatchFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the change.",
        string.Empty,
        clock);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("did not produce required relevant file-change evidence", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("branch=goal/", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("worktree=clean", task.LastVerification.StandardError, StringComparison.Ordinal);
    AssertExitCode(process.ExitCodePath, 0);
}

    [Xunit.Fact]
    public void BackgroundDispatchRunnerCompletedGitInspectionFailureFailsWithReceipt()
    {
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root,
            AgentRole.Developer,
            "Implemented and verified the change.",
            string.Empty,
            clock,
            worktree =>
            {
                File.WriteAllText(Path.Combine(worktree, "completed-change.txt"), "completed change");
                RunGit(worktree, ["add", "-A"], clock.UtcNow.AddMinutes(1));
                RunGit(worktree, ["commit", "-m", "Complete change"], clock.UtcNow.AddMinutes(1));
            });
        RunGit(process.WorkingDirectory, ["update-ref", "-d", $"refs/heads/{GoalWorktrees.BranchName(goal.Id)}"], clock.UtcNow.AddMinutes(2));
        var branchReceipt = GitCli.Run(process.WorkingDirectory, "branch", "--show-current");
        var headReceipt = GitCli.Run(process.WorkingDirectory, "rev-parse", "--short", "HEAD");
        Assert.Equal(0, branchReceipt.ExitCode);
        Assert.Equal(GoalWorktrees.BranchName(goal.Id), branchReceipt.Output.Trim());
        Assert.NotEqual(0, headReceipt.ExitCode);

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains("unavailable_reason=git-inspection-failed", task.LastVerification.StandardError, StringComparison.Ordinal);
        Assert.Contains("git_receipt=operation=head", task.LastVerification.StandardError, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_developer_no_change_rationale_without_source_change_fails")]
    public void BackgroundDispatchRunnerDeveloperNoChangeRationaleWithoutSourceChangeFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "NO_CHANGE: Existing code already satisfies the request.",
        string.Empty,
        clock);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("did not produce required relevant file-change evidence", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("changed_paths=none", task.LastVerification.StandardError, StringComparison.Ordinal);
    AssertExitCode(process.ExitCodePath, 0);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_developer_generated_noise_only_dispatch_fails")]
    public void BackgroundDispatchRunnerDeveloperGeneratedNoiseOnlyDispatchFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the change.",
        string.Empty,
        clock,
        worktree =>
        {
            var qwenDirectory = Path.Combine(worktree, ".qwen");
            Directory.CreateDirectory(qwenDirectory);
            File.WriteAllText(Path.Combine(qwenDirectory, "settings.json"), "{}");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Qwen settings noise"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("did not produce required relevant file-change evidence", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("commits_after_dispatch=1", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("changed_paths=.qwen/settings.json", task.LastVerification.StandardError, StringComparison.Ordinal);
    AssertExitCode(process.ExitCodePath, 0);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_tester_verification_only_clean_dispatch_passes")]
    public void BackgroundDispatchRunnerTesterVerificationOnlyCleanDispatchPasses()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "dotnet test --logger trx\r\nTRX 5/5 passed.\r\n" +
            WorkerResultBlock("none", "dotnet test --logger trx", "TRX 5/5 passed", "none"),
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run or attempt exact automated tests or manual smoke checks and record pass/fail evidence.");
    WriteHeartbeat(process, clock.UtcNow, clock.UtcNow, "completed", 256, 0, childPid: null, exitFileExists: true);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.False(task.LastVerification.StandardError.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
    Assert.False(task.LastVerification.StandardError.Contains("Dispatch failed with exit code 0", StringComparison.Ordinal));
    AssertExitCode(process.ExitCodePath, 0);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_tester_verification_only_exit_zero_blocker_fails")]
    public void BackgroundDispatchRunnerTesterVerificationOnlyExitZeroBlockerFails()
    {
        const string blocker = "worker-sweep-command-name-safety-list";
        var root = CreateSeededDispatchRepository();
        var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
        var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
            root,
            AgentRole.Tester,
            "dotnet test --logger trx\r\nTRX 5/5 passed.\r\n" +
                WorkerResultBlock("none", "dotnet test --logger trx", "pass - TRX 5/5 passed", blockers: blocker),
            string.Empty,
            clock,
            taskDescription: "Verify behavior with automated and manual checks",
            verificationPlan: "Run or attempt exact automated tests or manual smoke checks and record pass/fail evidence.");
        WriteHeartbeat(process, clock.UtcNow, clock.UtcNow, "completed", 256, 0, childPid: null, exitFileExists: true);

        new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.Equal(0, task.LastVerification!.ExitCode);
        Assert.Contains(blocker, task.LastVerification.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("rule=tester-worker-result-blocker", StringComparison.Ordinal));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskFailed &&
            evt.Message.Contains(blocker, StringComparison.Ordinal));
        Assert.DoesNotContain(goal.Timeline, evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskCompleted);
        AssertExitCode(process.ExitCodePath, 0);
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_tester_with_test_commit_and_green_trx_completes")]
    public void BackgroundDispatchRunnerTesterWithTestCommitAndGreenTrxCompletes()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "Added a focused regression test and ran it.\r\n" +
            WorkerResultBlock("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs", "dotnet test --logger trx", "TRX 5/5 passed", blockers: "none"),
        string.Empty,
        clock,
        worktree =>
        {
            var testsDirectory = Path.Combine(worktree, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
            Directory.CreateDirectory(testsDirectory);
            File.WriteAllText(Path.Combine(testsDirectory, "WorkerDispatchTests.cs"), "test-only regression");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Add tester regression"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        },
        taskDescription: "Add focused regression tests for the dispatch guard",
        verificationPlan: "Update tests and run the focused dispatch guard coverage.");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.True(task.LastVerification.HasCommittedChanges);
    Assert.False(task.LastVerification.StandardError.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
    Assert.False(task.LastVerification.StandardError.Contains("Dispatch failed with exit code 0", StringComparison.Ordinal));
    AssertExitCode(process.ExitCodePath, 0);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_tester_clean_dispatch_with_worker_result_without_passing_evidence_fails")]
    public void BackgroundDispatchRunnerTesterCleanDispatchWithWorkerResultWithoutPassingEvidenceFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "structured verification of behavioral contract\nconfidence: high\nblockers: none\nEND_WORKER_RESULT",
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.");

    // WORKER_RESULT shape alone is not passing evidence. A non-zero clean Tester dispatch must remain
    // failed unless the output contains real verification evidence.
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("did not produce required relevant file-change evidence", task.LastVerification.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_tester_clean_dispatch_without_passing_evidence_fails")]
    public void BackgroundDispatchRunnerTesterCleanDispatchWithoutPassingEvidenceFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "Ran dotnet test --filter OrchestratorHealthInspector.",
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run or attempt exact automated tests or manual smoke checks and record pass/fail evidence.");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("did not produce required relevant file-change evidence", task.LastVerification.StandardError, StringComparison.Ordinal);
    AssertExitCode(process.ExitCodePath, 0);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_tester_expected_file_change_with_green_tests_completes")]
    public void BackgroundDispatchRunnerTesterExpectedFileChangeWithGreenTestsCompletes()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "Added focused regression tests and ran dotnet test --logger trx.\r\nTRX 5/5 passed.",
        string.Empty,
        clock,
        taskDescription: "Add focused regression tests for the dispatch guard",
        verificationPlan: "Update tests and run the focused dispatch guard coverage.");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.False(task.LastVerification.StandardError.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
    Assert.False(task.LastVerification.StandardError.Contains("Dispatch failed with exit code 0", StringComparison.Ordinal));
    AssertExitCode(process.ExitCodePath, 0);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_dirty_worktree_without_commit_fails")]
    public void BackgroundDispatchRunnerFileRoleDirtyWorktreeWithoutCommitFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "Verified and updated a test.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "uncommitted"));

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("worktree=dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("status_short=?? dirty.txt", task.LastVerification.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_does_not_activate_commit_path_from_provider_display_name")]
    public void BackgroundDispatchRunnerDoesNotActivateCommitPathFromProviderDisplayName()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the feature and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "implemented but not committed"),
        workerName: "opaque-worker",
        command: "opaque worker prompt",
        providerName: "OpenAI");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains("feature.txt", ReadGit(worktree, ["status", "--short"]), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_receipt_only_dirty_worktree_is_clean_for_commit")]
    public void BackgroundDispatchRunnerReceiptOnlyDirtyWorktreeIsCleanForCommit()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "NO_CHANGE: Existing implementation already satisfies the request.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, WorkerSandboxPreparer.ReceiptFileName), "{}"));

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.DoesNotContain("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.DoesNotContain("Orchestrator committed the worker's verified worktree edits", task.LastVerification.StandardError, StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Equal($"?? {WorkerSandboxPreparer.ReceiptFileName}", ReadGit(worktree, ["status", "--short"]));
    Assert.Equal(string.Empty, ReadGit(worktree, ["ls-files", "--", WorkerSandboxPreparer.ReceiptFileName]));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_isolation_lease_only_dirty_worktree_is_clean_for_commit")]
    public void BackgroundDispatchRunnerIsolationLeaseOnlyDirtyWorktreeIsCleanForCommit()
{
    using var isolatedDotnetRoot = UseFixtureIsolatedDotnetRoot();
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "NO_CHANGE: Existing implementation already satisfies the request.",
        string.Empty,
        clock,
        WriteIsolationLeaseArtifacts);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.DoesNotContain("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.DoesNotContain("Orchestrator committed the worker's verified worktree edits", task.LastVerification.StandardError, StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.False(GitCli.IsWorktreeDirty(worktree));
    Assert.True(File.Exists(Path.Combine(worktree, "i", "goals", "infra-partition", "lease", "lease.json")));
    Assert.True(File.Exists(Path.Combine(worktree, "i", "slots", "slot-0", "lease.execution.lock")));
    Assert.Equal("1", ReadGit(worktree, ["rev-list", "--count", "HEAD"]));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_nonzero_exit_dirty_unverified_stays_failed")]
    public void BackgroundDispatchRunnerFileRoleNonZeroExitDirtyUnverifiedStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Started editing but the provider connection dropped before anything was verified.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "half-done, unverified"));

    // Dirty but UNVERIFIED on a non-zero exit: the orchestrator must NOT blindly commit unproven work.
    // It stays failed so it surfaces for retry/escalation.
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.False(task.LastVerification.StandardError.Contains("Orchestrator committed", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_verification_only_tester_nonzero_exit_with_evidence_fails")]
    public void BackgroundDispatchRunnerVerificationOnlyTesterNonZeroExitWithEvidenceFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "Reviewed the implementation and ran the focused suite.\r\nPassed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4.",
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.");

    // Green-looking verification text does not override a non-zero worker exit; the round must surface
    // for retry/escalation instead of being converted to success.
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.False(
        task.LastVerification.StandardError.Contains("Accepted on verification evidence despite a non-zero worker exit", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_clean_worktree_nonzero_exit_without_evidence_stays_failed")]
    public void BackgroundDispatchRunnerCleanWorktreeNonZeroExitWithoutEvidenceStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "Provider connection dropped before any checks ran.",
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.");

    // Clean worktree + non-zero exit but NO verification evidence: a genuine failure (the worker never
    // verified anything), not exit-code noise. Must stay failed.
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_short_zero_byte_streams_nonzero_root_exit_records_launch_retry")]
    public void BackgroundDispatchRunnerShortZeroByteStreamsRecordsLaunchRetry()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        string.Empty,
        string.Empty,
        clock,
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.",
        childExitCode: 23);
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Equal(999999, task.LastProcess!.ProcessId);
    Assert.Equal(1, task.LastProcess.ExitCode);
    Assert.Equal(888888, task.LastProcess.ChildProcessId);
    Assert.Equal(23, task.LastProcess.ChildExitCode);
    Assert.Equal(888888, task.LastVerification.ChildProcessId);
    Assert.Equal(23, task.LastVerification.ChildExitCode);
    Assert.Equal(1, task.EmptyOutputRetryCount);
    Assert.Equal(
        DispatchOutcomeKind.LaunchFailure,
        DispatchFailureClassifier.Classify(task, task.LastVerification).Kind);
    Assert.True(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(task.LastVerification));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_exit_zero_empty_streamed_output_with_worker_result_file_without_passing_evidence_fails")]
    public void BackgroundDispatchRunnerExitZeroEmptyStreamedOutputWithWorkerResultFileWithoutPassingEvidenceFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        string.Empty,
        string.Empty,
        clock,
        worktree => File.WriteAllText(
            Path.Combine(worktree, "WORKER_RESULT.md"),
            WorkerResultBlock("none", "dotnet test --filter BufferedWorker", "not-run")),
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.True(task.LastVerification.WorkerResultPresent);
    Assert.Equal(0, task.EmptyOutputRetryCount);
    Assert.False(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(task.LastVerification));
    AssertExitCode(process.ExitCodePath, 0);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_exit_zero_empty_streamed_output_with_committed_change_completes")]
    public void BackgroundDispatchRunnerExitZeroEmptyStreamedOutputWithCommittedChangeCompletes()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        string.Empty,
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.True(task.LastVerification.HasCommittedChanges);
    Assert.Equal(0, task.EmptyOutputRetryCount);
    Assert.False(DispatchFailureClassifier.IsTransientEmptyOutputDispatchFlake(task.LastVerification));
    AssertExitCode(process.ExitCodePath, 0);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_nonzero_exit_with_artifacts_does_not_complete")]
    public void BackgroundDispatchRunnerNonZeroExitWithArtifactsDoesNotComplete()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        string.Empty,
        string.Empty,
        clock,
        worktree => File.WriteAllText(
            Path.Combine(worktree, "WORKER_RESULT.md"),
            WorkerResultBlock("none", "dotnet test --filter BufferedWorker", "not-run")),
        taskDescription: "Verify behavior with automated and manual checks",
        verificationPlan: "Run the focused tests and confirm the acceptance criteria.");
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.True(task.LastVerification.WorkerResultPresent);
    Assert.NotEqual(
        DispatchOutcomeKind.VerifiedSuccess,
        DispatchFailureClassifier.Classify(task, task.LastVerification).Kind);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_committed_change_passes")]
    public void BackgroundDispatchRunnerFileRoleWithCommittedChangePasses()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation." + Environment.NewLine + WorkerResultBlock("feature.txt", "implemented feature", "Passed: 1"),
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_missing_policy_test_evidence_passes_advisory")]
    public void BackgroundDispatchRunnerFileRoleMissingPolicyTestEvidencePassesAdvisory()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation." + Environment.NewLine + WorkerResultBlock("src/Feature.cs", "implemented feature", "not run"),
        string.Empty,
        clock,
        worktree =>
        {
            Directory.CreateDirectory(Path.Combine(worktree, "src"));
            File.WriteAllText(Path.Combine(worktree, "src", "Feature.cs"), "public sealed class Feature {}");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    // WORKER_RESULT is advisory: a relevant commit on a clean worktree is sufficient at
    // dispatch time. Test evidence is enforced by the acceptance run, not the self-report.
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_structured_pass_token_ignores_failed_words_in_tests_prose")]
    public void BackgroundDispatchRunnerStructuredPassTokenIgnoresFailedWordsInTestsProse()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation." + Environment.NewLine + WorkerResultBlock(
            "src/Feature.cs",
            ".\\scripts\\Invoke-WorkerBuildCheck.ps1 src\\Feature.csproj",
            "pass - build check verifies previously failed timeout path",
            blockers: "none - retry blocker fixed"),
        string.Empty,
        clock,
        worktree =>
        {
            Directory.CreateDirectory(Path.Combine(worktree, "src"));
            File.WriteAllText(Path.Combine(worktree, "src", "Feature.cs"), "public sealed class Feature {}");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "WorkerResultParser_successful_result_rejects_no_opener_files_plus_tests_only")]
    public void WorkerResultParserSuccessfulResultRejectsNoOpenerFilesPlusTestsOnly()
{
    var output = """
        Completed work summary:
        files: src/Feature.cs
        tests: Passed: 2, Failed: 0
        """;

    var parsed = WorkerResultParser.TryParseSuccessfulResult(output, out _, out var diagnostic);

    Assert.False(parsed);
    Assert.Contains("missing WORKER_RESULT field(s): commands, blockers, model_fit, skills, confidence.", diagnostic);
}

    [Xunit.Fact(DisplayName = "WorkerResultParser_successful_result_accepts_complete_no_opener_field_scan")]
    public void WorkerResultParserSuccessfulResultAcceptsCompleteNoOpenerFieldScan()
{
    var output = """
        Completed work summary:
        files: src/Feature.cs
        commands: dotnet test --filter WorkerDispatch
        tests: Passed: 2, Failed: 0
        blockers: none
        model_fit: OpenAI/gpt-5.5 - adequate - parser regression
        skills: dotnet-windows-build-hygiene
        confidence: high
        """;

    var parsed = WorkerResultParser.TryParseSuccessfulResult(output, out var fields, out var diagnostic);

    Assert.True(parsed, diagnostic);
    Assert.Equal("src/Feature.cs", fields["files"]);
    Assert.Equal("none", fields["blockers"]);
}

    [Xunit.Fact(DisplayName = "WorkerResultParser_successful_result_uses_structured_pass_token_not_tests_prose")]
    public void WorkerResultParserSuccessfulResultUsesStructuredPassTokenNotTestsProse()
{
    var output = WorkerResultBlock(
        "src/Feature.cs",
        "dotnet test --filter WorkerDispatch",
        "pass - verified retry path that previously failed and timed out",
        blockers: "none - no blockers after retry");

    var parsed = WorkerResultParser.TryParseSuccessfulResult(output, out var fields, out var diagnostic);

    Assert.True(parsed, diagnostic);
    Assert.Equal("pass - verified retry path that previously failed and timed out", fields["tests"]);
    Assert.Equal("none - no blockers after retry", fields["blockers"]);
}

    [Xunit.Fact(DisplayName = "WorkerResultParser_successful_result_treats_blockers_as_advisory_when_tests_pass")]
    public void WorkerResultParserSuccessfulResultTreatsBlockersAsAdvisoryWhenTestsPass()
{
    var output = WorkerResultBlock(
        "src/Feature.cs",
        "dotnet test --filter WorkerDispatch",
        "Passed: 2, Failed: 0",
        blockers: "live loop evidence must be run by the operator");

    var parsed = WorkerResultParser.TryParseSuccessfulResult(output, out var fields, out var diagnostic);

    Assert.True(parsed, diagnostic);
    Assert.Equal("live loop evidence must be run by the operator", fields["blockers"]);
}

    [Xunit.Fact(DisplayName = "WorkerResultParser_successful_result_rejects_failing_tests_with_blockers")]
    public void WorkerResultParserSuccessfulResultRejectsFailingTestsWithBlockers()
{
    var output = WorkerResultBlock(
        "src/Feature.cs",
        "dotnet test --filter WorkerDispatch",
        "fail - timed out",
        blockers: "full Infrastructure no-build timed out at 214s");

    var parsed = WorkerResultParser.TryParseSuccessfulResult(output, out _, out var diagnostic);

    Assert.False(parsed);
    Assert.Contains("tests reported failure", diagnostic, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_failed_worker_build_check_fails_round_with_error_feedback")]
    public void BackgroundDispatchRunnerFailedWorkerBuildCheckFailsRoundWithErrorFeedback()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-07-06T12:00:00Z"));
    var buildError = "FAIL build: 1 error(s) (Invoke-WorkerBuildCheck) src/Feature.cs(10,20): error CS1002: ; expected";
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the change." + Environment.NewLine + WorkerResultBlock(
            "feature.txt",
            ".\\scripts\\Invoke-WorkerBuildCheck.ps1 src\\Feature.csproj",
            buildError),
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature"));

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.NotNull(task.LastVerification);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("WORKER_RESULT reported failed worker build check", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("CS1002", task.LastVerification.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_without_worker_result_contract_passes_advisory")]
    public void BackgroundDispatchRunnerFileRoleWithoutWorkerResultContractPassesAdvisory()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation.",
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    // A missing WORKER_RESULT block no longer fails the dispatch: git ground truth (the
    // relevant committed change on a clean worktree) carries the substance. The block is advisory.
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_partial_worker_result_contract_passes_advisory")]
    public void BackgroundDispatchRunnerFileRoleWithPartialWorkerResultContractPassesAdvisory()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var output = """
        Committed implementation.
        WORKER_RESULT:
        files: feature.txt
        commands: git add -A; git commit -m Feature
        tests: Passed: 1
        commit: abc123
        blockers: none
        model_fit: deterministic fixture - adequate - contract validation
        confidence: high
        END_WORKER_RESULT
        """;
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        output,
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    // A partial/odd-shaped WORKER_RESULT (here missing the skills field) no longer fails the
    // dispatch — field shape is advisory; the relevant committed change is the substance.
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_fake_commit_sha_passes_advisory")]
    public void BackgroundDispatchRunnerFileRoleWithFakeCommitShaPassesAdvisory()
{
    // The worker reports a fake commit SHA "abc123", but it actually committed a relevant
    // change on a clean worktree. The self-reported commit is advisory and no longer checked;
    // git ground truth (a relevant commit after dispatch) is the substance, so this passes.
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var output = """
        Committed implementation.
        WORKER_RESULT:
        files: feature.txt
        commands: git add -A; git commit -m Feature
        tests: Passed: 1
        commit: abc123
        blockers: none
        model_fit: deterministic fixture - adequate - contract validation
        skills: dotnet-windows-build-hygiene
        confidence: high
        """;
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        output,
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_worker_result_file_mismatch_passes_advisory")]
    public void BackgroundDispatchRunnerFileRoleWithWorkerResultFileMismatchPassesAdvisory()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation." + Environment.NewLine + WorkerResultBlock("other.txt", "implemented feature", "Passed: 1"),
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    // The worker's files field (other.txt) disagrees with what git shows changed (feature.txt).
    // The self-reported file list is advisory and no longer cross-checked; the relevant
    // committed change is the substance, so this passes.
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_apply_refresh_orchestrator_result_commit_completes_stale_verification_shape")]
    public void BackgroundDispatchRunnerApplyRefreshOrchestratorResultCommitCompletesStaleVerificationShape()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-07-08T13:16:47Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        string.Empty,
        string.Empty,
        clock);
    kernel.RecordDispatchBaseCommit(goal.Id, task.Id, "29edee5c");
    var completedProcess = process with { CompletedAt = clock.UtcNow.AddSeconds(30), ExitCode = 0 };
    var verification = new TaskVerificationRecord(
        process.Command,
        process.WorkingDirectory,
        0,
        WorkerResultBlock("src/Feature.cs", "implemented feature", "dotnet test --filter WorkerDispatchTests passed"),
        string.Empty,
        completedProcess.CompletedAt.Value,
        StandardOutputPath: process.StandardOutputPath,
        StandardErrorPath: process.StandardErrorPath,
        WorkerResultPresent: true,
        HasCommittedChanges: false);
    var outcome = new DispatchRefreshOutcome(
        completedProcess,
        verification,
        ResultCommit: "ce5e35c1",
        ResultCommitProvenance: "orchestrator");

    BackgroundDispatchRunner.ApplyRefreshOutcome(kernel, goal.Id, task.Id, outcome);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.True(task.LastVerification.HasCommittedChanges);
    Assert.Equal("ce5e35c1", task.LastDispatch?.ResultCommit);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("TaskOutputCommitted", StringComparison.Ordinal) &&
        evt.Message.Contains("sha=ce5e35c1", StringComparison.Ordinal) &&
        evt.Message.Contains("provenance=orchestrator", StringComparison.Ordinal));
    Assert.DoesNotContain(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskFailed &&
        evt.Message.Contains("Dispatch failed with exit code 0", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_exit_zero_clean_worker_commit_records_worker_provenance_without_double_commit")]
    public void BackgroundDispatchRunnerExitZeroCleanWorkerCommitRecordsWorkerProvenanceWithoutDoubleCommit()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation." + Environment.NewLine + WorkerResultBlock("feature.txt", "implemented feature", "Passed: 1"),
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
        });
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var workerCommit = ReadGit(worktree, ["rev-parse", "HEAD"]);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.True(task.LastVerification.HasCommittedChanges);
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Equal(workerCommit, ReadGit(worktree, ["rev-parse", "HEAD"]));
    Assert.Equal("1", ReadGit(worktree, ["rev-list", "--count", "HEAD~1..HEAD"]));
    Assert.Equal(workerCommit, task.LastDispatch?.ResultCommit);
    Assert.DoesNotContain("Orchestrator committed the worker's verified worktree edits", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("TaskOutputCommitted", StringComparison.Ordinal) &&
        evt.Message.Contains($"sha={workerCommit}", StringComparison.Ordinal) &&
        evt.Message.Contains("provenance=worker", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_exit_zero_clean_no_change_does_not_create_empty_commit")]
    public void BackgroundDispatchRunnerExitZeroCleanNoChangeDoesNotCreateEmptyCommit()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "No file changes were needed.",
        string.Empty,
        clock);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var startingHead = ReadGit(worktree, ["rev-parse", "HEAD"]);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.False(task.LastVerification.HasCommittedChanges);
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Equal(startingHead, ReadGit(worktree, ["rev-parse", "HEAD"]));
    Assert.Equal("1", ReadGit(worktree, ["rev-list", "--count", "HEAD"]));
    Assert.DoesNotContain(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("TaskOutputCommitted", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_commit_and_residual_dirty_worktree_commits_residual")]
    public void BackgroundDispatchRunnerFileRoleWithCommitAndResidualDirtyWorktreeCommitsResidual()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation.",
        SandboxPrepCompleteEvent(),
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            File.AppendAllText(Path.Combine(worktree, "seed.txt"), "leftover");
        },
        sandboxLowIntegrity: true);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.True(
        task.Status == WorkTaskStatus.Completed,
        task.LastVerification?.StandardError ?? "missing verification");
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains("Orchestrator committed the worker's verified worktree edits", task.LastVerification.StandardError, StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Equal("Developer task.: Committed implementation.", ReadGit(worktree, ["log", "-1", "--pretty=%s"]));
    Assert.Equal("2", ReadGit(worktree, ["rev-list", "--count", "HEAD~2..HEAD"]));
    Assert.Equal("seed.txt", ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_commit_on_behalf_stages_real_files_not_isolation_leases")]
    public void BackgroundDispatchRunnerCommitOnBehalfStagesRealFilesNotIsolationLeases()
{
    using var isolatedDotnetRoot = UseFixtureIsolatedDotnetRoot();
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        string.Join(
            "\n",
            "WORKER_RESULT:",
            "files: seed.txt",
            "commands: focused regression",
            "tests: pass - focused regression",
            "commit: none",
            "blockers: none",
            "model_fit: test - adequate - commit-on-behalf",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT"),
        string.Empty,
        clock,
        worktree =>
        {
            File.AppendAllText(Path.Combine(worktree, "seed.txt"), "real worker edit");
            WriteIsolationLeaseArtifacts(worktree);
        });

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains("Orchestrator committed the worker's verified worktree edits", task.LastVerification.StandardError, StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var status = ReadGit(worktree, ["status", "--short"]);
    Assert.DoesNotContain("seed.txt", status, StringComparison.Ordinal);
    Assert.False(GitCli.IsWorktreeDirty(worktree));
    Assert.True(File.Exists(Path.Combine(worktree, "i", "goals", "infra-partition", "lease", "lease.json")));
    Assert.True(File.Exists(Path.Combine(worktree, "i", "slots", "slot-0", "lease.execution.lock")));
    Assert.Equal("seed.txt", ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]));
    Assert.DoesNotContain("i/goals", ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_residual_commit_failure_surfaces_git_error_and_dirty_summary")]
    public void BackgroundDispatchRunnerFileRoleResidualCommitFailureSurfacesGitErrorAndDirtySummary()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation.",
        SandboxPrepCompleteEvent(),
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            File.AppendAllText(Path.Combine(worktree, "seed.txt"), "leftover");
        },
        sandboxLowIntegrity: true);
    var hookPath = Path.Combine(root, ".git", "hooks", "pre-commit");
    File.WriteAllText(
        hookPath,
        "#!/bin/sh\n" +
        "echo blocked residual commit >&2\n" +
        "exit 42\n");
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(
            hookPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains(
        "Orchestrator commit-on-behalf git command failed",
        task.LastVerification.StandardError,
        StringComparison.Ordinal);
    Assert.Contains("operation=commit", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("Developer/Tester dispatch exited 0 but left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("Commit-on-behalf failure is retryable; worktree preserved.", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("operator_action=inspect the preserved worktree, resolve the named git failure, then rerun refresh-dispatch for this task", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Equal(DispatchOutcomeKind.DirtyWorktreeRecoverable, DispatchFailureClassifier.Classify(task, task.LastVerification).Kind);
    Assert.Contains("status_short=M seed.txt", task.LastVerification.StandardError, StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains("seed.txt", ReadGit(worktree, ["status", "--short"]), StringComparison.Ordinal);
    Assert.Equal("1", ReadGit(worktree, ["rev-list", "--count", "HEAD~1..HEAD"]));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_nonzero_exit_with_commit_and_residual_dirty_worktree_fails")]
    public void BackgroundDispatchRunnerFileRoleNonZeroExitWithCommitAndResidualDirtyWorktreeFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Committed implementation.",
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature");
            RunGit(worktree, ["add", "-A"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            RunGit(worktree, ["commit", "-m", "Feature"], DateTimeOffset.Parse("2026-06-02T12:01:00Z"));
            File.AppendAllText(Path.Combine(worktree, "seed.txt"), "leftover");
        },
        workerName: "claude-cli",
        command: "claude prompt");
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains("seed.txt", ReadGit(worktree, ["status", "--short"]), StringComparison.Ordinal);
    Assert.Equal("1", ReadGit(worktree, ["rev-list", "--count", "HEAD~1..HEAD"]));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_no_change_rationale_and_clean_worktree_passes")]
    public void BackgroundDispatchRunnerFileRoleWithNoChangeRationaleAndCleanWorktreePasses()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "NO_CHANGE: Existing focused test already covers this behavior." + Environment.NewLine +
            WorkerResultBlock("none", "inspected existing tests", "pass - existing focused test covers behavior", "none"),
        string.Empty,
        clock);
    WriteHeartbeat(task.LastProcess!, clock.UtcNow, clock.UtcNow, "completed", 128, 0, childPid: null, exitFileExists: true);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_with_no_change_rationale_and_dirty_worktree_fails")]
    public void BackgroundDispatchRunnerFileRoleWithNoChangeRationaleAndDirtyWorktreeFails()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Tester,
        "NO_CHANGE: Existing focused test already covers this behavior.",
        string.Empty,
        clock,
        worktree => File.AppendAllText(Path.Combine(worktree, "seed.txt"), "leftover"));

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Contains("status_short=M seed.txt", task.LastVerification.StandardError, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_non_file_role_completion_is_unchanged")]
    public void BackgroundDispatchRunnerNonFileRoleCompletionIsUnchanged()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Reviewer,
        "Reviewed implementation evidence.",
        string.Empty,
        clock,
        worktree => File.AppendAllText(Path.Combine(worktree, "seed.txt"), "leftover"));

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_no_change_role_worker_result_blocker_is_advisory_when_tests_do_not_fail")]
    public void BackgroundDispatchRunnerNoChangeRoleWorkerResultBlockerIsAdvisoryWhenTestsDoNotFail()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Researcher,
        WorkerResultBlock(
            "none",
            "reviewed deterministic evidence",
            "pass",
            "none",
            blockers: "live conduct evidence deferred to operator post-landing"),
        string.Empty,
        clock);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(
        "advisory WORKER_RESULT blocker",
        goal.Timeline.Last(evt => evt.Kind == ProgressKind.TaskCompleted).Message,
        StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "DispatchDiagnostic_exit0_with_output_yields_success_record_with_all_fields")]
    public void DispatchDiagnosticExit0WithOutputYieldsSuccessRecordWithAllFields()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "abc12345-def67890-20260623.out.log");
    var stderr = Path.Combine(root, "abc12345-def67890-20260623.err.log");
    var exit = Path.Combine(root, "abc12345-def67890-20260623.exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-23T10:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Test success diagnostic record", [new TaskSpec(TaskId.New(), "Research the work", AgentRole.Researcher)]);
    var agent = new AgentDefinition(
        new AgentId("test-researcher"),
        "Test Researcher",
        AgentRole.Researcher,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    File.WriteAllText(stdout, "Worker completed the task successfully.");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "echo done", root, stdout, stderr, exit, clock.UtcNow, null, null));
    var spy = new CaptureDiagnosticWriter();

    new BackgroundDispatchRunner(clock, isStillRunning: _ => false, diagnosticWriter: spy)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, spy.Records.Count);
    var record = spy.Records.Single();
    Assert.Equal(goal.Id.Value, record.GoalId);
    Assert.Equal(task.Id.Value, record.TaskId);
    Assert.Equal("abc12345-def67890-20260623", record.Prefix);
    Assert.Equal(0, record.ExitCode);
    Assert.Equal(stdout, record.OutputPath);
    Assert.True(record.FileExists);
    Assert.True(record.FileLen > 0);
    Assert.True(record.ReadLen > 0);
    Assert.Equal("success", record.Classification);
    Assert.True(record.Reason.Contains("exit 0", StringComparison.OrdinalIgnoreCase));
    Assert.NotEmpty(record.Timestamp);
}

    [Xunit.Fact(DisplayName = "DispatchDiagnostic_exit1_zero_byte_streams_yields_launch_failure_record")]
    public void DispatchDiagnosticExit1EmptyOutputYieldsLaunchFailureRecord()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "abc12345-def67890-20260623.out.log");
    var stderr = Path.Combine(root, "abc12345-def67890-20260623.err.log");
    var exit = Path.Combine(root, "abc12345-def67890-20260623.exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-23T10:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Test genuine-failure diagnostic record", [new TaskSpec(TaskId.New(), "Plan the work", AgentRole.Planner)]);
    var agent = new AgentDefinition(
        new AgentId("test-planner"),
        "Test Planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "1");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "echo done", root, stdout, stderr, exit, clock.UtcNow, null, null));
    var spy = new CaptureDiagnosticWriter();

    new BackgroundDispatchRunner(clock, isStillRunning: _ => false, diagnosticWriter: spy)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, spy.Records.Count);
    var record = spy.Records.Single();
    Assert.Equal(1, record.ExitCode);
    Assert.Equal(0L, record.FileLen);
    Assert.Equal(0L, record.ReadLen);
    Assert.Equal(0L, record.StderrLen);
    Assert.Equal("launch-failure", record.Classification);
}

    [Xunit.Fact(DisplayName = "DispatchDiagnostic_exit1_rate_limit_in_stderr_yields_rate_limited_record")]
    public void DispatchDiagnosticExit1RateLimitInStderrYieldsRateLimitedRecord()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "abc12345-def67890-20260623.out.log");
    var stderr = Path.Combine(root, "abc12345-def67890-20260623.err.log");
    var exit = Path.Combine(root, "abc12345-def67890-20260623.exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-23T10:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Test rate-limited diagnostic record", [new TaskSpec(TaskId.New(), "Plan the work", AgentRole.Planner)]);
    var agent = new AgentDefinition(
        new AgentId("test-planner"),
        "Test Planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex to purchase more credits or try again at 5:00 PM.");
    File.WriteAllText(exit, "1");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "echo done", root, stdout, stderr, exit, clock.UtcNow, null, null));
    var spy = new CaptureDiagnosticWriter();

    new BackgroundDispatchRunner(clock, isStillRunning: _ => false, diagnosticWriter: spy)
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(1, spy.Records.Count);
    var record = spy.Records.Single();
    Assert.Equal(1, record.ExitCode);
    Assert.Equal("rate-limited", record.Classification);
    Assert.True(record.Reason.Contains("usage limit", StringComparison.OrdinalIgnoreCase));
}

    [Xunit.Fact(DisplayName = "DispatchDiagnostic_exception_in_writer_does_not_propagate_to_caller")]
    public void DispatchDiagnosticExceptionInWriterDoesNotPropagateToCaller()
{
    var root = CreateTempDirectory();
    var stdout = Path.Combine(root, "abc12345-def67890-20260623.out.log");
    var stderr = Path.Combine(root, "abc12345-def67890-20260623.err.log");
    var exit = Path.Combine(root, "abc12345-def67890-20260623.exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-23T10:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Test diagnostic exception swallowing", [new TaskSpec(TaskId.New(), "Research the work", AgentRole.Researcher)]);
    var agent = new AgentDefinition(
        new AgentId("test-researcher"),
        "Test Researcher",
        AgentRole.Researcher,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    File.WriteAllText(stdout, "Worker completed.");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "echo done", root, stdout, stderr, exit, clock.UtcNow, null, null));

    var completed = new BackgroundDispatchRunner(clock, isStillRunning: _ => false, diagnosticWriter: new ThrowingDiagnosticWriter())
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(0, completed.ExitCode);
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
}

    [Xunit.Fact(DisplayName = "DispatchDiagnostic_FileDiagnosticWriter_produces_parseable_jsonl_with_all_required_fields")]
    public void DispatchDiagnosticFileDiagnosticWriterProducesParseableJsonlWithAllRequiredFields()
{
    var root = CreateTempDirectory();
    var logDir = Path.Combine(root, "logs");
    Directory.CreateDirectory(logDir);
    var stdout = Path.Combine(logDir, "abc12345-def67890-20260623.out.log");
    var stderr = Path.Combine(logDir, "abc12345-def67890-20260623.err.log");
    var exit = Path.Combine(logDir, "abc12345-def67890-20260623.exit.txt");
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-23T10:00:00Z"));
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Test FileDiagnosticWriter JSONL output", [new TaskSpec(TaskId.New(), "Research the work", AgentRole.Researcher)]);
    var agent = new AgentDefinition(
        new AgentId("test-researcher"),
        "Test Researcher",
        AgentRole.Researcher,
        new ModelProfile("OpenAI", "gpt-5", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    File.WriteAllText(stdout, "Worker produced output.");
    File.WriteAllText(stderr, string.Empty);
    File.WriteAllText(exit, "0");
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("local", "echo done", root, clock.UtcNow));
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(999999, "echo done", root, stdout, stderr, exit, clock.UtcNow, null, null));
    WriteHeartbeat(
        task.LastProcess!,
        clock.UtcNow.AddSeconds(-5),
        clock.UtcNow.AddSeconds(-5),
        "exiting",
        stdoutBytes: 23,
        stderrBytes: 0,
        childPid: 123456,
        ownedPids: [999999, 123456],
        exitFileExists: true);

    new BackgroundDispatchRunner(clock, isStillRunning: _ => false, diagnosticWriter: new FileDiagnosticWriter())
        .RefreshLatestProcess(kernel, goal.Id, task.Id);

    var logPath = Path.Combine(logDir, "dispatch-diagnostics.jsonl");
    Assert.True(File.Exists(logPath));
    var line = File.ReadAllLines(logPath).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
    Assert.NotNull(line);
    var doc = JsonDocument.Parse(line!);
    var root2 = doc.RootElement;
    Assert.True(root2.TryGetProperty("goalId", out _));
    Assert.True(root2.TryGetProperty("taskId", out _));
    Assert.True(root2.TryGetProperty("prefix", out _));
    Assert.True(root2.TryGetProperty("exitCode", out _));
    Assert.True(root2.TryGetProperty("outputPath", out _));
    Assert.True(root2.TryGetProperty("fileExists", out _));
    Assert.True(root2.TryGetProperty("fileLen", out _));
    Assert.True(root2.TryGetProperty("readLen", out _));
    Assert.True(root2.TryGetProperty("stderrLen", out _));
    Assert.True(root2.TryGetProperty("classification", out var cls));
    Assert.Equal("success", cls.GetString());
    Assert.True(root2.TryGetProperty("reason", out _));
    Assert.True(root2.TryGetProperty("timestamp", out _));
    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.NotNull(task.LastVerification);
    Assert.True(root2.TryGetProperty("dispatchState", out var state));
    Assert.Equal("Completed", state.GetProperty("kind").GetString());
    Assert.Equal("none", state.GetProperty("recommendedAction").GetString());
    Assert.True(state.TryGetProperty("processTree", out var processTree));
    Assert.Equal(999999, processTree.GetProperty("wrapperProcessId").GetInt32());
    Assert.Equal(123456, processTree.GetProperty("childProcessId").GetInt32());
    Assert.Contains(
        processTree.GetProperty("ownedProcessIds").EnumerateArray(),
        pid => pid.GetInt32() == 123456);
    Assert.True(state.TryGetProperty("artifacts", out var artifacts));
    Assert.True(artifacts.GetProperty("standardOutputExists").GetBoolean());
    Assert.True(artifacts.GetProperty("exitCodeExists").GetBoolean());
    Assert.True(artifacts.GetProperty("heartbeatExists").GetBoolean());
    Assert.True(state.TryGetProperty("worktree", out var worktree));
    Assert.Equal(root, worktree.GetProperty("workingDirectory").GetString());
    Assert.True(worktree.GetProperty("exists").GetBoolean());
    Assert.True(state.TryGetProperty("staleThresholds", out var staleThresholds));
    Assert.True(staleThresholds.GetProperty("recentHeartbeatGrace").GetString() is { Length: > 0 });
    Assert.True(staleThresholds.GetProperty("liveIdleTimeout").GetString() is { Length: > 0 });
    Assert.True(staleThresholds.TryGetProperty("staleRetryBudgetRemaining", out _));
}

    [Xunit.Fact(DisplayName = "ShowOrchestratorLogArtifacts_defaults_to_latest_dispatch_run_and_all_restores_history")]
    public void ShowOrchestratorLogArtifactsDefaultsToLatestDispatchRunAndAllRestoresHistory()
{
    var root = CreateTempDirectory();
    var scripts = Path.Combine(root, "scripts");
    var logs = Path.Combine(root, ".orchestrator", "logs");
    Directory.CreateDirectory(scripts);
    Directory.CreateDirectory(logs);
    File.Copy(
        FindRepositoryFile("scripts", "Show-OrchestratorLogArtifacts.ps1"),
        Path.Combine(scripts, "Show-OrchestratorLogArtifacts.ps1"));

    const string goalPrefix = "946820de";
    const string taskPrefix = "1f0ce3bb";
    var olderPrefix = $"{goalPrefix}-{taskPrefix}-20260629120000";
    var latestPrefix = $"{goalPrefix}-{taskPrefix}-20260629130000";

    WriteDispatchArtifact(logs, olderPrefix, ".out.log", "older stdout line 1\nolder stdout tail", DateTime.Parse("2026-06-29T12:00:01"));
    WriteDispatchArtifact(logs, olderPrefix, ".err.log", "older stderr tail", DateTime.Parse("2026-06-29T12:00:02"));
    WriteDispatchArtifact(logs, olderPrefix, ".exit.txt", "1", DateTime.Parse("2026-06-29T12:00:03"));
    WriteDispatchArtifact(logs, latestPrefix, ".out.log", "latest stdout line 1\nlatest stdout tail", DateTime.Parse("2026-06-29T13:00:01"));
    WriteDispatchArtifact(logs, latestPrefix, ".err.log", "latest stderr tail", DateTime.Parse("2026-06-29T13:00:02"));
    WriteDispatchArtifact(logs, latestPrefix, ".exit.txt", "0", DateTime.Parse("2026-06-29T13:00:03"));

    var defaultResult = RunPowerShellCommand(
        root,
        "& '.\\scripts\\Show-OrchestratorLogArtifacts.ps1' -GoalPrefix '946820de' -TaskPrefix '1f0ce3bb' -TailLines 1");

    Assert.Equal(0, defaultResult.ExitCode);
    Assert.Contains(latestPrefix, defaultResult.StandardOutput);
    Assert.Contains("latest stdout tail", defaultResult.StandardOutput);
    Assert.DoesNotContain(olderPrefix, defaultResult.StandardOutput);
    Assert.DoesNotContain("older stdout tail", defaultResult.StandardOutput);

    var allResult = RunPowerShellCommand(
        root,
        "& '.\\scripts\\Show-OrchestratorLogArtifacts.ps1' -GoalPrefix '946820de' -TaskPrefix '1f0ce3bb' -TailLines 1 -All");

    Assert.Equal(0, allResult.ExitCode);
    Assert.Contains(olderPrefix, allResult.StandardOutput);
    Assert.Contains(latestPrefix, allResult.StandardOutput);
    Assert.Contains("older stdout tail", allResult.StandardOutput);
    Assert.Contains("latest stdout tail", allResult.StandardOutput);
}

private static (
    TaskSpec Task,
    TaskProcessRecord Process,
    DispatchRefreshOutcome Outcome) RunHungReadOnlyRoleScenario(
        AgentRole role,
        bool codexWrapper,
        string standardOutput,
        bool localDispatch = false,
        int? childExitCode = null)
{
    var root = CreateTempDirectory();
    File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, "worker.out.log");
    var stderr = Path.Combine(logs, "worker.err.log");
    var exit = Path.Combine(logs, "worker.exit.txt");
    var childExit = childExitCode is null ? null : Path.Combine(logs, "worker.child-exit.json");
    var now = DateTimeOffset.Parse("2026-08-08T06:00:00Z");
    var startedAt = now.AddMinutes(codexWrapper ? -5 : -40);
    var command = codexWrapper ? "codex exec prompt" : "claude prompt";
    var workerName = localDispatch ? "local" : codexWrapper ? "codex-cli" : "claude-cli";
    File.WriteAllText(stdout, standardOutput);
    File.WriteAllText(stderr, codexWrapper ? "Tokens used: input=123 output=45" : string.Empty);
    File.SetLastWriteTimeUtc(stdout, now.AddMinutes(-3).UtcDateTime);
    File.SetLastWriteTimeUtc(stderr, now.AddMinutes(-3).UtcDateTime);
    if (childExit is not null)
    {
        File.WriteAllText(
            childExit,
            JsonSerializer.Serialize(
                new DispatchProcessHost.DispatchChildExitRecord(888888, childExitCode, now.AddMinutes(-1)),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Hung read-only wrapper", [new TaskSpec(TaskId.New(), $"{role} task.", role)]);
    var agent = new AgentDefinition(
        new AgentId($"test-{role.ToString().ToLowerInvariant()}"),
        $"Test {role}",
        role,
        new ModelProfile("Test", "test-model", ModelCapability.Text, SubscriptionMode.ApiKey));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(workerName, command, root, startedAt));
    var process = new TaskProcessRecord(
        999999,
        command,
        root,
        stdout,
        stderr,
        exit,
        startedAt,
        null,
        null,
        ChildExitRecordPath: childExit);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    if (!codexWrapper)
    {
        WriteHeartbeat(
            process,
            now.AddMinutes(-31),
            now.AddMinutes(-31),
            "running",
            new FileInfo(stdout).Length,
            0,
            childPid: null);
    }

    var outcome = new BackgroundDispatchRunner(new TestClock(now), TimeSpan.FromMinutes(2), _ => true)
        .ReconcileLatestProcess(kernel, goal.Id, task.Id);
    return (task, process, outcome);
}

private static string BuildSuccessfulReadOnlyOutput(AgentRole role)
{
    var roleOutput = role == AgentRole.Planner
        ? PlannerContractPlanFixture() + Environment.NewLine
        : $"Completed {role} analysis." + Environment.NewLine;
    return roleOutput + WorkerResultBlock(
        "none",
        "source inspection",
        "pass - structured result complete",
        blockers: "none");
}

private static void AssertExitCode(string path, int expected)
{
    Assert.True(DispatchExitArtifacts.TryRead(path, out var artifact));
    Assert.Equal(expected, artifact.ExitCode);
}

private static void WriteIsolationLeaseArtifacts(string worktree)
{
    var goalLease = Path.Combine(worktree, "i", "goals", "infra-partition", "lease");
    Directory.CreateDirectory(goalLease);
    File.WriteAllText(Path.Combine(goalLease, "lease.json"), "{}");
    File.WriteAllText(Path.Combine(goalLease, "lease.lock"), "locked");

    var slot = Path.Combine(worktree, "i", "slots", "slot-0");
    Directory.CreateDirectory(slot);
    File.WriteAllText(Path.Combine(slot, "lease.execution.lock"), "locked");
}

private static FixtureIsolatedDotnetRootScope UseFixtureIsolatedDotnetRoot()
{
    return new FixtureIsolatedDotnetRootScope(
        Path.Combine(
            Path.GetTempPath(),
            $"{DotnetBuildEnvironmentManager.RootDirectoryName}-commit-on-behalf-{Guid.NewGuid():N}"));
}

private static TaskProcessRecord RecordStaleDeveloperDispatch(
    AgentOrchestratorKernel kernel,
    Goal goal,
    TaskSpec task,
    string worktree,
    string root,
    DateTimeOffset dispatchedAt,
    string suffix)
{
    var logs = Path.Combine(root, "logs");
    Directory.CreateDirectory(logs);
    var stdout = Path.Combine(logs, $"{suffix}.out.log");
    var stderr = Path.Combine(logs, $"{suffix}.err.log");
    var exit = Path.Combine(logs, $"{suffix}.exit.txt");
    File.WriteAllText(stdout, string.Empty);
    File.WriteAllText(stderr, string.Empty);
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("claude-cli", $"claude prompt {suffix}", worktree, dispatchedAt));
    var process = new TaskProcessRecord(999999, $"claude prompt {suffix}", worktree, stdout, stderr, exit, dispatchedAt, null, null);
    kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
    return process;
}

private sealed class FixtureIsolatedDotnetRootScope : IDisposable
{
    private readonly string? _previousValue;
    private readonly string _root;

    public FixtureIsolatedDotnetRootScope(string root)
    {
        _root = root;
        _previousValue = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _previousValue);
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort; failed tests may leave files open for inspection.
        }
    }
}

}
