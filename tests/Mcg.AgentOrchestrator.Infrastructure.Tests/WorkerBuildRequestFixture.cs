using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Win32.SafeHandles;

// Parallel-safe: isolated repository, canonical stores, slot grid, pipe and native child handles.
internal sealed class WorkerBuildRequestFixture : IDisposable
{
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "mcg-worker-build-request-" + Guid.NewGuid().ToString("N"));
    internal readonly string RepositoryRoot;
    internal readonly string StatePath;
    internal readonly AgentOrchestratorKernel Kernel = new();
    internal readonly GoalId GoalId;
    internal readonly TaskId TaskId;
    internal readonly WorkerBuildHostReference Reference;
    internal readonly WorkerBuildInvocationOperations Operations;
    internal readonly DotnetBuildStorageRoot Storage;
    internal readonly FixtureTime Time = new();
    internal readonly List<WorkerBuildInvocation> Observed = [];
    internal readonly CancellationTokenSource Lifetime = new();
    private readonly bool _lowIntegrity;
    private SafeFileHandle? _workerGroupObserver;
    internal WorkerBuildRequestChannel? Channel;
    internal OwnedProcessGroup.SuspendedProcessStart? Worker;
    internal bool PreserveArtifacts;
    internal const string Project = "src/Feature/Feature.csproj";
    internal const string Source = "src/Feature/Feature.cs";
    internal const string OtherProject = "src/Other/Other.csproj";
    internal const string OtherSource = "src/Other/Other.cs";

    internal WorkerBuildRequestFixture(bool compilerFailure = false, bool lowIntegrity = false, bool managedHost = false,
        bool goalBranch = false, bool extraProject = false, bool reportWorkerResult = false)
    {
        _lowIntegrity = lowIntegrity;
        RepositoryRoot = Path.Combine(Root, "repository");
        try
        {
            Directory.CreateDirectory(Path.Combine(RepositoryRoot, "src", "Feature"));
            File.WriteAllText(Path.Combine(RepositoryRoot, Project), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(RepositoryRoot, Source), "public sealed class Feature { }");
            if (extraProject)
            {
                Directory.CreateDirectory(Path.Combine(RepositoryRoot, "src", "Other"));
                File.WriteAllText(Path.Combine(RepositoryRoot, OtherProject),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(RepositoryRoot, OtherSource), "public sealed class Other { }");
            }
            Git("init", "-b", "main");
            Git("config", "user.email", "worker-build@fixture.invalid");
            Git("config", "user.name", "Worker build fixture");
            File.WriteAllText(Path.Combine(RepositoryRoot, ".git", "info", "exclude"), ".orchestrator-context/\nbin/\nobj/\n");
            Git("add", ".");
            Git("commit", "-m", "fixture seed");
            var task = new TaskSpec(TaskId.New(), "Implement fixture", AgentRole.Developer);
            var goal = Kernel.CreateGoal("Host worker build fixture", [task]);
            var agent = new AgentDefinition(new AgentId("fixture-worker"), "Fixture worker", AgentRole.Developer,
                new ModelProfile("OpenAI", "fixture", ModelCapability.Text, SubscriptionMode.ApiKey));
            Kernel.ActivateGoal(goal.Id, [agent]);
            GoalId = goal.Id;
            TaskId = task.Id;
            if (goalBranch) Git("switch", "-c", "goal/" + GoalId.Value[..8]);
            var at = DateTimeOffset.UtcNow;
            var command = managedHost ? "& '" + Path.Combine(FindRepository(), "scripts", "Invoke-WorkerBuildCheck.ps1").Replace("'", "''") + "' '" + Project + "'" : "fixture-command";
            if (reportWorkerResult)
            {
                if (!managedHost) throw new ArgumentException("Completion output requires a managed native host.");
                var result = string.Join('\n', "WORKER_RESULT:", "files: " + Source,
                    "commands: Invoke-WorkerBuildCheck.ps1", "tests: deferred - host build recorded",
                    "commit: none", "blockers: none", "assigned_scope_complete: true",
                    "model_fit: fixture/native - adequate - composition - owned compilation",
                    "skills: none", "confidence: high", "END_WORKER_RESULT");
                command += "; $buildExit = $LASTEXITCODE; [Console]::WriteLine([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(result)) + "'))); exit $buildExit";
            }
            Kernel.RecordTaskDispatch(GoalId, TaskId, new TaskDispatchRecord("fixture-worker", command, RepositoryRoot, at,
                SandboxLowIntegrity: lowIntegrity));
            WorkerBuildProjectScope.Prepare(RepositoryRoot, GoalId.Value, TaskId.Value, "main");
            WorkerBuildScopeDispatchPreparation.RecordDispatchBaseCommit(Kernel, GoalId, task,
                RepositoryRoot, Git("rev-parse", "HEAD").Trim());
            StatePath = Path.Combine(Root, ".orchestrator", "state.db");
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            _ = StateDbMigrations.EnsureUpToDate(StatePath);
            Storage = new DotnetBuildStorageRoot(Path.Combine(Root, "build-storage"));
            Operations = new(StatePath, reportWorkerResult ? RepositoryRoot : Root, Storage);
            var prepared = Operations.Prepare(Kernel, GoalId, TaskId);
            using var host = Process.GetCurrentProcess();
            var hostIdentity = WorkerBuildRequestChannel.ReadIdentity(host);
            if (!managedHost) Kernel.RecordTaskProcessStarted(GoalId, TaskId, new TaskProcessRecord(host.Id, command, RepositoryRoot,
                Path.Combine(Root, "host.out.log"), Path.Combine(Root, "host.err.log"), Path.Combine(Root, "host.exit.txt"),
                at, null, null, ProcessIdentityStartedAt: hostIdentity.StartedAt,
                ProcessIdentityImagePath: hostIdentity.ImagePath));
            var intents = new SqliteOperatorIntentStore(Path.Combine(Root, ".orchestrator", "operator-intents.db"), Root);
            var admission = intents.CaptureAdmission(GoalId.Value, TaskId.Value) with { DispatchedAt = at };
            intents.BindPreparedAdmission(admission, at);
            Reference = prepared with { Admission = admission };
            File.AppendAllText(Path.Combine(RepositoryRoot, Source), compilerFailure ? "\nthis is invalid C#;" : "\npublic sealed class Added { }");
            Save();
        }
        catch (Exception setup)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Worker build fixture setup and cleanup failed.", setup, cleanup); }
            throw;
        }
    }

    internal WorkerBuildRequestHost Host(Action<WorkerBuildInvocation>? observe = null) => new(Reference, Time,
        TimeSpan.FromMinutes(5), invocation => { lock (Observed) Observed.Add(invocation); observe?.Invoke(invocation); }, Storage);

    internal async Task<WorkerBuildInvocation> RunHelper(Action<WorkerBuildInvocation>? observe = null, bool assertAdapterExit = true)
    {
        Channel = new WorkerBuildRequestChannel(RepositoryRoot, Host(observe));
        var helper = Path.Combine(FindRepository(), "scripts", "Invoke-WorkerBuildCheck.ps1");
        var start = new ProcessStartInfo(WorkerShell.Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepositoryRoot };
        var workerTemp = Path.Combine(Root, "worker-temp");
        Directory.CreateDirectory(workerTemp);
        start.Environment["TEMP"] = workerTemp;
        start.Environment["TMP"] = workerTemp;
        if (_lowIntegrity) Assert.True(new IcaclsIntegrityLabeler().SetIntegrity(workerTemp, "(OI)(CI)L", recursive: true));
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-File", helper, Project }) start.ArgumentList.Add(arg);
        Worker = OwnedProcessGroup.StartSuspendedContainedWithFileCapture(start, Path.Combine(Root, "worker.out.log"), Path.Combine(Root, "worker.err.log"));
        if (!Worker.Group.TryDuplicateAccountingHandle(out _workerGroupObserver))
            throw new InvalidOperationException("Worker fixture could not retain its owned job observer before resume.");
        Channel.Register(Worker.Process, Worker.Group);
        if (_lowIntegrity) Worker.LowerIntegrityBeforeResume();
        Worker.Resume();
        // Hang-only failsafe: actual completion/outcome, never elapsed time, determines the verdict.
        await Worker.WaitForOwnedExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(4));
        Channel.Dispose();
        Channel = null;
        var invocation = Assert.Single(Operations.Load(GoalId.Value).GetTask(GoalId, TaskId).WorkerBuildInvocations);
        if (assertAdapterExit) Assert.Equal(invocation.Outcome == WorkerBuildOutcome.Success ? 0 : 1, Worker.TryReadOwnedExitCode());
        return invocation;
    }

    internal WorkerBuildConsumption Consume() => Operations.Consume(Kernel, GoalId, TaskId, [Source], [Source]);
    internal void Save() => new SqliteOrchestratorStateRepository(StatePath).SaveAsync(Kernel).GetAwaiter().GetResult();

    // Copy bounded, attributable diagnostics into the test receipt before successful custody
    // cleanup removes this fixture's tree. Logs explain failures; they never authorize builds.
    internal string DescribeHostBuildDiagnostics(string? hostDiagnosticPath = null)
    {
        var output = new StringBuilder($"Worker build diagnostics: root='{Root}'; goal={GoalId.Value}; task={TaskId.Value}\n");
        try
        {
            var task = Operations.Load(GoalId.Value).GetTask(GoalId, TaskId);
            output.AppendLine($"task_status={task.Status}; dispatch={Reference.DispatchId}; invocations={task.WorkerBuildInvocations.Count}");
            if (hostDiagnosticPath is not null) AppendTail(hostDiagnosticPath);
            if (task.LastProcess is { } process)
            {
                output.AppendLine($"host={process.ProcessId}/{process.ProcessIdentityStartedAt:O}/'{process.ProcessIdentityImagePath}'; exit={process.ExitCode}");
                AppendTail(process.StandardOutputPath);
                AppendTail(process.StandardErrorPath);
                AppendTail(process.ExitCodePath);
            }
            // These two failing controls each submit exactly one invocation and one project.
            // Bound diagnostic work even if a regression unexpectedly adds more records.
            foreach (var invocation in task.WorkerBuildInvocations.Take(2))
            {
                output.AppendLine($"invocation={invocation.Id}; outcome={invocation.Outcome}; compiler_exit={invocation.CompilerExitCode}; custody_released={invocation.CustodyReleased}; diagnostic={invocation.Diagnostic}");
                output.AppendLine("last_phases=" + string.Join(",", invocation.Observations.TakeLast(20).Select(o => $"{o.Phase}@{o.At:O}")));
                var logs = Path.Combine(DotnetBuildEnvironmentManager.GoalArtifactsPath(GoalId, Storage), "worker-host-builds", invocation.Id);
                foreach (var (child, index) in (invocation.Children ?? []).Take(2).Select((child, index) => (child, index)))
                {
                    output.AppendLine($"compiler={child.Pid}/{child.StartedAt:O}/'{child.ImagePath}'");
                    AppendTail(Path.Combine(logs, $"{index}.out.log"));
                    AppendTail(Path.Combine(logs, $"{index}.err.log"));
                }
            }
            if (Worker is not null)
            {
                AppendTail(Path.Combine(Root, "worker.out.log"));
                AppendTail(Path.Combine(Root, "worker.err.log"));
            }
        }
        catch (Exception error) { output.AppendLine($"Diagnostic snapshot unavailable: {error.GetType().Name}: {error.Message}"); }
        return output.ToString();

        void AppendTail(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                output.AppendLine($"log='{path}'; bytes={stream.Length}; tail_chars<=4096");
                if (stream.Length > 4096)
                {
                    // A repeated MSBuild stack can push its original exception out of
                    // the tail. Retain the first diagnostic too, with bounded scan/output.
                    using var prefixReader = new StreamReader(stream, leaveOpen: true);
                    var prefix = new char[65536];
                    var scanned = new string(prefix, 0, prefixReader.ReadBlock(prefix, 0, prefix.Length));
                    var first = scanned.IndexOf(": error ", StringComparison.OrdinalIgnoreCase);
                    var exception = scanned.IndexOf("Exception", StringComparison.Ordinal);
                    if (first < 0 || (exception >= 0 && exception < first)) first = exception;
                    if (first >= 0)
                    {
                        first = scanned.LastIndexOf('\n', first) + 1;
                        output.AppendLine("first_error_or_exception_chars<=2048; scan_chars<=65536");
                        output.AppendLine(scanned.Substring(first, Math.Min(2048, scanned.Length - first)));
                    }
                    else output.AppendLine("first_error_or_exception=not-found-in-initial-65536-chars");
                }
                stream.Seek(Math.Max(0, stream.Length - 4096), SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var tail = new char[4096];
                output.AppendLine(new string(tail, 0, reader.ReadBlock(tail, 0, tail.Length)));
            }
            catch (Exception error) { output.AppendLine($"log='{path}'; unavailable={error.GetType().Name}: {error.Message}"); }
        }
    }

    internal string Git(params string[] arguments)
    {
        var result = GitCli.RunForRepository(RepositoryRoot, arguments);
        Assert.True(result.Succeeded, result.Error);
        return result.Output;
    }

    internal static string FindRepository([CallerFilePath] string source = "")
    {
        if (VerifiedRepositoryRoot.TryGetVerifiedRoot(out var verified)) return verified;
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
    }

    public void Dispose()
    {
        List<Exception> failures = [];
        void Attempt(string phase, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(new InvalidOperationException($"Fixture {phase} failed at '{Root}'.", error)); }
        }

        Attempt("cancellation", Lifetime.Cancel);
        var exited = Worker is null;
        if (Worker is not null)
        {
            Attempt("owned exit confirmation", () =>
            {
                // The worker-exit controls may already have closed Group. Keep an independent
                // observer from before resume, so teardown still proves the whole job exited.
                bool groupEmpty;
                if (_workerGroupObserver is { IsClosed: false, IsInvalid: false })
                {
                    Worker.Group.Kill();
                    groupEmpty = OwnedProcessGroup.WaitForJobExit(_workerGroupObserver, TimeSpan.FromSeconds(10));
                }
                else groupEmpty = Worker.Group.TerminateAndConfirmEmpty(TimeSpan.FromSeconds(10));
                var workerExited = Worker.WaitForOwnedExit(TimeSpan.FromSeconds(10));
                exited = groupEmpty && workerExited;
                if (!exited) throw new InvalidOperationException("Owned worker/descendant termination is unconfirmed; retain fixture artifacts.");
            });
        }
        Attempt("channel disposal", () => Channel?.Dispose());
        Attempt("worker disposal", () => Worker?.Dispose());
        Attempt("job observer disposal", () => _workerGroupObserver?.Dispose());
        Attempt("lifetime disposal", Lifetime.Dispose);
        // Git's loose objects are read-only on Windows. Use the shared bounded cleanup,
        // and never replace a process-custody failure with a recursive-delete failure.
        if (!PreserveArtifacts && exited && failures.Count == 0)
            Attempt("directory cleanup", () => SharedTestSupport.RemoveTempDirectory(Root));
        if (failures.Count != 0)
            throw new AggregateException($"Worker build fixture cleanup failed. {DescribeResidual()}", failures);
    }

    private string DescribeResidual()
    {
        try
        {
            if (!Directory.Exists(Root)) return $"Root '{Root}' removed.";
            var entries = new DirectoryInfo(Root).EnumerateFileSystemInfos("*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(8)
                .Select(entry => $"'{entry.FullName}' attributes={entry.Attributes}");
            return $"Residual root '{Root}' attributes={File.GetAttributes(Root)}; " + string.Join("; ", entries);
        }
        catch (Exception error) { return $"Residual root '{Root}'; attribute inspection failed: {error.Message}"; }
    }

    internal sealed class FixtureTime : TimeProvider
    {
        private readonly ManualStewardTimeProvider _timers = new();
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timers.CreateTimer(callback, state, dueTime, period);
        internal void Advance(TimeSpan by) { _now += by; _timers.Advance(by); }
    }
}
