using Mcg.AgentOrchestrator.Core;

public sealed class BuildAttributionReportTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly string C1 = new('1', 40), C2 = new('2', 40);

    [Fact]
    public void MixedSources_AttributeDuplicatesSharesAndWaits_Exactly()
    {
        var report = Build(Fixture());
        Assert.Equal(new BuildAttributionSummary(6, 400000, 9, 2.5, 1, 1, 0, 0), report.Summary);
        Assert.Equal(new BuildAttributionTreeRow("T1", 5, 2, 2.5, 2, 220000,
            "pre-review-evidence=2;worker-build-check=3"), report.Trees.Single(t => t.Tree == "T1"));
        Assert.Equal(1.0, report.Trees.Single(t => t.Tree == "unresolved").DuplicateFactor);
        Assert.Equal(3, report.Trees.Single(t => t.Tree == "unrecorded:goal-g").Compiles);
        Assert.Equal(new BuildAttributionConsumerRow("worker-build-check", 3, 160000, 0.4, 6, 2, 301000, 1),
            report.Consumers.Single(c => c.Class == "worker-build-check"));
        Assert.Equal(new BuildAttributionConsumerRow("pre-review-evidence", 2, 120000, 0.3, 2, 1, 0, 1),
            report.Consumers.Single(c => c.Class == "pre-review-evidence"));
        Assert.Equal(new BuildAttributionConsumerRow("unlabeled:run", 1, 120000, 0.3, 1, 0, 0, 0),
            report.Consumers.Single(c => c.Class == "unlabeled:run"));
        Assert.Equal(new BuildAttributionConsumerRow("cohort-gate", 0, 0, 0, 0, 2, 110000, 1),
            report.Consumers.Single(c => c.Class == "cohort-gate"));
        Assert.Equal("T1", report.Trees[0].Tree);
    }

    [Fact]
    public void ChangedWitnessTicks_CountAsAnotherCompile()
    {
        var report = Build(Fixture(ticks: 9));
        var tree = report.Trees.Single(t => t.Tree == "T1");
        Assert.Equal(6, tree.Compiles);
        Assert.Equal(3.0, tree.DuplicateFactor);
    }

    [Fact]
    public void Window_ExcludesEarlierAndUpperBoundaryHolds()
    {
        var input = Fixture();
        var upper = input.Records[0] with { RecordedAt = Since.AddDays(1), LeaseId = "upper" };
        Assert.Equal(Build(input), Build(input with { Records = [.. input.Records, upper] }), new ReportComparer());
        Assert.Equal(400000, Build(input).Summary.HeldMs);
    }

    [Fact]
    public void UnknownWitnessAndMissingRun_AddUnknownHoldsWithoutCompiles()
    {
        var input = Fixture();
        var unknown = Hold("pre-review-evidence:attempt-unknown", 100, "compiled=unknown");
        var missing = Hold("worker-build-check:run-missing", 100, "tree=wbc1:git-tree:T1 configuration=Debug", "missing");
        var report = Build(input with { Records = [.. input.Records, unknown, missing] });
        Assert.Equal(2, report.Summary.UnknownCompileHolds);
        Assert.Equal(9, report.Summary.Compiles);
        Assert.Equal(8, report.Summary.Holds);
    }

    [Fact]
    public void DuplicatePendingRecords_AreCountedOnce_AndResolverFailureIsUnresolved()
    {
        var input = Fixture();
        var report = Build(input with { Records = [.. input.Records, input.Records[0]] });
        Assert.Equal(Build(input).Summary, report.Summary);
        var failed = BuildAttributionReport.Build(input, Since, Since.AddDays(1), _ => throw new IOException());
        Assert.Equal(3, failed.Summary.UnresolvedTreeHolds);
    }

    [Fact]
    public void EmptyWindow_HasNullFactorsAndNoRows()
    {
        var report = Build(new([], [], []));
        Assert.Null(report.Summary.OverallDuplicateFactor);
        Assert.Empty(report.Consumers);
        Assert.Empty(report.Trees);
    }

    private static BuildAttributionReport Build(BuildAttributionInput input) =>
        BuildAttributionReport.Build(input, Since, Since.AddDays(1), head => head == C2 ? "T1" : null);

    private static BuildAttributionInput Fixture(int ticks = 1)
    {
        var records = new List<BuildAttributionJournalRecord>
        {
            Hold("worker-build-check:run-1", 60000, $"waitMs=1000 tree=wbc1:git-tree:T1 head={C1} configuration=Debug", "run-1"),
            Hold("worker-build-check:run-2", 40000, $"waitMs=0 tree=wbc1:git-tree:T1 head={C1} configuration=Debug", "run-2"),
            Hold("pre-review-evidence:goal-g:attempt-a", 100000, "compiled=App/debug@1|Core/debug@2", "P1"),
            Hold("pre-review-evidence:goal-g:attempt-a", 20000, $"compiled=App/debug@{ticks}", "P1"),
            Hold("unlabeled", 120000, "compiled=Core/debug@3", "P2") with { LeaseId = "run-lease" },
            Hold("worker-build-check:run-old", 999999, "tree=wbc1:git-tree:T1", "old") with { RecordedAt = Since.AddSeconds(-1) },
            new("LEASE_WAIT_TIMEOUT", Since, 1, "timeout", "timed-out", "holder=worker-build-check:run-t waitMs=300000", ""),
            new("ACCEPTANCE_SLOT_PRIORITY_WAIT", Since, 1, "priority-a", "acquired", "waitMs=30000", ""),
            new("ACCEPTANCE_SLOT_PRIORITY_WAIT", Since, 1, "priority-b", "expired", "waitMs=80000", "")
        };
        return new(records,
            [Run("run-1", ["App", "Core"]), Run("run-2", ["App"]), Run("legacy", ["App", "Core", "Infra"], 60)],
            [new("a", C2, Since), new("fault", null, Since, "fault", "blocked-build-slot", true)]);
    }

    private static BuildAttributionJournalRecord Hold(string holder, long ms, string details, string path = "P1") =>
        new("LEASE_HELD", Since.AddHours(1), 1, Guid.NewGuid().ToString("N"), "released", $"holder={holder} heldMs={ms} {details}", path);
    private static BuildAttributionRun Run(string path, string[] projects, int elapsed = 0) =>
        new(path, "g", "20261016T010000000Z-1-run", [.. projects.Select(p => $"Compilation request {p} (net10.0)"), $"Time Elapsed 00:01:{elapsed % 60:00}.00" ]);

    private sealed class ReportComparer : IEqualityComparer<BuildAttributionReport>
    {
        public bool Equals(BuildAttributionReport? x, BuildAttributionReport? y) =>
            x?.Summary == y?.Summary && x!.Consumers.SequenceEqual(y!.Consumers) && x.Trees.SequenceEqual(y.Trees);
        public int GetHashCode(BuildAttributionReport obj) => obj.Summary.GetHashCode();
    }
}
