using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Each case owns its stores, processes and artifacts; no ambient provider or shared test resource.
public sealed class TaskIntentDispatchAdmissionTests
{
    [Xunit.Theory]
    [Xunit.InlineData(OperatorIntentVerbs.VerifyManual)]
    [Xunit.InlineData(OperatorIntentVerbs.Retry)]
    [Xunit.InlineData(OperatorIntentVerbs.Progress)]
    [Xunit.InlineData(OperatorIntentVerbs.Adjudicate)]
    [Xunit.InlineData(OperatorIntentVerbs.CancelDispatch)]
    [Xunit.InlineData(OperatorIntentVerbs.Answer)]
    public async Task Enqueue_TaskTarget_IsHeldBeforePendingReturns(string verb)
    {
        using var seed = new Scenario();
        var before = seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value);
        var intent = await seed.Store.EnqueueAsync(seed.Intent(verb));
        var reopened = seed.Reopen();
        Xunit.Assert.Equal(OperatorIntentStatus.Pending, intent.Status);
        Xunit.Assert.Equal(seed.Now.AddMinutes(5), intent.AdmissionExpiresAt);
        Xunit.Assert.Contains(seed.Task.Id.Value, reopened.ListHeldTaskIds(seed.Goal.Id.Value));
        var launches = 0;
        var denial = Xunit.Assert.Throws<TaskIntentDispatchSuppressedException>(() =>
            TaskIntentAdmission.Launch(before, "held-launch", () => { launches++; throw new ProbeReached(); }));
        Xunit.Assert.Equal(0, launches);
        Xunit.Assert.Equal(intent.Id, denial.Receipt.IntentId);
        Xunit.Assert.Equal(verb, denial.Receipt.Verb);
        Xunit.Assert.Equal(1, denial.Receipt.TaskVersion);
        Xunit.Assert.Equal(denial.Receipt, reopened.GetSuppression("held-launch"));
    }

    [Xunit.Fact]
    public async Task EnqueuedManualVerification_PreparedHost_StopsBeforeChildAndAppliesOnce()
    {
        using var seed = new Scenario();
        var reference = seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value);
        Xunit.Assert.Contains(seed.Task.Id, DispatchReadinessRules.SelectFirstParallelSafeAssignedBatch(seed.Goal, seed.Agents).TaskIds);
        var marker = Path.Combine(seed.Root, "paid-worker-started");
        var gate = Path.Combine(seed.Root, "start-gate");
        var parameters = seed.Parameters(reference, marker, gate);
        var parametersPath = Path.Combine(seed.Root, "probe.dispatch.json");
        DispatchProcessHost.WriteParameters(parametersPath, parameters);
        var start = ManagedHostStart(seed.Root, parametersPath);
        OperatorIntentRecord? persisted = null;
        Exception? selectionFailure = null;
        var host = await CliChildProcessRunner.RunAsync(start, started: _ =>
        {
            try
            {
                // The host is prepared and concurrent; application remains paused until it exits.
                persisted = seed.Store.EnqueueAsync(seed.ManualVerification()).GetAwaiter().GetResult();
                var selected = new GoalDispatchOperations().StartSubscriptionReadyTasks(
                    seed.Kernel, seed.Workspace, seed.Goal, seed.Agents,
                    WorkerProfileCatalog.Default(), runner: new BackgroundDispatchRunner(disableProcessStart: false,
                        startProcess: _ => throw new InvalidOperationException("Held selection attempted a worker host.")), commandExists: _ => true);
                Xunit.Assert.Empty(selected.Dispatches);
                Xunit.Assert.Empty(selected.Processes.Tasks);
            }
            catch (Exception ex) { selectionFailure = ex; }
            finally { File.WriteAllText(gate, "go"); } // let the owned host finish before reporting a fixture failure
        });
        Xunit.Assert.Null(selectionFailure);
        Xunit.Assert.Equal(TaskIntentAdmission.SuppressedExitCode, host.ExitCode);
        Xunit.Assert.False(File.Exists(marker));
        var receipt = seed.Reopen().GetSuppression(parameters.StdoutPath);
        Xunit.Assert.NotNull(receipt);
        Xunit.Assert.Equal(persisted!.Id, receipt.IntentId);
        Xunit.Assert.Equal(reference.GoalId, receipt.GoalId);
        Xunit.Assert.Equal(reference.TaskId, receipt.TaskId);
        Xunit.Assert.Equal(reference.Version, receipt.PreparedVersion);
        Xunit.Assert.Equal(OperatorIntentVerbs.VerifyManual, receipt.Verb);
        Xunit.Assert.True(DispatchExitArtifacts.TryRead(parameters.ExitCodePath, out var exit));
        Xunit.Assert.Equal(DispatchExitArtifactOrigin.Native, exit.Origin);
        Xunit.Assert.Equal(TaskIntentAdmission.SuppressedExitCode, exit.ExitCode);

        var coordinator = new OperatorIntentCoordinator(seed.Store, utcNow: () => seed.Now);
        Xunit.Assert.True(coordinator.ExecutePending(seed.Kernel, seed.Goal).MutatedGoalState);
        await seed.Repository.SaveAsync(seed.Kernel);
        coordinator.CompletePersisted([seed.Goal.Id]);
        var restored = await seed.Repository.LoadAsync();
        var reopenedCoordinator = new OperatorIntentCoordinator(seed.Reopen(), utcNow: () => seed.Now);
        reopenedCoordinator.ExecutePending(restored, restored.GetGoal(seed.Goal.Id));
        reopenedCoordinator.ExecutePending(restored, restored.GetGoal(seed.Goal.Id));
        Xunit.Assert.Equal(WorkTaskStatus.Completed, restored.GetTask(seed.Goal.Id, seed.Task.Id).Status);
        Xunit.Assert.Single(restored.GetGoal(seed.Goal.Id).Timeline,
            item => item.OperatorIntentApplied?.IntentId == persisted.Id);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, (await seed.Reopen().GetAsync(persisted.Id))!.Status);
        Xunit.Assert.Empty(seed.Reopen().ListHeldTaskIds(seed.Goal.Id.Value));
    }

    [Xunit.Fact]
    public async Task PendingIntent_OtherTaskAndGoal_RetainAdmission()
    {
        using var seed = new Scenario();
        await seed.Store.EnqueueAsync(seed.Intent(OperatorIntentVerbs.Progress));
        foreach (var target in new[] { (seed.Goal.Id.Value, "another-task"), ("another-goal", seed.Task.Id.Value) })
        {
            var reference = seed.Store.CaptureAdmission(target.Item1, target.Item2);
            Xunit.Assert.Throws<ProbeReached>(() => TaskIntentAdmission.Launch(reference,
                Guid.NewGuid().ToString("N"), () => throw new ProbeReached()));
        }
        Xunit.Assert.Single(seed.Store.ListHeldTaskIds(seed.Goal.Id.Value));
    }

    [Xunit.Fact]
    public async Task RejectedIntent_OldPreparationStaysFenced_FreshAdmissionIsReleased()
    {
        using var seed = new Scenario();
        var before = seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value);
        var intent = await seed.Store.EnqueueAsync(seed.Intent(OperatorIntentVerbs.Adjudicate));
        await seed.Store.ClaimNextAsync(seed.Goal.Id.Value, "test-owner");
        await seed.Store.CompleteAsync(intent.Id, "test-owner", OperatorIntentStatus.Rejected, "stale precondition", seed.Now);
        Xunit.Assert.Empty(seed.Store.ListHeldTaskIds(seed.Goal.Id.Value));
        var denial = Xunit.Assert.Throws<TaskIntentDispatchSuppressedException>(() =>
            TaskIntentAdmission.Launch(before, "stale", () => throw new ProbeReached()));
        Xunit.Assert.Equal("task-admission-version-changed", denial.Receipt.Reason);
        var fresh = seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value);
        Xunit.Assert.Throws<ProbeReached>(() => TaskIntentAdmission.Launch(fresh, "fresh", () => throw new ProbeReached()));
    }

    [Xunit.Fact]
    public async Task DuplicateAndMultipleIntents_DoNotRenewOrReleaseOtherHolds()
    {
        using var seed = new Scenario();
        var original = seed.Intent(OperatorIntentVerbs.Progress);
        var first = await seed.Store.EnqueueAsync(original);
        seed.Now = seed.Now.AddMinutes(2);
        var duplicate = await seed.Store.EnqueueAsync(original with { Id = Guid.NewGuid().ToString("N") });
        Xunit.Assert.Equal(first.AdmissionExpiresAt, duplicate.AdmissionExpiresAt);
        Xunit.Assert.Equal(1, seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value).Version);
        var second = await seed.Store.EnqueueAsync(seed.Intent(OperatorIntentVerbs.Progress));
        await seed.Store.ClaimNextAsync(seed.Goal.Id.Value, "test-owner");
        await seed.Store.CompleteAsync(first.Id, "test-owner", OperatorIntentStatus.Rejected, "stale", seed.Now);
        Xunit.Assert.Contains(seed.Task.Id.Value, seed.Reopen().ListHeldTaskIds(seed.Goal.Id.Value));
        await seed.Store.ClaimNextAsync(seed.Goal.Id.Value, "test-owner");
        await seed.Store.CompleteAsync(second.Id, "test-owner", OperatorIntentStatus.Applied, "applied", seed.Now);
        Xunit.Assert.Empty(seed.Reopen().ListHeldTaskIds(seed.Goal.Id.Value));
    }

    [Xunit.Fact]
    public async Task ExpiredClaim_RestartRejectsAndCannotReplay()
    {
        using var seed = new Scenario();
        var intent = await seed.Store.EnqueueAsync(seed.ManualVerification());
        await seed.Store.ClaimNextAsync(seed.Goal.Id.Value, OperatorIntentCoordinator.ClaimOwner);
        seed.Now = seed.Now.AddMinutes(6);
        var coordinator = new OperatorIntentCoordinator(seed.Reopen(), utcNow: () => seed.Now);
        coordinator.ExecutePending(seed.Kernel, seed.Goal);
        coordinator.ExecutePending(seed.Kernel, seed.Goal);
        Xunit.Assert.Equal(OperatorIntentStatus.Rejected, (await seed.Store.GetAsync(intent.Id))!.Status);
        Xunit.Assert.Empty(seed.Store.ListHeldTaskIds(seed.Goal.Id.Value));
        Xunit.Assert.DoesNotContain(seed.Goal.Timeline, item => item.OperatorIntentApplied?.IntentId == intent.Id);
    }

    [Xunit.Fact]
    public async Task PersistedEffect_ExpiredAfterCrash_RecoversAppliedWithoutReplay()
    {
        using var seed = new Scenario();
        var intent = await seed.Store.EnqueueAsync(seed.ManualVerification());
        new OperatorIntentCoordinator(seed.Store, utcNow: () => seed.Now).ExecutePending(seed.Kernel, seed.Goal);
        await seed.Repository.SaveAsync(seed.Kernel); // crash before inbox completion
        seed.Now = seed.Now.AddMinutes(6);
        var restored = await seed.Repository.LoadAsync();
        new OperatorIntentCoordinator(seed.Reopen(), utcNow: () => seed.Now)
            .ExecutePending(restored, restored.GetGoal(seed.Goal.Id));
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, (await seed.Store.GetAsync(intent.Id))!.Status);
        Xunit.Assert.Single(restored.GetGoal(seed.Goal.Id).Timeline, item => item.OperatorIntentApplied?.IntentId == intent.Id);
        Xunit.Assert.Empty(seed.Store.ListHeldTaskIds(seed.Goal.Id.Value));
    }

    [Xunit.Fact]
    public async Task AdmissionWriteFails_EnqueueRollsBackInboxAndRevision()
    {
        using var seed = new Scenario();
        var reference = seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value);
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = reference.StorePath, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER deny_admission BEFORE INSERT ON task_intent_admission BEGIN SELECT RAISE(ABORT, 'admission-write-failed'); END";
            cmd.ExecuteNonQuery();
        }
        var intent = seed.Intent(OperatorIntentVerbs.Progress);
        await Xunit.Assert.ThrowsAsync<SqliteException>(() => seed.Store.EnqueueAsync(intent));
        Xunit.Assert.Null(await seed.Store.GetAsync(intent.Id));
        Xunit.Assert.Equal(reference, seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value));
        Xunit.Assert.Empty(seed.Store.ListHeldTaskIds(seed.Goal.Id.Value));
    }

    [Xunit.Fact]
    public async Task LegacyInbox_MigrationBackfillsPendingAndClaimedHolds()
    {
        using var seed = new Scenario();
        var first = await seed.Store.EnqueueAsync(seed.Intent(OperatorIntentVerbs.Progress));
        var second = await seed.Store.EnqueueAsync(seed.Intent(OperatorIntentVerbs.Retry));
        await seed.Store.ClaimNextAsync(seed.Goal.Id.Value, OperatorIntentCoordinator.ClaimOwner);
        var path = seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value).StorePath;
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP TABLE task_intent_preparations; DROP TABLE task_intent_launches;
                DROP TABLE task_intent_suppressions; DROP TABLE task_intent_admission;
                ALTER TABLE operator_intents DROP COLUMN admission_expires_at;
                UPDATE store_schema_versions SET version = 1 WHERE store_name = 'operator-intents';
                """;
            cmd.ExecuteNonQuery();
        }
        var migrated = seed.Reopen();
        Xunit.Assert.Equal(2, migrated.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value).Version);
        Xunit.Assert.Contains(seed.Task.Id.Value, migrated.ListHeldTaskIds(seed.Goal.Id.Value));
        Xunit.Assert.NotNull((await migrated.GetAsync(first.Id))!.AdmissionExpiresAt);
        Xunit.Assert.NotNull((await migrated.GetAsync(second.Id))!.AdmissionExpiresAt);
        Xunit.Assert.NotNull(migrated.RecordSuppression(new(path, seed.Goal.Id.Value, seed.Task.Id.Value, 0), "legacy-launch"));
    }

    [Xunit.Fact]
    public async Task Preparation_ReloadAfterIntentRelease_RetainsOriginalRevision()
    {
        using var seed = new Scenario();
        var at = seed.Now;
        var old = seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value);
        seed.Store.BindPreparedAdmission(old, at);
        var intent = await seed.Store.EnqueueAsync(seed.Intent(OperatorIntentVerbs.Progress));
        await seed.Store.ClaimNextAsync(seed.Goal.Id.Value, "owner");
        await seed.Store.CompleteAsync(intent.Id, "owner", OperatorIntentStatus.Rejected, "stale", seed.Now);
        var current = seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value);
        seed.Store.BindPreparedAdmission(current, at); // duplicate preparation cannot relabel an old attempt
        Xunit.Assert.Equal(old.Version, seed.Reopen().GetPreparedAdmission(old.GoalId, old.TaskId, at).Version);
        Xunit.Assert.NotNull(seed.Store.RecordSuppression(old, "old-preparation"));
        seed.Store.BindPreparedAdmission(current, at.AddTicks(1));
        Xunit.Assert.Null(seed.Store.RecordSuppression(seed.Reopen().GetPreparedAdmission(old.GoalId, old.TaskId, at.AddTicks(1)), "new-preparation"));
    }

    [Xunit.Fact]
    public async Task GoalIntent_DoesNotReserveTaskAdmission()
    {
        using var seed = new Scenario();
        await seed.Store.EnqueueAsync(seed.Intent(OperatorIntentVerbs.Progress) with { TaskId = null });
        Xunit.Assert.Empty(seed.Store.ListHeldTaskIds(seed.Goal.Id.Value));
        Xunit.Assert.Equal(0, seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value).Version);
    }

    [Xunit.Fact]
    public async Task SuppressedHost_Reconciliation_DoesNotCreateFailedOrPaidRetryRound()
    {
        using var seed = new Scenario();
        var before = seed.Store.CaptureAdmission(seed.Goal.Id.Value, seed.Task.Id.Value);
        seed.Kernel.RecordTaskDispatch(seed.Goal.Id, seed.Task.Id, new TaskDispatchRecord("probe", "probe", seed.Root, seed.Now));
        seed.Store.BindPreparedAdmission(before, seed.Now);
        var admission = seed.Store.GetPreparedAdmission(before.GoalId, before.TaskId, seed.Now);
        var parameters = seed.Parameters(admission, Path.Combine(seed.Root, "child"), Path.Combine(seed.Root, "gate"));
        DispatchProcessHost.WriteParameters(Path.Combine(seed.Root, "probe.dispatch.json"), parameters);
        var intent = await seed.Store.EnqueueAsync(seed.ManualVerification());
        Xunit.Assert.NotNull(seed.Store.RecordSuppression(admission, parameters.StdoutPath));
        DispatchExitArtifacts.Write(parameters.ExitCodePath, DispatchExitArtifacts.Native(TaskIntentAdmission.SuppressedExitCode, "suppressed", seed.Now));
        var process = new TaskProcessRecord(12345, "probe", seed.Root, parameters.StdoutPath, parameters.StderrPath,
            parameters.ExitCodePath, seed.Now, null, null, ProcessIdentityStartedAt: seed.Now);
        seed.Kernel.RecordTaskProcessStarted(seed.Goal.Id, seed.Task.Id, process);
        seed.Kernel.ApplyPreparedRetryAdmission(seed.Goal.Id, seed.Task.Id,
            new(RetryAdmissionDecision.Allowed, new("reservation", RetryCause.ProviderInterruption,
            new(1, "unchanged"), RetryAdmissionDecision.Allowed, RetryAdmissionRoute.SameRole, PaidRouteClassification.Paid,
            seed.Now, seed.Now, WorkerStartedAt: seed.Now, ReservationOwnerId: "owner", WorkerStartClaimedAt: seed.Now,
            ReservationLeaseExpiresAt: seed.Now.AddMinutes(10))));
        var retryCount = seed.Goal.Timeline.Count(item => item.Kind == ProgressKind.TaskRetried);
        new BackgroundDispatchRunner(isStillRunning: _ => false, readProcessIdentity: _ => null,
            processCommandLineSnapshotFactory: () => new ProcessCommandLineSnapshot(new Dictionary<int, string>()))
            .RefreshLatestProcess(seed.Kernel, seed.Goal.Id, seed.Task.Id);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, seed.Task.Status);
        Xunit.Assert.Null(seed.Task.LastVerification);
        Xunit.Assert.Null(seed.Task.LastProcess);
        Xunit.Assert.Null(seed.Task.LastDispatch);
        var reservation = Xunit.Assert.Single(seed.Task.RetryAdmissionHistory);
        Xunit.Assert.Null(reservation.WorkerStartedAt);
        Xunit.Assert.Null(reservation.WorkerStartClaimedAt);
        Xunit.Assert.Equal(retryCount, seed.Goal.Timeline.Count(item => item.Kind == ProgressKind.TaskRetried));
        Xunit.Assert.Equal(OperatorIntentStatus.Pending, (await seed.Store.GetAsync(intent.Id))!.Status);
    }

    private static ProcessStartInfo ManagedHostStart(string root, string parametersPath)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.ArgumentList.Add(typeof(CliArgumentParser).Assembly.Location);
        start.ArgumentList.Add(DispatchProcessHost.SubcommandName);
        start.ArgumentList.Add(parametersPath);
        return start;
    }

    private sealed class ProbeReached : Exception { }

    private sealed class Scenario : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "mcg-intent-admission-" + Guid.NewGuid().ToString("N"));
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal readonly AgentOrchestratorKernel Kernel = new();
        internal readonly AgentDefinition[] Agents = [new(new AgentId("developer"), "Developer", AgentRole.Developer,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"))];
        internal readonly OrchestratorWorkspace Workspace;
        internal readonly SqliteOrchestratorStateRepository Repository;
        internal readonly SqliteOperatorIntentStore Store;
        internal readonly string StorePath;
        internal readonly Goal Goal;
        internal readonly TaskSpec Task;
        internal Scenario()
        {
            Directory.CreateDirectory(Root);
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Repository = InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
            Goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, Agents, "Task intent admission");
            Task = Goal.Tasks.Single();
            Kernel.SetGoalRefinedSpec(Goal.Id, new RefinedSpec("Task intent admission", ["Task intent wins admission"], VerificationClass.TestVerifiable, [], []));
            StorePath = Path.Combine(Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
            Store = new SqliteOperatorIntentStore(StorePath, Workspace.LogDirectory, utcNow: () => Now);
        }
        internal SqliteOperatorIntentStore Reopen() => new(StorePath, Workspace.LogDirectory, utcNow: () => Now);
        internal OperatorIntentRecord Intent(string verb) => new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), verb,
            Goal.Id.Value, Task.Id.Value, "{}", [], "operator", "cli", "local-process", Now);
        internal OperatorIntentRecord ManualVerification() => Intent(OperatorIntentVerbs.VerifyManual) with
        {
            PayloadJson = JsonSerializer.Serialize(new ManualVerificationOperatorIntentPayload(new TaskVerificationRecord(
                "manual-verification passed", Root, 0, "Inspected clean candidate", "", Now, ModelFitNote: "OpenAI/gpt-test - adequate")), JsonSerializerOptions.Web)
        };
        internal DispatchProcessHost.DispatchRunParameters Parameters(TaskIntentAdmissionReference reference, string marker, string gate) =>
            new("$null = [Console]::In.ReadToEnd(); [IO.File]::WriteAllText('" + marker.Replace("'", "''") + "','started')", Root,
                Path.Combine(Root, "probe.out.log"), Path.Combine(Root, "probe.err.log"), Path.Combine(Root, "probe.exit.txt"),
                Path.Combine(Root, "probe.heartbeat.json"), false, TaskIntentAdmission: reference, StartGatePath: gate);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
