using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsCriticalDispatchLifecycleEventGate
{
    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void StaleCheckpointThenNextTickWritesOnlyTheCommittedDispatch()
    {
        using var repo = CreateSeededGitRepository();
        var root = repo.WorkingDirectory;
        SeedLocalSkillCatalog(root);
        RunGit(root, "branch", "-M", "main");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "seed skills");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        var clock = new MutableClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Checkpoint dispatch history");
        using var dispatchProcesses = new TestOwnedDispatchProcesses(kernel, () => kernel.GetGoal(goal.Id));
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Checkpoint dispatch history", ["Only committed dispatches reach disk."],
            VerificationClass.TestVerifiable, [], []));
        GoalWorktrees.Ensure(root, goal.Id);
        var taskId = goal.Tasks.Single().Id;
        var writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
        foreach (var entry in goal.Timeline)
            writer.AppendTimelineEvent(entry);
        repository.SaveAsync(kernel).GetAwaiter().GetResult();
        var baselines = kernel.ExportSnapshot().Goals.ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        var checkpointCalls = 0;
        var conflicts = 0;
        var savedCheckpoints = 0;
        const string operatorNote = "operator update after tick baseline";
        var driver = new ConductorDriver(
            kernel, workspace, new FakeAcceptanceVerifier(), DefaultAgents(), WorkerProfileCatalog.Default(),
            persistCriticalDispatchStart: (checkpoint, ids) =>
            {
                checkpointCalls++;
                if (checkpointCalls <= 2)
                {
                    Assert.Single(checkpoint.GetGoal(goal.Id).Timeline.Where(entry =>
                        entry.Kind == ProgressKind.TaskDispatchRecorded && entry.TaskId == taskId));
                    Assert.Empty(ReadDispatches(writer, goal.Id, taskId));
                }
                if (checkpointCalls == 1)
                {
                    var operatorKernel = repository.LoadAsync().GetAwaiter().GetResult();
                    operatorKernel.SetEventWriter(writer);
                    operatorKernel.RecordTaskNote(goal.Id, taskId, operatorNote);
                    repository.SaveAsync(operatorKernel).GetAwaiter().GetResult();
                }
                try
                {
                    CliPersistentStateRunner.PersistCriticalGoalSnapshotsOrThrow(
                        repository, checkpoint, ids, baselines, workspace.SqliteStatePath,
                        results =>
                        {
                            foreach (var result in results)
                            {
                                CliPersistentStateRunner.RebaseCheckpointAfterDurableSave(checkpoint, result);
                                if (result.PersistedSnapshot is { } persisted)
                                    baselines[result.GoalId] = persisted;
                            }
                        });
                    savedCheckpoints++;
                }
                catch (DispatchCheckpointConflictException ex)
                {
                    Assert.Contains("rejected stale state", ex.Message, StringComparison.Ordinal);
                    conflicts++;
                    throw;
                }
            });

        var first = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
        Assert.IsType<ConductorAdvanceOutcome.Held>(first.Outcome);
        Assert.Equal(1, conflicts);
        Assert.Equal(0, savedCheckpoints);
        Assert.Null(kernel.GetTask(goal.Id, taskId).LastDispatch);
        Assert.Null(kernel.GetTask(goal.Id, taskId).LastProcess);
        Assert.Empty(ReadDispatches(writer, goal.Id, taskId));
        Assert.Contains(kernel.GetGoal(goal.Id).Timeline, entry => entry.Message == operatorNote);

        clock.Advance();
        // The assembly's worker-command guard substitutes the subscription launcher with
        // a harmless local command; this never invokes a real model worker.
        var second = driver.AdvanceOnce(kernel.GetGoal(goal.Id), ConductorAutonomyPolicy.Conservative);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(second.Outcome);
        Assert.True(savedCheckpoints > 0);
        Assert.NotNull(kernel.GetTask(goal.Id, taskId).LastProcess);
        repository.SaveAsync(kernel).GetAwaiter().GetResult(); // End-of-tick persistence.
        var stored = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
        var loggedDispatch = Assert.Single(ReadDispatches(writer, goal.Id, taskId));
        var storedDispatch = Assert.Single(stored.Timeline.Where(entry =>
            entry.Kind == ProgressKind.TaskDispatchRecorded && entry.TaskId == taskId));
        Assert.Equal(StateLogEntry.FromProgressEvent(storedDispatch), loggedDispatch.Entry);
        Assert.True(loggedDispatch.Cursor < ReadLog(writer, goal.Id).First(line =>
            line.Entry.Kind == nameof(ProgressKind.TaskProcessStarted) && line.Entry.TaskId == taskId.Value).Cursor);
        using (var json = JsonDocument.Parse(File.ReadLines(writer.EventFilePath(goal.Id)).Single(line =>
            StateLogDivergenceComparer.ParseLine(line)?.Entry == loggedDispatch.Entry)))
            Assert.Equal("TaskDispatched", json.RootElement.GetProperty("eventType").GetString());
        var report = StateLogDivergenceComparer.Compare(
            stored.Timeline.Select(StateLogEntry.FromProgressEvent), ReadLog(writer, goal.Id));
        Assert.Equal(0, report.Lost);
        Assert.Equal(0, report.Repeated);
        Assert.Equal(0, report.StoredOnly);
    }

    [Fact]
    public void GenuineDuplicateWithoutRejectionStillReportsRepeated()
    {
        var (kernel, goal) = SimpleGoal();
        var writer = new GoalLifecycleEventWriter(ConductorDriverTests.CreateTempDirectory());
        foreach (var entry in goal.Timeline)
            writer.AppendTimelineEvent(entry);
        kernel.SetEventWriter(writer);
        DispatchTask(kernel, goal, goal.Tasks.Single());
        var dispatch = goal.Timeline.Single(entry => entry.Kind == ProgressKind.TaskDispatchRecorded);
        writer.AppendTimelineEvent(dispatch with { OccurredAt = dispatch.OccurredAt.AddTicks(1) });

        var report = StateLogDivergenceComparer.Compare(
            goal.Timeline.Select(StateLogEntry.FromProgressEvent), ReadLog(writer, goal.Id));
        Assert.Equal(2, ReadDispatches(writer, goal.Id, goal.Tasks.Single().Id).Length);
        Assert.Equal(1, report.Repeated);
        Assert.Equal(0, report.Lost);
        Assert.Equal(0, report.StoredOnly);
    }

    [Fact]
    public void GateReleasesAfterCommitAndHoldsLaterRefreshUntilItsCheckpoint()
    {
        var (kernel, goal) = SimpleGoal();
        var inner = DispatchProxy.Create<IGoalLifecycleEventWriter, RecordingWriterProxy>();
        var recorded = (RecordingWriterProxy)inner;
        var gate = new CriticalDispatchLifecycleEventGate(inner);
        kernel.SetEventWriter(gate);
        using var hold = gate.Hold(goal.Id, kernel);
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        Assert.Empty(recorded.Calls);
        gate.CommitCheckpoint(goal.Id, () => Assert.Empty(recorded.Calls));
        Assert.Single(recorded.Calls);
        var original = goal.Timeline.Last();
        var refreshed = original with { OccurredAt = original.OccurredAt.AddTicks(1) };
        gate.AppendTimelineEvent(refreshed);
        Assert.Single(recorded.Calls);
        gate.CommitCheckpoint(goal.Id, () => Assert.Single(recorded.Calls));
        Assert.Equal(new[] { original, refreshed }, recorded.Calls.Select(call => (ProgressEvent)call.Args.Single()!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopeEndDropsRolledBackOrEvictedDispatch(bool evict)
    {
        var (kernel, goal) = SimpleGoal();
        var before = kernel.ExportGoalSnapshot(goal.Id);
        var inner = DispatchProxy.Create<IGoalLifecycleEventWriter, RecordingWriterProxy>();
        var gate = new CriticalDispatchLifecycleEventGate(inner);
        kernel.SetEventWriter(gate);
        using (gate.Hold(goal.Id, kernel))
        {
            DispatchTask(kernel, goal, goal.Tasks.Single());
            if (evict)
            {
                var authoritative = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([before], []));
                authoritative.CancelGoal(goal.Id, "terminal authoritative state");
                kernel.ReplaceGoalWithSnapshot(authoritative.ExportGoalSnapshot(goal.Id));
                kernel.EvictTerminalGoalAggregates([goal.Id]);
            }
            else
                kernel.ReplaceGoalWithSnapshot(before);
        }
        Assert.Empty(((RecordingWriterProxy)inner).Calls);
    }

    [Fact]
    public void RejectedCheckpointDropsDispatchEvenWithoutAnAuthoritativeRebase()
    {
        var (kernel, goal) = SimpleGoal();
        var inner = DispatchProxy.Create<IGoalLifecycleEventWriter, RecordingWriterProxy>();
        var gate = new CriticalDispatchLifecycleEventGate(inner);
        kernel.SetEventWriter(gate);
        using (gate.Hold(goal.Id, kernel))
        {
            DispatchTask(kernel, goal, goal.Tasks.Single());
            Assert.Throws<DispatchCheckpointConflictException>(() => gate.CommitCheckpoint(goal.Id,
                () => throw new DispatchCheckpointConflictException("missing baseline")));
        }
        Assert.Empty(((RecordingWriterProxy)inner).Calls);
    }

    [Fact]
    public void NoCheckpointScopeEndPreservesSurvivingDispatchAndUnguardedWrites()
    {
        var (kernel, goal) = SimpleGoal();
        var inner = DispatchProxy.Create<IGoalLifecycleEventWriter, RecordingWriterProxy>();
        var recorded = (RecordingWriterProxy)inner;
        var gate = new CriticalDispatchLifecycleEventGate(inner);
        kernel.SetEventWriter(gate);
        using (gate.Hold(goal.Id, kernel))
        {
            DispatchTask(kernel, goal, goal.Tasks.Single());
            Assert.Empty(recorded.Calls);
            kernel.RecordTaskNote(goal.Id, goal.Tasks.Single().Id, "unchanged emission point");
            Assert.Single(recorded.Calls);
        }
        Assert.Equal(2, recorded.Calls.Count);
        var otherGoal = kernel.CreateGoal("unguarded goal");
        recorded.Calls.Clear();
        var dispatch = goal.Timeline.Single(entry => entry.Kind == ProgressKind.TaskDispatchRecorded);
        using (gate.Hold(goal.Id, kernel))
            gate.AppendTimelineEvent(dispatch with { GoalId = otherGoal.Id });
        Assert.Single(recorded.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckpointScopeTracksCommittedStateAndDiscardsRejectedEvents(bool commitFirst)
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var inner = DispatchProxy.Create<IGoalLifecycleEventWriter, RecordingWriterProxy>();
        var recorded = (RecordingWriterProxy)inner;
        var gate = new CriticalDispatchLifecycleEventGate(inner);
        kernel.SetEventWriter(gate);
        var reject = !commitFirst;
        var notifications = 0;
        CriticalDispatchLifecycleCheckpoint? checkpoint = null;
        checkpoint = CriticalDispatchLifecycleCheckpoint.Begin(gate, kernel, goal.Id,
            (current, ids) =>
            {
                Assert.Same(kernel, current);
                Assert.Equal(goal.Id, Assert.Single(ids));
                Assert.Empty(recorded.Calls);
                if (reject)
                    throw new DispatchCheckpointConflictException("missing baseline");
            },
            id =>
            {
                Assert.Equal(goal.Id, id);
                Assert.True(checkpoint!.HasCommitted);
                Assert.Single(recorded.Calls); // Release precedes the success notification.
                notifications++;
            });
        Assert.NotNull(checkpoint);
        using (checkpoint)
        {
            Assert.False(checkpoint.HasCommitted);
            DispatchTask(kernel, goal, task);
            if (commitFirst)
            {
                checkpoint.BeforeWorkerStart(kernel, goal.Id, task.Id, DispatchRecordCheckpointPhase.BeforeProcessStart);
                recorded.Calls.Clear();
                reject = true;
                gate.AppendTimelineEvent(goal.Timeline.Last() with { OccurredAt = goal.Timeline.Last().OccurredAt.AddTicks(1) });
            }
            var failure = Assert.Throws<DispatchRecordWriteException>(() => checkpoint.BeforeWorkerStart(
                kernel, goal.Id, task.Id, DispatchRecordCheckpointPhase.BeforeProcessStart));
            Assert.True(failure.PreservesAuthoritativeState);
            Assert.Equal(commitFirst, checkpoint.HasCommitted);
        }
        Assert.Equal(commitFirst ? 1 : 0, notifications);
        Assert.Empty(recorded.Calls); // Dispose cannot project a rejected dispatch.
    }

    [Fact]
    public void EveryInterfaceMemberIncludingDefaultMethodsForwardsExactly()
    {
        var inner = DispatchProxy.Create<IGoalLifecycleEventWriter, RecordingWriterProxy>();
        var recorded = (RecordingWriterProxy)inner;
        IGoalLifecycleEventWriter gate = new CriticalDispatchLifecycleEventGate(inner);
        foreach (var method in typeof(IGoalLifecycleEventWriter).GetMethods())
        {
            var args = method.GetParameters().Select(parameter => SampleArgument(parameter.ParameterType)).ToArray();
            recorded.Calls.Clear();
            method.Invoke(gate, args);
            var forwarded = Assert.Single(recorded.Calls);
            Assert.Equal(method, forwarded.Method);
            Assert.Equal(args, forwarded.Args);
        }
    }

    private static object? SampleArgument(Type type)
    {
        if (type == typeof(string)) return "forwarded value";
        if (type == typeof(GoalId)) return GoalId.New();
        if (type == typeof(TaskId)) return TaskId.New();
        if (type == typeof(ProgressEvent)) return new ProgressEvent(
            GoalId.New(), TaskId.New(), ProgressKind.TaskNote, "forwarded event", DateTimeOffset.MinValue);
        if (type == typeof(IReadOnlyList<string>)) return new[] { "entry" };
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private static StateLogLine[] ReadLog(GoalLifecycleEventWriter writer, GoalId goalId) =>
        File.ReadLines(writer.EventFilePath(goalId))
            .Select(StateLogDivergenceComparer.ParseLine).OfType<StateLogLine>().ToArray();

    private static StateLogLine[] ReadDispatches(GoalLifecycleEventWriter writer, GoalId goalId, TaskId taskId) =>
        ReadLog(writer, goalId).Where(line => line.Entry.Kind == nameof(ProgressKind.TaskDispatchRecorded) &&
            line.Entry.TaskId == taskId.Value).ToArray();

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-10-08T22:00:00Z");
        internal void Advance() => UtcNow = UtcNow.AddSeconds(1);
    }

    public class RecordingWriterProxy : DispatchProxy
    {
        public List<(MethodInfo Method, object?[] Args)> Calls { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add((targetMethod!, args!));
            return null;
        }
    }
}
