using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// The real intent store is scoped to this test's unique root; no OS process is spawned.
[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AnsweredTesterBlockerRequeueIsolatedProcessSpawningConductorTests
{
    [Xunit.Fact]
    public async Task Answer_intent_requeues_blocked_tester_and_tick_dispatches_it()
    {
        const string question = "The objective names JSON fields in `snake_case` but also requires `JsonSerializerDefaults.Web`, which emits `camelCase`. Should the JSON keys be snake_case or camelCase?";
        const string answer = "Use camelCase JSON keys. Serialize the report with `new JsonSerializerOptions(JsonSerializerDefaults.Web)` and set no `PropertyNamingPolicy` and no `[JsonPropertyName]` attributes.";
        var root = Path.Combine(Path.GetTempPath(), $"answered-tester-intent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Verify the JSON contract.", AgentRole.Tester);
            var goal = kernel.CreateGoal("Resume answered blocker verification.", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var recordedAt = new DateTimeOffset(2026, 10, 6, 8, 23, 47, TimeSpan.Zero);
            const string command = "tester-worker.exe";
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("tester", command, root, recordedAt));
            var process = new TaskProcessRecord(12345, command, root,
                Path.Combine(root, "out.log"), Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"), recordedAt, null, null);
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
            kernel.RecordTaskProcessRefreshed(goal.Id, task.Id,
                process with { CompletedAt = recordedAt, ExitCode = 0 }, null);
            var output = $$"""
                WORKER_RESULT:
                files: none
                commands: none
                tests: deferred - Suite.SomeTests
                commit: none
                blockers: exact-blocker - JSON key contract needs operator clarification; execution evidence is pending
                assigned_scope_complete: true
                model_fit: OpenAI/gpt-6.1-sol - adequate - verification
                skills: none
                confidence: high
                END_WORKER_RESULT
                HUMAN_INPUT: {{question}}
                """;
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
                new TaskVerificationRecord(command, root, 0, output, string.Empty, recordedAt,
                    WorkerResultPresent: true, HumanInputQuestion: question));
            var request = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
            Assert.True(task.LastVerification!.Succeeded);
            Assert.Equal(VerificationGateReason.TesterWorkerResultBlocker,
                Assert.Single(kernel.BuildVerificationGate(goal.Id).Tasks).Reason);

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
                request.Id.Value, goal.Id.Value, answer, OperatorActorKind.Human);
            var intent = await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "operator", "cli",
                "local-process", recordedAt));
            var coordinator = new OperatorIntentCoordinator(intents,
                decisions: CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
                goalStateVersionResolver: _ => 0);

            Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            coordinator.CompletePersisted([goal.Id]);

            Assert.Equal(OperatorIntentStatus.Applied, (await intents.GetAsync(intent.Id))!.Status);
            var dispatched = new List<TaskId>();
            var driver = ConductorDriverTests.MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                isVerificationGateSatisfied: currentGoal => kernel.BuildVerificationGate(currentGoal.Id).IsSatisfied,
                dispatchAndStart: currentGoal =>
                {
                    var currentTask = kernel.GetTask(currentGoal.Id, task.Id);
                    Assert.Equal(WorkTaskStatus.Assigned, currentTask.Status);
                    Assert.Null(currentTask.LastVerification);
                    dispatched.Add(currentTask.Id);
                    return DispatchStartOutcome.Started([currentTask]);
                });

            var advance = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(advance.Outcome);
            Assert.Equal(task.Id, Assert.Single(dispatched));
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.Equal(RetryCause.ContractClarification, task.PendingRetryCause);
            Assert.Equal(answer, Assert.Single(goal.Timeline.Where(item => item.TaskId == task.Id &&
                item.Kind == ProgressKind.TaskRetried)).Message);
            Assert.DoesNotContain(kernel.BuildVerificationGate(goal.Id).Tasks,
                item => item.TaskId == task.Id && item.Reason == VerificationGateReason.TesterWorkerResultBlocker);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
