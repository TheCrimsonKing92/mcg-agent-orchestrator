using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure decisions and local delegates, with no shared resources.
public sealed class WorkerAdoptionTransferTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 7, 1, 0, 0, TimeSpan.Zero);

    [Xunit.Fact]
    public void Decide_UnprovenIdentity_DefersDespiteMatchingTask() =>
        Assert.Equal("deferred-identity-unproven",
            WorkerAdoptionTransfer.Decide(Row() with { Verdict = "identity-unproven" }, Recorded()));

    [Xunit.Fact]
    public void Decide_RuntimeOwned_RejectsBeforeMissingTask() =>
        Assert.Equal("lifecycle-not-conductor-detached",
            WorkerAdoptionTransfer.Decide(Row() with { Lifecycle = SpawnRegistryLifecycle.RuntimeOwned }, null));

    [Xunit.Fact]
    public void Decide_MissingTask_ReportsMissing() =>
        Assert.Equal("task-missing", WorkerAdoptionTransfer.Decide(Row(), null));

    [Xunit.Fact]
    public void Decide_CompletedTask_ReportsNotRunning() =>
        Assert.Equal("task-not-running",
            WorkerAdoptionTransfer.Decide(Row(), Recorded() with { CompletedAt = StartedAt.AddMinutes(1) }));

    [Xunit.Fact]
    public void Decide_AttachedTask_ReportsNotDetached() =>
        Assert.Equal("task-not-detached",
            WorkerAdoptionTransfer.Decide(Row(), Recorded() with { WasGracefullyDetachedByConductor = false }));

    [Xunit.Fact]
    public void Decide_DifferentPid_ReportsPidMismatch() =>
        Assert.Equal("task-pid-mismatch",
            WorkerAdoptionTransfer.Decide(Row(), Recorded() with { ProcessId = 102 }));

    [Xunit.Fact]
    public void Decide_MissingStartIdentity_ReportsUnproven() =>
        Assert.Equal("task-identity-unproven",
            WorkerAdoptionTransfer.Decide(Row(), Recorded() with { ProcessIdentityStartedAt = null }));

    [Xunit.Fact]
    public void Decide_DifferentStartIdentity_ReportsStartMismatch() =>
        Assert.Equal("task-start-mismatch",
            WorkerAdoptionTransfer.Decide(Row(), Recorded() with { ProcessIdentityStartedAt = StartedAt.AddSeconds(1) }));

    [Xunit.Fact]
    public void Decide_MatchingDetachedTask_Adopts() =>
        Assert.Equal("adopt", WorkerAdoptionTransfer.Decide(Row(), Recorded()));

    [Xunit.Theory]
    [Xunit.InlineData("current-generation")]
    [Xunit.InlineData("worker-gone")]
    [Xunit.InlineData("orphaned")]
    [Xunit.InlineData("owner-held")]
    public void Decide_IneligibleVerdict_ReturnsNull(string verdict) =>
        Assert.Null(WorkerAdoptionTransfer.Decide(Row() with { Verdict = verdict }, Recorded()));

    [Xunit.Theory]
    [Xunit.InlineData("task-not-running")]
    [Xunit.InlineData("task-not-detached")]
    [Xunit.InlineData("task-pid-mismatch")]
    public void Decide_MultipleFailures_ReportsFirstOutcome(string first)
    {
        var recorded = Recorded() with { ProcessIdentityStartedAt = null };
        recorded = first switch
        {
            "task-not-running" => recorded with { ExitCode = 0, WasGracefullyDetachedByConductor = false, ProcessId = 102 },
            "task-not-detached" => recorded with { WasGracefullyDetachedByConductor = false, ProcessId = 102 },
            _ => recorded with { ProcessId = 102 }
        };
        Assert.Equal(first, WorkerAdoptionTransfer.Decide(Row(), recorded));
    }

    [Xunit.Fact]
    public void Run_MixedRows_PreservesOrderAndTransfersOnlyMatches()
    {
        WorkerAdoptionCensusRow[] rows =
        [Row("goal:success"), Row("goal:lost"), Row("goal:missing"), Row("goal:held") with { Verdict = "owner-held" }];
        var transfers = new List<WorkerAdoptionCensusRow>();
        var reads = new List<string>();

        var results = WorkerAdoptionTransfer.Run(rows, ownerId =>
        {
            reads.Add(ownerId);
            return ownerId == "goal:missing" ? null : Recorded();
        }, row => { transfers.Add(row); return row.OwnerId == "goal:success"; });

        Assert.Equal(new[] { "adopted", "transfer-lost", "task-missing" }, results.Select(result => result.Outcome));
        Assert.Equal(rows.Take(3), results.Select(result => result.Row));
        Assert.Equal(rows.Take(2), transfers);
        Assert.Equal(rows.Take(3).Select(row => row.OwnerId), reads);
    }

    [Xunit.Fact]
    public void Run_UnprovenIdentity_DefersWithoutReadingTaskOrTransferring()
    {
        var row = Row() with { Verdict = "identity-unproven" };
        var result = Assert.Single(WorkerAdoptionTransfer.Run([row],
            _ => throw new InvalidOperationException("Task must not be read."),
            _ => throw new InvalidOperationException("Worker must not transfer.")));
        Assert.Equal(row, result.Row);
        Assert.Equal("deferred-identity-unproven", result.Outcome);
    }

    [Xunit.Fact]
    public void CreateDefault_NoInheritableRows_DoesNotLoadGoals()
    {
        var adopt = WorkerAdoptionTransfer.CreateDefault("unused.db",
            _ => throw new InvalidOperationException("Goals must not be loaded."));
        var result = Assert.Single(adopt([Row() with { Verdict = "identity-unproven" }]));
        Assert.Equal("deferred-identity-unproven", result.Outcome);
    }

    private static WorkerAdoptionCensusRow Row(string ownerId = "goal:task") =>
        new(ownerId, 101, StartedAt, SpawnRegistryLifecycle.ConductorDetached,
            41, StartedAt.AddHours(-1), SpawnOwnerLiveness.DeadOrRecycled, "inheritable", "worker live; owner gone");

    private static TaskProcessRecord Recorded() =>
        new(101, "command", "workspace", "stdout", "stderr", "exit", StartedAt, null, null,
            WasGracefullyDetachedByConductor: true, ProcessIdentityStartedAt: StartedAt);
}
