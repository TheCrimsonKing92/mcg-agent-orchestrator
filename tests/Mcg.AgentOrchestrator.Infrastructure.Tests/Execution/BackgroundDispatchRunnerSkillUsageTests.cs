using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated roots, fake process liveness, injected transcript roots.
public sealed class BackgroundDispatchRunnerSkillUsageTests
{
    [Fact]
    public void CompletedCodexRoundRecordsSelectionReadAndClaimInSourceOrder() => WithRoot(root =>
    {
        var (kernel, goal, task) = Refresh(root, ProviderKind.OpenAICodexCli, "present");
        var expected = $"SKILLS goal={goal.Id.Value[..8]} task={task.Id.Value[..8]} " +
            "selected=dotnet-windows-build-hygiene,verification-before-completion " +
            "read=verification-before-completion,orchestrator-worker-verification " +
            "claimed=verification-before-completion,dotnet-windows-build-hygiene";
        Assert.Equal(expected, Assert.Single(goal.Timeline.Where(e => e.TaskId == task.Id &&
            e.Kind == ProgressKind.TaskNote && e.Message.StartsWith("SKILLS "))).Message);
        Assert.NotNull(kernel.GetTask(goal.Id, task.Id).LastVerification);
    });

    [Theory]
    [InlineData("missing")]
    [InlineData("throwing")]
    public void UnavailableClaudeTranscriptPreservesRefreshVerdictAndOtherSkillEvidence(string mode) => WithRoot(root =>
    {
        var (_, presentGoal, present) = Refresh(Path.Combine(root, "present"), ProviderKind.AnthropicClaudeCli, "present");
        var (_, absentGoal, absent) = Refresh(Path.Combine(root, "absent"), ProviderKind.AnthropicClaudeCli, mode);
        Assert.Equal(present.Status, absent.Status);
        Assert.Equal(present.LastVerification!.ExitCode, absent.LastVerification!.ExitCode);
        Assert.Equal(present.LastVerification.Succeeded, absent.LastVerification.Succeeded);
        Assert.Equal(present.LastVerification.CompletionVerdictRule, absent.LastVerification.CompletionVerdictRule);
        Assert.Equal(present.LastVerification.WorkerResultPresent, absent.LastVerification.WorkerResultPresent);
        var note = Assert.Single(absentGoal.Timeline.Where(e => e.Message.StartsWith("SKILLS "))).Message;
        Assert.Contains("selected=dotnet-windows-build-hygiene,verification-before-completion", note);
        Assert.Contains("read=unavailable", note);
        Assert.Contains("claimed=verification-before-completion,dotnet-windows-build-hygiene", note);
        Assert.Contains("read=verification-before-completion", Assert.Single(
            presentGoal.Timeline.Where(e => e.Message.StartsWith("SKILLS "))).Message);
    });

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) Refresh(string root, ProviderKind provider, string mode)
    {
        Directory.CreateDirectory(root);
        var (kernel, goal) = SimpleGoal("Record round skill observations");
        var task = goal.Tasks.First();
        var started = DateTimeOffset.Parse("2026-09-05T15:05:00Z");
        var command = provider == ProviderKind.OpenAICodexCli ? "codex exec" : "claude -p";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("worker", command, root, started,
            WorkerProviderKind: provider, ProviderSessionId: "skill-session",
            SelectedSkills: ["dotnet-windows-build-hygiene", "verification-before-completion"]));
        var stdout = Path.Combine(root, "worker.out.log");
        var stderr = Path.Combine(root, "worker.err.log");
        var exit = Path.Combine(root, "worker.exit.txt");
        File.WriteAllText(stdout, """
            WORKER_RESULT:
            files: none
            commands: none
            tests: deferred - FixtureTests
            commit: none
            blockers: none
            assigned_scope_complete: true
            model_fit: fixture - adequate
            skills: verification-before-completion, dotnet-windows-build-hygiene
            confidence: high
            END_WORKER_RESULT
            """);
        File.WriteAllText(stderr, "");
        File.WriteAllText(exit, "1");
        if (provider == ProviderKind.OpenAICodexCli) File.WriteAllText(stdout + ".jsonl", WorkerSkillReadParserTests.CodexEvents);
        else if (mode != "missing") File.WriteAllText(Path.Combine(root, "skill-session.jsonl"), """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":".agents/skills/verification-before-completion/SKILL.md"}}]}}
            """);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(4242, command, root,
            stdout, stderr, exit, started, null, null, OwnedProcessIds: [4242]));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "round exited");
        var runner = new BackgroundDispatchRunner(isStillRunning: _ => false)
        {
            ClaudeTranscriptUsage = new ClaudeTranscriptUsageReader([root]),
            SkillTranscripts = mode == "throwing" ? new([root], _ => throw new IOException("fixture")) : new([root])
        };
        Assert.Equal(1, runner.SweepExitedProcesses(kernel));
        Assert.Equal(0, runner.SweepExitedProcesses(kernel));
        Assert.Single(goal.Timeline.Where(e => e.Message.StartsWith("SKILLS ")));
        return (kernel, goal, kernel.GetTask(goal.Id, task.Id));
    }

    private static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "skill-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
