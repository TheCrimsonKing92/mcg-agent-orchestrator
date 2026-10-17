using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated roots and named mutexes; process, gate-load and clock seams are injected.
// No build slot is acquired and no real holder process is launched.
public sealed class SweepLeaseGateAdmissionScenarioTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void BlockedRealSweep_WithOnlyTerminalReceipts_AdmitsReadyGoalAndReportsRunning() => RunScenario(false);

    [Fact]
    public void BlockedRealSweep_WithMissingTerminalReceipt_HoldsThenAdmitsAfterSweep() => RunScenario(true);

    private void RunScenario(bool missingTerminalReceipt)
    {
        using var liveGates = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var root = CreateTempDirectory("mcg-sweep-lease-gate-admission");
        try
        {
            var now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var orchestrator = Path.Combine(root, ".orchestrator");
            var attemptsRoot = Path.Combine(orchestrator, "acceptance-gate-attempts");
            var retiredGoal = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(attemptsRoot, retiredGoal);
            Directory.CreateDirectory(directory);
            var receipted = WriteLegacy("settled", 1, terminalReceipt: true);
            var other = WriteLegacy("other", 0, terminalReceipt: !missingTerminalReceipt);
            Assert.Equal(SpawnTrackedProcessStatus.DeadOrRecycled, AcceptanceHolderIdentity.Classify(
                other.OwnerProcessId, other.OwnerProcessStartedAt, other.OwnerExecutablePath));
            Assert.False(StorageRetentionMaintenance.AcceptanceHolderDirectoryPreventsRetention(directory));

            var kernel = new AgentOrchestratorKernel(new ScenarioClock(now));
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(),
                "Update src/SweepAdmission.cs for gate admission");
            PassVerificationAt(kernel, goal, goal.Tasks.Single(), now);
            var eventsPath = Path.Combine(root, "conduct-events.log");
            var generation = new SpawnProcessIdentity(909, now, "conductor.exe");
            var process = new SpawnProcessIdentity(7116, now, "dotnet.exe");
            var lease = new AcceptanceHolderLease(utcNow: () => now,
                classify: _ => SpawnTrackedProcessStatus.LiveMatch,
                attach: _ => new EmptyDisposable(), job: (_, _, _, _) => new(true, false, "fixture-live-holder"));
            var launches = new List<ConductorParallelAcceptanceAttempt>();
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptsRoot, TrunkBranchName.Default,
                utcNow: () => now, holderLease: lease, generationIdentity: generation,
                classifyHolder: attempt => attempt.GoalId == retiredGoal
                    ? SpawnTrackedProcessStatus.DeadOrRecycled : SpawnTrackedProcessStatus.LiveMatch,
                hostBootTime: () => null, acquireStableSlotLease: (_, _) => null,
                conductEventLogWriter: new ConductEventLogWriter(eventsPath),
                launchOwnedProcess: launch =>
                {
                    Assert.Equal(goal.Id.Value, launch.Attempt.GoalId);
                    launches.Add(launch.Attempt);
                    using var claim = lease.Claim(AcceptanceHolderBinding.For(launch.Attempt), process);
                    return new(process.ProcessId, process.StartedAt, process.ImagePath);
                });
            var escalations = new List<string>();
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException("Expected owned-process launch."),
                getLandingFileScopes: _ => ["src/SweepAdmission.cs"],
                parallelAcceptanceAttemptCoordinator: coordinator, getAcceptanceSlotCount: _ => 1,
                resolveAcceptanceHeads: _ => ("branch-sha", "main-sha"), utcNow: () => now,
                writeEscalation: (_, _, detail) => escalations.Add(detail));
            Assert.True(driver.ParallelAcceptanceEnabled);

            using var sweep = new BlockedSweep(directory, hook => StorageRetentionMaintenance.Run(
                Path.Combine(root, "logs"), orchestrator, Path.Combine(root, "execution"),
                [new StorageRetentionGoal(retiredGoal, GoalStatus.Completed, new Dictionary<string, WorkTaskStatus>())],
                now, mtpResultsRoot: null, beforeAttemptCandidateDeletionForTests: hook,
                reclaimOptions: new(null, null, null, null, () => [])));
            using (var second = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(directory))
                Assert.Null(second);

            var census = coordinator.GetAllCapacityReservingAttempts();
            if (missingTerminalReceipt)
            {
                Assert.Equal(other.AttemptId, Assert.Single(census).AttemptId);
                var heldTick = Tick();
                Assert.Empty(launches);
                Assert.DoesNotContain(heldTick.ProgressLines!, IsRunningOrEscalated);
                Assert.Empty(escalations);
                var busyHolds = Events().Where(record => record.EventKind == "acceptance-holder" &&
                    record.Detail.StartsWith("ACCEPTANCE_HOLDER_HOLD ", StringComparison.Ordinal) &&
                    record.Detail.Contains("reason=attempt-writer-lease-busy", StringComparison.Ordinal)).ToArray();
                Assert.NotEmpty(busyHolds);
                Assert.All(busyHolds, record => Assert.StartsWith(
                    $"ACCEPTANCE_HOLDER_HOLD attempt={other.AttemptId} ", record.Detail, StringComparison.Ordinal));
                Assert.DoesNotContain(busyHolds, record => record.Detail.Contains(
                    $"attempt={receipted.AttemptId} ", StringComparison.Ordinal));
            }
            else
            {
                Assert.Empty(census);
                AssertResult(Tick(), "started");
                Assert.Equal(goal.Id.Value, Assert.Single(launches).GoalId);
                AssertResult(Tick(), "running");
                Assert.Single(launches);
            }

            var result = sweep.Complete();
            Assert.False(result.Failed);
            Assert.Contains(" status=completed ", RunEventMaintenanceCadence.FormatArtifactRetentionReceipt(result),
                StringComparison.Ordinal);
            Assert.DoesNotContain(result.Decisions, decision => decision.Action is
                EvidenceRetentionAction.Failed or EvidenceRetentionAction.DeferredLease or
                EvidenceRetentionAction.DeferredLive or EvidenceRetentionAction.DeferredLocked or
                EvidenceRetentionAction.RetainedUndecidable);
            Assert.True(File.Exists(other.MetadataPath), "Sweep removed the metadata needed by the post-release tick.");
            if (missingTerminalReceipt)
            {
                Assert.False(HasReceipt(other));
                AssertResult(Tick(), "started");
                Assert.True(HasReceipt(other));
                Assert.Contains(Events(), record => record.EventKind == "acceptance-holder" && record.Detail.StartsWith(
                    $"ACCEPTANCE_HOLDER_RELEASE attempt={other.AttemptId} ", StringComparison.Ordinal));
                Assert.Equal(goal.Id.Value, Assert.Single(launches).GoalId);
            }
            Assert.Empty(escalations);
            Assert.DoesNotContain(Events(), record => record.Detail.Contains("result=escalated", StringComparison.Ordinal) ||
                record.EventKind.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
                record.EventKind.Contains("failed", StringComparison.OrdinalIgnoreCase));

            ConductorParallelAcceptanceAttempt WriteLegacy(string id, int slot, bool terminalReceipt)
            {
                // This is the fixture's own PID with a deliberately mismatched start, never a machine-wide PID guess.
                var attempt = new ConductorParallelAcceptanceAttempt(id, retiredGoal, retiredGoal[..8], slot,
                    "old-branch", "old-main", now.AddDays(-2), now.AddDays(-1), Environment.ProcessId,
                    ConductorParallelAcceptanceAttemptOutcome.Passed,
                    Path.Combine(directory, id + ".out"), Path.Combine(directory, id + ".err"),
                    Path.Combine(directory, id + ".exit"), Path.Combine(directory, id + ".heartbeat"),
                    Path.Combine(directory, id + ".result"), Path.Combine(directory, id + ".attempt.json"),
                    ReconciledAt: now.AddDays(-1))
                { OwnerProcessStartedAt = now.AddDays(-2), OwnerExecutablePath = "legacy-dotnet.exe" };
                File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt, json));
                var binding = AcceptanceHolderBinding.For(attempt);
                if (terminalReceipt)
                    Assert.True(LegacyAcceptanceHolderSettlement.RecordTerminal(binding, attempt.OwnerProcessId,
                        attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath, attempt.StartedAt, now, "fixture-holder-exited"));
                else
                {
                    // Whole-tree operator settlement permits retention; admission still needs its terminal receipt.
                    File.WriteAllText(LegacyAcceptanceHolderSettlement.ReceiptPath(binding), JsonSerializer.Serialize(
                        new LegacyAcceptanceHolderSettlementReceipt(binding, attempt.OwnerProcessId,
                            attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath, attempt.StartedAt, now,
                            "fixture-operator-confirmed-tree-exit")));
                    Assert.False(HasReceipt(attempt));
                }
                return attempt;
            }

            bool HasReceipt(ConductorParallelAcceptanceAttempt attempt) => LegacyAcceptanceHolderSettlement.HasTerminalReceipt(
                AcceptanceHolderBinding.For(attempt), attempt.OwnerProcessId, attempt.OwnerProcessStartedAt,
                attempt.OwnerExecutablePath, attempt.StartedAt);
            ConductEventRecord[] Events() => File.Exists(eventsPath) ? File.ReadLines(eventsPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, json)!).ToArray() : [];
            BatchTickSummary Tick()
            {
                BatchTickSummary? tick = null;
                new ConductorBatchLoop(utcNow: () => now).Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                    NoStopPath(), maxIterations: 1, onTick: current => tick = current);
                return Assert.IsType<BatchTickSummary>(tick);
            }
            void AssertResult(BatchTickSummary tick, string expected) => Assert.Contains(tick.ProgressLines!, line =>
                line.Contains($"ACCEPTANCE goal={goal.Id.Value[..8]}", StringComparison.Ordinal) &&
                line.Contains($"result={expected}", StringComparison.Ordinal));
            bool IsRunningOrEscalated(string line) => line.Contains($"ACCEPTANCE goal={goal.Id.Value[..8]}", StringComparison.Ordinal) &&
                (line.Contains("result=running", StringComparison.Ordinal) || line.Contains("result=started", StringComparison.Ordinal) ||
                 line.Contains("result=escalated", StringComparison.Ordinal));
        }
        finally { TryDeleteDirectory(root); }
    }

    private sealed record ScenarioClock(DateTimeOffset UtcNow) : IClock;
    private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }

    private sealed class BlockedSweep : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private bool _hookEntered;
        private bool _joined;
        private StorageRetentionResult? _result;
        private Exception? _failure;

        internal BlockedSweep(string directory, Func<Action<string>, StorageRetentionResult> run)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _result = run(path =>
                    {
                        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), Path.GetFullPath(directory))) return;
                        _hookEntered = true;
                        _entered.Set();
                        Assert.True(_release.Wait(TimeSpan.FromSeconds(60)), "Sweep release was not signaled.");
                    });
                }
                catch (Exception ex) { _failure = ex; }
                finally { _entered.Set(); }
            }) { IsBackground = true };
            _thread.Start();
            if (!_entered.Wait(TimeSpan.FromSeconds(60)) || !_hookEntered)
            {
                Dispose();
                Assert.Fail("Sweep did not reach the attempt-directory hook: " + JsonSerializer.Serialize(_result));
            }
        }

        internal StorageRetentionResult Complete()
        {
            _release.Set();
            if (!_joined)
            {
                Assert.True(_thread.Join(TimeSpan.FromSeconds(60)), "Retention sweep thread did not finish.");
                _joined = true;
            }
            Assert.Null(_failure);
            return Assert.IsType<StorageRetentionResult>(_result);
        }

        public void Dispose()
        {
            try { Complete(); }
            finally { _entered.Dispose(); _release.Dispose(); }
        }
    }
}
