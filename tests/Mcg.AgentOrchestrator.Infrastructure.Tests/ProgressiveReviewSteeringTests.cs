using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProgressiveReviewSteeringTests
{
    [Fact(DisplayName = "ProgressiveReviewSteering_cancels_confirms_dead_then_warm_resumes_with_guidance")]
    public void CancelsConfirmsDeadThenWarmResumesWithGuidance()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-warm");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        var intent = Intent(goal, task, now, "fix the scoped slice");
        store.EnqueueIntentAsync(intent).GetAwaiter().GetResult();
        var order = new List<string>();

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: (k, goalId, taskId) =>
            {
                order.Add("cancel");
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                return cancelled;
            },
            startProcess: (k, goalId, taskId) =>
            {
                order.Add("start");
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Contains(" resume 'session-12345678' -", dispatch.Command, StringComparison.Ordinal);
                Assert.Contains("--sandbox workspace-write", dispatch.Command, StringComparison.Ordinal);
                Assert.Contains("ProgressiveReviewSteer", File.ReadAllText(dispatch.PromptPath!));
                var started = new TaskProcessRecord(7001, dispatch.Command, dispatch.WorkingDirectory, "out2.log", "err2.log", "exit2.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            });

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.Equal(["cancel", "start"], order);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("warm-resume", receipt.Decision);
        Assert.Contains("tree-dead", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Contains("SameGoal:Passed", receipt.AdmissionChecks);
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_falls_back_to_fresh_dispatch_when_admission_fails")]
    public void FallsBackToFreshDispatchWhenAdmissionFails()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-fresh");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, "not-an-ancestor", sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "fresh fallback guidance")).GetAwaiter().GetResult();
        var preparedFresh = false;

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: (k, goalId, taskId) =>
            {
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                return cancelled;
            },
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Equal("fresh-guided", dispatch.Command);
                var started = new TaskProcessRecord(7002, dispatch.Command, dispatch.WorkingDirectory, "out3.log", "err3.log", "exit3.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            prepareFreshDispatch: (k, g, t, guidance) =>
            {
                preparedFresh = true;
                Assert.Contains("fresh fallback guidance", guidance, StringComparison.Ordinal);
                k.RecordTaskDispatch(g.Id, t.Id, new TaskDispatchRecord(
                    "codex-cli",
                    "fresh-guided",
                    root,
                    now.AddSeconds(2),
                    "OpenAI",
                    "gpt-5.5",
                    WorkerProviderKind: ProviderKind.OpenAICodexCli));
            });

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.True(preparedFresh);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("fresh-dispatch", receipt.Decision);
        Assert.Contains(receipt.AdmissionChecks, check => check.Contains("SpawnHeadAncestor:Failed", StringComparison.Ordinal));
        Assert.Equal(head, GitCli.Run(root, "rev-parse", "HEAD").Output.Trim());
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_routes_to_attention_when_tree_death_is_unconfirmed")]
    public void RoutesToAttentionWhenTreeDeathIsUnconfirmed()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-attention");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "do not start while pid is alive")).GetAwaiter().GetResult();
        var attentionStore = new CollaborationItemStore(Path.Combine(root, ".orchestrator", "items.db"));
        var started = false;

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            isProcessRunning: pid => pid == 6001,
            cancelProcess: (k, goalId, taskId) =>
            {
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                return cancelled;
            },
            startProcess: (_, _, _) =>
            {
                started = true;
                throw new InvalidOperationException("start must not run");
            });

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.False(started);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("owned pid(s) still alive", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
    }

    private static ProgressiveReviewSteeringCoordinator NewCoordinator(
        string root,
        InMemoryProgressiveReviewSteeringStore store,
        ICollaborationItemStore? attentionStore = null,
        Func<int, bool>? isProcessRunning = null,
        Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord>? cancelProcess = null,
        Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord>? startProcess = null,
        Action<AgentOrchestratorKernel, Goal, TaskSpec, string>? prepareFreshDispatch = null)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        return new ProgressiveReviewSteeringCoordinator(
            workspace,
            AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(),
            new InMemoryModelProviderRegistry([]),
            store,
            attentionStore,
            utcNow: () => new DateTimeOffset(2026, 7, 20, 12, 0, 10, TimeSpan.Zero),
            isProcessRunning: isProcessRunning ?? (_ => false),
            cancelProcess: cancelProcess,
            startProcess: startProcess,
            prepareFreshDispatch: prepareFreshDispatch);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) RunningDeveloper(
        string root,
        DateTimeOffset dispatchedAt,
        string? worktreeHead,
        string? sessionId)
    {
        Directory.CreateDirectory(Path.Combine(root, ".orchestrator", "logs"));
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(new TaskId("developer-task-0001"), "Implement feature.\n\nACCEPTANCE\n- Stay scoped", AgentRole.Developer);
        var goal = kernel.CreateGoal(new GoalId("goal-progressive-review-0001"), "Progressive review steering goal", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "codex-cli",
            "codex exec --skip-git-repo-check --sandbox workspace-write -",
            root,
            dispatchedAt,
            "OpenAI",
            "gpt-5.5",
            TaskComplexity: TaskComplexity.Complex,
            PromptCharacterCount: 1234,
            PromptPath: Path.Combine(root, ".orchestrator", "prompts", "initial.md"),
            WorkerProviderKind: ProviderKind.OpenAICodexCli,
            ProviderSessionId: sessionId,
            WorktreeHeadSha: worktreeHead,
            DirtyStateHash: "clean"));
        var process = new TaskProcessRecord(
            6001,
            task.LastDispatch!.Command,
            root,
            Path.Combine(root, ".orchestrator", "logs", "out.log"),
            Path.Combine(root, ".orchestrator", "logs", "err.log"),
            Path.Combine(root, ".orchestrator", "logs", "exit.txt"),
            dispatchedAt,
            null,
            null,
            OwnedProcessIds: [6001]);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        return (kernel, goal, task);
    }

    private static ProgressiveReviewSteerIntent Intent(Goal goal, TaskSpec task, DateTimeOffset now, string guidance) =>
        new(
            Guid.NewGuid().ToString("n"),
            goal.Id.Value,
            task.Id.Value,
            AgentRole.Developer.ToString(),
            $"{goal.Id.Value}|{task.Id.Value}|{now.UtcTicks}",
            "glance-abc",
            "inputs-hash",
            now,
            "diff shows wrong subsystem",
            guidance,
            $"ProgressiveReviewSteer guidance. {guidance}",
            now);

    private static string CreateGitRepository(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Assert.True(GitCli.Run(root, "init").Succeeded);
        Assert.True(GitCli.Run(root, "config", "user.email", "test@example.test").Succeeded);
        Assert.True(GitCli.Run(root, "config", "user.name", "Test User").Succeeded);
        File.WriteAllText(Path.Combine(root, "README.md"), "test");
        Assert.True(GitCli.Run(root, "add", "README.md").Succeeded);
        Assert.True(GitCli.Run(root, "commit", "-m", "initial").Succeeded);
        return root;
    }
}
