using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its files and database; clocks and holder processes are fake.
public sealed class CohortMemberReopenedGateAbandonmentTests
{
    [Theory]
    [InlineData("cohort")]
    [InlineData("train")]
    public async Task ApplyPending_ReopenedMember_StopsGateWithoutVerdict(string kind)
    {
        using var f = new Fixture();
        var a = f.Goal();
        var b = f.Goal();
        var attempt = f.Group(kind, "group", a, b);
        Assert.True(f.Grouped.ReservesCapacity(attempt));
        var counts = new[] { a.AutomaticAcceptanceRetryCount, b.AutomaticAcceptanceRetryCount };
        var timeline = JsonSerializer.Serialize(a.Timeline);
        var intent = await f.Queue(b);
        var coordinator = f.Coordinator();

        var application = OperatorIntentGoalApplication.ApplyPending(f.Kernel, b, coordinator, f.Solo);

        Assert.True(application.Result.MutatedGoalState);
        Assert.Equal(new[] { attempt.AttemptId }, f.Stops);
        var stopped = ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath);
        Assert.Equal("Reconciled", stopped.Outcome);
        Assert.Equal(Fixture.Now, stopped.ReconciledAt);
        Assert.Equal($"refused:cohort-member-reopened goal={b.Id.Value} intent={intent.Id}", stopped.Detail);
        Assert.False(f.Grouped.ReservesCapacity(stopped));
        var record = Assert.Single(f.Events());
        Assert.Equal(Fixture.Now, record.Timestamp);
        Assert.Equal(b.Id.Value, record.GoalId);
        Assert.Equal($"GATE_CANCELLED attempt={attempt.AttemptId} kind={kind} members={a.Id.Value}+{b.Id.Value} intent={intent.Id} reason=cohort-member-reopened goal={b.Id.Value}", record.Detail);
        Assert.Equal(new[] { $"COHORT_MEMBER_REOPENED_GATE_ABANDONED attempt={attempt.AttemptId} kind={kind} goal={b.Id.Value[..8]} intent={intent.Id}" },
            application.Lines.Where(line => line.StartsWith("COHORT_MEMBER_REOPENED", StringComparison.Ordinal)));
        Assert.Equal(GoalStatus.Verified, a.Status);
        Assert.Equal(GoalStatus.Active, b.Status);
        Assert.Equal(counts, new[] { a.AutomaticAcceptanceRetryCount, b.AutomaticAcceptanceRetryCount });
        Assert.All(new[] { a, b }, goal =>
        {
            Assert.Null(goal.RetainedAcceptanceFailure);
            Assert.NotEqual(GoalStatus.AcceptanceFailed, goal.Status);
        });
        Assert.Equal(timeline, JsonSerializer.Serialize(a.Timeline));
        Assert.Empty(Directory.GetFiles(f.Root, "failing-test-index.v1.jsonl", SearchOption.AllDirectories));
        coordinator.CompletePersisted([b.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await f.Store.GetAsync(intent.Id))!.Status);
        Assert.Empty(f.Handler.Apply(b));
        Assert.Single(f.Stops);
        Assert.Single(f.Events());
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("priority")]
    [InlineData("follower")]
    [InlineData("result")]
    [InlineData("empty-result")]
    [InlineData("partial-result")]
    [InlineData("exit")]
    [InlineData("reconciled")]
    [InlineData("later")]
    [InlineData("null-handler")]
    public async Task ApplyPending_ExcludedAttempt_PreservesAllMetadataSilently(string exclusion)
    {
        using var f = new Fixture();
        var a = f.Goal();
        var b = f.Goal();
        var target = exclusion == "outside" ? f.Goal() : b;
        var attempt = f.Group(exclusion == "follower" ? "follower" : "cohort", "excluded", a, b);
        if (exclusion == "result") File.WriteAllText(attempt.ResultPath, "{}");
        if (exclusion == "empty-result") File.WriteAllText(attempt.ResultPath, "");
        if (exclusion == "partial-result") File.WriteAllText(attempt.ResultPath, "{\"outcome\":");
        if (exclusion == "exit") File.WriteAllText(attempt.ExitCodePath, "0");
        if (exclusion == "reconciled")
            ConductorGroupedGateAttemptCoordinator.Save(attempt with { ReconciledAt = Fixture.Earlier });
        if (exclusion == "later")
            ConductorGroupedGateAttemptCoordinator.Save(attempt with { StartedAt = Fixture.Now.AddSeconds(1) });
        await f.Queue(target, priority: exclusion == "priority");
        var before = f.Metadata();

        var application = OperatorIntentGoalApplication.ApplyPending(
            f.Kernel, target, f.Coordinator(exclusion != "null-handler"), f.Solo);

        Assert.True(application.Result.MutatedGoalState);
        Fixture.AssertFilesEqual(before, f.Metadata());
        Assert.Empty(f.Stops);
        Assert.Empty(f.Events());
        Assert.DoesNotContain(application.Lines,
            line => line.StartsWith("COHORT_MEMBER_REOPENED", StringComparison.Ordinal));
        Assert.Equal(exclusion == "priority" ? GoalStatus.Verified : GoalStatus.Active, target.Status);
    }

    [Fact]
    public async Task ApplyPending_UnprovenStop_PreservesMetadataAndCompletesIntent()
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var attempt = f.Group("cohort", "unproven", goal);
        f.StopSucceeds = false;
        var intent = await f.Queue(goal);
        var before = f.Metadata();
        var coordinator = f.Coordinator();

        var application = OperatorIntentGoalApplication.ApplyPending(f.Kernel, goal, coordinator, f.Solo);

        Assert.Equal(new[] { attempt.AttemptId }, f.Stops);
        Fixture.AssertFilesEqual(before, f.Metadata());
        Assert.Empty(f.Events());
        Assert.Equal(new[] { $"COHORT_MEMBER_REOPENED_GATE_ABANDON_UNPROVEN attempt={attempt.AttemptId} kind=cohort goal={goal.Id.Value[..8]} intent={intent.Id}" },
            application.Lines.Where(line => line.StartsWith("COHORT_MEMBER_REOPENED", StringComparison.Ordinal)));
        coordinator.CompletePersisted([goal.Id]);
        Assert.Equal(OperatorIntentStatus.Applied, (await f.Store.GetAsync(intent.Id))!.Status);
    }

    [Fact]
    public async Task ApplyPending_PublicationDuringSelection_RefusesStopWithUnprovenLine()
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var attempt = f.Group("cohort", "published", goal);
        var intent = await f.Queue(goal);
        var before = f.Metadata();
        // Capacity evaluation occurs after the selector's file checks and before the locked stop.
        f.OnClassify = current => File.WriteAllText(current.ResultPath, "{\"outcome\":");

        var application = OperatorIntentGoalApplication.ApplyPending(f.Kernel, goal, f.Coordinator(), f.Solo);

        Assert.True(File.Exists(attempt.ResultPath));
        Fixture.AssertFilesEqual(before, f.Metadata());
        Assert.Empty(f.Stops);
        Assert.Empty(f.Events());
        Assert.Equal(new[] { $"COHORT_MEMBER_REOPENED_GATE_ABANDON_UNPROVEN attempt={attempt.AttemptId} kind=cohort goal={goal.Id.Value[..8]} intent={intent.Id}" },
            application.Lines.Where(line => line.StartsWith("COHORT_MEMBER_REOPENED", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ApplyPending_MultipleAttempts_StopsInOrdinalOrderLeavingUnrelated()
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var other = f.Goal();
        f.Group("cohort", "x", goal, other);
        f.Group("train", "w", other, goal);
        var unrelated = f.Group("train", "unrelated", f.Goal(), f.Goal());
        var bytes = File.ReadAllBytes(unrelated.MetadataPath);
        Assert.True(f.Grouped.ReservesCapacity(unrelated));
        var intent = await f.Queue(goal);

        OperatorIntentGoalApplication.ApplyPending(f.Kernel, goal, f.Coordinator(), f.Solo);

        Assert.Equal(new[] { "w", "x" }, f.Stops);
        Assert.Equal(new[]
        {
            $"GATE_CANCELLED attempt=w kind=train members={other.Id.Value}+{goal.Id.Value} intent={intent.Id} reason=cohort-member-reopened goal={goal.Id.Value}",
            $"GATE_CANCELLED attempt=x kind=cohort members={goal.Id.Value}+{other.Id.Value} intent={intent.Id} reason=cohort-member-reopened goal={goal.Id.Value}"
        }, f.Events().Select(record => record.Detail));
        Assert.All(f.Events(), record => Assert.Equal(Fixture.Now, record.Timestamp));
        Assert.Equal(bytes, File.ReadAllBytes(unrelated.MetadataPath));
        Assert.True(f.Grouped.ReservesCapacity(ConductorGroupedGateAttemptCoordinator.Read(unrelated.MetadataPath)));
    }

    [Theory]
    [InlineData("result")]
    [InlineData("exit")]
    [InlineData("reconciled")]
    [InlineData("completed")]
    public void AbandonForReopenedMember_FinishedAttempt_DoesNotStopHolder(string state)
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var attempt = f.Group("cohort", "finished", goal);
        if (state == "result") File.WriteAllText(attempt.ResultPath, "{}");
        if (state == "exit") File.WriteAllText(attempt.ExitCodePath, "0");
        if (state == "reconciled")
            ConductorGroupedGateAttemptCoordinator.Save(attempt with { ReconciledAt = Fixture.Earlier });
        if (state == "completed")
            ConductorGroupedGateAttemptCoordinator.Save(attempt with { Outcome = "Completed" });
        var before = f.Metadata();

        Assert.False(f.Grouped.AbandonForReopenedMember(attempt, goal.Id.Value, "intent"));

        Fixture.AssertFilesEqual(before, f.Metadata());
        Assert.Empty(f.Stops);
    }

    [Fact]
    public void CreateDefault_TempWorkspace_WiresReopenedMemberAbandonment()
    {
        using var f = new Fixture();
        var coordinator = OperatorIntentCoordinator.CreateDefault(OrchestratorWorkspace.ForDirectory(f.Root));
        Assert.NotNull(coordinator.ReopenedMemberGateAbandonment);
        Assert.NotNull(coordinator.GateCancellation);
    }

    [Fact]
    public void Apply_TimelineFiltering_UsesLastAppliedIntentForAnOpenTask()
    {
        using var f = new Fixture();
        var goal = f.Goal();
        var closed = f.Kernel.AddTask(goal.Id, AgentRole.Developer, "Closed task", AgentCatalog.Default().Agents);
        f.Kernel.RecordTaskVerification(goal.Id, closed.Id,
            ManualVerificationRecorder.Create(true, "Passed.", f.Root, Fixture.Now));
        var attempt = f.Group("cohort", "last-intent", goal);
        f.Kernel.RetryTaskWithAuthoritativeFeedback(goal.Id, goal.Tasks[0].Id, "repair", RetryCause.NewTestFinding,
            invalidateDownstream: false);
        f.RecordIntent(goal, "first", goal.Tasks[0].Id.Value, "applied");
        f.RecordIntent(goal, "last", goal.Tasks[0].Id.Value, "applied");
        f.RecordIntent(goal, "rejected", goal.Tasks[0].Id.Value, "rejected");
        f.RecordIntent(goal, "wrong-case", goal.Tasks[0].Id.Value, "Applied");
        f.RecordIntent(goal, "closed", closed.Id.Value, "applied");
        f.RecordIntent(goal, "missing", TaskId.New().Value, "applied");

        var before = JsonSerializer.Serialize(f.Kernel.ExportGoalSnapshot(goal.Id));
        var lines = f.Handler.Apply(goal);

        Assert.Equal(new[] { $"COHORT_MEMBER_REOPENED_GATE_ABANDONED attempt={attempt.AttemptId} kind=cohort goal={goal.Id.Value[..8]} intent=last" }, lines);
        Assert.Equal(before, JsonSerializer.Serialize(f.Kernel.ExportGoalSnapshot(goal.Id)));
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => Fixture.Now; }

    private sealed class Fixture : IDisposable
    {
        internal static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20000);
        internal static readonly DateTimeOffset Earlier = Now.AddMinutes(-1);
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "reopened-gate-" + Guid.NewGuid().ToString("N"));
        internal readonly AgentOrchestratorKernel Kernel = new(new Clock());
        internal readonly ConductorParallelAcceptanceAttemptCoordinator Solo;
        internal readonly ConductorGroupedGateAttemptCoordinator Grouped;
        internal readonly SqliteOperatorIntentStore Store;
        internal readonly CohortMemberReopenedGateAbandonment Handler;
        internal readonly List<string> Stops = [];
        internal bool StopSucceeds = true;
        internal Action<ConductorGroupedGateAttempt>? OnClassify;
        private readonly string groupedRoot;
        private readonly string eventPath;

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            groupedRoot = Path.Combine(Root, "grouped");
            eventPath = Path.Combine(Root, "conduct-events.log");
            Solo = new(Path.Combine(Root, "solo"), "main", utcNow: () => Now, isProcessAlive: _ => true);
            Grouped = new(groupedRoot, generationId: 111, utcNow: () => Now, isProcessAlive: _ => true,
                classifyHolder: attempt =>
                {
                    OnClassify?.Invoke(attempt);
                    return SpawnTrackedProcessStatus.LiveMatch;
                }, stop: attempt =>
                {
                    Stops.Add(attempt.AttemptId);
                    return StopSucceeds;
                }, generationIdentity: new(111, Earlier, "C:\\fake-dotnet.exe"));
            Store = SqliteOperatorIntentStore.ForDirectories(Root, Path.Combine(Root, "logs"));
            Handler = new(Grouped, new ConductEventLogWriter(eventPath, utcNow: () => Now), () => Now);
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
                members.Select(goal => new ConductorGroupedGateMember(goal.Id.Value, "branch", "candidate", [], [], ChangeRiskTier.Behavior)).ToArray(),
                "main", "tree", "manifest", "identity-" + id, Earlier, 222, 111,
                Path.Combine(dir, id + ".attempt.json"), Path.Combine(dir, "result"), Path.Combine(dir, "exit"),
                Path.Combine(dir, "out"), Path.Combine(dir, "err"), Root, "{}", Earlier, "C:\\fake-dotnet.exe");
            ConductorGroupedGateAttemptCoordinator.Save(attempt);
            return attempt;
        }

        internal Task<OperatorIntentRecord> Queue(Goal goal, bool priority = false)
        {
            var id = Guid.NewGuid().ToString("N");
            var payload = priority
                ? JsonSerializer.Serialize(new GoalPriorityOperatorIntentPayload("high", "Operator priority"), OperatorIntentJson.Options)
                : JsonSerializer.Serialize(new RetryOperatorIntentPayload("New test finding", null, RetryCause: RetryCause.NewTestFinding), OperatorIntentJson.Options);
            return Store.EnqueueAsync(new(id, id, priority ? "goal-priority" : "retry", goal.Id.Value,
                priority ? null : goal.Tasks.Single().Id.Value, payload, [], "operator", "cli", "test", Now));
        }

        internal OperatorIntentCoordinator Coordinator(bool available = true) => new(Store, utcNow: () => Now)
            { ReopenedMemberGateAbandonment = available ? Handler : null };

        internal void RecordIntent(Goal goal, string id, string taskId, string outcome) =>
            Kernel.RecordOperatorIntentApplied(goal.Id, id, "retry", taskId, "operator", "cli", "test", id, outcome: outcome);

        internal Dictionary<string, byte[]> Metadata() => Directory.GetFiles(Root, "*.attempt.json", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

        internal static void AssertFilesEqual(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
        {
            Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
            foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
        }

        internal ConductEventRecord[] Events() => !File.Exists(eventPath) ? [] : File.ReadLines(eventPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, Json)!)
            .Where(record => record.EventKind == "gate-cancel").ToArray();

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
