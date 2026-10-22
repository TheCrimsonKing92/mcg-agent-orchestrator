using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique repositories/SQLite files/artifact identities; fake process defaults.
internal sealed class CompletionBuildEvidenceFixture : WorkerDispatchTestSupport
{
    internal readonly string Root;
    internal readonly string StatePath;
    internal readonly string ReceiptPath;
    internal readonly DotnetBuildStorageRoot Storage;
    internal AgentOrchestratorKernel Kernel;
    internal readonly GoalId GoalId;
    internal readonly TaskId TaskId;
    internal readonly FixtureClock Clock = new(DateTimeOffset.Parse("2026-10-12T12:00:00Z"));
    internal Goal Goal => Kernel.GetGoal(GoalId);
    internal TaskSpec Task => Kernel.GetTask(GoalId, TaskId);
    internal int Launches;
    internal int Authorizations;
    internal int Stops;
    internal bool Live = true;
    internal bool Unknown;
    internal bool FailLaunch;
    internal bool FailChildCheckpoint;
    internal bool FailLaunchIntent;
    internal bool BusyLease;
    internal bool FailStop;
    internal Action? BeforeChildCheckpoint;
    internal CompletionBuildEvidenceOperation? Launched;

    internal CompletionBuildEvidenceFixture()
    {
        Root = CreateSeededDispatchRepository();
        Storage = new DotnetBuildStorageRoot(Path.Combine(Root, "build-storage"));
        StatePath = Path.Combine(Root, "state.db");
        ReceiptPath = Path.Combine(Root, "build-receipt.json");
        var fixture = CreateCompletedGoalWorktreeDispatch(Root, AgentRole.Developer,
            WorkerResultBlock("src/Feature/Feature.cs", "implemented feature", "deferred - Acceptance owns tests"),
            string.Empty, Clock, worktree =>
            {
                var directory = Path.Combine(worktree, "src", "Feature");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "Feature.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
                File.WriteAllText(Path.Combine(directory, "Feature.cs"), "public sealed class Feature { }");
            });
        Kernel = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot(), Clock);
        GoalId = fixture.Goal.Id;
        TaskId = fixture.Task.Id;
        // This fixture owns the isolated bootstrap database; repositories never migrate implicitly.
        _ = StateDbMigrations.EnsureUpToDate(StatePath);
        new SqliteOrchestratorStateRepository(StatePath).SaveAsync(Kernel).GetAwaiter().GetResult();
    }

    internal CompletionBuildEvidenceCoordinator Coordinator(
        Func<ProcessStartInfo, Process>? startProcess = null,
        Func<Process, SpawnProcessIdentityReadResult>? readIdentity = null,
        Func<Process, (TextReader Output, TextReader Error)>? hostReaders = null,
        Func<DateTimeOffset>? utcNow = null) => new(StatePath, Root,
        utcNow: utcNow ?? (() => Clock.UtcNow),
        persist: FailChildCheckpoint || FailLaunchIntent || BeforeChildCheckpoint is not null ? (operation, previous) =>
        {
            if (FailLaunchIntent && previous is null)
                throw new IOException("injected launch intent persistence failure");
            if (operation.Phase == CompletionBuildEvidencePhase.Pending)
            {
                BeforeChildCheckpoint?.Invoke();
                if (FailChildCheckpoint) throw new IOException("injected child identity checkpoint failure");
            }
            return Persist(operation);
        } : null,
        launch: startProcess is not null ? null : operation =>
        {
            Launches++;
            if (FailLaunch) throw new IOException("injected child launch failure");
            Launched = operation;
            return new(42, Clock.UtcNow, "fake-owned-build-host", () =>
            {
                var stored = new SqliteOrchestratorStateRepository(StatePath).LoadGoalsAsync([GoalId]).GetAwaiter().GetResult()
                    .GetTask(GoalId, TaskId).CompletionBuildEvidence;
                Xunit.Assert.Equal(operation.Id, stored!.Id);
                Xunit.Assert.Equal(42, stored.ChildPid);
                Xunit.Assert.Equal(CompletionBuildEvidencePhase.Pending, stored.Phase);
                Authorizations++;
            }, () => { Stops++; Live = false; });
        },
        // Real children use the production identity probe; fake liveness is only meaningful
        // alongside the fake launcher and stop callback that update it.
        probe: startProcess is not null ? null : _ => Unknown ? CompletionBuildChildLiveness.Unknown : Live ? CompletionBuildChildLiveness.Live : CompletionBuildChildLiveness.Dead,
        stop: startProcess is not null ? null : _ =>
        {
            Stops++;
            if (FailStop) throw new InvalidOperationException("controlled child termination unconfirmed");
            Live = false;
        },
        begin: (goal, child, id) => BusyLease ? new(null, null) :
            new GoalEvidenceOperationCoordinator(new ReconcileSweepRemediationStore(StatePath), Root,
                TimeSpan.FromMinutes(10), () => Clock.UtcNow, () => child.Pid, () => id, () => child.StartedAt,
                new FakePidProbe()).TryBegin(goal, CompletionBuildEvidenceCoordinator.OperationName),
        startProcess: startProcess, readIdentity: readIdentity, hostReaders: hostReaders, storageRoot: Storage);

    private CompletionBuildEvidenceOperation Persist(CompletionBuildEvidenceOperation operation)
    {
        Kernel.RecordCompletionBuildEvidence(GoalId, TaskId, operation);
        new SqliteOrchestratorStateRepository(StatePath).SaveAsync(Kernel).GetAwaiter().GetResult();
        return operation;
    }

    internal BackgroundDispatchRunner Runner(CompletionBuildEvidenceCoordinator? coordinator = null) => new(Clock,
        isStillRunning: _ => false, completionBuildEvidence: coordinator ?? Coordinator(), resolveWorkerBuildReceiptPath: _ => ReceiptPath);

    internal void Reload() => Kernel = AgentOrchestratorKernel.FromSnapshot(
        new SqliteOrchestratorStateRepository(StatePath).LoadGoalsAsync([GoalId]).GetAwaiter().GetResult().ExportSnapshot(), Clock);

    internal void ResetWorkerMetadata()
    {
        Kernel.RequeueInterruptedDispatch(GoalId, TaskId, "controlled metadata reset", RetryCause.EnvironmentApparatusFailure);
        Xunit.Assert.Equal(Clock.UtcNow, Task.LatestRetryAt);
        Xunit.Assert.Null(Task.LastProcess);
        Xunit.Assert.Null(Task.LastDispatch);
        new SqliteOrchestratorStateRepository(StatePath).SaveAsync(Kernel).GetAwaiter().GetResult();
    }

    internal void Release(CompletionBuildEvidencePhase phase = CompletionBuildEvidencePhase.Success, bool receipt = true)
    {
        var operation = Task.CompletionBuildEvidence!;
        Directory.CreateDirectory(Path.GetDirectoryName(operation.ResultPath)!);
        File.WriteAllText(operation.ResultPath, JsonSerializer.Serialize(operation with
        { Phase = phase, BuildExitCode = phase == CompletionBuildEvidencePhase.Success ? 0 : 1, Diagnostic = "controlled owned build result" }));
        if (receipt) WriteReceipt(operation.Projects);
        Live = false; // Explicit release signal, never elapsed-time pacing.
    }

    internal void WriteReceipt(IReadOnlyList<string> projects)
    {
        Xunit.Assert.True(WorktreeTreeDigest.TryCompute(Task.LastProcess!.WorkingDirectory, out var digest, out _));
        File.WriteAllText(ReceiptPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, treeDigest = digest, digestAlgorithm = WorktreeTreeDigest.Algorithm,
            buildOutcome = "success", generatedAtUtc = Clock.UtcNow.ToString("O"),
            worktreeRoot = Task.LastProcess.WorkingDirectory, configuration = "Debug", projects
        }));
    }

    private sealed class FakePidProbe : IConductLockPidProbe
    {
        public bool IsRunning(int pid) => true;
        public bool IsSameProcess(int pid, DateTimeOffset expectedStartedAt) => true;
    }

    internal sealed class FixtureClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;
        public void Advance(TimeSpan elapsed) => UtcNow += elapsed;
    }
}
