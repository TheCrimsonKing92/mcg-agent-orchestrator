using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("EnvMutation")]
public sealed class DispatchHostLifetimeHandoffTests : CliCommandTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task StartDispatch_CallerLifetime_RuntimeOwnershipSurvives(bool holdCaller)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var arm = await RunArmAsync(holdCaller);

        Assert.Equal(!holdCaller, arm.CallerExitedBeforeWorkerRelease);
        Assert.NotEqual(arm.CallerProcessId, arm.HostProcessId);
        Assert.NotEqual(arm.HostProcessId, arm.ChildProcessId);
        Assert.Equal(0, arm.HostExitCode);
        Assert.Equal(0, arm.ChildExitCode);
        Assert.Equal(WorkTaskStatus.Completed, arm.TaskStatus);
        Assert.True(arm.WorkerResultPresent);
        Assert.Equal(DispatchExitArtifactOrigin.Native, arm.ExitArtifactOrigin);
        Assert.False(arm.WasGracefullyDetachedByConductor);
        Assert.False(arm.RegisteredInSuccessorBeforeDetach);
        Assert.False(arm.RegisteredInSuccessorAfterDetach);
        Assert.True(arm.SuccessorDetachWasIdempotent);
        Assert.True(arm.UnrelatedProcessSurvived);
    }

    [Xunit.Fact]
    public async Task StartDispatch_RuntimeHandoffFailure_IsLoudAndIdentityBound()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        await using var unrelated = UnrelatedSleeper.Start();
        AppCaller? caller = null;
        try
        {
            InstallRuntimeHandoffFailureTrigger(fixture.Workspace.SqliteStatePath);
            caller = AppCaller.Start(
                fixture.Root,
                BuildArguments(
                    "start-dispatch", "--goal", fixture.Goal.Id.Value, "1", "--confirm-dispatch-start"));

            var start = await caller.WaitForExitAsync();

            Assert.Equal(1, start.ExitCode);
            Assert.Contains("worker-process-handoff-failed", start.StandardError, StringComparison.Ordinal);
            Assert.Contains("durable-lifecycle-transition", start.StandardError, StringComparison.Ordinal);
            var failedKernel = await fixture.Repository.LoadAsync();
            var failedGoal = failedKernel.GetGoal(fixture.Goal.Id);
            var failedTask = failedKernel.GetTask(fixture.Goal.Id, fixture.Task.Id);
            Assert.Equal(WorkTaskStatus.Failed, failedTask.Status);
            Assert.Null(failedTask.LastProcess);
            Assert.Contains(
                failedGoal.Timeline,
                entry => entry.TaskId == fixture.Task.Id &&
                         entry.Kind == ProgressKind.TaskFailed &&
                         entry.Message.Contains("worker-process-handoff-failed", StringComparison.Ordinal));

            var host = ReadLatestSpawnReceipt(fixture.Workspace.SqliteStatePath);
            Assert.Equal(caller.ProcessId, host.OwnerProcessId);
            Assert.Equal(SpawnRegistryLifecycle.Owned, host.Lifecycle);
            Assert.NotNull(host.ReleasedAt);
            Assert.Contains("detach-failed-reaped", host.LastDiagnostic, StringComparison.Ordinal);
            Assert.True(await WaitUntilNotRunningAsync(host.ProcessId, TimeSpan.FromSeconds(5)));
            Assert.False(WorkerProcessJobs.HasRegisteredJob(host.ProcessId));
            Assert.Empty(Directory.GetFiles(fixture.Workspace.LogDirectory, "*.start-gate"));

            var dispatchPath = Assert.Single(Directory.GetFiles(fixture.Workspace.LogDirectory, "*.dispatch.json"));
            using var dispatchJson = JsonDocument.Parse(await File.ReadAllTextAsync(dispatchPath));
            var exitCodePath = Assert.IsType<string>(dispatchJson.RootElement.GetProperty("exitCodePath").GetString());
            Assert.False(File.Exists(exitCodePath));
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            if (caller is not null)
            {
                await caller.DisposeAsync();
            }

            await CleanupFixtureAsync(fixture);
        }
    }

    [Xunit.Fact]
    public async Task StartDispatch_EarlyDeadHost_TransfersDurableAuthorityAndRequeues()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        AppCaller? caller = null;
        try
        {
            caller = AppCaller.Start(
                fixture.Root,
                BuildArguments(
                    "start-dispatch", "--goal", fixture.Goal.Id.Value, "1", "--confirm-dispatch-start"));
            var start = await caller.WaitForExitAsync();
            Assert.True(start.ExitCode == 0, start.StandardError + start.StandardOutput);

            var ownership = await WaitForOwnershipAsync(
                fixture.Workspace.SqliteStatePath,
                caller.ProcessId);
            var successorKernel = await WaitForPersistedProcessAsync(fixture, ownership.ProcessId);
            var startedProcess = Assert.IsType<TaskProcessRecord>(
                successorKernel.GetTask(fixture.Goal.Id, fixture.Task.Id).LastProcess);
            Assert.Equal(ownership.ProcessStartedAt, startedProcess.ProcessIdentityStartedAt);

            await KillExpectedHostAsync(ownership);
            Assert.Empty(Directory.GetFiles(fixture.Workspace.LogDirectory, "*.exit.txt"));

            WorkerProcessJobs.ConfigureRegistry(fixture.Workspace.SqliteStatePath);
            var successorRunner = new BackgroundDispatchRunner(isStillRunning: _ => false);

            Assert.Equal(1, successorRunner.DetachRunningProcessesForGoal(successorKernel, fixture.Goal.Id));
            var detachedProcess = Assert.IsType<TaskProcessRecord>(
                successorKernel.GetTask(fixture.Goal.Id, fixture.Task.Id).LastProcess);
            Assert.True(detachedProcess.WasGracefullyDetachedByConductor);
            Assert.Equal(
                SpawnRegistryLifecycle.ConductorDetached,
                Assert.Single(
                    WorkerProcessJobs.ListActiveRegistryEntriesForTests(),
                    entry => entry.Id == ownership.Id).Lifecycle);

            var syntheticFailure = detachedProcess with
            {
                CompletedAt = DateTimeOffset.UtcNow,
                ExitCode = 1,
                ExitArtifactOrigin = DispatchExitArtifactOrigin.Synthetic,
                ExitArtifactReason = "test host exit before worker release"
            };
            successorKernel.RecordTaskProcessRefreshed(fixture.Goal.Id, fixture.Task.Id, syntheticFailure, verification: null);
            successorKernel.ReportTaskProgress(
                fixture.Goal.Id,
                fixture.Task.Id,
                WorkTaskStatus.Failed,
                "test synthetic missing-exit outcome after durable graceful detach");

            Assert.Equal(1, successorRunner.RequeueInterruptedDispatches(successorKernel));
            Assert.Equal(WorkTaskStatus.Assigned, successorKernel.GetTask(fixture.Goal.Id, fixture.Task.Id).Status);
        }
        finally
        {
            if (caller is not null)
            {
                await caller.DisposeAsync();
            }

            await CleanupFixtureAsync(fixture);
        }
    }

    private static async Task<ArmReceipt> RunArmAsync(bool holdCaller)
    {
        var fixture = await CreateFixtureAsync();
        await using var unrelated = UnrelatedSleeper.Start();
        AppCaller? caller = null;
        try
        {
            Task<string>? startGateWait = null;
            if (holdCaller)
            {
                startGateWait = WaitForSingleFileAsync(fixture.Workspace.LogDirectory, "*.start-gate");
                caller = AppCaller.Start(fixture.Root, []);
                caller.Send(BuildInteractiveCommand(
                    "start-dispatch", fixture.Goal.Id.Value, "1", "--confirm-dispatch-start"));
                try
                {
                    _ = await startGateWait;
                }
                catch (TimeoutException timeout)
                {
                    caller.Send("exit");
                    var rejected = await caller.WaitForExitAsync();
                    throw new InvalidOperationException(
                        "Held caller did not release a start gate. stdout-tail=" + Tail(rejected.StandardOutput) +
                        "; stderr-tail=" + Tail(rejected.StandardError),
                        timeout);
                }
            }
            else
            {
                caller = AppCaller.Start(
                    fixture.Root,
                    BuildArguments(
                        "start-dispatch", "--goal", fixture.Goal.Id.Value, "1", "--confirm-dispatch-start"));
                var start = await caller.WaitForExitAsync();
                Assert.True(start.ExitCode == 0, start.StandardError + start.StandardOutput);
                Assert.Single(Directory.GetFiles(fixture.Workspace.LogDirectory, "*.start-gate"));
            }

            var callerExitedBeforeRelease = caller.HasExited;
            Assert.Equal(!holdCaller, callerExitedBeforeRelease);
            var ownership = await WaitForOwnershipAsync(
                fixture.Workspace.SqliteStatePath,
                caller.ProcessId);
            Assert.Equal(caller.ProcessId, ownership.OwnerProcessId);
            Assert.Equal(SpawnRegistryLifecycle.RuntimeOwned, ownership.Lifecycle);
            Assert.Contains("runtime-owned", ownership.LastDiagnostic, StringComparison.Ordinal);
            var dispatchPath = Assert.Single(Directory.GetFiles(fixture.Workspace.LogDirectory, "*.dispatch.json"));
            using var dispatchJson = JsonDocument.Parse(await File.ReadAllTextAsync(dispatchPath));
            var dispatch = dispatchJson.RootElement;
            var exitCodePath = Assert.IsType<string>(dispatch.GetProperty("exitCodePath").GetString());
            var heartbeatPath = Assert.IsType<string>(dispatch.GetProperty("heartbeatPath").GetString());
            var standardErrorPath = Assert.IsType<string>(dispatch.GetProperty("stderrPath").GetString());
            var standardOutputPath = Assert.IsType<string>(dispatch.GetProperty("stdoutPath").GetString());
            var hostDiagnosticPath = dispatch.TryGetProperty("hostDiagnosticPath", out var diagnosticPath)
                ? diagnosticPath.GetString() : null;
            Assert.False(File.Exists(exitCodePath));
            using var hostProcess = Process.GetProcessById(ownership.ProcessId);
            Assert.Equal(ownership.ProcessStartedAt.UtcDateTime, hostProcess.StartTime.ToUniversalTime());
            WorkerProcessJobs.ConfigureRegistry(fixture.Workspace.SqliteStatePath);
            var registeredInSuccessorBeforeDetach = WorkerProcessJobs.HasRegisteredJob(ownership.ProcessId);
            var successorKernel = await WaitForPersistedProcessAsync(fixture, ownership.ProcessId);
            var successorRunner = new BackgroundDispatchRunner();
            Assert.Equal(1, successorRunner.DetachRunningProcessesForGoal(successorKernel, fixture.Goal.Id));
            var successorProcess = Assert.IsType<TaskProcessRecord>(
                successorKernel.GetTask(fixture.Goal.Id, fixture.Task.Id).LastProcess);
            Assert.True(successorProcess.WasGracefullyDetachedByConductor);
            Assert.Equal(
                SpawnRegistryLifecycle.ConductorDetached,
                Assert.Single(
                    WorkerProcessJobs.ListActiveRegistryEntriesForTests(),
                    entry => entry.ProcessId == ownership.ProcessId).Lifecycle);
            var registeredInSuccessorAfterDetach = WorkerProcessJobs.HasRegisteredJob(ownership.ProcessId);
            Assert.Equal(0, successorRunner.DetachRunningProcessesForGoal(successorKernel, fixture.Goal.Id));
            Assert.Equal(0, WorkerProcessJobs.SweepStartupOrphans());
            Assert.False(hostProcess.HasExited);
            Assert.False(unrelated.HasExited);
            SpawnProcessIdentity childIdentity;
            try
            {
                childIdentity = await WaitForHeartbeatChildIdentityAsync(
                    heartbeatPath, hostDiagnosticPath, standardOutputPath, standardErrorPath);
            }
            catch (InvalidOperationException failure)
            {
                var hostState = hostProcess.HasExited ? $"exited:{hostProcess.ExitCode}" : "running";
                var callerState = caller.HasExited ? "exited" : "running";
                var standardErrorTail = File.Exists(standardErrorPath)
                    ? Tail(ReadAllTextShared(standardErrorPath))
                    : "missing";
                var exitArtifactTail = File.Exists(exitCodePath)
                    ? Tail(ReadAllTextShared(exitCodePath))
                    : "missing";
                throw new InvalidOperationException(
                    $"Dispatch host did not publish a child identity. host={hostState}; caller={callerState}; " +
                    $"stderr-tail={standardErrorTail}; exit-tail={exitArtifactTail}",
                    failure);
            }
            using var childProcess = Process.GetProcessById(childIdentity.ProcessId);
            Assert.Equal(childIdentity.StartedAt.UtcDateTime, childProcess.StartTime.ToUniversalTime());
            _ = childProcess.SafeHandle;

            var exitArtifactWait = WaitForSingleFileAsync(
                Path.GetDirectoryName(exitCodePath)!,
                Path.GetFileName(exitCodePath));
            File.WriteAllText(fixture.ReleasePath, "release");
            _ = await exitArtifactWait;
            await hostProcess.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await childProcess.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            if (holdCaller)
            {
                Assert.False(caller.HasExited);
                caller.Send("exit");
                var heldExit = await caller.WaitForExitAsync();
                Assert.True(heldExit.ExitCode == 0, heldExit.StandardError + heldExit.StandardOutput);
            }

            await using (var refreshCaller = AppCaller.Start(
                fixture.Root,
                BuildArguments("refresh-dispatch", "--goal", fixture.Goal.Id.Value, "1")))
            {
                var refresh = await refreshCaller.WaitForExitAsync();
                Assert.True(refresh.ExitCode == 0, refresh.StandardError + refresh.StandardOutput);
            }

            var completedTask = await LoadTaskAsync(fixture);
            var completedProcess = Assert.IsType<TaskProcessRecord>(completedTask.LastProcess);
            Assert.Equal(ownership.ProcessId, completedProcess.ProcessId);
            var childExit = JsonSerializer.Deserialize<DispatchProcessHost.DispatchChildExitRecord>(
                await File.ReadAllTextAsync(Assert.IsType<string>(completedProcess.ChildExitRecordPath)),
                JsonOptions);
            Assert.NotNull(childExit);
            if (childExit.ProcessId == 0)
            {
                Assert.Equal("not-observed", childExit.State);
                Assert.Null(childExit.ExitCode);
            }
            else if (childExit.State == "exited")
            {
                Assert.Equal(childIdentity.ProcessId, childExit.ProcessId);
                Assert.Equal(0, childExit.ExitCode);
            }
            else
            {
                Assert.Equal(childIdentity.ProcessId, childExit.ProcessId);
                Assert.Equal("running-after-root-exit", childExit.State);
                Assert.Null(childExit.ExitCode);
            }
            Assert.True(File.Exists(completedProcess.StandardErrorPath));

            return new ArmReceipt(
                caller.ProcessId,
                completedProcess.ProcessId,
                childExit.ProcessId,
                callerExitedBeforeRelease,
                completedProcess.ExitCode,
                childProcess.ExitCode,
                completedTask.Status,
                completedTask.LastVerification?.WorkerResultPresent == true,
                completedProcess.ExitArtifactOrigin,
                completedProcess.WasGracefullyDetachedByConductor,
                registeredInSuccessorBeforeDetach,
                registeredInSuccessorAfterDetach,
                successorProcess.WasGracefullyDetachedByConductor,
                !unrelated.HasExited);
        }
        finally
        {
            if (caller is not null)
            {
                await caller.DisposeAsync();
            }

            await CleanupFixtureAsync(fixture);
        }
    }

    private static async Task<DispatchFixture> CreateFixtureAsync()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        Directory.CreateDirectory(workspace.LogDirectory);
        var releasePath = Path.Combine(root, "release-worker");
        var workerPath = Path.Combine(root, "fixture-worker.ps1");
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".orchestrator/\nrelease-worker\n");
        await File.WriteAllTextAsync(Path.Combine(root, "seed.txt"), "Owned local dispatch fixture.\n");
        await File.WriteAllTextAsync(workerPath, BuildWorkerScript());
        RunGit(root, "init", "--quiet", "--initial-branch=main");
        RunGit(root, "add", ".gitignore", "seed.txt", "fixture-worker.ps1");
        RunGit(root, "-c", "user.name=Dispatch Fixture", "-c", "user.email=fixture@localhost", "commit", "--quiet", "-m", "Seed fixture");

        const string profileName = "lifetime-fixture";
        var command =
            $"& {PowerShellLiteral(WorkerShell.Executable)} -NoProfile -NonInteractive -File " +
            $"{PowerShellLiteral(workerPath)} -ReleasePath {PowerShellLiteral(releasePath)}";
        WorkerProfileStore.Save(
            workspace.WorkerProfilePath,
            new WorkerProfileCatalog([new WorkerProfile(profileName, command)]));
        var agent = new AgentDefinition(
            new AgentId("lifetime-reviewer"),
            "Lifetime fixture Reviewer",
            AgentRole.Reviewer,
            new ModelProfile("OpenAI", "local-fixture", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile(profileName));
        AgentCatalogStore.Save(workspace.AgentCatalogPath, new AgentCatalog([agent]));

        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Inspect the deterministic local fixture.", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Prove dispatch host caller lifetime handoff", [task]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "The deterministic local fixture completes.",
            ["The local worker returns a valid Reviewer result."],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, [agent]);
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord(
                profileName,
                command,
                root,
                DateTimeOffset.UtcNow,
                ProviderName: "OpenAI",
                ModelName: "local-fixture",
                DispatchLane: profileName));
        await repository.SaveAsync(kernel);
        return new DispatchFixture(root, workspace, repository, goal, task, releasePath);
    }

    private static async Task<TaskSpec> LoadTaskAsync(DispatchFixture fixture)
    {
        var kernel = await fixture.Repository.LoadAsync();
        return kernel.GetTask(fixture.Goal.Id, fixture.Task.Id);
    }

    private static async Task<AgentOrchestratorKernel> WaitForPersistedProcessAsync(
        DispatchFixture fixture,
        int processId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var kernel = await fixture.Repository.LoadAsync();
                if (kernel.GetTask(fixture.Goal.Id, fixture.Task.Id).LastProcess?.ProcessId == processId)
                {
                    return kernel;
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // The public caller may still be committing the command; retry the identity-bound read.
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for persisted dispatch host {processId}.");
    }

    private static void InstallRuntimeHandoffFailureTrigger(string databasePath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var trigger = connection.CreateCommand();
        trigger.CommandText =
            """
            CREATE TRIGGER fail_runtime_handoff
            BEFORE UPDATE OF lifecycle ON spawn_registry
            WHEN NEW.last_diagnostic LIKE 'spawn_registry: runtime-owned%'
            BEGIN
                SELECT RAISE(ABORT, 'forced runtime handoff failure');
            END
            """;
        trigger.ExecuteNonQuery();
    }

    private static HandoffFailureReceipt ReadLatestSpawnReceipt(string databasePath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT process_id, process_started_at, image_path, released_at, last_diagnostic,
                   owner_process_id, lifecycle
            FROM spawn_registry
            ORDER BY id DESC
            LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), "The failed runtime handoff must retain a durable spawn receipt.");
        return new HandoffFailureReceipt(
            reader.GetInt32(0),
            reader.GetDateTimeOffset(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDateTimeOffset(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            Enum.Parse<SpawnRegistryLifecycle>(reader.GetString(6)));
    }

    private static async Task<bool> WaitUntilNotRunningAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsRunning(processId))
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return !IsRunning(processId);
    }

    private static async Task KillExpectedHostAsync(SpawnRegistryEntry ownership)
    {
        using var host = Process.GetProcessById(ownership.ProcessId);
        Assert.Equal(ownership.ProcessStartedAt.UtcDateTime, host.StartTime.ToUniversalTime());
        host.Kill(entireProcessTree: true);
        await host.WaitForExitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task CleanupFixtureAsync(DispatchFixture fixture)
    {
        try
        {
            var task = await LoadTaskAsync(fixture);
            if (task.LastProcess is { } process)
            {
                WorkerProcessJobs.ConfigureRegistry(fixture.Workspace.SqliteStatePath);
                _ = WorkerProcessJobs.TryKillOrFallbackAndWait(process.ProcessId, TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
            // The per-fixture directory is still removed below; process cleanup is best-effort after a failed assertion.
        }
        finally
        {
            WorkerProcessJobs.ClearRegistryForTests();
            try { Directory.Delete(fixture.Root, recursive: true); } catch { }
        }
    }

    private static async Task<string> WaitForSingleFileAsync(string directory, string filter)
    {
        Directory.CreateDirectory(directory);
        var existing = Directory.GetFiles(directory, filter);
        if (existing.Length > 0)
        {
            return Assert.Single(existing);
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, filter)
        {
            NotifyFilter = NotifyFilters.FileName
        };
        watcher.Created += (_, args) => completion.TrySetResult(args.FullPath);
        watcher.Renamed += (_, args) => completion.TrySetResult(args.FullPath);
        watcher.EnableRaisingEvents = true;
        existing = Directory.GetFiles(directory, filter);
        if (existing.Length > 0)
        {
            completion.TrySetResult(Assert.Single(existing));
        }

        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
    }

    private static async Task<SpawnRegistryEntry> WaitForOwnershipAsync(string databasePath, int ownerProcessId)
    {
        var completion = new TaskCompletionSource<SpawnRegistryEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new SpawnRegistry(databasePath);

        void TryRead()
        {
            try
            {
                var matches = registry.ListActive()
                    .Where(entry => entry.OwnerProcessId == ownerProcessId)
                    .ToArray();
                if (matches.Length == 1)
                {
                    completion.TrySetResult(matches[0]);
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // The command may still own its write transaction; the next database event retries the read.
            }
        }

        using var watcher = new FileSystemWatcher(
            Path.GetDirectoryName(databasePath)!,
            Path.GetFileName(databasePath) + "*")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
        watcher.Changed += (_, _) => TryRead();
        watcher.Created += (_, _) => TryRead();
        watcher.Renamed += (_, _) => TryRead();
        watcher.EnableRaisingEvents = true;
        TryRead();
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    private static async Task<SpawnProcessIdentity> WaitForHeartbeatChildIdentityAsync(
        string heartbeatPath, string? hostDiagnosticPath, string stdoutPath, string stderrPath)
    {
        var completion = new TaskCompletionSource<SpawnProcessIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? lastObservedState = null;

        void TryRead()
        {
            try
            {
                using var heartbeat = JsonDocument.Parse(ReadAllTextShared(heartbeatPath));
                var root = heartbeat.RootElement;
                var state = root.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
                Volatile.Write(ref lastObservedState, state);
                if (state is not ("launched" or "running" or "exited")) return;
                if (!root.TryGetProperty("childPid", out var childPidElement) ||
                    childPidElement.ValueKind != JsonValueKind.Number ||
                    !childPidElement.TryGetInt32(out var childProcessId) ||
                    !root.TryGetProperty("ownedProcessIdentities", out var identitiesElement) ||
                    identitiesElement.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                foreach (var identityElement in identitiesElement.EnumerateArray())
                {
                    if (identityElement.GetProperty("processId").GetInt32() != childProcessId)
                    {
                        continue;
                    }

                    completion.TrySetResult(new SpawnProcessIdentity(
                        childProcessId,
                        identityElement.GetProperty("startedAt").GetDateTimeOffset(),
                        Assert.IsType<string>(identityElement.GetProperty("imagePath").GetString())));
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Atomic heartbeat replacement can race the read; the next file event retries it.
            }
        }

        using var watcher = new FileSystemWatcher(
            Path.GetDirectoryName(heartbeatPath)!,
            Path.GetFileName(heartbeatPath) + "*")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
        watcher.Changed += (_, _) => TryRead();
        watcher.Created += (_, _) => TryRead();
        watcher.Renamed += (_, _) => TryRead();
        watcher.EnableRaisingEvents = true;
        TryRead();
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException timeout)
        {
            // FileSystemWatcher notifications are advisory. A final shared read makes the durable
            // heartbeat authoritative without extending the bounded wait.
            TryRead();
            if (completion.Task.IsCompletedSuccessfully)
            {
                return completion.Task.Result;
            }

            throw new InvalidOperationException(
                DescribeMissingLaunchedHeartbeat(
                    heartbeatPath, Volatile.Read(ref lastObservedState), hostDiagnosticPath, stdoutPath, stderrPath),
                timeout);
        }
    }

    internal static string DescribeMissingLaunchedHeartbeat(
        string heartbeatPath, string? lastObservedState, string? hostDiagnosticPath, string stdoutPath, string stderrPath)
    {
        static string ReadTail(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "missing";
            try { return Tail(ReadAllTextShared(path)); }
            catch (IOException failure) { return $"unreadable:{failure.GetType().Name}"; }
        }

        return $"Launched heartbeat with an identity-bound child was not observed. last-state={lastObservedState ?? "none"}; " +
            $"heartbeat-tail={ReadTail(heartbeatPath)}; host-diagnostic-tail={ReadTail(hostDiagnosticPath)}; " +
            $"stdout-log={(File.Exists(stdoutPath) ? "present" : "absent")}; " +
            $"stderr-log={(File.Exists(stderrPath) ? "present" : "absent")}";
    }

    private static string[] BuildArguments(params string[] arguments) =>
        [.. arguments, "--project=default", "--tenant=default"];

    private static string BuildInteractiveCommand(params string[] arguments) =>
        string.Join(' ', arguments);

    private static string Tail(string value, int maximumLength = 4000) =>
        value.Length <= maximumLength ? value : value[^maximumLength..];

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string BuildWorkerScript() =>
        """
        param([Parameter(Mandatory=$true)][string]$ReleasePath)
        $directory = [System.IO.Path]::GetDirectoryName($ReleasePath)
        $leaf = [System.IO.Path]::GetFileName($ReleasePath)
        $watcher = [System.IO.FileSystemWatcher]::new($directory, $leaf)
        $watcher.EnableRaisingEvents = $true
        try {
            if (-not [System.IO.File]::Exists($ReleasePath)) {
                $change = $watcher.WaitForChanged([System.IO.WatcherChangeTypes]::Created, 60000)
                if ($change.TimedOut) { throw 'release signal was not observed' }
            }
        }
        finally {
            $watcher.Dispose()
        }
        @'
        WORKER_RESULT:
        files: none
        commands: local fixture
        tests: pass - local fixture
        commit: none
        blockers: none
        model_fit: local/fixture - adequate - inspection - deterministic fixture
        skills: none
        confidence: high
        findings: []
        touched_anchors: []
        criteria_verdicts: [{"criterion_index":0,"verdict":"met","evidence":"Local deterministic fixture completed."}]
        verdict: pass
        END_WORKER_RESULT
        '@
        """;

    private sealed record DispatchFixture(
        string Root,
        OrchestratorWorkspace Workspace,
        SqliteOrchestratorStateRepository Repository,
        Goal Goal,
        TaskSpec Task,
        string ReleasePath);

    private sealed record ArmReceipt(
        int CallerProcessId,
        int HostProcessId,
        int ChildProcessId,
        bool CallerExitedBeforeWorkerRelease,
        int? HostExitCode,
        int? ChildExitCode,
        WorkTaskStatus TaskStatus,
        bool WorkerResultPresent,
        DispatchExitArtifactOrigin ExitArtifactOrigin,
        bool WasGracefullyDetachedByConductor,
        bool RegisteredInSuccessorBeforeDetach,
        bool RegisteredInSuccessorAfterDetach,
        bool SuccessorDetachWasIdempotent,
        bool UnrelatedProcessSurvived);

    private sealed record HandoffFailureReceipt(
        int ProcessId,
        DateTimeOffset ProcessStartedAt,
        string ImagePath,
        DateTimeOffset? ReleasedAt,
        string? LastDiagnostic,
        int? OwnerProcessId,
        SpawnRegistryLifecycle Lifecycle);

    private sealed class AppCaller : IAsyncDisposable
    {
        private static readonly string[] AllowedEnvironmentNames =
        [
            "PATH", "PATHEXT", "SystemRoot", "WINDIR", "COMSPEC", "TEMP", "TMP",
            "USERPROFILE", "LOCALAPPDATA", "APPDATA", "ProgramFiles", "ProgramFiles(x86)"
        ];

        private readonly Process _process;
        private readonly Task<string> _standardOutput;
        private readonly Task<string> _standardError;
        private AppProcessResult? _result;

        private AppCaller(Process process)
        {
            _process = process;
            _standardOutput = process.StandardOutput.ReadToEndAsync();
            _standardError = process.StandardError.ReadToEndAsync();
        }

        public int ProcessId => _process.Id;
        public bool HasExited => _process.HasExited;

        public static AppCaller Start(string workingDirectory, IReadOnlyList<string> arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveDotnetHostPath(),
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var allowedEnvironment = AllowedEnvironmentNames
                .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
                .Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .ToArray();
            startInfo.Environment.Clear();
            foreach (var item in allowedEnvironment)
            {
                startInfo.Environment[item.Name] = item.Value!;
            }

            startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = workingDirectory;
            startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] =
                Path.Combine(workingDirectory, ".orchestrator", "test-dotnet");
            startInfo.Environment["MCG_ORCHESTRATOR_PROJECT"] = "default";
            startInfo.Environment["MCG_ORCHESTRATOR_TENANT"] = "default";
            startInfo.Environment["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"));
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start the owned app caller.");
            return new AppCaller(process);
        }

        public void Send(string command)
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }

        public async Task<AppProcessResult> WaitForExitAsync()
        {
            if (_result is not null)
            {
                return _result;
            }

            try
            {
                await _process.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            }
            catch
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(CancellationToken.None);
                }

                throw;
            }

            _result = new AppProcessResult(_process.ExitCode, await _standardOutput, await _standardError);
            return _result;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }

            _process.Dispose();
        }
    }

    private sealed record AppProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class UnrelatedSleeper : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardOutput;
        private readonly Task<string> _standardError;

        private UnrelatedSleeper(Process process)
        {
            _process = process;
            _standardOutput = process.StandardOutput.ReadToEndAsync();
            _standardError = process.StandardError.ReadToEndAsync();
        }

        public int ProcessId => _process.Id;

        public bool HasExited => _process.HasExited;

        public static UnrelatedSleeper Start()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = WorkerShell.Executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }.WithArguments(WorkerShell.BaseArguments().Concat(["Start-Sleep -Seconds 120"]));
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start unrelated sleeper control.");
            process.StandardInput.Close();
            _ = process.SafeHandle;
            return new UnrelatedSleeper(process);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }

            _ = await _standardOutput;
            _ = await _standardError;
            _process.Dispose();
        }
    }
}
