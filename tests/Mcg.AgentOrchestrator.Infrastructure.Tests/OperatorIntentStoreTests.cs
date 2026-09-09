using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OperatorIntentStoreTests
{
    [Xunit.Fact(DisplayName = "OperatorIntentStore_concurrent_submissions_have_no_busy_failures_or_lost_intents")]
    public async Task OperatorIntentStoreConcurrentSubmissionsHaveNoBusyFailuresOrLostIntents()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            const int intentCount = 48;
            using var start = new ManualResetEventSlim(false);
            var failures = new ConcurrentQueue<Exception>();
            var submissions = Enumerable.Range(0, intentCount)
                .Select(index => Task.Run(async () =>
                {
                    start.Wait();
                    try
                    {
                        await store.EnqueueAsync(CreateRetryIntent(
                            goalId: "goal-chaos",
                            taskId: $"task-{index}",
                            intentId: $"intent-{index}",
                            idempotencyKey: $"key-{index}"));
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }))
                .ToArray();

            start.Set();
            await Task.WhenAll(submissions);

            Xunit.Assert.Empty(failures);
            var persisted = await store.ListForGoalAsync("goal-chaos", intentCount);
            Xunit.Assert.Equal(intentCount, persisted.Count);
            Xunit.Assert.Equal(intentCount, persisted.Select(intent => intent.Id).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "OperatorIntentStore_concurrent_tick_drains_and_submissions_leave_every_intent_terminal")]
    public async Task OperatorIntentStoreConcurrentTickDrainsAndSubmissionsLeaveEveryIntentTerminal()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 16)
                .Select(index =>
                {
                    var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                        kernel,
                        AgentCatalog.Default().Agents,
                        $"Concurrent intent goal {index}");
                    var task = goal.Tasks.Single();
                    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
                    return (Goal: goal, Task: task, Index: index);
                })
                .ToArray();
            var coordinator = new OperatorIntentCoordinator(store);
            using var start = new ManualResetEventSlim(false);
            using var allSubmitted = new ManualResetEventSlim(false);
            var failures = new ConcurrentQueue<Exception>();

            var drain = Task.Run(() =>
            {
                start.Wait();
                while (!allSubmitted.IsSet || coordinator.ListActionableGoalIds().Count > 0)
                {
                    foreach (var goalId in coordinator.ListActionableGoalIds())
                    {
                        try
                        {
                            var goal = kernel.Goals.Single(candidate => candidate.Id.Value == goalId);
                            var result = coordinator.ExecutePending(kernel, goal);
                            if (result.MutatedGoalState)
                            {
                                coordinator.CompletePersisted([goal.Id]);
                            }
                        }
                        catch (Exception ex)
                        {
                            failures.Enqueue(ex);
                        }
                    }

                    Thread.Yield();
                }
            });
            var submissions = goals.Select(item => Task.Run(async () =>
            {
                start.Wait();
                try
                {
                    await store.EnqueueAsync(CreateRetryIntent(
                        item.Goal.Id.Value,
                        item.Task.Id.Value,
                        $"tick-intent-{item.Index}",
                        $"tick-key-{item.Index}"));
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            })).ToArray();

            start.Set();
            await Task.WhenAll(submissions);
            allSubmitted.Set();
            await drain.WaitAsync(TimeSpan.FromSeconds(10));

            Xunit.Assert.Empty(failures);
            foreach (var item in goals)
            {
                var intent = Xunit.Assert.Single(await store.ListForGoalAsync(item.Goal.Id.Value));
                Xunit.Assert.Equal(OperatorIntentStatus.Applied, intent.Status);
            }
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "OperatorIntentStore_idempotency_key_returns_one_record_and_rejects_payload_collision")]
    public async Task OperatorIntentStoreIdempotencyKeyReturnsOneRecordAndRejectsPayloadCollision()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var first = await store.EnqueueAsync(CreateRetryIntent(
                "goal-one",
                "task-one",
                "intent-one",
                "stable-key"));
            var duplicate = await store.EnqueueAsync(CreateRetryIntent(
                "goal-one",
                "task-one",
                "intent-two",
                "stable-key"));

            Xunit.Assert.Equal(first.Id, duplicate.Id);
            Xunit.Assert.Single(await store.ListForGoalAsync("goal-one"));

            var collision = CreateRetryIntent(
                "goal-one",
                "task-one",
                "intent-three",
                "stable-key",
                message: "different payload");
            var ex = await Xunit.Assert.ThrowsAsync<InvalidOperationException>(
                () => store.EnqueueAsync(collision));
            Xunit.Assert.Contains("different intent", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "OperatorIntentStore_claim_is_one_shot_and_outcome_is_pollable")]
    public async Task OperatorIntentStoreClaimIsOneShotAndOutcomeIsPollable()
    {
        var root = CreateTempDirectory();
        try
        {
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            await store.EnqueueAsync(CreateRetryIntent("goal-one", "task-one", "intent-one", "key-one"));

            var claimed = await store.ClaimNextAsync("goal-one", "conductor-a");
            Xunit.Assert.NotNull(claimed);
            Xunit.Assert.Equal(OperatorIntentStatus.Claimed, claimed!.Status);
            Xunit.Assert.Null(await store.ClaimNextAsync("goal-one", "conductor-b"));

            await store.CompleteAsync(
                claimed.Id,
                "conductor-a",
                OperatorIntentStatus.Applied,
                "Applied in tick 7.",
                DateTimeOffset.UtcNow);
            var outcome = await store.GetAsync(claimed.Id);

            Xunit.Assert.NotNull(outcome);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied, outcome!.Status);
            Xunit.Assert.Equal("Applied in tick 7.", outcome.Outcome);
            Xunit.Assert.Null(await store.ClaimNextAsync("goal-one", "conductor-a"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "OperatorIntentCoordinator_applies_manual_verification_with_full_evidence")]
    public async Task OperatorIntentCoordinatorAppliesManualVerificationWithFullEvidence()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "Verify through operator inbox");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Work completed.");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var verification = new TaskVerificationRecord(
                "manual-verification passed",
                root,
                0,
                "Operator inspected the result.",
                string.Empty,
                DateTimeOffset.UtcNow,
                ModelFitNote: "OpenAI/gpt-test - adequate");
            var intent = new OperatorIntentRecord(
                "verify-intent",
                "verify-key",
                OperatorIntentVerbs.VerifyManual,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new ManualVerificationOperatorIntentPayload(verification),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                [Path.Combine(root, "evidence.md")],
                "operator",
                "dashboard",
                "dashboard-operator-control",
                DateTimeOffset.UtcNow);
            await store.EnqueueAsync(intent);
            var coordinator = new OperatorIntentCoordinator(store);

            var result = coordinator.ExecutePending(kernel, goal);
            coordinator.CompletePersisted([goal.Id]);
            var outcome = await store.GetAsync(intent.Id);

            Xunit.Assert.True(result.MutatedGoalState);
            Xunit.Assert.Equal(verification, kernel.GetTask(goal.Id, task.Id).LastVerification);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied, outcome!.Status);
            Xunit.Assert.Equal("dashboard", outcome.Channel);
            Xunit.Assert.Equal("dashboard-operator-control", outcome.AuthenticationAssurance);
            Xunit.Assert.Equal(Path.Combine(root, "evidence.md"), Xunit.Assert.Single(outcome.PayloadFileReferences));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "OperatorIntentCoordinator_rejected_intent_has_pollable_outcome")]
    public async Task OperatorIntentCoordinatorRejectedIntentHasPollableOutcome()
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "Reject invalid operator intent");
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = CreateRetryIntent(
                goal.Id.Value,
                TaskId.New().Value,
                "rejected-intent",
                "rejected-key");
            await store.EnqueueAsync(intent);

            var result = new OperatorIntentCoordinator(store).ExecutePending(kernel, goal);
            var outcome = await store.GetAsync(intent.Id);

            Xunit.Assert.False(result.MutatedGoalState);
            Xunit.Assert.Contains(result.ProgressLines, line =>
                line.Contains("result=rejected", StringComparison.Ordinal));
            Xunit.Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
            Xunit.Assert.Contains("was not found", outcome.Outcome, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Theory(DisplayName = "OperatorIntentCoordinator_missing_retry_cause_creates_operator_actionable_hold")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task OperatorIntentCoordinatorMissingRetryCauseCreatesOperatorActionableHold(bool explicitUnknown)
    {
        var root = CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                "Classify an untyped operator retry");
            var task = goal.Tasks.Single();
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "failed");
            var latestRetryAtBefore = task.LatestRetryAt;
            var store = new SqliteOperatorIntentStore(
                Path.Combine(root, "operator-intents.db"),
                Path.Combine(root, "logs"));
            var intent = CreateRetryIntent(
                goal.Id.Value,
                task.Id.Value,
                "missing-cause-intent",
                "missing-cause-key",
                retryCause: explicitUnknown ? RetryCause.Unknown : null);
            await store.EnqueueAsync(intent);
            var coordinator = new OperatorIntentCoordinator(store);

            var result = coordinator.ExecutePending(kernel, goal);

            Xunit.Assert.True(result.MutatedGoalState);
            var request = Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id));
            Xunit.Assert.Equal(task.Id, request.TaskId);
            Xunit.Assert.Contains("retry cause", request.Question, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, kernel.GetTask(goal.Id, task.Id).Status);
            Xunit.Assert.Equal(latestRetryAtBefore, kernel.GetTask(goal.Id, task.Id).LatestRetryAt);
            Xunit.Assert.DoesNotContain(
                goal.Timeline,
                item => item.TaskId == task.Id && item.Kind == ProgressKind.TaskRetried);
            Xunit.Assert.Equal(OperatorIntentStatus.Claimed, (await store.GetAsync(intent.Id))!.Status);

            var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
            var restoredGoal = restored.GetGoal(goal.Id);
            var restoredRequest = Xunit.Assert.Single(restored.GetPendingHumanInput(goal.Id));
            restored.SubmitHumanInput(restoredRequest.Id, nameof(RetryCause.NewSourceFinding));
            var resumedCoordinator = new OperatorIntentCoordinator(store);
            var resumed = resumedCoordinator.ExecutePending(restored, restoredGoal);
            Xunit.Assert.True(resumed.MutatedGoalState);
            Xunit.Assert.Equal(RetryCause.NewSourceFinding, restored.GetTask(goal.Id, task.Id).PendingRetryCause);
            Xunit.Assert.Single(restoredGoal.Timeline.Where(item =>
                item.TaskId == task.Id && item.Kind == ProgressKind.TaskRetried));
            resumedCoordinator.CompletePersisted([goal.Id]);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "OperatorIntentStore_read_only_open_does_not_create_schema")]
    public void OperatorIntentStoreReadOnlyOpenDoesNotCreateSchema()
    {
        var root = CreateTempDirectory();
        try
        {
            var orchestratorDirectory = Path.Combine(root, ".orchestrator");
            _ = SqliteOperatorIntentStore.OpenExisting(
                orchestratorDirectory,
                Path.Combine(orchestratorDirectory, "logs"));

            Xunit.Assert.False(File.Exists(Path.Combine(
                orchestratorDirectory,
                SqliteOperatorIntentStore.DatabaseFileName)));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public async Task ActionableSummariesBatchCountsAndLatestTypedTimestamp()
    {
        var root = CreateTempDirectory();
        try
        {
            var dbPath = Path.Combine(root, SqliteOperatorIntentStore.DatabaseFileName);
            var logPath = Path.Combine(root, "logs");
            var store = new SqliteOperatorIntentStore(dbPath, logPath);
            var first = CreateRetryIntent("goal-one", "task-one", "intent-one", "key-one") with
            {
                CreatedAt = DateTimeOffset.Parse("2026-08-17T10:00:00Z")
            };
            var second = CreateRetryIntent("goal-one", "task-two", "intent-two", "key-two") with
            {
                CreatedAt = DateTimeOffset.Parse("2026-08-17T11:00:00Z")
            };
            await store.EnqueueAsync(first);
            await store.EnqueueAsync(second);
            await store.EnqueueAsync(CreateRetryIntent("goal-other", "task-three", "intent-three", "key-three"));

            var summaries = await new SqliteOperatorIntentStore(dbPath, logPath, readOnly: true)
                .ListActionableSummariesAsync(["goal-one"]);

            var summary = Xunit.Assert.Single(summaries).Value;
            Xunit.Assert.Equal(2, summary.Count);
            Xunit.Assert.Equal(second.CreatedAt, summary.LatestAt);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static OperatorIntentRecord CreateRetryIntent(
        string goalId,
        string taskId,
        string intentId,
        string idempotencyKey,
        string message = "retry",
        RetryCause? retryCause = null)
    {
        var payload = new RetryOperatorIntentPayload(message, RetryRoundKind.Mechanical, RetryCause: retryCause);
        return new OperatorIntentRecord(
            intentId,
            idempotencyKey,
            OperatorIntentVerbs.Retry,
            goalId,
            taskId,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            [],
            "operator",
            "test",
            "test-assurance",
            DateTimeOffset.UtcNow);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-operator-intent-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
