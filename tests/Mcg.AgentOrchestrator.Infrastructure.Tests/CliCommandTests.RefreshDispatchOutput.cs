using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("GoalWorktreeCleanupHooks")]
public sealed class CliCommandTestsRefreshDispatchOutput : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "refresh-dispatch_default_is_bounded_and_decision_oriented_for_deep_history")]
    public void RefreshDispatchDefaultIsBoundedAndDecisionOrientedForDeepHistory()
    {
        var fixture = CreateDeepHistoryFixture();

        var output = ExecuteCliAndCapture(["refresh-dispatch", "1"], fixture.Kernel, fixture.Workspace);

        Xunit.Assert.True(CountNonEmptyLines(output) < 100, output);
        Xunit.Assert.Contains($"Task: id={fixture.Task.Id.Value[..8]} status=Completed", output);
        Xunit.Assert.Contains("Dispatch: worker=codex-cli provider=OpenAI model=gpt-test lane=codex-cli session=session-sentinel", output);
        Xunit.Assert.Contains("Process: wrapper_pid=999999 wrapper_alive=false child_pid=888888 child_alive=false running=false", output);
        Xunit.Assert.Contains("Dispatch state: kind=Completed", output);
        Xunit.Assert.Contains("Heartbeat: heartbeat unavailable", output);
        Xunit.Assert.Contains("Exit evidence: exit=0 origin=Native reason=native-exit-sentinel artifact=present", output);
        Xunit.Assert.Contains("Exit classification: kind=VerifiedSuccess", output);
        Xunit.Assert.Contains("Structured blockers: status=none value=none", output);
        Xunit.Assert.Contains("Latest verification: exit=0", output);
        Xunit.Assert.Contains($"stdout_path={fixture.Task.LastProcess!.StandardOutputPath}", output);
        Xunit.Assert.Contains($"stderr_path={fixture.Task.LastProcess.StandardErrorPath}", output);
        Xunit.Assert.Contains("Disposition: state=Idle", output);
        Xunit.Assert.Contains("Next action: next", output);
        Xunit.Assert.DoesNotContain("Task timeline:", output);
        Xunit.Assert.DoesNotContain("HISTORY_SENTINEL_000", output);
        Xunit.Assert.DoesNotContain("WORKER_RESULT:", output);
    }

    [Xunit.Fact(DisplayName = "refresh-dispatch_history_prints_complete_deep_history_oldest_first")]
    public void RefreshDispatchHistoryPrintsCompleteDeepHistoryOldestFirst()
    {
        var fixture = CreateDeepHistoryFixture();

        var output = ExecuteCliAndCapture(["refresh-dispatch", "1", "--history"], fixture.Kernel, fixture.Workspace);

        var first = output.IndexOf("HISTORY_SENTINEL_000", StringComparison.Ordinal);
        var middle = output.IndexOf("HISTORY_SENTINEL_130", StringComparison.Ordinal);
        var last = output.IndexOf("HISTORY_SENTINEL_259", StringComparison.Ordinal);
        Xunit.Assert.Contains("Task timeline:", output);
        Xunit.Assert.True(first >= 0 && first < middle && middle < last, output);
    }

    [Xunit.Fact(DisplayName = "refresh-dispatch_history_limit_prints_only_newest_requested_events")]
    public void RefreshDispatchHistoryLimitPrintsOnlyNewestRequestedEvents()
    {
        var fixture = CreateDeepHistoryFixture();

        var output = ExecuteCliAndCapture(["refresh-dispatch", "1", "--history-limit", "3"], fixture.Kernel, fixture.Workspace);
        var timeline = output[(output.IndexOf("Task timeline:", StringComparison.Ordinal) + "Task timeline:".Length)..];

        Xunit.Assert.Equal(3, CountNonEmptyLines(timeline));
        Xunit.Assert.DoesNotContain("HISTORY_SENTINEL_000", timeline);
    }

    [Xunit.Fact(DisplayName = "refresh-dispatch_invalid_history_limits_fail_before_reconciliation")]
    public void RefreshDispatchInvalidHistoryLimitsFailBeforeReconciliation()
    {
        var fixture = CreateDeepHistoryFixture();
        var originalTimelineCount = fixture.Goal.Timeline.Count;
        IReadOnlyList<IReadOnlyList<string>> invalidCommands =
        [
            ["refresh-dispatch", "1", "--history-limit"],
            ["refresh-dispatch", "1", "--history-limit", "0"],
            ["refresh-dispatch", "1", "--history-limit", "-1"],
            ["refresh-dispatch", "1", "--history-limit", "not-a-number"],
            ["refresh-dispatch", "1", "--history-limit", "2147483648"],
            ["refresh-dispatch", "1", "--history-limit", "1", "--history-limit", "2"]
        ];

        foreach (var command in invalidCommands)
        {
            var exception = Xunit.Assert.Throws<ArgumentException>(() =>
                ExecuteCliAndCapture(command, fixture.Kernel, fixture.Workspace));
            Xunit.Assert.Contains(CliCommandHelp.RefreshDispatchUsage, exception.Message);
            Xunit.Assert.Equal(originalTimelineCount, fixture.Goal.Timeline.Count);
            Xunit.Assert.True(fixture.Task.LastProcess!.IsRunning);
            Xunit.Assert.Null(fixture.Task.LastVerification);
        }
    }

    private static DeepHistoryFixture CreateDeepHistoryFixture()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Reconcile the completed worker", AgentRole.Tester);
        var goal = kernel.CreateGoal("Deep refresh history", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);

        for (var index = 0; index < 260; index++)
        {
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Running, $"HISTORY_SENTINEL_{index:000}");
        }

        var stdout = Path.Combine(root, "refresh.out.log");
        var stderr = Path.Combine(root, "refresh.err.log");
        var exit = Path.Combine(root, "refresh.exit.json");
        File.WriteAllText(stdout, """
            WORKER_RESULT:
            files: none
            commands: inspected dispatch output
            tests: pass - focused verification passed
            commit: none
            blockers: none
            model_fit: OpenAI/gpt-test - adequate - verification - sufficient
            skills: none
            confidence: high
            END_WORKER_RESULT
            """);
        File.WriteAllText(stderr, string.Empty);
        DispatchExitArtifacts.Write(exit, DispatchExitArtifacts.Native(0, "native-exit-sentinel", DateTimeOffset.UtcNow));
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec\n--model gpt-test",
            root,
            DateTimeOffset.UtcNow,
            ProviderName: "OpenAI",
            ModelName: "gpt-test",
            DispatchLane: "codex-cli",
            ProviderSessionId: "session-sentinel"));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
            999999,
            "codex exec\n--model gpt-test",
            root,
            stdout,
            stderr,
            exit,
            DateTimeOffset.UtcNow,
            null,
            null,
            OwnedProcessIds: [999999, 888888],
            ChildProcessId: 888888));

        return new DeepHistoryFixture(kernel, goal, task, workspace);
    }

    private sealed record DeepHistoryFixture(
        AgentOrchestratorKernel Kernel,
        Goal Goal,
        TaskSpec Task,
        OrchestratorWorkspace Workspace);
}
