using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-case files/databases; all clocks, holder identities and jobs are fake.
public sealed class OperatorGateCancellationTests
{
    [Theory]
    [InlineData("cohort")]
    [InlineData("train")]
    public async Task Apply_GroupedGate_StopsHolderAndPersistsAppliedIntent(string kind)
    {
        using var f = new Fixture();
        var a = f.Goal();
        var b = f.Goal();
        var attempt = f.Group(kind, "group", a, b);
        Assert.True(f.Grouped.ReservesCapacity(attempt));
        var intent = await f.Queue(b);
        var coordinator = f.Coordinator();
        Assert.True(coordinator.ExecutePending(f.Kernel, b).MutatedGoalState);
        var stopped = ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath);
        Assert.Equal(new[] { attempt.AttemptId }, f.Stops);
        Assert.Equal(Fixture.Now, stopped.ReconciledAt);
        Assert.Equal("Reconciled", stopped.Outcome);
        Assert.Equal($"refused:operator-cancelled intent={intent.Id}", stopped.Detail);
        Assert.False(f.Grouped.ReservesCapacity(stopped));
        f.AssertEvent(intent, attempt.AttemptId, kind, a, b);
        coordinator.CompletePersisted([b.Id]);
        var persisted = await f.Store.GetAsync(intent.Id);
        Assert.Equal(OperatorIntentStatus.Applied, persisted!.Status);
        Assert.Equal($"Applied gate-cancel to goal {b.Id.Value[..8]}.", persisted.Outcome);
    }

    [Fact]
    public void Apply_ReopenedGroupedMember_PreservesBothMembers()
    {
        using var f = new Fixture();
        var a = f.Goal();
        var b = f.Goal();
        f.Group("cohort", "group", a, b);
        f.Kernel.RetryTaskWithAuthoritativeFeedback(b.Id, b.Tasks.Single().Id, "repair", RetryCause.NewTestFinding);
        Assert.Equal(GoalStatus.Active, b.Status);
        var before = b.Tasks.Select(t => t.Status).ToArray();
        var counts = new[] { a.AutomaticAcceptanceRetryCount, b.AutomaticAcceptanceRetryCount };
        var timelines = new[] { a, b }.Select(g => JsonSerializer.Serialize(g.Timeline)).ToArray();
        f.Handler.Apply(f.Kernel, b, f.Intent(b), Fixture.Now);
        Assert.Equal(new[] { "group" }, f.Stops);
        Assert.Equal(GoalStatus.Verified, a.Status);
        Assert.Equal(GoalStatus.Active, b.Status);
        Assert.Equal(before, b.Tasks.Select(t => t.Status));
        Assert.Equal(counts, new[] { a.AutomaticAcceptanceRetryCount, b.AutomaticAcceptanceRetryCount });
        Assert.All(new[] { a, b }, g => Assert.Null(g.RetainedAcceptanceFailure));
        Assert.Equal(timelines, new[] { a, b }.Select(g => JsonSerializer.Serialize(g.Timeline)));
        Assert.Empty(Directory.GetFiles(f.Root, "failing-test-index.v1.jsonl", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_SoloGate_ReleasesLeaseAndPreservesStaleOutcome(bool stale)
    {
        using var f = new Fixture();
        var goal = f.Goal();
        if (stale) f.Kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, goal.Tasks.Single().Id, "repair", RetryCause.NewSourceFinding);
        else Assert.True(f.Kernel.BeginGoalAcceptanceVerification(goal.Id, "gate"));
        var attempt = f.Single(goal, stale: stale);
        Assert.Single(f.Solo.GetAllCapacityReservingAttempts());
        var intent = f.Intent(goal);
        f.Handler.Apply(f.Kernel, goal, intent, Fixture.Now);
        Assert.Contains(f.Jobs, call => call.Job == AcceptanceHolderBinding.For(attempt).JobName && call.Cancel);
        Assert.Equal("Cancelled", f.Lease.Read(AcceptanceHolderBinding.For(attempt)).Phase);
        var stopped = Fixture.ReadSolo(attempt);
        Assert.Equal(stale ? ConductorParallelAcceptanceAttemptOutcome.StaleCandidate : ConductorParallelAcceptanceAttemptOutcome.Cancelled, stopped.Outcome);
        Assert.Equal(Fixture.Now, stopped.CompletedAt);
        Assert.Equal(stale ? Fixture.Earlier : Fixture.Now, stopped.ReconciledAt);
        Assert.Equal($"operator-cancelled intent={intent.Id}", stopped.Detail);
        Assert.Equal(attempt.TransientFailureCount, stopped.TransientFailureCount);
        Assert.Equal("1", File.ReadAllText(attempt.ExitCodePath));
        Assert.Empty(f.Solo.GetAllCapacityReservingAttempts());
        Assert.Equal(stale ? GoalStatus.Active : GoalStatus.Verified, goal.Status);
        if (!stale) Assert.Equal($"Operator gate-cancel intent {intent.Id} cancelled acceptance attempt {attempt.AttemptId}; goal returned to Verified without a verdict.", goal.Timeline.Last().Message);
        f.AssertEvent(intent, attempt.AttemptId, "solo", goal);
    }

    [Theory]
    [InlineData("no-live-gate-attempt")]
    [InlineData("attempt-not-live")]
    [InlineData("holder-not-cancellable")]
    [InlineData("holder-stop-unproven")]
    [InlineData("invalid-payload")]
    [InlineData("gate-cancel-unavailable")]
    public async Task Apply_RejectedRequest_PreservesMetadataAndGoalSnapshots(string code)
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var other = f.Goal();
        string? pin = null;
        if (code == "holder-not-cancellable") f.Single(goal, inProcess: true);
        else if (code != "no-live-gate-attempt") f.Group("cohort", "own", goal);
        if (code == "attempt-not-live") pin = f.Group("train", "other", other).AttemptId;
        if (code == "holder-stop-unproven") f.StopSucceeds = false;
        var metadata = f.Metadata();
        var snapshots = f.Snapshots();
        var intent = await f.Queue(goal, pin, code == "invalid-payload" ? " " : "stale");
        var result = f.Coordinator(code != "gate-cancel-unavailable").ExecutePending(f.Kernel, goal);
        var rejected = await f.Store.GetAsync(intent.Id);
        Assert.Equal(OperatorIntentStatus.Rejected, rejected!.Status);
        Assert.Equal($"Rejected gate-cancel: {code}", rejected.Outcome);
        Assert.Contains(result.ProgressLines, line => line.Contains($"result=rejected reason={code}", StringComparison.Ordinal));
        Fixture.AssertFilesEqual(metadata, f.Metadata());
        Assert.Equal(snapshots, f.Snapshots());
        Assert.Empty(f.Events());
    }

    [Fact]
    public void Apply_OneMember_LeavesUnrelatedTrainAndSoloByteIdentical()
    {
        using var f = new Fixture();
        var a = f.Goal();
        var c = f.Goal();
        var d = f.Goal();
        var e = f.Goal();
        f.Group("cohort", "target", a);
        var y = f.Group("train", "unrelated", c, d);
        var z = f.Single(e);
        // A fixture-owned lease artifact also checks that unrelated grouped holder state is untouched.
        File.WriteAllText(AcceptanceHolderBinding.For(y).LeasePath, "unrelated-grouped-holder");
        var paths = new[] { y.MetadataPath, AcceptanceHolderBinding.For(y).LeasePath, z.MetadataPath, AcceptanceHolderBinding.For(z).LeasePath };
        Assert.True(f.Grouped.ReservesCapacity(y));
        Assert.Equal(z.AttemptId, Assert.Single(f.Solo.GetAllCapacityReservingAttempts()).AttemptId);
        var before = paths.ToDictionary(path => path, File.ReadAllBytes);
        var snapshots = new[] { c, d, e }.Select(f.Snapshot).ToArray();
        f.Jobs.Clear();
        f.Handler.Apply(f.Kernel, a, f.Intent(a), Fixture.Now);
        Fixture.AssertFilesEqual(before, paths.ToDictionary(path => path, File.ReadAllBytes));
        Assert.Equal(new[] { "target" }, f.Stops);
        Assert.Empty(f.Jobs);
        Assert.Equal(snapshots, new[] { c, d, e }.Select(f.Snapshot));
        Assert.True(f.Grouped.ReservesCapacity(y));
        Assert.Equal(z.AttemptId, Assert.Single(f.Solo.GetAllCapacityReservingAttempts()).AttemptId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_OverlappingGates_StopsInOrdinalOrderOrOnlyPinnedAttempt(bool pinned)
    {
        using var f = new Fixture();
        var a = f.Goal();
        var x = f.Group("cohort", "x", a);
        var w = f.Group("train", "w", a);
        var beforeX = File.ReadAllBytes(x.MetadataPath);
        f.Handler.Apply(f.Kernel, a, f.Intent(a, pinned ? w.AttemptId : null), Fixture.Now);
        Assert.Equal(pinned ? new[] { "w" } : new[] { "w", "x" }, f.Stops);
        Assert.Equal(pinned ? 1 : 2, f.Events().Length);
        Assert.Contains("attempt=w ", f.Events()[0].Detail);
        if (pinned) Assert.Equal(beforeX, File.ReadAllBytes(x.MetadataPath));
        else Assert.Contains("attempt=x ", f.Events()[1].Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_ReplayedAfterCrash_UsesDurableAttemptMarker(bool single)
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var path = single ? f.Single(goal).MetadataPath : f.Group("cohort", "group", goal).MetadataPath;
        var intent = await f.Queue(goal);
        // Crash after the holder effect, before the kernel application marker or intent completion.
        Assert.NotNull(await f.Store.ClaimNextAsync(goal.Id.Value, OperatorIntentCoordinator.ClaimOwner));
        f.Handler.Apply(f.Kernel, goal, intent, Fixture.Now);
        var bytes = File.ReadAllBytes(path);
        var stops = f.Stops.Count;
        var jobs = f.Jobs.Count;
        f.Handler.Apply(f.Kernel, goal, intent, Fixture.Now);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(stops, f.Stops.Count);
        Assert.Equal(jobs, f.Jobs.Count);
        Assert.Single(f.Events());
        var fresh = f.Coordinator();
        Assert.True(fresh.ExecutePending(f.Kernel, goal).MutatedGoalState);
        fresh.CompletePersisted([goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await f.Store.GetAsync(intent.Id))!.Status);
        Assert.Single(f.Events());
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(stops, f.Stops.Count);
        Assert.Equal(jobs, f.Jobs.Count);
    }

    [Fact]
    public void Apply_LaterStopFails_KeepsEarlierCancellationAndEvent()
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var a = f.Group("cohort", "a", goal);
        var b = f.Single(goal, inProcess: true);
        var intent = f.Intent(goal);
        Assert.Equal("holder-not-cancellable", Assert.Throws<InvalidOperationException>(() =>
            f.Handler.Apply(f.Kernel, goal, intent, Fixture.Now)).Message);
        Assert.NotNull(ConductorGroupedGateAttemptCoordinator.Read(a.MetadataPath).ReconciledAt);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, Fixture.ReadSolo(b).Outcome);
        f.AssertEvent(intent, "a", "cohort", goal);
    }

    [Theory]
    [InlineData("follower")]
    [InlineData("focused-evidence")]
    public void Apply_ExcludedAttempt_LeavesHolderAndMetadataUntouched(string kind)
    {
        using var f = new Fixture();
        var goal = f.Goal();
        if (kind == "follower") f.Group(kind, "excluded", goal);
        else
        {
            var attempt = f.Single(goal);
            Fixture.SaveSolo(attempt with { Kind = kind });
        }
        var before = f.Metadata();
        f.Jobs.Clear();
        Assert.Equal("no-live-gate-attempt", Assert.Throws<InvalidOperationException>(() =>
            f.Handler.Apply(f.Kernel, goal, f.Intent(goal), Fixture.Now)).Message);
        Fixture.AssertFilesEqual(before, f.Metadata());
        Assert.Empty(f.Stops);
        Assert.DoesNotContain(f.Jobs, j => j.Cancel);
        Assert.Empty(f.Events());
    }

    [Theory]
    [InlineData("unsettled")]
    [InlineData("lease-fault")]
    [InlineData("writer-busy")]
    public void CancelForOperator_UnprovenStop_DoesNotPublishCancellation(string fault)
    {
        using var f = new Fixture();
        var attempt = f.Single(f.Goal());
        var before = File.ReadAllBytes(attempt.MetadataPath);
        f.CancelSettles = fault != "unsettled";
        f.CancelThrows = fault == "lease-fault";
        f.WriterAvailable = fault != "writer-busy";
        Assert.Equal(OperatorGateStopResult.Unproven, f.Solo.CancelForOperator(attempt, "intent"));
        Assert.Equal(before, File.ReadAllBytes(attempt.MetadataPath));
        Assert.False(File.Exists(attempt.ExitCodePath));
        Assert.Contains(f.Jobs, j => j.Cancel);
    }

    [Theory]
    [InlineData("result")]
    [InlineData("exit")]
    [InlineData("reconciled")]
    [InlineData("completed")]
    public void CancelForOperator_FinishedGroupedAttempt_DoesNotStopHolder(string state)
    {
        using var f = new Fixture();
        var attempt = f.Group("cohort", "finished", f.Goal());
        if (state == "result") File.WriteAllText(attempt.ResultPath, "{}");
        if (state == "exit") File.WriteAllText(attempt.ExitCodePath, "0");
        if (state == "reconciled") ConductorGroupedGateAttemptCoordinator.Save(attempt with { ReconciledAt = Fixture.Earlier });
        if (state == "completed") ConductorGroupedGateAttemptCoordinator.Save(attempt with { Outcome = "Completed" });
        var before = File.ReadAllBytes(attempt.MetadataPath);
        Assert.False(f.Grouped.CancelForOperator(attempt, "intent"));
        Assert.Equal(before, File.ReadAllBytes(attempt.MetadataPath));
        Assert.Empty(f.Stops);
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Fixture.Now; }
    private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20000);
        internal static readonly DateTimeOffset Earlier = Now.AddMinutes(-1);
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "gate-cancel-" + Guid.NewGuid().ToString("N"));
        internal readonly AgentOrchestratorKernel Kernel = new(new Clock());
        internal readonly ConductorParallelAcceptanceAttemptCoordinator Solo;
        internal readonly ConductorGroupedGateAttemptCoordinator Grouped;
        internal readonly AcceptanceHolderLease Lease;
        internal readonly SqliteOperatorIntentStore Store;
        internal readonly OperatorGateCancellation Handler;
        internal readonly List<string> Stops = [];
        internal readonly List<(string Job, bool Cancel)> Jobs = [];
        internal bool StopSucceeds = true;
        internal bool CancelSettles = true;
        internal bool CancelThrows;
        internal bool WriterAvailable = true;
        private readonly string soloRoot;
        private readonly string groupedRoot;
        private readonly string eventPath;
        private readonly SpawnProcessIdentity holder = new(222, Earlier, "C:\\fake-dotnet.exe");
        private readonly SpawnProcessIdentity generation = new(111, Earlier, "C:\\fake-dotnet.exe");
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            soloRoot = Path.Combine(Root, "solo");
            groupedRoot = Path.Combine(Root, "grouped");
            eventPath = Path.Combine(Root, "conduct-events.log");
            var cancelled = new HashSet<string>(StringComparer.Ordinal);
            Lease = new(utcNow: () => Now, classify: _ => SpawnTrackedProcessStatus.LiveMatch,
                job: (job, _, _, cancel) =>
                {
                    Jobs.Add((job, cancel));
                    if (cancel && CancelThrows) throw new IOException("fake lease fault");
                    if (cancel && CancelSettles) cancelled.Add(job);
                    return new(!cancelled.Contains(job), cancelled.Contains(job), "fake-job");
                }, attach: _ => new EmptyDisposable(), stopUnclaimed: _ => false);
            Solo = new(soloRoot, "main", utcNow: () => Now, isProcessAlive: _ => true,
                holderLease: Lease, generationIdentity: generation,
                tryAcquireAttemptWriterLease: (_, _) => WriterAvailable ? new EmptyDisposable() : null);
            Grouped = new(groupedRoot, generationId: 111, utcNow: () => Now, isProcessAlive: _ => true,
                classifyHolder: _ => SpawnTrackedProcessStatus.LiveMatch, stop: attempt =>
                {
                    Stops.Add(attempt.AttemptId);
                    return StopSucceeds;
                }, generationIdentity: generation);
            Store = SqliteOperatorIntentStore.ForDirectories(Root, Path.Combine(Root, "logs"));
            Handler = new(Solo, Grouped, new ConductEventLogWriter(eventPath, utcNow: () => Now));
        }
        internal Goal Goal()
        {
            var goal = Kernel.CreateGoal("Member", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            Kernel.RecordTaskVerification(goal.Id, goal.Tasks.Single().Id,
                ManualVerificationRecorder.Create(true, "Passed.", Root, Now));
            Assert.Equal(GoalStatus.Verified, goal.Status);
            return goal;
        }
        internal ConductorGroupedGateAttempt Group(string kind, string id, params Goal[] members)
        {
            var dir = Path.Combine(groupedRoot, id);
            Directory.CreateDirectory(dir);
            var attempt = new ConductorGroupedGateAttempt(id, kind,
                members.Select(g => new ConductorGroupedGateMember(g.Id.Value, "branch", "candidate", [], [], ChangeRiskTier.Behavior)).ToArray(),
                "main", "tree", "manifest", "identity-" + id, Earlier, 222, 111,
                Path.Combine(dir, id + ".attempt.json"), Path.Combine(dir, "result"), Path.Combine(dir, "exit"),
                Path.Combine(dir, "out"), Path.Combine(dir, "err"), Root, "{}", Earlier, holder.ImagePath);
            ConductorGroupedGateAttemptCoordinator.Save(attempt);
            return attempt;
        }
        internal ConductorParallelAcceptanceAttempt Single(Goal goal, bool stale = false, bool inProcess = false)
        {
            var id = "solo-" + goal.Id.Value[..8];
            var dir = Path.Combine(soloRoot, goal.Id.Value);
            Directory.CreateDirectory(dir);
            var attempt = new ConductorParallelAcceptanceAttempt(id, goal.Id.Value, goal.Id.Value[..8], 0, "branch", "main", Earlier, Earlier,
                holder.ProcessId, stale ? ConductorParallelAcceptanceAttemptOutcome.StaleCandidate : ConductorParallelAcceptanceAttemptOutcome.Running,
                Path.Combine(dir, id + ".out"), Path.Combine(dir, id + ".err"), Path.Combine(dir, id + ".exit"), Path.Combine(dir, id + ".heartbeat"),
                Path.Combine(dir, id + ".result"), Path.Combine(dir, id + ".attempt.json"),
                ReconciledAt: stale ? Earlier : null, TransientFailureCount: 2,
                ExecutionProtocol: inProcess ? "in-process" : "out-of-process")
            { HolderContractVersion = 1, OwnerProcessStartedAt = holder.StartedAt, OwnerExecutablePath = holder.ImagePath };
            SaveSolo(attempt);
            var binding = AcceptanceHolderBinding.For(attempt);
            Lease.Reserve(binding, generation);
            using (Lease.Claim(binding, holder)) { }
            return attempt;
        }
        internal static void SaveSolo(ConductorParallelAcceptanceAttempt attempt) => File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt, Json));
        internal static ConductorParallelAcceptanceAttempt ReadSolo(ConductorParallelAcceptanceAttempt attempt) =>
            JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(attempt.MetadataPath), Json)!;
        internal OperatorIntentRecord Intent(Goal goal, string? pin = null, string reason = "stale")
        {
            var id = Guid.NewGuid().ToString("N");
            return new(id, id, "gate-cancel", goal.Id.Value, null,
                JsonSerializer.Serialize(new GateCancelOperatorIntentPayload(reason, pin), OperatorIntentJson.Options), [], "operator", "cli", "test", Now);
        }
        internal Task<OperatorIntentRecord> Queue(Goal goal, string? pin = null, string reason = "stale") => Store.EnqueueAsync(Intent(goal, pin, reason));
        internal OperatorIntentCoordinator Coordinator(bool available = true) => new(Store, utcNow: () => Now) { GateCancellation = available ? Handler : null };
        internal string Snapshot(Goal goal) => JsonSerializer.Serialize(Kernel.ExportGoalSnapshot(goal.Id));
        internal string[] Snapshots() => Kernel.Goals.Select(Snapshot).ToArray();
        internal Dictionary<string, byte[]> Metadata() => Directory.GetFiles(Root, "*.attempt.json", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        internal static void AssertFilesEqual(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
        {
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
        }
        internal ConductEventRecord[] Events() => !File.Exists(eventPath) ? [] : File.ReadLines(eventPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, Json)!).Where(e => e.EventKind == "gate-cancel").ToArray();
        internal void AssertEvent(OperatorIntentRecord intent, string attemptId, string kind, params Goal[] members)
        {
            var record = Assert.Single(Events());
            Assert.Equal(Now, record.Timestamp);
            Assert.Equal(intent.GoalId, record.GoalId);
            Assert.Equal($"GATE_CANCELLED attempt={attemptId} kind={kind} members={string.Join('+', members.Select(g => g.Id.Value))} intent={intent.Id} reason=operator", record.Detail);
        }
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
