using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OrphanFixtureReaperTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string TestRoot = @"C:\Temp\Low\mcg-tests";

    [Xunit.Fact]
    public void Own_pid_root_orphan_is_revalidated_stopped_and_reported()
    {
        var fake = Fixture();
        var orphan = Process(27068, 999, Now.AddMinutes(-16));
        fake.Add(orphan);
        fake.Roots.Add(Root(orphan));

        var lines = Run(fake);

        Assert.Equal([27068], fake.Stopped);
        Assert.Equal(1, fake.CurrentReads);
        Assert.Contains($"SWEEP_ORPHAN_FIXTURE_REAPED pid=27068 name=pwsh.exe started={orphan.StartedAt:O} evidence=p69bc parent=absent", lines);
    }

    [Xunit.Fact]
    public void Recycled_parent_and_allowed_command_path_are_reported()
    {
        var fake = Fixture();
        fake.Add(Process(44, 5, Now.AddMinutes(-30), command: @"pwsh.exe -File C:\Temp\Low\mcg-tests\p999\fixture.ps1"));
        fake.Add(Process(5, 1, Now.AddMinutes(-20)));

        var lines = Run(fake);

        Assert.Equal([44], fake.Stopped);
        Assert.Contains(lines, line => line.Contains(@"evidence=C:\Temp\Low\mcg-tests\p999\fixture.ps1 parent=recycled", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void Parent_alive_no_evidence_and_young_orphans_are_untouched()
    {
        var fake = Fixture();
        var parent = Process(5, 1, Now.AddHours(-2));
        fake.Add(parent);
        var aliveParent = Process(41, 5, Now.AddMinutes(-20));
        var noEvidence = Process(42, 999, Now.AddMinutes(-20));
        var young = Process(43, 999, Now.AddMinutes(-14));
        var boundary = Process(44, 999, Now.AddMinutes(-15));
        foreach (var process in new[] { aliveParent, noEvidence, young, boundary }) fake.Add(process);
        foreach (var process in new[] { aliveParent, young, boundary }) fake.Roots.Add(Root(process));

        Assert.Empty(Run(fake));
        Assert.Empty(fake.Stopped);
    }

    [Xunit.Fact]
    public void Wrong_root_location_or_stale_pid_key_is_not_ownership_evidence()
    {
        var fake = Fixture();
        var misplaced = Process(61, 999, Now.AddMinutes(-20));
        var stale = Process(62, 999, Now.AddMinutes(-20));
        fake.Add(misplaced);
        fake.Add(stale);
        fake.Roots.Add(Root(misplaced) with { Path = @"C:\Other\p3d" });
        fake.Roots.Add(Root(stale) with { CreatedAt = Now.AddHours(-1) });

        Assert.Empty(Run(fake));
        Assert.Empty(fake.Stopped);
    }

    [Xunit.Fact]
    public void Conductor_supervisor_registry_and_lineage_are_protected()
    {
        var fake = Fixture();
        var pids = new[] { 41, 42, 43, 44, 45, 46 };
        foreach (var pid in pids)
        {
            var process = Process(pid, 999, Now.AddMinutes(-20));
            fake.Add(process);
            fake.Roots.Add(Root(process));
        }
        fake.Conductors.Add(41);
        fake.Supervisors.Add(42);
        fake.Registered.UnionWith([43, 44, 45]); // owned, runtime-owned, detached records
        fake.Lineage.Add(46);

        Assert.Empty(Run(fake));
        Assert.Empty(fake.Stopped);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void Changed_start_or_executable_refuses_stop(bool changeStart)
    {
        var fake = Fixture();
        var recorded = Process(51, 999, Now.AddMinutes(-20));
        fake.Add(recorded);
        fake.Roots.Add(Root(recorded));
        fake.Current[51] = changeStart
            ? recorded with { StartedAt = recorded.StartedAt!.Value.AddSeconds(1) }
            : recorded with { ExecutablePath = @"C:\Other\pwsh.exe" };

        var lines = Run(fake);

        Assert.Empty(fake.Stopped);
        Assert.Contains(lines, line => line.Contains("SWEEP_ORPHAN_FIXTURE_REFUSED pid=51", StringComparison.Ordinal) &&
            line.Contains("reason=identity-mismatch", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void Disabled_policy_skips_and_absent_setting_enables()
    {
        var fake = Fixture();
        var process = Process(51, 999, Now.AddMinutes(-20));
        fake.Add(process);
        fake.Roots.Add(Root(process));
        fake.PolicyJson = "{\"orphanFixtureReaperEnabled\":false}";

        Assert.Equal(["SWEEP_ORPHAN_FIXTURE_SKIPPED reason=disabled-by-policy"], Run(fake));
        Assert.Empty(fake.Stopped);

        fake.PolicyJson = "{\"acceptanceWidth\":2}";
        Assert.Contains(Run(fake), line => line.StartsWith("SWEEP_ORPHAN_FIXTURE_REAPED pid=51", StringComparison.Ordinal));
        Assert.Equal([51], fake.Stopped);
    }

    [Xunit.Theory]
    [Xunit.InlineData("conductor-lock")]
    [Xunit.InlineData("supervisor-lease")]
    [Xunit.InlineData("spawn-registry")]
    [Xunit.InlineData("lineage")]
    public void Unavailable_protection_source_fails_closed(string source)
    {
        var fake = Fixture();
        var process = Process(51, 999, Now.AddMinutes(-20));
        fake.Add(process);
        fake.Roots.Add(Root(process));
        fake.ThrowSource = source;

        Assert.Equal([$"SWEEP_ORPHAN_FIXTURE_SKIPPED reason=protection-source-unavailable source={source}"], Run(fake));
        Assert.Empty(fake.Stopped);
    }

    [Xunit.Fact]
    public void Command_path_alone_does_not_admit_unlisted_executable()
    {
        var fake = Fixture();
        fake.Add(Process(51, 999, Now.AddMinutes(-20), "notepad.exe", @"notepad.exe C:\Temp\Low\mcg-tests\p51\notes.txt"));

        Assert.Equal(["SWEEP_ORPHAN_FIXTURE_SKIPPED reason=executable-not-allowed pid=51 name=notepad.exe"], Run(fake));
        Assert.Empty(fake.Stopped);
    }

    [Xunit.Fact]
    public void Protected_descendant_refuses_tree_stop()
    {
        var fake = Fixture();
        var process = Process(51, 999, Now.AddMinutes(-20));
        fake.Add(process);
        fake.Roots.Add(Root(process));
        fake.Add(Process(52, 51, Now.AddMinutes(-18)));
        fake.Registered.Add(52);

        Assert.Contains(Run(fake), line => line.Contains("reason=protected-descendant", StringComparison.Ordinal));
        Assert.Empty(fake.Stopped);
    }

    [Xunit.Fact]
    public void At_most_ten_oldest_attempts_are_made_and_remainder_deferred()
    {
        var fake = Fixture();
        for (var pid = 100; pid < 113; pid++)
        {
            var process = Process(pid, 999, Now.AddMinutes(-40 + pid - 100));
            fake.Add(process);
            fake.Roots.Add(Root(process));
        }
        fake.Current[101] = fake.Current[101] with { StartedAt = Now };

        var lines = Run(fake);

        Assert.Equal(9, fake.Stopped.Count);
        Assert.DoesNotContain(101, fake.Stopped);
        Assert.DoesNotContain(fake.Stopped, pid => pid >= 110);
        Assert.Equal(10, fake.CurrentReads);
        Assert.Contains("SWEEP_ORPHAN_FIXTURE_DEFERRED count=3 pids=110,111,112", lines);
    }

    private static FakeSources Fixture() => new();
    private static IReadOnlyList<string> Run(FakeSources fake) =>
        new OrphanFixtureReaper(fake, new FixedClock(Now)).RunPass();

    private static ProcessInspectionRecord Process(int pid, int parent, DateTimeOffset started,
        string name = "pwsh.exe", string? command = null) =>
        new(pid, parent, name, @"C:\Windows\System32\" + name, started,
            command ?? name + " -NoProfile -Command fixture", ProcessInspectionStatus.Available);

    private static OwnedFixtureRoot Root(ProcessInspectionRecord process) =>
        new(process.ProcessId, $"p{process.ProcessId:x}",
            TestRoot + $"\\p{process.ProcessId:x}", process.StartedAt!.Value.AddSeconds(1));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeSources : IOrphanFixtureReaperSources
    {
        public string? PolicyJson { get; set; }
        public string? ThrowSource { get; set; }
        public Dictionary<int, ProcessInspectionRecord> Snapshot { get; } = [];
        public Dictionary<int, ProcessInspectionRecord> Current { get; } = [];
        public List<OwnedFixtureRoot> Roots { get; } = [];
        public HashSet<int> Conductors { get; } = [];
        public HashSet<int> Supervisors { get; } = [];
        public HashSet<int> Registered { get; } = [];
        public HashSet<int> Lineage { get; } = [];
        public List<int> Stopped { get; } = [];
        public int CurrentReads { get; private set; }
        public IReadOnlyList<string> SharedTestRoots => [TestRoot];
        public IReadOnlyList<string> EvidenceRoots => [TestRoot, @"C:\Temp\mcg-run\tmp"];

        public void Add(ProcessInspectionRecord process)
        {
            Snapshot.Add(process.ProcessId, process);
            Current.Add(process.ProcessId, process);
        }

        public string? ReadPolicyJson() => PolicyJson;
        public IReadOnlyDictionary<int, ProcessInspectionRecord> ReadSnapshot() => Snapshot;
        public IReadOnlyList<OwnedFixtureRoot> ListOwnedRoots() => Roots;
        public IReadOnlyCollection<int> ReadConductorPids() => Read("conductor-lock", Conductors);
        public IReadOnlyCollection<int> ReadSupervisorPids() => Read("supervisor-lease", Supervisors);
        public IReadOnlyCollection<int> ReadRegisteredPids() => Read("spawn-registry", Registered);
        public IReadOnlyCollection<int> ReadLineagePids(IReadOnlyDictionary<int, ProcessInspectionRecord> _) => Read("lineage", Lineage);
        public ProcessInspectionRecord? ReadCurrent(int pid)
        {
            CurrentReads++;
            return Current.GetValueOrDefault(pid);
        }
        public bool TryStopTree(int pid)
        {
            Stopped.Add(pid);
            return true;
        }
        private IReadOnlyCollection<int> Read(string source, HashSet<int> pids) =>
            ThrowSource == source ? throw new IOException(source) : pids;
    }
}
