using System.Diagnostics;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

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
    internal WorkerBuildRequestChannel? Channel;
    internal OwnedProcessGroup.SuspendedProcessStart? Worker;
    internal const string Project = "src/Feature/Feature.csproj";
    internal const string Source = "src/Feature/Feature.cs";

    internal WorkerBuildRequestFixture(bool compilerFailure = false, bool lowIntegrity = false, bool managedHost = false)
    {
        _lowIntegrity = lowIntegrity;
        RepositoryRoot = Path.Combine(Root, "repository");
        Directory.CreateDirectory(Path.Combine(RepositoryRoot, "src", "Feature"));
        File.WriteAllText(Path.Combine(RepositoryRoot, Project), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(RepositoryRoot, Source), "public sealed class Feature { }");
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
        var at = DateTimeOffset.UtcNow;
        var command = managedHost ? "& '" + Path.Combine(FindRepository(), "scripts", "Invoke-WorkerBuildCheck.ps1").Replace("'", "''") + "' '" + Project + "'" : "fixture-command";
        Kernel.RecordTaskDispatch(GoalId, TaskId, new TaskDispatchRecord("fixture-worker", command, RepositoryRoot, at,
            SandboxLowIntegrity: lowIntegrity));
        WorkerBuildProjectScope.Prepare(RepositoryRoot, GoalId.Value, TaskId.Value, "main");
        Kernel.RecordDispatchBaseCommit(GoalId, TaskId, Git("rev-parse", "HEAD").Trim(),
            WorkerBuildProjectScope.ReadDispatchIdentity(RepositoryRoot));
        StatePath = Path.Combine(Root, ".orchestrator", "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        _ = StateDbMigrations.EnsureUpToDate(StatePath);
        Storage = new DotnetBuildStorageRoot(Path.Combine(Root, "build-storage"));
        Operations = new(StatePath, Root, Storage);
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
        Lifetime.Cancel();
        if (Worker is not null) { Worker.Group.Kill(); _ = Worker.WaitForOwnedExit(TimeSpan.FromSeconds(10)); }
        Channel?.Dispose();
        Worker?.Dispose();
        Lifetime.Dispose();
        Directory.Delete(Root, recursive: true);
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
