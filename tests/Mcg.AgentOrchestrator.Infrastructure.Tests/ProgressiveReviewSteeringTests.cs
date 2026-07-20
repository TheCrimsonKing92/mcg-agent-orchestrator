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
                WriteExitAndHeartbeat(cancelled, now.AddSeconds(1), childPid: null, ownedPids: [6001], state: "exited");
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
            },
            isProcessRunning: pid =>
            {
                order.Add($"probe:{pid}");
                return false;
            },
            getLineageDescendants: process =>
            {
                order.Add($"lineage:{process.ProcessId}");
                return [];
            });

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.True(order.IndexOf("cancel") >= 0);
        Assert.True(order.IndexOf("probe:6001") > order.IndexOf("cancel"));
        Assert.True(order.IndexOf("start") > order.IndexOf("probe:6001"));
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("warm-resume", receipt.Decision);
        Assert.Contains("tree-dead", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Contains(receipt.AdmissionChecks, check => check.StartsWith("SameGoal:Passed:", StringComparison.Ordinal));
        Assert.Equal(WorkTaskStatus.Running, kernel.GetTask(goal.Id, task.Id).Status);
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_writes_terminal_cancel_proof_after_owned_tree_is_dead")]
    public void WritesTerminalCancelProofAfterOwnedTreeIsDead()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-produced-proof");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var cancelledProcessRecord = task.LastProcess!;
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "resume after generated cancel proof")).GetAwaiter().GetResult();

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
                var started = new TaskProcessRecord(7010, dispatch.Command, dispatch.WorkingDirectory, "out-produced.log", "err-produced.log", "exit-produced.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            });

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("warm-resume", receipt.Decision);
        Assert.Contains("partial dispatch receipt consumed", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.True(File.Exists(cancelledProcessRecord.ExitCodePath));
        Assert.Equal("cancelled", ProcessLogReader.ReadHeartbeat(cancelledProcessRecord).State);
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
                WriteExitAndHeartbeat(cancelled, now.AddSeconds(1), childPid: null, ownedPids: [6001], state: "exited");
                return cancelled;
            },
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Equal("fresh-guided", dispatch.Command);
                Assert.Contains("fresh fallback guidance", File.ReadAllText(dispatch.PromptPath!), StringComparison.Ordinal);
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

    [Fact(DisplayName = "ProgressiveReviewSteering_falls_back_to_fresh_dispatch_when_acceptance_criteria_hash_changed")]
    public void FallsBackToFreshDispatchWhenAcceptanceCriteriaHashChanged()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-ac-drift");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        Directory.CreateDirectory(Path.GetDirectoryName(task.LastDispatch!.PromptPath!)!);
        File.WriteAllText(task.LastDispatch.PromptPath!, "Acceptance criteria:\n- old criterion\n");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "contract",
            ["new criterion"],
            VerificationClass.TestVerifiable,
            [],
            []));
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "fresh fallback after AC drift")).GetAwaiter().GetResult();
        var preparedFresh = false;

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Equal("fresh-guided", dispatch.Command);
                var started = new TaskProcessRecord(7011, dispatch.Command, dispatch.WorkingDirectory, "out-ac.log", "err-ac.log", "exit-ac.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            prepareFreshDispatch: (k, g, t, _) =>
            {
                preparedFresh = true;
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
        Assert.Contains(receipt.AdmissionChecks, check => check.Contains("AcceptanceCriteriaHash:Failed", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_falls_back_to_fresh_dispatch_when_current_model_differs")]
    public void FallsBackToFreshDispatchWhenCurrentModelDiffers()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-model-drift");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "fresh fallback after model drift")).GetAwaiter().GetResult();
        var preparedFresh = false;
        var agents = AgentCatalog.Default().Agents
            .Select(agent => agent.Role == AgentRole.Developer
                ? agent with { Subscription = agent.Subscription! with { ModelAlias = "gpt-other" } }
                : agent)
            .ToArray();

        var coordinator = NewCoordinator(
            root,
            store,
            agents: agents,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Equal("fresh-guided", dispatch.Command);
                var started = new TaskProcessRecord(7003, dispatch.Command, dispatch.WorkingDirectory, "out4.log", "err4.log", "exit4.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            prepareFreshDispatch: (k, g, t, _) =>
            {
                preparedFresh = true;
                k.RecordTaskDispatch(g.Id, t.Id, new TaskDispatchRecord(
                    "codex-cli",
                    "fresh-guided",
                    root,
                    now.AddSeconds(2),
                    "OpenAI",
                    "gpt-other",
                    WorkerProviderKind: ProviderKind.OpenAICodexCli));
            });

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.True(preparedFresh);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("fresh-dispatch", receipt.Decision);
        Assert.Contains(receipt.AdmissionChecks, check => check.Contains("SameModel:Failed", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_falls_back_to_fresh_dispatch_when_integration_changed_after_spawn")]
    public void FallsBackToFreshDispatchWhenIntegrationChangedAfterSpawn()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-integration-drift");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        new GoalLifecycleEventWriter(
            workspace.GoalLifecycleEventsDirectory,
            new TestClock(now.AddSeconds(5))).AppendGoalLanded(goal.Id, "main", "goal/test");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "fresh fallback after integration changed")).GetAwaiter().GetResult();
        var preparedFresh = false;

        var coordinator = NewCoordinator(
            root,
            store,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (k, goalId, taskId) =>
            {
                var dispatch = k.GetTask(goalId, taskId).LastDispatch!;
                Assert.Equal("fresh-guided", dispatch.Command);
                var started = new TaskProcessRecord(7004, dispatch.Command, dispatch.WorkingDirectory, "out5.log", "err5.log", "exit5.txt", now.AddSeconds(2), null, null);
                k.RecordTaskProcessStarted(goalId, taskId, started);
                return started;
            },
            prepareFreshDispatch: (k, g, t, _) =>
            {
                preparedFresh = true;
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
        Assert.Contains(receipt.AdmissionChecks, check => check.Contains("NoIntegrationChangeSinceCapture:Failed", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ProgressiveReviewSteeringStore_reserves_running_intent_after_loop_restart")]
    public void StoreReservesRunningIntentAfterLoopRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-steer-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var store = new SqliteProgressiveReviewSteeringStore(Path.Combine(root, "steering.db"));
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var intent = new ProgressiveReviewSteerIntent(
            "intent-running",
            "goal-1",
            "task-1",
            AgentRole.Developer.ToString(),
            "round-1",
            "glance-1",
            "inputs-1",
            now,
            "misdirected",
            "correct it",
            "guidance",
            now,
            ProgressiveReviewSteerIntentStatus.Running);
        store.EnqueueIntentAsync(intent).GetAwaiter().GetResult();

        var reserved = store.ReserveNextPendingAsync("goal-1").GetAwaiter().GetResult();

        Assert.NotNull(reserved);
        Assert.Equal("intent-running", reserved!.Id);
        Assert.Equal(ProgressiveReviewSteerIntentStatus.Running, reserved.Status);
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

    [Fact(DisplayName = "ProgressiveReviewSteering_blocks_resume_when_heartbeat_owned_child_survives")]
    public void BlocksResumeWhenHeartbeatOwnedChildSurvives()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-owned-child");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        WriteHeartbeat(task.LastProcess!, now, childPid: 6002, ownedPids: [6001, 6002, 6003], state: "running");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "do not start while child is alive")).GetAwaiter().GetResult();
        var attentionStore = new CollaborationItemStore(Path.Combine(root, ".orchestrator", "items.db"));
        var started = false;

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            isProcessRunning: pid => pid == 6002,
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
                throw new InvalidOperationException("start must not run over a live heartbeat-owned child");
            });

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.False(started);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("owned pid(s) still alive: 6002", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_blocks_resume_when_live_lineage_descendant_survives_cancel")]
    public void BlocksResumeWhenLiveLineageDescendantSurvivesCancel()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-lineage-child");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "do not start while lineage child is alive")).GetAwaiter().GetResult();
        var attentionStore = new CollaborationItemStore(Path.Combine(root, ".orchestrator", "items.db"));
        var started = false;

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            isProcessRunning: pid => pid == 6102,
            getLineageDescendants: _ => [6102],
            cancelProcess: (k, goalId, taskId) =>
            {
                var current = k.GetTask(goalId, taskId).LastProcess!;
                var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                WriteExitAndHeartbeat(cancelled, now.AddSeconds(1), childPid: null, ownedPids: [6001], state: "exited");
                return cancelled;
            },
            startProcess: (_, _, _) =>
            {
                started = true;
                throw new InvalidOperationException("start must not run over a live lineage descendant");
            });

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.False(started);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("owned pid(s) still alive: 6102", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_blocks_resume_when_terminal_cancel_proof_is_absent")]
    public void BlocksResumeWhenTerminalCancelProofIsAbsent()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-missing-proof");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "do not start without terminal proof")).GetAwaiter().GetResult();
        var attentionStore = new CollaborationItemStore(Path.Combine(root, ".orchestrator", "items.db"));
        var started = false;

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
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
                throw new InvalidOperationException("start must not run without terminal proof");
            },
            options: new ProgressiveReviewSteeringOptions(WriteTerminalCancelProofArtifacts: false));

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        Assert.False(started);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("exit artifact missing", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
    }

    [Fact(DisplayName = "ProgressiveReviewSteering_records_receipt_and_attention_when_steer_start_throws")]
    public void RecordsReceiptAndAttentionWhenSteerStartThrows()
    {
        var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var root = CreateGitRepository("mcg-steer-start-throws");
        var head = GitCli.Run(root, "rev-parse", "HEAD").Output.Trim();
        var (kernel, goal, task) = RunningDeveloper(root, now, head, sessionId: "session-12345678");
        var store = new InMemoryProgressiveReviewSteeringStore();
        store.EnqueueIntentAsync(Intent(goal, task, now, "start throws")).GetAwaiter().GetResult();
        var attentionStore = new CollaborationItemStore(Path.Combine(root, ".orchestrator", "items.db"));

        var coordinator = NewCoordinator(
            root,
            store,
            attentionStore,
            cancelProcess: CancelWithTerminalProof(now),
            startProcess: (_, _, _) => throw new InvalidOperationException("dispatch start failed"));

        var result = coordinator.ExecutePending(kernel, goal);

        Assert.True(result.MutatedTaskState);
        var receipt = Assert.Single(store.Receipts);
        Assert.Equal("operator-attention", receipt.Decision);
        Assert.Contains("tree-dead", receipt.CancelConfirmation, StringComparison.Ordinal);
        Assert.Contains("dispatch start failed", receipt.Outcome, StringComparison.Ordinal);
        Assert.Single(attentionStore.ListAsync(goal.Id.Value).GetAwaiter().GetResult());
    }

    private static ProgressiveReviewSteeringCoordinator NewCoordinator(
        string root,
        InMemoryProgressiveReviewSteeringStore store,
        ICollaborationItemStore? attentionStore = null,
        Func<int, bool>? isProcessRunning = null,
        Func<TaskProcessRecord, IReadOnlyList<int>>? getLineageDescendants = null,
        Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord>? cancelProcess = null,
        Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord>? startProcess = null,
        Action<AgentOrchestratorKernel, Goal, TaskSpec, string>? prepareFreshDispatch = null,
        IReadOnlyList<AgentDefinition>? agents = null,
        ProgressiveReviewSteeringOptions? options = null)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        return new ProgressiveReviewSteeringCoordinator(
            workspace,
            agents ?? AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(),
            new InMemoryModelProviderRegistry([]),
            store,
            attentionStore,
            options,
            utcNow: () => new DateTimeOffset(2026, 7, 20, 12, 0, 10, TimeSpan.Zero),
            isProcessRunning: isProcessRunning ?? (_ => false),
            getLineageDescendants: getLineageDescendants,
            cancelProcess: cancelProcess,
            startProcess: startProcess,
            prepareFreshDispatch: prepareFreshDispatch);
    }

    private static Func<AgentOrchestratorKernel, GoalId, TaskId, TaskProcessRecord> CancelWithTerminalProof(DateTimeOffset now) =>
        (k, goalId, taskId) =>
        {
            var current = k.GetTask(goalId, taskId).LastProcess!;
            var cancelled = current with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
            k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
            WriteExitAndHeartbeat(cancelled, now.AddSeconds(1), childPid: null, ownedPids: [6001], state: "exited");
            return cancelled;
        };

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

    private static void WriteHeartbeat(
        TaskProcessRecord process,
        DateTimeOffset observedAt,
        int? childPid,
        IReadOnlyList<int> ownedPids,
        string state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackgroundDispatchRunner.GetHeartbeatPath(process))!);
        var childPidJson = childPid.HasValue
            ? childPid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "null";
        File.WriteAllText(
            BackgroundDispatchRunner.GetHeartbeatPath(process),
            $$"""
            {"pid":{{process.ProcessId}},"childPid":{{childPidJson}},"ownedPids":[{{string.Join(",", ownedPids)}}],"state":"{{state}}","lastObservedAt":"{{observedAt:O}}","lastProgressAt":"{{observedAt:O}}","stdoutBytes":0,"stderrBytes":0,"ownedCpuMs":0}
            """);
    }

    private static void WriteExitAndHeartbeat(
        TaskProcessRecord process,
        DateTimeOffset observedAt,
        int? childPid,
        IReadOnlyList<int> ownedPids,
        string state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(process.ExitCodePath)!);
        File.WriteAllText(process.ExitCodePath, "1");
        WriteHeartbeat(process, observedAt, childPid, ownedPids, state);
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
