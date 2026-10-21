using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: owns the repository, canonical stores, build grid and native process handles.
internal sealed class ExitReconcileBuildFixture : IDisposable
{
    internal readonly WorkerBuildRequestFixture Build;
    internal readonly OrchestratorWorkspace Workspace;
    internal AgentOrchestratorKernel Kernel;
    private Process? _host;
    private SpawnProcessIdentity? _hostIdentity;
    private string? _hostDiagnosticPath;
    private string? _childExitPath;
    private readonly WorkerBuildOutcome _expectedBuildOutcome;
    internal TaskSpec Task => Kernel.GetTask(Build.GoalId, Build.TaskId);
    internal Goal Goal => Kernel.GetGoal(Build.GoalId);

    internal ExitReconcileBuildFixture(bool compilerFailure = false, bool extraProject = false)
    {
        _expectedBuildOutcome = compilerFailure ? WorkerBuildOutcome.CompilerFailure : WorkerBuildOutcome.Success;
        Build = new(compilerFailure: compilerFailure, managedHost: true, goalBranch: true,
            extraProject: extraProject, reportWorkerResult: true);
        Kernel = Build.Kernel;
        // State deliberately lives outside the execution repository. No worker path selects it.
        Workspace = OrchestratorWorkspace.ForDirectory(Build.Root, Build.RepositoryRoot) with
        {
            OrchestratorDirectory = Path.GetDirectoryName(Build.StatePath)!,
            SqliteStatePath = Build.StatePath,
            LogDirectory = Path.Combine(Build.Root, "logs")
        };
        Kernel.SetGoalRefinedSpec(Build.GoalId, new RefinedSpec("Native reconciliation fixture",
            ["Consume only the applicable host build"], VerificationClass.TestVerifiable, [], []));
        Build.Save();
    }

    internal async Task<WorkerBuildInvocation> CompleteNativeDispatch()
    {
        Assert.True(WorktreeTreeDigest.TryCompute(Build.RepositoryRoot, out var candidateDigest, out var digestReason), digestReason);
        var readyPath = Path.Combine(Workspace.LogDirectory, "worker-child-ready");
        var releaseName = "Local\\mcg-exit-reconcile-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        string? heartbeatPath = null;
        var starter = new BackgroundDispatchRunner(disableProcessStart: false, startProcess: start =>
        {
            var gate = start.Environment[DispatchProcessHost.StartGatePathVariable];
            var parametersPath = start.ArgumentList[^1];
            var parameters = DispatchProcessHost.ReadParameters(parametersPath);
            _hostDiagnosticPath = parameters.HostDiagnosticPath;
            _childExitPath = parameters.ChildExitRecordPath;
            heartbeatPath = parameters.HeartbeatPath;
            // A build directly in the worker shell has no selected CLI child. Run the
            // unchanged command in a real descendant and hold it after compilation until
            // the host has retained that child's handle. Runtime files stay off the tree.
            var scriptPath = Path.Combine(Build.Root, "native-worker.ps1");
            File.WriteAllText(scriptPath, parameters.Command, new UTF8Encoding(false));
            var childCommand = "& '" + scriptPath.Replace("'", "''") + "'; $workerExit = $LASTEXITCODE; " +
                "[IO.File]::WriteAllText('" + readyPath.Replace("'", "''") + "', [string]$PID); " +
                "$release = [Threading.EventWaitHandle]::OpenExisting('" + releaseName + "'); " +
                "try { [void]$release.WaitOne() } finally { $release.Dispose() }; exit $workerExit";
            DispatchProcessHost.WriteParameters(parametersPath, parameters with
            {
                Command = "& '" + WorkerShell.Executable.Replace("'", "''") +
                    "' -NoProfile -NonInteractive -InputFormat None -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(childCommand)) + "; exit $LASTEXITCODE",
                HeartbeatIntervalMilliseconds = 100
            });
            // Retain runtime prerequisites only; no inherited provider, sandbox or gate switches.
            var allowed = new[] { "SystemRoot", "WINDIR", "PATH", "COMSPEC", "PATHEXT", "ProgramFiles",
                "ProgramFiles(x86)", "ProgramW6432", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
                "TEMP", "TMP", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86", "NUGET_PACKAGES" };
            start.Environment.Clear();
            foreach (var name in allowed)
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment[DispatchProcessHost.StartGatePathVariable] = gate;
            start.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = WorkerBuildRequestFixture.FindRepository();
            _host = Process.Start(start) ?? throw new InvalidOperationException("Owned dispatch host did not start.");
            _hostIdentity = SpawnProcessIdentityReader.ReadForRegistration(_host).Identity
                ?? throw new InvalidOperationException("Owned dispatch host has no process identity.");
            return _host;
        }) { WorkerBuildInvocations = Build.Operations };
        var result = starter.TryStartLatestDispatch(Kernel, Build.GoalId, Build.TaskId, Workspace.LogDirectory,
            checkpointBeforeWorkerStart: (_, _, _, _) => Save(), taskIntentAdmission: Build.Reference.Admission,
            sandboxOptions: new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount,
                WorkerSandboxOptions.DefaultCredentialTarget));
        Assert.Null(result.RecoveryAction);
        Assert.Null(result.FailureReason);
        var started = Assert.IsType<TaskProcessRecord>(result.ProcessRecord);
        Assert.NotNull(_host);
        Assert.Equal(started.ProcessId, _host.Id);
        Assert.NotNull(heartbeatPath);
        int selectedChildPid;
        try
        {
            selectedChildPid = await AwaitSelectedChild(readyPath, heartbeatPath);
        }
        finally { release.Set(); }
        // Hang-only failsafe. Completion is the host exit event, never an elapsed-time verdict.
        await _host.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        Kernel = Build.Operations.Load(Build.GoalId.Value);
        var invocation = StoredInvocation();
        Assert.True(WorktreeTreeDigest.TryCompute(Build.RepositoryRoot, out var completedDigest, out digestReason), digestReason);
        Assert.True(candidateDigest == completedDigest, "Native host runtime state changed candidate content. " + Describe());
        Assert.True(invocation.IsTerminal, Describe());
        Assert.True(invocation.CustodyReleased, Describe());
        Assert.False(invocation.Consumed, Describe());
        Assert.Equal(WorkerBuildPhase.Completed, invocation.Phase);
        Assert.Equal(Task.LastProcess!.ProcessId, invocation.Host.Pid);
        Assert.Equal(Task.LastProcess.ProcessIdentityStartedAt, invocation.Host.StartedAt);
        Assert.Equal(Task.LastProcess.ProcessIdentityImagePath, invocation.Host.ImagePath);
        Assert.Equal(BackgroundDispatchRunner.BuildDispatchId(Build.GoalId, Build.TaskId,
            Task.LastDispatch! with { ProviderSessionId = null }), invocation.DispatchId);
        Assert.True(invocation.Outcome == _expectedBuildOutcome, Describe());
        Assert.True(invocation.Children is { Count: > 0 }, Describe());
        Assert.All(invocation.Children!, child => Assert.False(WorkerBuildInvocationOperations.IsExactProcessAlive(child)));
        Assert.True(DispatchExitArtifacts.TryRead(Task.LastProcess.ExitCodePath, out var exit), Describe());
        Assert.Equal(DispatchExitArtifactOrigin.Native, exit.Origin);
        Assert.Equal(_host.ExitCode, exit.ExitCode);
        var childExit = JsonSerializer.Deserialize<DispatchProcessHost.DispatchChildExitRecord>(
            File.ReadAllText(_childExitPath!), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(childExit);
        Assert.Equal(selectedChildPid, childExit.ProcessId);
        Assert.Equal("exited", childExit.State);
        Assert.Equal(_host.ExitCode, childExit.ExitCode);
        Assert.True(WorkerResultBlockers.HasCompleteWorkerResult(File.ReadAllText(Task.LastProcess.StandardOutputPath)), Describe());
        Assert.Null(Task.LastVerification);
        // Reproduce the stale Assigned state consumed by batch recovery and dispatch preflight.
        RewriteTask(task => task with { Status = WorkTaskStatus.Assigned });
        return invocation;
    }

    private async Task<int> AwaitSelectedChild(string readyPath, string heartbeatPath)
    {
        var observed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(Workspace.LogDirectory);
        watcher.Created += Check;
        watcher.Changed += Check;
        watcher.Renamed += Check;
        watcher.Error += (_, args) => observed.TrySetException(args.GetException());
        watcher.EnableRaisingEvents = true;
        Check(null, null); // Close the subscription/read race if the child was already observed.
        var hostExit = _host!.WaitForExitAsync();
        var signal = await System.Threading.Tasks.Task.WhenAny(observed.Task, hostExit)
            .WaitAsync(TimeSpan.FromMinutes(5)); // Hang-only: waiting for child selection, not pacing.
        Assert.True(signal == observed.Task, "Native host exited before selecting the fixture child. " + Describe());
        return await observed.Task;

        void Check(object? sender, FileSystemEventArgs? args)
        {
            try
            {
                if (!File.Exists(readyPath) || !File.Exists(heartbeatPath) ||
                    !int.TryParse(File.ReadAllText(readyPath), out var pid)) return;
                using var heartbeat = JsonDocument.Parse(File.ReadAllText(heartbeatPath));
                var child = heartbeat.RootElement.GetProperty("childPid");
                if (child.ValueKind == JsonValueKind.Number && child.TryGetInt32(out var selected) && selected == pid)
                    observed.TrySetResult(pid);
            }
            catch (IOException) { } // The next owned file event retries an in-progress write.
            catch (JsonException) { }
        }
    }

    internal WorkerBuildInvocation StoredInvocation() => Assert.Single(
        Build.Operations.Load(Build.GoalId.Value).GetTask(Build.GoalId, Build.TaskId).WorkerBuildInvocations);

    internal void RewriteTask(Func<TaskSnapshot, TaskSnapshot> rewrite)
    {
        // Negative stimuli deliberately remove/corrupt fixture-owned evidence. Ordinary
        // snapshot saves correctly preserve history and reject conflicting origins.
        new SqliteOrchestratorStateRepository(Build.StatePath).TransactGoalStateAsync<int>(Build.GoalId, (state, _) =>
        {
            if (state is null) throw new InvalidOperationException("Fixture canonical goal is missing.");
            var next = state with { Goal = state.Goal with { Tasks = state.Goal.Tasks.Select(rewrite).ToArray() } };
            return System.Threading.Tasks.Task.FromResult((true, (GoalStateSnapshot?)next, 0));
        }).GetAwaiter().GetResult();
        Reload();
    }

    internal void Save() => new SqliteOrchestratorStateRepository(Build.StatePath).SaveAsync(Kernel).GetAwaiter().GetResult();
    internal void Reload() => Kernel = Build.Operations.Load(Build.GoalId.Value);

    internal int TerminalSweep(OrchestratorWorkspace? authority = null)
    {
        authority ??= Workspace;
        var result = TerminalGoalSweep.Run(Kernel, authority.ExecutionDirectory, "main", onlyGoalId: Build.GoalId,
            orchestratorDirectory: authority.OrchestratorDirectory);
        return result.Goals.SelectMany(goal => goal.Repairs).Count(repair => repair.Kind == "dispatch-exit-reconciled");
    }

    internal int BatchRecovery(OrchestratorWorkspace? authority = null)
    {
        var before = Task.VerificationHistory.Count;
        var observed = false;
        var driver = ConductorDriverTests.MakeDriver(getFacts: _ => GoalLifecycleFacts.None);
        var loop = new ConductorBatchLoop(workspace: authority ?? Workspace,
            recoverInterruptedDispatches: _ => observed = true,
            utcNow: () => Task.LastDispatch!.DispatchedAt.AddMinutes(1), writeJitter: () => 0,
            countLiveGroupedGates: () => 0);
        loop.Run(Kernel, driver, ConductorAutonomyPolicy.Conservative,
            Path.Combine(Build.Root, "absent-stop-file"), maxIterations: 1);
        Assert.True(observed, "The batch recovery composition was not invoked.");
        return Task.VerificationHistory.Count - before;
    }

    internal int DispatchPreflight(OrchestratorWorkspace? authority = null)
    {
        var before = Task.VerificationHistory.Count;
        var operations = new GoalDispatchOperations(isProcessRunning: _ => false, readProcessIdentity: _ => null);
        var batch = operations.SubscriptionDispatchReadyBatch(Kernel, authority ?? Workspace, Goal, [],
            new WorkerProfileCatalog([]));
        Assert.Empty(batch.Dispatches);
        return Task.VerificationHistory.Count - before;
    }

    internal void AssertSuccess(WorkerBuildInvocation before)
    {
        Assert.True(Task.LastVerification?.Succeeded == true, Describe());
        Assert.Equal(WorkTaskStatus.Completed, Task.Status);
        Assert.Equal(0, Task.LastProcess!.ExitCode);
        Assert.Equal(0, Task.LastProcess.ChildExitCode);
        Assert.Equal(DispatchExitArtifactOrigin.Native, Task.LastProcess.ExitArtifactOrigin);
        Assert.Single(Task.VerificationHistory);
        Assert.True(Task.LastVerification.CompletionVerdictVerifiedSuccess, Describe());
        Assert.NotNull(Task.LastDispatch!.ResultCommit);
        var after = StoredInvocation();
        Assert.Equal(before.Id, after.Id);
        Assert.True(after.Consumed);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Contains("worker_build_invocation=" + before.Id, Task.LastVerification.StandardError);
        Assert.Equal(0, Task.WorkerBuildCheckRecoveryCount);
        Assert.Equal(0, Task.EmptyOutputRetryCount);
        Assert.Equal(0, Task.CriterionRetryCount);
    }

    internal string Describe() => Build.DescribeHostBuildDiagnostics(_hostDiagnosticPath) +
        "\nverification=" + Task.LastVerification?.StandardError;

    public void Dispose()
    {
        try
        {
            if (_host is not null)
            {
                if (_hostIdentity is null) throw new InvalidOperationException("Cannot clean up an unidentified native host.");
                TestOwnedProcessStop.StopRootIfSame(_host, _hostIdentity, TimeSpan.FromSeconds(30));
                if (WorkerProcessJobs.TryGetRegisteredIdentity(_host.Id, out var registered))
                {
                    Assert.Equal(_hostIdentity, registered);
                    WorkerProcessJobs.Release(_host.Id);
                }
            }
        }
        catch { Build.PreserveArtifacts = true; throw; }
        finally { _host?.Dispose(); Build.Dispose(); }
    }
}
