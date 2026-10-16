using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Every artifact and process boundary belongs to this fixture; no real holder is launched.
[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsLegacyHolderAdmission(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Theory]
    [InlineData("Passed", "DeadOrRecycled")]
    [InlineData("Failed", "DeadOrRecycled")]
    [InlineData("Faulted", "DeadOrRecycled")]
    [InlineData("GateEngineFault", "DeadOrRecycled")]
    [InlineData("Reconciled", "DeadOrRecycled")]
    [InlineData("Passed", "LiveMatch")]
    [InlineData("Passed", "Unknown")]
    [InlineData("Passed", "RecycledStart")]
    [InlineData("Passed", "RecycledStartAndImage")]
    [InlineData("Passed", "MismatchedImage")]
    public void ReconciledLegacyHolder_ReleasesAbsentIdentityWithoutEscalation(
        string outcomeName, string statusName)
    {
        using var liveGates = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var root = CreateTempDirectory("mcg-conductor-legacy-reconciled-admission");
        try
        {
            var now = DateTimeOffset.Parse("2026-10-14T09:00:00Z");
            var clock = new FixedClock(now);
            var kernel = new AgentOrchestratorKernel(clock);
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel, DefaultAgents(), "Update src/Legacy.cs for legacy holder admission");
            PassVerificationAt(kernel, goal, goal.Tasks.Single(), now);
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal, 0, ["src/Legacy.cs"], "branch-sha", "main-sha");
            var directory = Path.Combine(root, goal.Id.Value);
            Directory.CreateDirectory(directory);
            var outcome = Enum.Parse<ConductorParallelAcceptanceAttemptOutcome>(outcomeName);
            var legacy = new ConductorParallelAcceptanceAttempt("legacy", goal.Id.Value, goal.Id.Value[..8],
                0, "branch-sha", "main-sha", now.AddDays(-2), now.AddDays(-1), 7115, outcome,
                Path.Combine(directory, "out"), Path.Combine(directory, "err"),
                Path.Combine(directory, "exit"), Path.Combine(directory, "heartbeat"),
                Path.Combine(directory, "result"), Path.Combine(directory, "legacy.attempt.json"),
                // The Reconciled enum alone must also retain its tree-settlement fence.
                ReconciledAt: outcomeName == "Reconciled" ? null : now.AddDays(-1))
            { OwnerProcessStartedAt = now.AddDays(-2), OwnerExecutablePath = "legacy-dotnet.exe" };
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            File.WriteAllText(legacy.MetadataPath, JsonSerializer.Serialize(legacy, json));
            var recorded = new SpawnProcessIdentity(legacy.OwnerProcessId,
                legacy.OwnerProcessStartedAt!.Value, legacy.OwnerExecutablePath!);
            var observed = statusName switch
            {
                "RecycledStart" => recorded with { StartedAt = recorded.StartedAt.AddDays(1) },
                "RecycledStartAndImage" => recorded with
                { StartedAt = recorded.StartedAt.AddDays(1), ImagePath = "replacement.exe" },
                "MismatchedImage" => recorded with { ImagePath = "unverifiable.exe" },
                _ => recorded
            };
            var status = statusName is "RecycledStart" or "RecycledStartAndImage" or "MismatchedImage"
                ? SpawnProcessIdentityReader.EvaluateRecordedIdentity(recorded, observed, out _)
                : Enum.Parse<SpawnTrackedProcessStatus>(statusName);
            if (statusName.StartsWith("Recycled", StringComparison.Ordinal))
            {
                Assert.Equal(recorded.ProcessId, observed.ProcessId);
                Assert.NotEqual(recorded.StartedAt, observed.StartedAt);
                Assert.Equal(SpawnTrackedProcessStatus.DeadOrRecycled, status);
            }
            if (statusName == "MismatchedImage")
                Assert.Equal(SpawnTrackedProcessStatus.Unknown, status);
            var eventLog = Path.Combine(root, "conduct-events.log");
            var launches = 0;
            var generation = new SpawnProcessIdentity(909, now, "conductor.exe");
            var holder = new SpawnProcessIdentity(7116, now, "dotnet.exe");
            var lease = new AcceptanceHolderLease(utcNow: () => now,
                classify: _ => SpawnTrackedProcessStatus.LiveMatch,
                attach: _ => new EmptyDisposable(),
                job: (_, _, _, _) => new(true, false, "fixture-live-holder"));
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default,
                utcNow: () => now, classifyHolder: attempt => attempt.AttemptId == legacy.AttemptId
                    ? status : SpawnTrackedProcessStatus.LiveMatch,
                holderLease: lease, generationIdentity: generation,
                conductEventLogWriter: new ConductEventLogWriter(eventLog),
                acquireStableSlotLease: (_, _) => null,
                launchOwnedProcess: launch =>
                {
                    Assert.True(File.Exists(LegacyAcceptanceHolderSettlement.TerminalReceiptPath(
                        AcceptanceHolderBinding.For(legacy))));
                    Assert.Equal(outcome, ReadLegacy().Outcome);
                    launches++;
                    using var claim = lease.Claim(AcceptanceHolderBinding.For(launch.Attempt), holder);
                    return new(holder.ProcessId, holder.StartedAt, holder.ImagePath);
                });

            var selected = legacy;
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException(
                    "The out-of-process fixture must launch through its owned-process seam."),
                getLandingFileScopes: _ => ["src/Legacy.cs"],
                parallelAcceptanceAttemptCoordinator: coordinator,
                resolveAcceptanceHeads: _ => ("branch-sha", "main-sha"),
                utcNow: () => now);
            Assert.True(driver.ParallelAcceptanceEnabled);
            if (status != SpawnTrackedProcessStatus.DeadOrRecycled)
            {
                selected = Assert.Single(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running,
                    coordinator.ObserveExistingAttempt(selected, candidate).Kind);
                Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running,
                    coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative, PassingRun).Kind);
                var heldTick = RunTick();
                Assert.Contains(heldTick.ProgressLines!, line =>
                    line.Contains($"ACCEPTANCE goal={goal.Id.Value[..8]}", StringComparison.Ordinal) &&
                    line.Contains("slot=slot-0", StringComparison.Ordinal) &&
                    line.Contains("result=running", StringComparison.Ordinal));
                Assert.DoesNotContain(heldTick.ProgressLines!, line =>
                    line.Contains("result=escalated", StringComparison.Ordinal));
                Assert.Equal(0, launches);
                Assert.Equal(legacy.AttemptId, Assert.Single(coordinator.GetAllCapacityReservingAttempts()).AttemptId);
                Assert.Equal(outcome, ReadLegacy().Outcome);
                Assert.Equal(legacy.ReconciledAt, ReadLegacy().ReconciledAt);
                Assert.False(File.Exists(LegacyAcceptanceHolderSettlement.TerminalReceiptPath(
                    AcceptanceHolderBinding.For(legacy))));
                var holdEvents = File.ReadLines(eventLog)
                    .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, json)!)
                    .Where(record => record.EventKind == "acceptance-holder" &&
                        record.Detail.StartsWith($"ACCEPTANCE_HOLDER_HOLD attempt={legacy.AttemptId} ",
                            StringComparison.Ordinal)).ToArray();
                // Admission checks and loop detach may each publish a changed hold reason.
                Assert.NotEmpty(holdEvents);
                Assert.All(holdEvents, record =>
                {
                    var holdLog = record.Detail;
                    Assert.Contains("ACCEPTANCE_HOLDER_HOLD", holdLog, StringComparison.Ordinal);
                    Assert.Contains($"attempt={legacy.AttemptId} slot=slot-0", holdLog, StringComparison.Ordinal);
                    Assert.Contains($"holder={recorded.ProcessId} holderStartedAt={recorded.StartedAt:O}",
                        holdLog, StringComparison.Ordinal);
                    Assert.Contains($"holderImage={recorded.ImagePath}", holdLog, StringComparison.Ordinal);
                });
            }

            var binding = AcceptanceHolderBinding.For(legacy);
            if (status != SpawnTrackedProcessStatus.DeadOrRecycled)
            {
                // A live or uninspectable identity retains capacity until the deterministic exit signal.
                Assert.Single(coordinator.GetAllCapacityReservingAttempts());
                Assert.Equal(outcome, ReadLegacy().Outcome);
                Assert.Equal(0, launches);
            }
            status = SpawnTrackedProcessStatus.DeadOrRecycled; // deterministic settlement signal
            Assert.Empty(coordinator.GetAllCapacityReservingAttempts());
            Assert.Empty(coordinator.GetUnreconciledAttempts([goal.Id.Value]));
            Assert.True(LegacyAcceptanceHolderSettlement.HasTerminalReceipt(binding, legacy.OwnerProcessId,
                legacy.OwnerProcessStartedAt, legacy.OwnerExecutablePath, legacy.StartedAt));
            Assert.False(File.Exists(LegacyAcceptanceHolderSettlement.ReceiptPath(binding)));
            // A receipt published after selection must not turn that stale snapshot into escalation.
            var settledSnapshot = coordinator.ObserveExistingAttempt(selected, candidate);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Running, settledSnapshot.Kind);
            Assert.Null(settledSnapshot.Run);
            var admittedTick = RunTick();
            Assert.Contains(admittedTick.ProgressLines!, line =>
                line.Contains("slot=slot-0", StringComparison.Ordinal) &&
                line.Contains("result=started", StringComparison.Ordinal));
            Assert.DoesNotContain(admittedTick.ProgressLines!, line =>
                line.Contains("result=escalated", StringComparison.Ordinal));
            Assert.Equal(1, launches);
            var successor = Assert.Single(coordinator.GetAllCapacityReservingAttempts());
            Assert.NotEqual(legacy.AttemptId, successor.AttemptId);
            Assert.Equal(0, successor.SlotIndex);
            Assert.Equal(1, successor.HolderContractVersion);
            Assert.Equal(outcome, ReadLegacy().Outcome);

            ConductorParallelAcceptanceAttempt ReadLegacy() =>
                JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                    File.ReadAllText(legacy.MetadataPath), json)!;
            BatchTickSummary RunTick()
            {
                BatchTickSummary? tick = null;
                new ConductorBatchLoop(utcNow: () => now).Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                    NoStopPath(), maxIterations: 1, onTick: current => tick = current);
                return Assert.IsType<BatchTickSummary>(tick);
            }
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("Passed", true, false, "both", false)]
    [InlineData("Failed", true, false, "both", false)]
    [InlineData("Passed", true, false, "start", false)]
    [InlineData("Passed", true, false, "image", false)]
    [InlineData("Passed", true, true, "both", true)]
    [InlineData("Passed", true, true, "start", true)]
    [InlineData("Passed", true, true, "image", true)]
    [InlineData("Running", false, false, "both", true)]
    [InlineData("Running", true, false, "both", true)]
    [InlineData("Passed", false, false, "both", true)]
    [InlineData("Passed", true, false, "pid-appeared", true)]
    public void IncompleteIdentity_ReleasesOnlyTerminalReconciledAttemptsWithAbsentPid(
        string outcomeName, bool reconciled, bool pidExists, string missing, bool holds)
    {
        using var liveGates = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var root = CreateTempDirectory("mcg-conductor-incomplete-legacy-admission");
        try
        {
            var now = DateTimeOffset.Parse("2026-10-15T09:00:00Z");
            var kernel = new AgentOrchestratorKernel(new FixedClock(now));
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel, DefaultAgents(), "Update src/LegacyUpgrade.cs for legacy admission");
            PassVerificationAt(kernel, goal, goal.Tasks.Single(), now);
            var directory = Path.Combine(root, goal.Id.Value);
            Directory.CreateDirectory(directory);
            var legacy = new ConductorParallelAcceptanceAttempt("legacy", goal.Id.Value, goal.Id.Value[..8],
                0, "branch-sha", "main-sha", now.AddDays(-2), now, 7115,
                Enum.Parse<ConductorParallelAcceptanceAttemptOutcome>(outcomeName),
                Path.Combine(directory, "out"), Path.Combine(directory, "err"),
                Path.Combine(directory, "exit"), Path.Combine(directory, "heartbeat"),
                Path.Combine(directory, "result"), Path.Combine(directory, "legacy.attempt.json"),
                ReconciledAt: reconciled ? now.AddDays(-1) : null)
            {
                OwnerProcessStartedAt = missing == "image" ? now.AddDays(-2) : null,
                OwnerExecutablePath = missing == "start" ? "legacy-dotnet.exe" : null
            };
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            File.WriteAllText(legacy.MetadataPath, JsonSerializer.Serialize(legacy, json));
            var binding = AcceptanceHolderBinding.For(legacy);
            var eventLog = Path.Combine(root, "conduct-events.log");
            var absenceChecks = 0;
            var launches = 0;
            var holder = new SpawnProcessIdentity(7116, now, "dotnet.exe");
            var lease = new AcceptanceHolderLease(utcNow: () => now,
                classify: _ => SpawnTrackedProcessStatus.LiveMatch,
                attach: _ => new EmptyDisposable(),
                job: (_, _, _, _) => new(true, false, "fixture-live-holder"));
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, TrunkBranchName.Default,
                utcNow: () => now, classifyHolder: _ => SpawnTrackedProcessStatus.Unknown,
                isHolderProcessAbsent: pid =>
                {
                    Assert.Equal(legacy.OwnerProcessId, pid);
                    absenceChecks++;
                    // A PID appearing between the initial observation and the writer check holds.
                    return !pidExists && (missing != "pid-appeared" || absenceChecks == 1);
                },
                holderLease: lease, generationIdentity: new(909, now, "conductor.exe"),
                conductEventLogWriter: new ConductEventLogWriter(eventLog),
                acquireStableSlotLease: (_, _) => null,
                launchOwnedProcess: launch =>
                {
                    Assert.True(LegacyAcceptanceHolderSettlement.HasTerminalReceipt(binding,
                        legacy.OwnerProcessId, legacy.OwnerProcessStartedAt, legacy.OwnerExecutablePath,
                        legacy.StartedAt));
                    launches++;
                    using var claim = lease.Claim(AcceptanceHolderBinding.For(launch.Attempt), holder);
                    return new(holder.ProcessId, holder.StartedAt, holder.ImagePath);
                });

            var reservations = coordinator.GetAllCapacityReservingAttempts();
            Assert.Equal(holds ? 1 : 0, reservations.Count);
            if (holds) Assert.Equal(legacy.AttemptId, Assert.Single(reservations).AttemptId);
            Assert.Equal(!holds, File.Exists(LegacyAcceptanceHolderSettlement.TerminalReceiptPath(binding)));
            Assert.False(File.Exists(LegacyAcceptanceHolderSettlement.ReceiptPath(binding)));
            if (!holds)
            {
                var receipt = JsonSerializer.Deserialize<LegacyAcceptanceHolderSettlementReceipt>(
                    File.ReadAllText(LegacyAcceptanceHolderSettlement.TerminalReceiptPath(binding)))!;
                Assert.Equal("holder-terminal", receipt.Transition);
                Assert.Equal("legacy-terminal-reconciled-incomplete-identity-pid-absent", receipt.Evidence);
                Assert.True(absenceChecks >= 2); // Initial observation and canonical writer recheck.
            }
            if (outcomeName == "Running" || !reconciled) Assert.Equal(0, absenceChecks);

            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException("Expected owned launch."),
                getLandingFileScopes: _ => ["src/LegacyUpgrade.cs"],
                parallelAcceptanceAttemptCoordinator: coordinator,
                resolveAcceptanceHeads: _ => ("branch-sha", "main-sha"), utcNow: () => now);
            BatchTickSummary? tick = null;
            new ConductorBatchLoop(utcNow: () => now).Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: 1, onTick: current => tick = current);
            Assert.Equal(holds ? 0 : 1, launches);
            Assert.DoesNotContain(tick!.ProgressLines!, line => line.Contains("result=escalated", StringComparison.Ordinal));
            var remaining = Assert.Single(coordinator.GetAllCapacityReservingAttempts());
            Assert.Equal(0, remaining.SlotIndex);
            if (holds) Assert.Equal(legacy.AttemptId, remaining.AttemptId);
            else
            {
                Assert.NotEqual(legacy.AttemptId, remaining.AttemptId);
                Assert.Equal(1, remaining.HolderContractVersion);
                Assert.Contains(File.ReadLines(eventLog), line => line.Contains("ACCEPTANCE_HOLDER_RELEASE", StringComparison.Ordinal));
            }
            var persisted = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(legacy.MetadataPath), json)!;
            Assert.Equal(legacy.Outcome, persisted.Outcome);
            Assert.Equal(legacy.ReconciledAt, persisted.ReconciledAt);
        }
        finally { TryDeleteDirectory(root); }
    }

    [Theory]
    [InlineData("valid", "both", false)]
    [InlineData("valid", "start", false)]
    [InlineData("valid", "image", false)]
    [InlineData("evidence", "both", true)]
    [InlineData("binding", "both", true)]
    [InlineData("malformed", "both", true)]
    [InlineData("running", "both", true)]
    [InlineData("unreconciled", "both", true)]
    public void IncompleteIdentity_PidReusedAfterRelease_UsesOnlyBoundTerminalReceipt(
        string receiptCase, string missing, bool holds)
    {
        var root = CreateTempDirectory("mcg-conductor-durable-legacy-release");
        try
        {
            var now = DateTimeOffset.Parse("2026-10-15T09:00:00Z");
            var directory = Path.Combine(root, "legacy-goal");
            Directory.CreateDirectory(directory);
            var legacy = new ConductorParallelAcceptanceAttempt("legacy", "legacy-goal", "legacy-goal",
                0, "branch-sha", "main-sha", now.AddDays(-2), now.AddDays(-1), 7115,
                ConductorParallelAcceptanceAttemptOutcome.Passed,
                Path.Combine(directory, "out"), Path.Combine(directory, "err"),
                Path.Combine(directory, "exit"), Path.Combine(directory, "heartbeat"),
                Path.Combine(directory, "result"), Path.Combine(directory, "legacy.attempt.json"),
                ReconciledAt: now.AddDays(-1))
            {
                OwnerProcessStartedAt = missing == "image" ? now.AddDays(-2) : null,
                OwnerExecutablePath = missing == "start" ? "legacy-dotnet.exe" : null
            };
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            File.WriteAllText(legacy.MetadataPath, JsonSerializer.Serialize(legacy, json));
            var pidExists = false;
            var absenceChecks = 0;
            var first = CreateCoordinator();
            Assert.Empty(first.GetAllCapacityReservingAttempts());
            Assert.Equal(2, absenceChecks); // Initial observation and canonical writer recheck.
            var receiptPath = LegacyAcceptanceHolderSettlement.TerminalReceiptPath(
                AcceptanceHolderBinding.For(legacy));
            var receipt = JsonSerializer.Deserialize<LegacyAcceptanceHolderSettlementReceipt>(
                File.ReadAllText(receiptPath))!;
            Assert.Equal("legacy-terminal-reconciled-incomplete-identity-pid-absent", receipt.Evidence);
            Assert.Equal("holder-terminal", receipt.Transition);
            Assert.False(File.Exists(LegacyAcceptanceHolderSettlement.ReceiptPath(receipt.Binding)));

            // Windows reuses the released PID; a fresh conductor must consume durable proof.
            pidExists = true;
            if (receiptCase == "evidence") receipt = receipt with { Evidence = "different-release-rule" };
            if (receiptCase == "binding") receipt = receipt with
            {
                Binding = receipt.Binding with { AttemptId = "another-attempt" }
            };
            File.WriteAllText(receiptPath, receiptCase == "malformed" ? "{" : JsonSerializer.Serialize(receipt));
            if (receiptCase == "running") legacy = legacy with { Outcome = ConductorParallelAcceptanceAttemptOutcome.Running };
            if (receiptCase == "unreconciled") legacy = legacy with { ReconciledAt = null };
            File.WriteAllText(legacy.MetadataPath, JsonSerializer.Serialize(legacy, json));
            var originalReceipt = File.ReadAllText(receiptPath);
            var successor = CreateCoordinator();
            for (var census = 0; census < 2; census++)
            {
                var reservations = successor.GetAllCapacityReservingAttempts();
                Assert.Equal(holds ? 1 : 0, reservations.Count);
                if (holds) Assert.Equal(legacy.AttemptId, Assert.Single(reservations).AttemptId);
            }
            if (!holds) Assert.Equal(2, absenceChecks); // Receipt reuse never re-probes the PID.
            Assert.Equal(originalReceipt, File.ReadAllText(receiptPath));
            using var metadata = JsonDocument.Parse(File.ReadAllText(legacy.MetadataPath));
            Assert.Equal(holds, StorageRetentionMaintenance.AcceptanceHolderPreventsRetention(metadata.RootElement));
            Assert.Equal(holds, StorageRetentionMaintenance.AcceptanceHolderDirectoryPreventsRetention(directory));

            ConductorParallelAcceptanceAttemptCoordinator CreateCoordinator() => new(root,
                TrunkBranchName.Default, utcNow: () => now,
                classifyHolder: _ => SpawnTrackedProcessStatus.Unknown,
                isHolderProcessAbsent: pid =>
                {
                    Assert.Equal(legacy.OwnerProcessId, pid);
                    absenceChecks++;
                    return !pidExists;
                },
                holderLease: new AcceptanceHolderLease(utcNow: () => now),
                generationIdentity: new(909, now, "conductor.exe"));
        }
        finally { TryDeleteDirectory(root); }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
