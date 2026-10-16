using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Parallel-safe: every database, WAL, lock and log belongs to this fixture's unique temp root.
// Process boundaries and activation deadlines are explicit seams; no real child is launched.
public sealed class ConductorContinuitySupervisorSchemaUpgradeHandoffTests
{
    [Xunit.Fact]
    public async Task ForwardPlan_BacksUpBeforeSetupThenChecksAndLaunches()
    {
        using var fixture = new Fixture();
        // Keep WAL content uncheckpointed, so a main-file copy cannot satisfy the backup assertion.
        using var writer = fixture.Open("a");
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE wal_payload(value TEXT); INSERT INTO wal_payload VALUES ('before-upgrade')";
            command.ExecuteNonQuery();
        }

        Assert.Equal(0, await fixture.RunAsync());

        var backup = fixture.BackupFile("a");
        Assert.True(File.Exists(backup));
        using var saved = Fixture.OpenPath(backup);
        Assert.Equal(1, StoreSchemaVersions.Read(saved, "a"));
        using var read = saved.CreateCommand();
        read.CommandText = "SELECT value FROM wal_payload";
        Assert.Equal("before-upgrade", read.ExecuteScalar());
        Assert.Equal(2, fixture.Version("a"));
        Assert.Equal(new[] { "plan", "setup", "plan", "self-check", "successor-launch" },
            fixture.Steps.Where(s => s is "plan" or "setup" or "self-check" or "successor-launch"));
        Assert.True(fixture.Steps.FindIndex(s => s.StartsWith("LOOP_HANDOFF_SCHEMA_UPGRADE ", StringComparison.Ordinal) &&
            s.Contains("result=applied", StringComparison.Ordinal)) < fixture.Steps.IndexOf("successor-launch"));
        Assert.Contains(fixture.Events, e => e.Contains("LOOP_HANDOFF_SCHEMA_UPGRADE stores=a:1->2", StringComparison.Ordinal) && e.Contains("result=applied", StringComparison.Ordinal));
        Assert.Equal(new[] { "dotnet-fixture", fixture.SuccessorDll }, fixture.Host.Requests[1].CommandPrefix);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task HeldLockOrLiveChild_RefusesSetupAndRetainsBuild(bool childStillAlive)
    {
        using var fixture = new Fixture { PreviousChildExited = !childStillAlive };
        using var heldLock = childStillAlive ? null : ConductorLoopLease.Acquire(fixture.StateDirectory);
        Assert.Equal(0, await fixture.RunAsync());
        Assert.Equal(0, fixture.SetupCalls);
        Assert.Contains(fixture.Events, e => e.Contains("phase=schema-upgrade reason=quiescence", StringComparison.Ordinal));
        Assert.Equal(2, fixture.Host.Requests.Count);
        Assert.Null(fixture.Host.Requests[1].CommandPrefix);
        Assert.False(Directory.Exists(Path.Combine(fixture.StateDirectory, "backups")));
    }

    [Xunit.Fact]
    public async Task DetachedWorkers_RefusesSetupDespiteRebuild()
    {
        using var fixture = new Fixture
        {
            DrainLog = "LOOP_RELAUNCH_DRAIN active=1 liveGates=0\nLOOP_RELAUNCH_DETACH active=1\nLOOP_RELAUNCH_REBUILD active=0"
        };
        Assert.Equal(0, await fixture.RunAsync());
        Assert.Equal(0, fixture.SetupCalls);
        Assert.Contains(fixture.Events, e => e.Contains("phase=schema-upgrade reason=detached-workers", StringComparison.Ordinal));
        Assert.Null(fixture.Host.Requests[1].CommandPrefix);
    }

    [Xunit.Fact]
    public async Task NewerStore_PreservesSelfCheckRefusalWithoutBackup()
    {
        using var fixture = new Fixture(twoStores: true);
        fixture.SetVersion("a", 3);
        Assert.Equal(0, await fixture.RunAsync());
        Assert.Equal(0, fixture.SetupCalls);
        Assert.False(Directory.Exists(Path.Combine(fixture.StateDirectory, "backups")));
        Assert.DoesNotContain(fixture.Events, e => e.StartsWith("LOOP_HANDOFF_SCHEMA_UPGRADE ", StringComparison.Ordinal));
        Assert.Contains(fixture.Events, e => e.Contains("phase=self-check", StringComparison.Ordinal) &&
            e.Contains("fixture existing newer-store refusal", StringComparison.Ordinal));
        Assert.Null(fixture.Host.Requests[1].CommandPrefix);
        Assert.Equal(1, fixture.Version("b"));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task PartialSetupFailure_ReportsProgressAndEscalatesWithoutFallback(bool throws)
    {
        using var fixture = new Fixture(twoStores: true) { PartialFailure = true, SetupThrows = throws };
        Assert.Equal(1, await fixture.RunAsync());
        Assert.Single(fixture.Host.Requests);
        Assert.Equal(2, fixture.Version("a"));
        Assert.Equal(1, fixture.Version("b"));
        var failure = Assert.Single(fixture.Events.Where(e => e.Contains("phase=schema-upgrade", StringComparison.Ordinal)));
        Assert.Contains("completed=a pending=b", failure);
        Assert.Contains("Setup stopped; completed: a; pending: b", failure);
        Assert.Contains(fixture.BackupDirectory, failure);
        Assert.Contains(fixture.Events, e => e.Contains("SUPERVISOR_ESCALATED reason=schema-upgrade-incomplete", StringComparison.Ordinal) &&
            e.Contains(fixture.BackupDirectory, StringComparison.Ordinal));
        foreach (var store in new[] { "a", "b" })
        {
            using var saved = Fixture.OpenPath(fixture.BackupFile(store));
            Assert.Equal(1, StoreSchemaVersions.Read(saved, store));
        }
        Assert.DoesNotContain("self-check", fixture.Steps);
    }

    [Xunit.Fact]
    public async Task CurrentPlan_MatchesExistingHandoffWithoutSetupOrBackup()
    {
        using var fixture = new Fixture { RequiredVersion = 1 };
        using var baseline = new Fixture { RequiredVersion = 1 };
        Assert.Equal(0, await fixture.RunAsync());
        Assert.Equal(0, await baseline.RunAsync(enableUpgrade: false));
        Assert.Equal(0, fixture.SetupCalls);
        Assert.False(Directory.Exists(Path.Combine(fixture.StateDirectory, "backups")));
        Assert.DoesNotContain(fixture.Events, e => e.StartsWith("LOOP_HANDOFF_SCHEMA_UPGRADE ", StringComparison.Ordinal));
        // Event paths are fixture-owned; compare event kinds and launch command selection.
        Assert.Equal(baseline.Events.Select(e => e.Split(' ')[0]), fixture.Events.Select(e => e.Split(' ')[0]));
        Assert.Equal(baseline.Host.Requests.Select(r => r.CommandPrefix is null), fixture.Host.Requests.Select(r => r.CommandPrefix is null));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task AppliedUpgrade_FailedActivationEscalatesWithoutRetainedLaunch(bool stall)
    {
        using var fixture = new Fixture { Stall = stall, Crash = !stall };
        Assert.Equal(1, await fixture.RunAsync());
        Assert.Equal(2, fixture.Host.Requests.Count);
        Assert.Contains(fixture.Events, e => e.Contains("SUPERVISOR_ESCALATED reason=fallback-incompatible-after-upgrade", StringComparison.Ordinal) &&
            e.Contains("stores=a:1->2", StringComparison.Ordinal) && e.Contains(fixture.BackupDirectory, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Events, e => e.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal));
        Assert.Equal(stall ? 1 : 0, fixture.Deadlines);
    }

    [Xunit.Fact]
    public async Task ReleasedProbe_SetupAcquiresOwnLeaseAndRacingOwnerRefuses()
    {
        using var fixture = new Fixture { RacingOwner = true };
        Assert.Equal(0, await fixture.RunAsync());
        Assert.Equal(1, fixture.SetupCalls);
        Assert.Contains(fixture.Events, e => e.Contains("phase=schema-upgrade reason=setup", StringComparison.Ordinal) &&
            e.Contains("Forward migration refused: active conductor pid", StringComparison.Ordinal));
        Assert.Null(fixture.Host.Requests[1].CommandPrefix);
        Assert.Equal(1, fixture.Version("a"));
    }

    [Xunit.Fact]
    public async Task BackupFailure_PreventsSetupAndRetainsCompatibleBuild()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.StateDirectory, "backups"), "blocks directory creation");
        Assert.Equal(0, await fixture.RunAsync());
        Assert.Equal(0, fixture.SetupCalls);
        Assert.Contains(fixture.Events, e => e.Contains("phase=schema-upgrade reason=backup", StringComparison.Ordinal));
        Assert.Null(fixture.Host.Requests[1].CommandPrefix);
        Assert.Equal(1, fixture.Version("a"));
    }

    [Xunit.Fact]
    public async Task UnconfirmedSetupTermination_EscalatesEvenWhenStoresDidNotAdvance()
    {
        using var fixture = new Fixture { UnconfirmedSetup = true };
        Assert.Equal(1, await fixture.RunAsync());
        Assert.Single(fixture.Host.Requests);
        Assert.Equal(1, fixture.Steps.Count(s => s == "plan"));
        Assert.Equal(1, fixture.Version("a"));
        Assert.Contains(fixture.Events, e => e.Contains("SUPERVISOR_ESCALATED reason=schema-upgrade-incomplete", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task LaunchThrowsAfterUpgrade_EscalatesBeforeRecordingReversion()
    {
        using var fixture = new Fixture { LaunchThrows = true };
        Assert.Equal(1, await fixture.RunAsync());
        Assert.Equal(2, fixture.Host.Requests.Count);
        Assert.Contains(fixture.Events, e => e.Contains("SUPERVISOR_ESCALATED reason=fallback-incompatible-after-upgrade", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Events, e => e.StartsWith("ACTIVATION_REVERTED ", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task ReplanStillPending_EscalatesWithoutLaunchingSuccessor()
    {
        using var fixture = new Fixture(twoStores: true) { IncompleteSetup = true };
        Assert.Equal(1, await fixture.RunAsync());
        Assert.Single(fixture.Host.Requests);
        Assert.Equal(2, fixture.Steps.Count(s => s == "plan"));
        Assert.Contains(fixture.Events, e => e.Contains("phase=schema-upgrade reason=re-plan", StringComparison.Ordinal) &&
            e.Contains("completed=a pending=b", StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData("LOOP_RELAUNCH_DRAIN active=0 liveGates=0")]
    [Xunit.InlineData("LOOP_RELAUNCH_REBUILD active=0")]
    public async Task RecordedQuiescence_AllowsLiteralDrainOrNoWaitRebuild(string log)
    {
        using var fixture = new Fixture { DrainLog = log };
        Assert.Equal(0, await fixture.RunAsync());
        Assert.Equal(1, fixture.SetupCalls);
    }

    [Xunit.Theory]
    [Xunit.InlineData("")]
    [Xunit.InlineData("LOOP_RELAUNCH_DRAIN active=0 liveGates=1")]
    [Xunit.InlineData("LOOP_RELAUNCH_REBUILD active=0\nLOOP_RELAUNCH_DRAIN active=1 liveGates=1")]
    public async Task MissingOrIncompleteDrain_RefusesSetup(string log)
    {
        using var fixture = new Fixture { DrainLog = log };
        Assert.Equal(0, await fixture.RunAsync());
        Assert.Equal(0, fixture.SetupCalls);
        Assert.Contains(fixture.Events, e => e.Contains("phase=schema-upgrade reason=quiescence", StringComparison.Ordinal));
    }

    [Xunit.Theory]
    [Xunit.InlineData("unparseable")]
    [Xunit.InlineData("a: recorded 1; required broken; action migrate (a.db)")]
    public async Task UnavailablePlan_LeavesSelfCheckInCharge(string output)
    {
        using var fixture = new Fixture { PlanOverride = output };
        Assert.Equal(0, await fixture.RunAsync());
        Assert.Equal(0, fixture.SetupCalls);
        Assert.Contains(fixture.Events, e => e.StartsWith("LOOP_HANDOFF_SCHEMA_PLAN_UNAVAILABLE ", StringComparison.Ordinal));
        Assert.Contains(fixture.Events, e => e.Contains("phase=self-check", StringComparison.Ordinal));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("schema-handoff-").FullName;
        private readonly string[] _stores;
        internal string StateDirectory { get; }
        internal string SuccessorDll => Path.Combine(_root, "successor", "Mcg.AgentOrchestrator.App.dll");
        internal List<string> Steps { get; } = [];
        internal List<string> Events { get; } = [];
        internal Host Host { get; }
        internal int RequiredVersion { get; init; } = 2;
        internal int SetupCalls { get; private set; }
        internal int Deadlines { get; private set; }
        internal bool PreviousChildExited { get; init; } = true;
        internal bool PartialFailure { get; init; }
        internal bool SetupThrows { get; init; }
        internal bool IncompleteSetup { get; init; }
        internal bool UnconfirmedSetup { get; init; }
        internal bool LaunchThrows { get; init; }
        internal bool RacingOwner { get; init; }
        internal bool Stall { get; init; }
        internal bool Crash { get; init; }
        internal string? PlanOverride { get; init; }
        internal string DrainLog { get; init; } = "LOOP_RELAUNCH_DRAIN active=1 liveGates=1\nLOOP_RELAUNCH_REBUILD active=0 admitting=false";
        internal string BackupDirectory => Assert.Single(Directory.GetDirectories(Path.Combine(StateDirectory, "backups")));
        internal string BackupFile(string store) => Path.Combine(BackupDirectory, store + ".db");

        internal Fixture(bool twoStores = false)
        {
            StateDirectory = Directory.CreateDirectory(Path.Combine(_root, ".orchestrator")).FullName;
            _stores = twoStores ? ["a", "b"] : ["a"];
            foreach (var store in _stores)
            {
                using var connection = Open(store);
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE store_schema_versions(store_name TEXT PRIMARY KEY, version INTEGER, applied_at TEXT); INSERT INTO store_schema_versions VALUES ($name, 1, 'fixture')";
                command.Parameters.AddWithValue("$name", store);
                command.ExecuteNonQuery();
            }
            Host = new Host(this);
        }

        internal SqliteConnection Open(string store) => OpenPath(Path.Combine(StateDirectory, store + ".db"));
        internal static SqliteConnection OpenPath(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }
        internal int Version(string store)
        {
            using var connection = Open(store);
            return StoreSchemaVersions.Read(connection, store)!.Value;
        }
        internal void SetVersion(string store, int version)
        {
            using var connection = Open(store);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_schema_versions SET version=$version";
            command.Parameters.AddWithValue("$version", version);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        private ConductorHandoffSchemaUpgrade.ProcessOutput RunChild(string dll, IReadOnlyList<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(SuccessorDll, dll);
            Assert.Equal(new[] { "conductor", "setup" }, arguments.Take(2));
            Assert.Contains("--project=fixture", arguments);
            Assert.Contains("--tenant=tenant", arguments);
            Assert.Contains("--disposable-fixture", arguments);
            Assert.Contains(StateDirectory, arguments);
            if (arguments.Contains("--plan"))
            {
                Steps.Add("plan");
                return new(_stores.Any(s => Version(s) > RequiredVersion) ? 1 : 0, PlanOverride ?? string.Join("\n", _stores.Select(store =>
                {
                    var entry = new StoreSetupPlanEntry(store, Path.Combine(StateDirectory, store + ".db"), Version(store), RequiredVersion);
                    return $"{store}: recorded {entry.RecordedVersion}; required {entry.RequiredVersion}; action {entry.Action} ({entry.DatabasePath})";
                })), "");
            }
            Steps.Add("setup");
            SetupCalls++;
            Assert.False(File.Exists(Path.Combine(StateDirectory, "conduct-loop.lock")));
            Assert.False(ConductorLoopLease.IsActive(StateDirectory));
            if (UnconfirmedSetup) return new(-1, "", "setup termination undetermined", TerminationConfirmed: false);
            if (RacingOwner)
            {
                using var racingLease = ConductorLoopLease.Acquire(StateDirectory);
                Assert.True(ConductorLoopLease.IsActive(StateDirectory));
                Assert.Throws<InvalidOperationException>(() => ConductorLoopLease.Acquire(StateDirectory));
                return new(1, "", "Forward migration refused: active conductor pid fixture");
            }
            // The real setup takes this lease itself. Prove the supervisor released its probe.
            using var ownLease = ConductorLoopLease.Acquire(StateDirectory);
            SetVersion("a", RequiredVersion);
            if (SetupThrows) throw new InvalidOperationException("Setup stopped; completed: a; pending: b. fixture setup failure");
            if (PartialFailure) return new(1, "", "Setup stopped; completed: a; pending: b. fixture setup failure");
            if (IncompleteSetup) return new(0, "", "");
            foreach (var store in _stores.Skip(1)) SetVersion(store, RequiredVersion);
            return new(0, "setup finished", "");
        }

        internal Task<int> RunAsync(bool enableUpgrade = true)
        {
            var supervisor = new ConductorContinuitySupervisor(Host, new RecordingEvents(),
                schemaUpgrade: enableUpgrade ? new ConductorHandoffSchemaUpgrade(StateDirectory, _root, RunChild) : null,
                stageSuccessorWithHook: (hook, token) =>
                {
                    hook(SuccessorDll, "commit-b", token);
                    Steps.Add("self-check");
                    if (_stores.Any(s => Version(s) != RequiredVersion))
                        throw new ConductorSelfRelaunchPreparationException("self-check", "fixture existing newer-store refusal");
                    return new(Path.GetDirectoryName(SuccessorDll)!, SuccessorDll, "commit-b", "commit-b", "fixture self-check passed");
                },
                inspectStores: () => _stores.Select(s => new StoreSetupPlanEntry(s, Path.Combine(StateDirectory, s + ".db"), Version(s), 1)).ToArray(),
                appendConductEvent: (_, _, detail) => { Events.Add(detail); Steps.Add(detail); },
                dotnetPath: "dotnet-fixture", activationHealthyTicks: 3,
                activationDelay: (_, _) =>
                {
                    Assert.True(Stall);
                    Assert.Contains("successor-launch", Steps);
                    Deadlines++;
                    return Task.CompletedTask; // An explicit deadline signal, independent of wall-clock pacing.
                });
            return supervisor.RunAsync(["conduct", "--loop", "--repository-root", _root,
                    "--expected-state-directory", StateDirectory, "--disposable-fixture"], _root,
                Path.Combine(StateDirectory, "continuity"), "fixture", "tenant");
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class Host(Fixture fixture) : IConductorSupervisorProcessHost
    {
        internal List<ConductorSupervisorProcessRequest> Requests { get; } = [];
        public Task<ConductorSupervisorProcessResult> RunAsync(ConductorSupervisorProcessRequest request, CancellationToken token)
        {
            var index = Requests.Count;
            Requests.Add(request);
            Assert.InRange(index, 0, 1);
            if (index == 0)
            {
                File.WriteAllText(request.StdoutPath, fixture.DrainLog);
                ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("self-relaunch", 1, 1, true));
                return Task.FromResult(new ConductorSupervisorProcessResult(0, 100, TerminationConfirmed: fixture.PreviousChildExited));
            }
            if (request.CommandPrefix is not null)
            {
                fixture.Steps.Add("successor-launch");
                if (fixture.LaunchThrows) throw new IOException("fixture launch failed");
                request.OnStandardOutputLine!("LOOP_READY lock=acquired");
                request.OnStandardOutputLine("LOOP_START tick=0");
                if (fixture.Stall)
                {
                    var stopped = new TaskCompletionSource<ConductorSupervisorProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    token.Register(() => stopped.TrySetResult(new(-1, 101)));
                    return stopped.Task;
                }
                if (fixture.Crash) return Task.FromResult(new ConductorSupervisorProcessResult(-1, 101));
                for (var tick = 1; tick <= 3; tick++) request.OnStandardOutputLine($"TICK_END tick={tick} activation=true");
            }
            ConductorContinuityExitArtifact.Write(request.ExitArtifactPath, new("stop-file", 3, 1, false));
            return Task.FromResult(new ConductorSupervisorProcessResult(0, 101));
        }
    }

    private sealed class RecordingEvents : IRunEventStore
    {
        public Task<RunEventRecord> AppendAsync(RunEventAppend evt, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RunEventRecord(1, evt.EventId ?? "fixture", evt.OccurredAt ?? DateTimeOffset.MinValue,
                evt.EventType, evt.GoalId, evt.Operation, evt.Status, evt.Detail, evt.PayloadJson));
        public Task<IReadOnlyList<RunEventRecord>> ReadSinceAsync(long afterSequence = 0, string? goalId = null,
            int maxCount = 500, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RunEventRecord>>([]);
    }
}
