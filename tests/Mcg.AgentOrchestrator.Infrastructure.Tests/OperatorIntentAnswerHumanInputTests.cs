using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorIntentAnswerHumanInputTests
{
    [Xunit.Theory]
    [Xunit.InlineData("missing", "Answer", "was not found")]
    [Xunit.InlineData("empty", "  ", "text is empty")]
    public async Task Invalid_answer_intent_is_rejected_without_decision(
        string target, string text, string reason)
    {
        var root = Path.Combine(Path.GetTempPath(), $"answer-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Reject invalid answer");
            var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
                target, goal.Id.Value, text, OperatorActorKind.Human);
            var intent = await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "operator", "cli",
                "local-process", DateTimeOffset.UtcNow)).WaitAsync(TimeSpan.FromSeconds(30));
            var coordinator = new OperatorIntentCoordinator(intents, decisions: collaboration,
                goalStateVersionResolver: _ => 0);

            Xunit.Assert.False(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            var rejected = await intents.GetAsync(intent.Id).WaitAsync(TimeSpan.FromSeconds(30));
            Xunit.Assert.Equal(OperatorIntentStatus.Rejected, rejected!.Status);
            Xunit.Assert.Contains(reason, rejected.Outcome, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.Null(await collaboration.GetDecisionStateAsync($"answer-{intent.Id}")
                .WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public async Task Answer_intent_passes_claimed_retry_hold()
    {
        var root = Path.Combine(Path.GetTempPath(), $"answer-retry-hold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel, AgentCatalog.Default().Agents, "Classify retry through typed answer");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var retry = await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Retry, goal.Id.Value, task.Id.Value,
                JsonSerializer.Serialize(new RetryOperatorIntentPayload("Retry with classified cause", null,
                    RetryCause: RetryCause.Unknown), OperatorIntentJson.Options), [], "operator", "cli",
                "local-process", DateTimeOffset.UtcNow)).WaitAsync(TimeSpan.FromSeconds(30));
            var coordinator = new OperatorIntentCoordinator(intents,
                decisions: CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
                goalStateVersionResolver: _ => 0);
            Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            var request = Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id));
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
                request.Id.Value, goal.Id.Value, nameof(RetryCause.NewSourceFinding), OperatorActorKind.Human);
            var answer = await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "operator", "cli",
                "local-process", DateTimeOffset.UtcNow)).WaitAsync(TimeSpan.FromSeconds(30));

            Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            coordinator.CompletePersisted([goal.Id]);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied,
                (await intents.GetAsync(answer.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);
            Xunit.Assert.Equal(OperatorIntentStatus.Claimed,
                (await intents.GetAsync(retry.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);
            Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            coordinator.CompletePersisted([goal.Id]);
            Xunit.Assert.Equal(RetryCause.NewSourceFinding, task.PendingRetryCause);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied,
                (await intents.GetAsync(retry.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public async Task Answer_intent_resumes_waiting_task_and_records_decision()
    {
        var root = Path.Combine(Path.GetTempPath(), $"answer-human-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Use selected implementation", AgentRole.Developer);
            var goal = kernel.CreateGoal("Select implementation", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which implementation?",
                HumanWaitKind.SpecClarification);
            var directKernel = new AgentOrchestratorKernel();
            var directTask = new TaskSpec(TaskId.New(), "Use selected implementation", AgentRole.Developer);
            var directGoal = directKernel.CreateGoal("Select implementation", [directTask]);
            directKernel.ActivateGoal(directGoal.Id, AgentCatalog.Default().Agents);
            var directRequest = directKernel.RequestHumanInput(directGoal.Id, directTask.Id,
                "Which implementation?", HumanWaitKind.SpecClarification);
            var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
                request.Id.Value, goal.Id.Value, "Use existing path", OperatorActorKind.Agent);
            var intent = await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "author", "cli",
                "local-process", DateTimeOffset.UtcNow, ActorKind: OperatorActorKind.Agent))
                .WaitAsync(TimeSpan.FromSeconds(30));
            var coordinator = new OperatorIntentCoordinator(intents, decisions: collaboration,
                goalStateVersionResolver: _ => 0);

            Xunit.Assert.False(request.IsCompleted);
            Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);
            Xunit.Assert.Equal(directTask.Status, task.Status);
            directKernel.SubmitHumanInput(directRequest.Id, "Use existing path");
            Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            coordinator.CompletePersisted([goal.Id]);

            Xunit.Assert.True(request.IsCompleted);
            Xunit.Assert.Equal("Use existing path", request.Answer);
            Xunit.Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, directTask.Status);
            Xunit.Assert.Equal(directTask.Status, task.Status);
            Xunit.Assert.Equal(directGoal.Status, goal.Status);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied,
                (await intents.GetAsync(intent.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);
            var decision = await collaboration.GetDecisionStateAsync($"answer-{intent.Id}").WaitAsync(TimeSpan.FromSeconds(30));
            Xunit.Assert.Equal("agent:author", decision!.Receipt!.ActorId);
            Xunit.Assert.Contains(request.Id.Value, decision.Request.Subject);
            Xunit.Assert.Contains(goal.Timeline, entry => entry.OperatorIntentApplied?.DecisionId == decision.Receipt.Id &&
                entry.OperatorIntentApplied.ActorKind == OperatorActorKind.Agent);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public async Task Prior_decision_replay_resumes_request_from_uncheckpointed_goal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"answer-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Resume after restart", AgentRole.Developer);
            var goal = kernel.CreateGoal("Answer after restart", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which path?", HumanWaitKind.SpecClarification);
            var uncheckpointed = kernel.ExportSnapshot();
            var decisions = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
                request.Id.Value, goal.Id.Value, "Use existing path", OperatorActorKind.Human);
            var intent = await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "operator", "cli",
                "local-process", DateTimeOffset.UtcNow)).WaitAsync(TimeSpan.FromSeconds(30));
            var firstCoordinator = new OperatorIntentCoordinator(intents, decisions: decisions,
                goalStateVersionResolver: _ => 0);
            Xunit.Assert.True(firstCoordinator.ExecutePending(kernel, goal).MutatedGoalState);
            Xunit.Assert.NotNull((await decisions.GetDecisionStateAsync($"answer-{intent.Id}")
                .WaitAsync(TimeSpan.FromSeconds(30)))?.Receipt);
            Xunit.Assert.Equal(OperatorIntentStatus.Claimed,
                (await intents.GetAsync(intent.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);

            var restored = AgentOrchestratorKernel.FromSnapshot(uncheckpointed);
            var restoredGoal = restored.GetGoal(goal.Id);
            Xunit.Assert.False(restored.HumanInputRequests.Single().IsCompleted);
            var replayCoordinator = new OperatorIntentCoordinator(intents, decisions: decisions,
                goalStateVersionResolver: _ => 0);
            Xunit.Assert.True(replayCoordinator.ExecutePending(restored, restoredGoal).MutatedGoalState);
            replayCoordinator.CompletePersisted([goal.Id]);

            Xunit.Assert.True(restored.HumanInputRequests.Single().IsCompleted);
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, restoredGoal.Tasks.Single().Status);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied,
                (await intents.GetAsync(intent.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Xunit.Fact]
    public async Task Rejected_answer_does_not_hide_new_retry_correction_request()
    {
        var root = Path.Combine(Path.GetTempPath(), $"answer-retry-correction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel, AgentCatalog.Default().Agents, "Classify retry cause");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Retry, goal.Id.Value, task.Id.Value,
                JsonSerializer.Serialize(new RetryOperatorIntentPayload("Retry after classification", null,
                    RetryCause: RetryCause.Unknown), OperatorIntentJson.Options), [], "operator", "cli",
                "local-process", DateTimeOffset.UtcNow)).WaitAsync(TimeSpan.FromSeconds(30));
            var coordinator = new OperatorIntentCoordinator(intents,
                decisions: CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
                goalStateVersionResolver: _ => 0);
            Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            var firstRequest = Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id));
            kernel.SubmitHumanInput(firstRequest.Id, "typo");
            var duplicate = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
                firstRequest.Id.Value, goal.Id.Value, "another answer", OperatorActorKind.Human);
            var answer = await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
                JsonSerializer.Serialize(duplicate, OperatorIntentJson.Options), [], "operator", "cli",
                "local-process", DateTimeOffset.UtcNow)).WaitAsync(TimeSpan.FromSeconds(30));

            Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            Xunit.Assert.Equal(OperatorIntentStatus.Rejected,
                (await intents.GetAsync(answer.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);
            Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id));
            Xunit.Assert.NotEqual(firstRequest.Id, kernel.GetPendingHumanInput(goal.Id).Single().Id);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
