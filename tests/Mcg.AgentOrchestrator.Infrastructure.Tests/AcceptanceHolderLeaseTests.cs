using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

// Parallel-safe: every durable artifact has a unique root; process/job boundaries are injected.
public sealed class AcceptanceHolderLeaseTests
{
    [Fact]
    public void Retention_PreservesReconciledAndOwnerlessArtifactsUntilDescendantsSettle()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        using var claim = lease.Claim(fixture.Binding, fixture.Holder);
        var attempt = new ConductorParallelAcceptanceAttempt("attempt", "candidate", "candidate", 0,
            "branch", "main", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, fixture.Holder.ProcessId,
            ConductorParallelAcceptanceAttemptOutcome.Passed, "out", "err", "exit", "heartbeat", "result",
            fixture.Binding.MetadataPath, ReconciledAt: DateTimeOffset.UnixEpoch)
        { HolderContractVersion = 1 };
        var path = Path.Combine(fixture.Root, "attempt.attempt.json");
        File.WriteAllText(path, JsonSerializer.Serialize(attempt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        fixture.HolderAlive = false;
        fixture.ActiveShards = 2;
        Assert.True(StorageRetentionMaintenance.AcceptanceHolderPreventsRetention(document.RootElement, lease));
        Assert.True(StorageRetentionMaintenance.AcceptanceHolderDirectoryPreventsRetention(fixture.Root, lease));
        fixture.ActiveShards = 0;
        Assert.False(StorageRetentionMaintenance.AcceptanceHolderDirectoryPreventsRetention(fixture.Root, lease));
    }

    [Fact]
    public void ParallelCoordinator_SuccessorFencesOutOfScopeHolderAndTerminalizesBeforeAdmission()
    {
        using var fixture = new Fixture();
        var goal = new AgentOrchestratorKernel().CreateGoal("Holder handoff");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", "main");
        var first = Coordinator(fixture.FirstGeneration);
        var started = first.Evaluate(candidate, ConductorAutonomyPolicy.Permissive,
            (_, _, _, _, _) => throw new InvalidOperationException("Verifier must execute only in the owned child."));
        fixture.ActiveShards = 2;
        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
        first.PrepareHolderDetach();
        Assert.Equal("Detached", fixture.Lease().Read(fixture.Binding).Phase);
        fixture.FirstGenerationAlive = false;
        var successor = Coordinator(fixture.SecondGeneration);
        Assert.Empty(successor.GetCapacityReservingAttempts([]));
        var unrelatedGoal = new AgentOrchestratorKernel().CreateGoal("Unrelated acceptance query");
        Assert.Empty(successor.GetCapacityReservingAttempts([unrelatedGoal.Id.Value]));
        // No selected goals: the durable holder must still reserve its logical slot.
        Assert.Equal(started.Attempt!.AttemptId,
            Assert.Single(successor.GetAllCapacityReservingAttempts()).AttemptId);
        Assert.Equal(fixture.SecondGeneration, fixture.Lease().Read(fixture.Binding).Generation);
        fixture.HolderAlive = false;
        Assert.Single(successor.GetAllCapacityReservingAttempts());
        Assert.Equal(1, fixture.HolderLaunches);
        fixture.ActiveShards = 0;
        Assert.Empty(successor.GetAllCapacityReservingAttempts());
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied,
            Assert.Single(successor.GetUnreconciledAttempts([goal.Id.Value])).Outcome);

        ConductorParallelAcceptanceAttemptCoordinator Coordinator(SpawnProcessIdentity generation) => new(
            Path.Combine(fixture.Root, "attempts"), "integration",
            isProcessAlive: pid => pid == fixture.Holder.ProcessId && fixture.HolderAlive,
            utcNow: () => DateTimeOffset.UnixEpoch,
            holderLease: fixture.Lease(), generationIdentity: generation,
            launchOwnedProcess: launch =>
            {
                fixture.Binding = AcceptanceHolderBinding.For(launch.Attempt);
                using var claim = fixture.Lease().Claim(fixture.Binding, fixture.Holder);
                return new(fixture.Holder.ProcessId, fixture.Holder.StartedAt, fixture.Holder.ImagePath);
            });
    }

    [Fact]
    public void GroupedCoordinator_DetachesAndReconcilesDescendantsBeforeFreeingCapacity()
    {
        using var fixture = new Fixture();
        var first = Coordinator(fixture.FirstGeneration);
        var attempt = first.Create("cohort", [], "main", "tree", "manifest", "identity",
            fixture.Root, ConductorAutonomyPolicy.Permissive);
        first.Launch(attempt);
        fixture.ActiveShards = 2;
        first.PrepareHolderDetach();
        fixture.FirstGenerationAlive = false;
        var successor = Coordinator(fixture.SecondGeneration);
        Assert.Equal(1, ConductorLiveGroupedGateCount.Count(successor));
        Assert.Equal("Adopted", fixture.Lease().Read(fixture.Binding).Phase);
        fixture.HolderAlive = false;
        Assert.Equal(1, ConductorLiveGroupedGateCount.Count(successor));
        fixture.ActiveShards = 0;
        Assert.Equal(0, ConductorLiveGroupedGateCount.Count(successor));
        Assert.NotNull(ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath).ReconciledAt);
        Assert.Equal(1, fixture.HolderLaunches);

        ConductorGroupedGateAttemptCoordinator Coordinator(SpawnProcessIdentity generation) => new(
            Path.Combine(fixture.Root, "grouped"),
            isProcessAlive: pid => pid == fixture.Holder.ProcessId && fixture.HolderAlive,
            holderLease: fixture.Lease(), generationIdentity: generation,
            launch: launch =>
            {
                fixture.Binding = AcceptanceHolderBinding.For(launch);
                using var claim = fixture.Lease().Claim(fixture.Binding, fixture.Holder);
                return new(fixture.Holder.ProcessId, fixture.Holder.StartedAt, fixture.Holder.ImagePath);
            });
    }

    [Fact]
    public void BounceWithActiveShards_AdoptsOneHolderAndWaitsForDescendantSettlement()
    {
        using var fixture = new Fixture();
        var first = fixture.Lease();
        first.Reserve(fixture.Binding, fixture.FirstGeneration);
        using var containment = first.Claim(fixture.Binding, fixture.Holder);
        fixture.ActiveShards = 2;
        Assert.True(first.Detach(fixture.Binding, fixture.FirstGeneration));
        fixture.FirstGenerationAlive = false;

        // A newly constructed coordinator reads only durable state, as on successor startup.
        var successor = fixture.Lease();
        Assert.True(successor.Adopt(fixture.Binding, fixture.SecondGeneration));
        Assert.True(successor.Adopt(fixture.Binding, fixture.SecondGeneration));
        Assert.True(successor.Observe(fixture.Binding).ReservesCapacity);
        fixture.TryReplacement(successor);
        Assert.Equal(1, fixture.HolderLaunches);
        Assert.Equal(2, fixture.ActiveShards);

        // Result publication and root death both precede shard cleanup in this control.
        File.WriteAllText(fixture.Binding.MetadataPath + ".result", "passed");
        fixture.HolderAlive = false;
        fixture.TryReplacement(successor);
        Assert.Equal(1, fixture.HolderLaunches);
        Assert.True(successor.Observe(fixture.Binding).ReservesCapacity);
        fixture.ActiveShards = 0;
        fixture.TryReplacement(successor);
        Assert.Equal(2, fixture.HolderLaunches);
        Assert.Equal(1, fixture.MaximumConcurrentHolderTrees);
        var record = successor.Read(fixture.Binding);
        Assert.Equal("Released", record.Phase);
        Assert.Equal(new[] { "claim", "detach", "adopt", "release" }, record.Receipts.Select(receipt => receipt.Transition));
        Assert.Equal("job-custody-ready", record.Receipts[0].Reason);
        Assert.Equal(fixture.Holder, record.Holder);
        Assert.Equal(fixture.SecondGeneration, record.Generation);
        Assert.All(record.Receipts, receipt => Assert.True(receipt.DurationMilliseconds >= 1));
    }

    [Fact]
    public void RecycledConductorIdentity_AdoptsDespiteLiveReplacementPid()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        using var containment = lease.Claim(fixture.Binding, fixture.Holder);
        fixture.FirstGenerationAlive = false; // the exact identity is dead; its PID is now unrelated
        Assert.True(fixture.RecycledPidAlive);
        Assert.True(lease.CanAdopt(fixture.Binding, fixture.SecondGeneration));
        Assert.True(lease.Adopt(fixture.Binding, fixture.SecondGeneration));
        Assert.Equal(fixture.SecondGeneration, lease.Read(fixture.Binding).Generation);
    }

    [Fact]
    public void RecycledHolder_CancelsOnlyBoundJobAndPublishesTerminalReceipt()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        using var containment = lease.Claim(fixture.Binding, fixture.Holder);
        fixture.ActiveShards = 2;
        fixture.HolderAlive = false;
        fixture.FirstGenerationAlive = false;
        Assert.True(lease.Observe(fixture.Binding).ReservesCapacity);
        Assert.True(lease.Cancel(fixture.Binding, fixture.SecondGeneration, "identity-changed"));
        Assert.Equal(1, fixture.CancelledJobs);
        Assert.True(fixture.RecycledPidAlive);
        Assert.False(lease.Observe(fixture.Binding).ReservesCapacity);
        var receipt = Assert.Single(lease.Read(fixture.Binding).Receipts, receipt => receipt.Transition == "cancel");
        Assert.Equal("cancel", receipt.Transition);
        Assert.Contains("identity-changed", receipt.Reason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => lease.Claim(fixture.Binding, fixture.Holder));
    }

    [Fact]
    public void ForeignLiveGeneration_CannotAdoptOrCancelItsHolder()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        using var containment = lease.Claim(fixture.Binding, fixture.Holder);
        Assert.False(lease.CanAdopt(fixture.Binding, fixture.SecondGeneration));
        Assert.False(lease.Adopt(fixture.Binding, fixture.SecondGeneration));
        Assert.False(lease.Cancel(fixture.Binding, fixture.SecondGeneration, "foreign-conductor"));
        Assert.Equal(0, fixture.CancelledJobs);
        Assert.True(lease.Observe(fixture.Binding).ReservesCapacity);
    }

    [Theory]
    [InlineData("identity-unknown")]
    [InlineData("job-unavailable")]
    [InlineData("job-mismatch")]
    public void UnverifiableHolder_RetainsCapacityAndRefusesTransferOrCancellation(string failure)
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        using var containment = lease.Claim(fixture.Binding, fixture.Holder);
        fixture.Failure = failure;
        fixture.FirstGenerationAlive = false;
        Assert.True(lease.Observe(fixture.Binding).ReservesCapacity);
        Assert.False(lease.Adopt(fixture.Binding, fixture.SecondGeneration));
        Assert.False(lease.Detach(fixture.Binding, fixture.FirstGeneration));
        Assert.False(lease.Cancel(fixture.Binding, fixture.SecondGeneration, "fallback"));
        Assert.Equal(0, fixture.CancelledJobs);
        Assert.Equal("Claimed", lease.Read(fixture.Binding).Phase);
    }

    [Fact]
    public void MissingOrMismatchedLease_IsAnUnresolvedHold()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        Assert.True(lease.Observe(fixture.Binding).ReservesCapacity);
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        var otherCandidate = fixture.Binding with { Candidate = "different-candidate" };
        Assert.True(lease.Observe(otherCandidate).ReservesCapacity);
        Assert.Throws<InvalidDataException>(() => lease.Detach(otherCandidate, fixture.FirstGeneration));
    }

    [Fact]
    public void ClaimedHolderMismatch_RefusesMetadataIdentityInsteadOfFreeingCapacity()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        using var containment = lease.Claim(fixture.Binding, fixture.Holder);
        var recycled = fixture.Holder with { StartedAt = fixture.Holder.StartedAt.AddDays(1) };
        Assert.Throws<InvalidDataException>(() => lease.ValidateRecordedHolder(fixture.Binding, recycled));
        Assert.True(lease.Observe(fixture.Binding).ReservesCapacity);
    }

    [Fact]
    public void MalformedTerminalLease_DoesNotAuthorizeAdmission()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        using var containment = lease.Claim(fixture.Binding, fixture.Holder);
        var invalid = lease.Read(fixture.Binding) with { Phase = "Released", Receipts = [] };
        File.WriteAllText(fixture.Binding.LeasePath, JsonSerializer.Serialize(invalid));
        Assert.True(lease.Observe(fixture.Binding).ReservesCapacity);
        Assert.Throws<InvalidDataException>(() => lease.Read(fixture.Binding));
    }

    [Fact]
    public void LegacyTerminalMetadata_RetainsFenceThroughGracefulDetachUntilSettlementIsProven()
    {
        using var fixture = new Fixture();
        var receipts = new List<string>();
        var coordinator = new ConductorGroupedGateAttemptCoordinator(fixture.Root,
            isProcessAlive: _ => false, holderLease: fixture.Lease(), generationIdentity: fixture.FirstGeneration,
            eventSink: receipts.Add);
        var legacy = coordinator.Create("cohort", [], "main", "tree", "manifest", "identity",
            fixture.Root, ConductorAutonomyPolicy.Permissive) with
        { HolderContractVersion = 0, OwnerProcessId = 1234, ReconciledAt = DateTimeOffset.UnixEpoch };
        ConductorGroupedGateAttemptCoordinator.Save(legacy);
        Assert.True(coordinator.ReservesCapacity(legacy));
        Assert.Equal(1, ConductorLiveGroupedGateCount.Count(coordinator));
        Assert.Contains("legacy-tree-settlement-unavailable", Assert.Single(receipts), StringComparison.Ordinal);
        coordinator.PrepareHolderDetach();
        Assert.Contains(receipts, line => line.Contains("legacy-detach-capacity-fenced", StringComparison.Ordinal));
        Assert.True(coordinator.ReservesCapacity(legacy));
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Passed")]
    public void LegacyGroupedSettlement_TerminalizesBeforeFreeingCapacityAndPreservesVerdict(string outcome)
    {
        using var fixture = new Fixture();
        var coordinator = new ConductorGroupedGateAttemptCoordinator(fixture.Root,
            classifyHolder: _ => SpawnTrackedProcessStatus.DeadOrRecycled,
            holderLease: fixture.Lease(), generationIdentity: fixture.FirstGeneration);
        var legacy = coordinator.Create("cohort", [], "main", "tree", "manifest", "identity",
            fixture.Root, ConductorAutonomyPolicy.Permissive) with
        { HolderContractVersion = 0, OwnerProcessId = 1234, Outcome = outcome };
        ConductorGroupedGateAttemptCoordinator.Save(legacy);
        Assert.True(coordinator.ReservesCapacity(legacy)); // root death alone is insufficient
        WriteLegacySettlement(AcceptanceHolderBinding.For(legacy), legacy.OwnerProcessId,
            legacy.OwnerProcessStartedAt, legacy.OwnerExecutablePath, legacy.StartedAt);
        Assert.False(coordinator.ReservesCapacity(legacy));
        Assert.Equal(0, ConductorLiveGroupedGateCount.Count(coordinator));
        var terminal = ConductorGroupedGateAttemptCoordinator.Read(legacy.MetadataPath);
        Assert.Equal(outcome == "Running" ? "Reconciled" : outcome, terminal.Outcome);
        if (outcome == "Running") Assert.NotNull(terminal.ReconciledAt);
        coordinator.PrepareHolderDetach();
    }

    [Fact]
    public void LegacySettlement_RevalidatesBindingAndRejectsLiveOrUnknownHolder()
    {
        using var fixture = new Fixture();
        WriteLegacySettlement(fixture.Binding, fixture.Holder.ProcessId, fixture.Holder.StartedAt,
            fixture.Holder.ImagePath, DateTimeOffset.UnixEpoch);
        bool Settled(AcceptanceHolderBinding binding, SpawnProcessIdentity holder, SpawnTrackedProcessStatus status) =>
            LegacyAcceptanceHolderSettlement.IsSettled(binding, holder.ProcessId, holder.StartedAt,
                holder.ImagePath, DateTimeOffset.UnixEpoch, () => status);
        Assert.True(Settled(fixture.Binding, fixture.Holder, SpawnTrackedProcessStatus.DeadOrRecycled));
        Assert.False(Settled(fixture.Binding, fixture.Holder, SpawnTrackedProcessStatus.LiveMatch));
        Assert.False(Settled(fixture.Binding, fixture.Holder, SpawnTrackedProcessStatus.Unknown));
        Assert.False(Settled(fixture.Binding with { Candidate = "other" }, fixture.Holder, SpawnTrackedProcessStatus.DeadOrRecycled));
        Assert.False(Settled(fixture.Binding with { Slot = "slot-1" }, fixture.Holder, SpawnTrackedProcessStatus.DeadOrRecycled));
        Assert.False(Settled(fixture.Binding, fixture.Holder with { StartedAt = fixture.Holder.StartedAt.AddDays(1) },
            SpawnTrackedProcessStatus.DeadOrRecycled));
        File.WriteAllText(LegacyAcceptanceHolderSettlement.ReceiptPath(fixture.Binding), "{}");
        Assert.False(Settled(fixture.Binding, fixture.Holder, SpawnTrackedProcessStatus.DeadOrRecycled));
    }

    [Theory]
    [InlineData("Running", "DeadOrRecycled", false)]
    [InlineData("Passed", "DeadOrRecycled", false)]
    [InlineData("Running", "LiveMatch", true)]
    [InlineData("Passed", "LiveMatch", false)]
    [InlineData("Running", "Unknown", true)]
    public void LegacyGroupedIdentity_PreLeaseRuleKeepsPublishedRecordsUnchanged(
        string outcome, string statusName, bool reserves)
    {
        using var fixture = new Fixture();
        var events = new List<string>();
        var coordinator = new ConductorGroupedGateAttemptCoordinator(fixture.Root,
            classifyHolder: _ => Enum.Parse<SpawnTrackedProcessStatus>(statusName),
            holderLease: fixture.Lease(), generationIdentity: fixture.FirstGeneration,
            eventSink: events.Add);
        var legacy = coordinator.Create("cohort", [], "main", "tree", "manifest", "identity",
            fixture.Root, ConductorAutonomyPolicy.Permissive) with
        {
            HolderContractVersion = 0, OwnerProcessId = fixture.Holder.ProcessId,
            OwnerProcessStartedAt = fixture.Holder.StartedAt, OwnerExecutablePath = fixture.Holder.ImagePath,
            Outcome = outcome
        };
        ConductorGroupedGateAttemptCoordinator.Save(legacy);
        Assert.Equal(reserves, coordinator.ReservesCapacity(legacy));
        var binding = AcceptanceHolderBinding.For(legacy);
        Assert.False(LegacyAcceptanceHolderSettlement.HasTerminalReceipt(binding,
            legacy.OwnerProcessId, legacy.OwnerProcessStartedAt, legacy.OwnerExecutablePath, legacy.StartedAt));
        Assert.False(File.Exists(LegacyAcceptanceHolderSettlement.ReceiptPath(binding)));
        var terminal = ConductorGroupedGateAttemptCoordinator.Read(legacy.MetadataPath);
        Assert.Equal(outcome, terminal.Outcome);
        if (reserves) Assert.Contains("legacy-pre-lease-owner-live", Assert.Single(events), StringComparison.Ordinal);
        else Assert.Empty(events);
        Assert.Equal(reserves, coordinator.ReservesCapacity(terminal));
        Assert.Equal(reserves ? 1 : 0, events.Count);
    }

    [Theory]
    [InlineData("DeadOrRecycled", false)]
    [InlineData("LiveMatch", true)]
    [InlineData("Unknown", true)]
    public void LegacyParallelIdentity_TerminalPublicationPrecedesCapacityRelease(
        string statusName, bool reserves)
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Root, "goal");
        Directory.CreateDirectory(directory);
        var legacy = new ConductorParallelAcceptanceAttempt("legacy", "goal", "goal", 0,
            "branch", "main", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, fixture.Holder.ProcessId,
            ConductorParallelAcceptanceAttemptOutcome.Running,
            Path.Combine(directory, "out"), Path.Combine(directory, "err"),
            Path.Combine(directory, "exit"), Path.Combine(directory, "heartbeat"),
            Path.Combine(directory, "result"), Path.Combine(directory, "legacy.attempt.json"))
        { OwnerProcessStartedAt = fixture.Holder.StartedAt, OwnerExecutablePath = fixture.Holder.ImagePath };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        File.WriteAllText(legacy.MetadataPath, JsonSerializer.Serialize(legacy, json));
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(fixture.Root, TrunkBranchName.Default,
            utcNow: () => DateTimeOffset.UnixEpoch.AddDays(1),
            classifyHolder: _ => Enum.Parse<SpawnTrackedProcessStatus>(statusName),
            holderLease: fixture.Lease(), generationIdentity: fixture.FirstGeneration);

        Assert.Equal(reserves ? 1 : 0, coordinator.GetAllCapacityReservingAttempts().Count);
        var terminal = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(legacy.MetadataPath), json)!;
        Assert.Equal(reserves ? ConductorParallelAcceptanceAttemptOutcome.Running
            : ConductorParallelAcceptanceAttemptOutcome.ProcessDied, terminal.Outcome);
        Assert.Equal(!reserves, LegacyAcceptanceHolderSettlement.HasTerminalReceipt(
            AcceptanceHolderBinding.For(legacy), legacy.OwnerProcessId, legacy.OwnerProcessStartedAt,
            legacy.OwnerExecutablePath, legacy.StartedAt));
    }

    private static void WriteLegacySettlement(AcceptanceHolderBinding binding, int processId,
        DateTimeOffset? startedAt, string? image, DateTimeOffset attemptStartedAt,
        DateTimeOffset? holderObservedAt = null) =>
        File.WriteAllText(LegacyAcceptanceHolderSettlement.ReceiptPath(binding), JsonSerializer.Serialize(
            new LegacyAcceptanceHolderSettlementReceipt(binding, processId, startedAt, image, attemptStartedAt,
                attemptStartedAt.AddDays(1), "operator inspected all descendants and proved exit",
                HolderObservedAt: holderObservedAt)));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void LegacySettlement_IncompleteIdentity_RequiresReplacementAfterRecordedObservation(
        int replacementOffset, bool expected)
    {
        using var fixture = new Fixture();
        var observed = DateTimeOffset.UnixEpoch.AddMinutes(1);
        WriteLegacySettlement(fixture.Binding, fixture.Holder.ProcessId, null, null, DateTimeOffset.UnixEpoch, observed);
        var probes = 0;
        DateTimeOffset? Probe(int pid)
        {
            Assert.Equal(fixture.Holder.ProcessId, pid);
            probes++;
            return observed.AddTicks(replacementOffset);
        }
        Assert.Equal(expected, LegacyAcceptanceHolderSettlement.IsSettled(fixture.Binding,
            fixture.Holder.ProcessId, null, null, DateTimeOffset.UnixEpoch,
            () => SpawnTrackedProcessStatus.Unknown, observed, Probe));
        Assert.Equal(1, probes);
        Assert.False(LegacyAcceptanceHolderSettlement.IsSettled(fixture.Binding,
            fixture.Holder.ProcessId, null, null, DateTimeOffset.UnixEpoch,
            () => SpawnTrackedProcessStatus.Unknown, readProcessStartedAt: Probe));
    }

    [Theory]
    [InlineData(-1)] // observation before attempt creation
    [InlineData(86401)] // observation after settlement confirmation
    public void LegacySettlement_IncompleteIdentity_RejectsObservationOutsideReceiptLifetime(int seconds)
    {
        using var fixture = new Fixture();
        var invalidObservation = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        WriteLegacySettlement(fixture.Binding, fixture.Holder.ProcessId, null, null,
            DateTimeOffset.UnixEpoch, invalidObservation);
        Assert.False(LegacyAcceptanceHolderSettlement.IsSettled(fixture.Binding,
            fixture.Holder.ProcessId, null, null, DateTimeOffset.UnixEpoch,
            () => SpawnTrackedProcessStatus.Unknown, invalidObservation,
            _ => DateTimeOffset.UnixEpoch.AddDays(2)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void LegacySettlement_MissingImage_UsesRecordedStartToDistinguishReplacement(bool replaced, bool expected)
    {
        using var fixture = new Fixture();
        var started = DateTimeOffset.UnixEpoch.AddMinutes(1);
        WriteLegacySettlement(fixture.Binding, fixture.Holder.ProcessId, started, null, DateTimeOffset.UnixEpoch);
        Assert.Equal(expected, LegacyAcceptanceHolderSettlement.IsSettled(fixture.Binding,
            fixture.Holder.ProcessId, started, null, DateTimeOffset.UnixEpoch,
            () => SpawnTrackedProcessStatus.Unknown,
            readProcessStartedAt: _ => replaced ? started.AddTicks(1) : started));
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Passed")]
    public void LegacyParallelSettlement_RecycledPid_ReleasesHistoricalSlotAfterTerminalPublication(
        string outcomeName)
    {
        var outcome = Enum.Parse<ConductorParallelAcceptanceAttemptOutcome>(outcomeName);
        using var fixture = new Fixture();
        // The test host is an owned, retained live handle standing in for an unrelated
        // replacement. Its creation timestamp is a fixed input, never a pacing assertion.
        using var replacement = System.Diagnostics.Process.GetCurrentProcess();
        var replacementStart = new DateTimeOffset(replacement.StartTime.ToUniversalTime());
        var root = Path.Combine(fixture.Root, "attempts");
        var directory = Path.Combine(root, "legacy-goal");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.attempt.json");
        var legacy = new ConductorParallelAcceptanceAttempt("legacy", "legacy-goal", "legacy-goal", 0,
            "branch", "main", replacementStart.AddMinutes(-2), replacementStart.AddMinutes(-1),
            replacement.Id, outcome, Path.Combine(directory, "out"), Path.Combine(directory, "err"),
            Path.Combine(directory, "exit"), Path.Combine(directory, "heartbeat"), Path.Combine(directory, "result"), path);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        File.WriteAllText(path, JsonSerializer.Serialize(legacy, options));
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(root, "integration",
            utcNow: () => replacementStart.AddDays(2),
            classifyHolder: _ => SpawnTrackedProcessStatus.Unknown,
            holderLease: fixture.Lease(), generationIdentity: fixture.FirstGeneration);
        Assert.Single(coordinator.GetAllCapacityReservingAttempts());
        coordinator.PrepareHolderDetach(); // the retained legacy fence must not abort stop
        WriteLegacySettlement(AcceptanceHolderBinding.For(legacy), replacement.Id, null, null,
            legacy.StartedAt, legacy.LastHeartbeatAt);
        Assert.Empty(coordinator.GetAllCapacityReservingAttempts());
        using var terminalDocument = JsonDocument.Parse(File.ReadAllText(path));
        var terminal = terminalDocument.RootElement.Deserialize<ConductorParallelAcceptanceAttempt>(options)!;
        Assert.Equal(outcome == ConductorParallelAcceptanceAttemptOutcome.Running
            ? ConductorParallelAcceptanceAttemptOutcome.ProcessDied : outcome, terminal.Outcome);
        Assert.False(StorageRetentionMaintenance.AcceptanceHolderPreventsRetention(terminalDocument.RootElement));
        coordinator.MarkReconciled(terminal);
        Assert.Empty(coordinator.GetAllCapacityReservingAttempts());
        using var reconciledDocument = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(replacementStart.AddDays(2), reconciledDocument.RootElement
            .Deserialize<ConductorParallelAcceptanceAttempt>(options)!.LastHeartbeatAt);
        Assert.False(StorageRetentionMaintenance.AcceptanceHolderPreventsRetention(reconciledDocument.RootElement));
        Assert.False(replacement.HasExited); // recovery never kills the unrelated PID
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyGroupedSettlement_RecycledPid_UsesFrozenProofAcrossReconciliation(bool hasObservation)
    {
        using var fixture = new Fixture();
        using var replacement = System.Diagnostics.Process.GetCurrentProcess();
        var replacementStart = new DateTimeOffset(replacement.StartTime.ToUniversalTime());
        var observed = replacementStart.AddMinutes(-1);
        var coordinator = new ConductorGroupedGateAttemptCoordinator(fixture.Root,
            utcNow: () => replacementStart.AddDays(2),
            classifyHolder: _ => SpawnTrackedProcessStatus.Unknown,
            holderLease: fixture.Lease(), generationIdentity: fixture.FirstGeneration);
        var legacy = coordinator.Create("cohort", [], "main", "tree", "manifest", "identity",
            fixture.Root, ConductorAutonomyPolicy.Permissive) with
        {
            HolderContractVersion = 0, OwnerProcessId = replacement.Id,
            StartedAt = hasObservation ? replacementStart.AddMinutes(-2) : replacementStart.AddDays(-2),
            ReconciledAt = hasObservation ? observed : null
        };
        ConductorGroupedGateAttemptCoordinator.Save(legacy);
        Assert.True(coordinator.ReservesCapacity(legacy));
        WriteLegacySettlement(AcceptanceHolderBinding.For(legacy), replacement.Id, null, null,
            legacy.StartedAt, legacy.ReconciledAt);
        Assert.False(coordinator.ReservesCapacity(legacy));
        var terminal = ConductorGroupedGateAttemptCoordinator.Read(legacy.MetadataPath);
        Assert.Equal("Reconciled", terminal.Outcome);
        Assert.Equal(replacementStart.AddDays(2), terminal.ReconciledAt);
        Assert.False(coordinator.ReservesCapacity(terminal));
        Assert.Equal(0, ConductorLiveGroupedGateCount.Count(coordinator));
        Assert.False(replacement.HasExited);
    }

    [Fact]
    public void LegacySettlement_RootAbsentAtConfirmation_StaysSettledWhenPidIsReused()
    {
        using var fixture = new Fixture();
        WriteLegacySettlement(fixture.Binding, fixture.Holder.ProcessId, null, null, DateTimeOffset.UnixEpoch);
        DateTimeOffset? processStart = null;
        bool Settled(DateTimeOffset? metadataObservation) => LegacyAcceptanceHolderSettlement.IsSettled(
            fixture.Binding, fixture.Holder.ProcessId, null, null, DateTimeOffset.UnixEpoch,
            () => SpawnTrackedProcessStatus.Unknown, metadataObservation, _ => processStart);
        Assert.True(Settled(null));
        processStart = DateTimeOffset.UnixEpoch.AddDays(1).AddTicks(1);
        Assert.True(Settled(DateTimeOffset.UnixEpoch.AddDays(2)));
        processStart = DateTimeOffset.UnixEpoch.AddDays(1); // equality does not prove reuse
        Assert.False(Settled(DateTimeOffset.UnixEpoch.AddDays(2)));
    }

    [Fact]
    public void LegacyUnresolvedLaunch_RequiresTerminalStateBeforeOperatorRecovery()
    {
        using var fixture = new Fixture();
        var coordinator = new ConductorGroupedGateAttemptCoordinator(fixture.Root,
            classifyHolder: _ => SpawnTrackedProcessStatus.Unknown,
            holderLease: fixture.Lease(), generationIdentity: fixture.FirstGeneration);
        var legacy = coordinator.Create("cohort", [], "main", "tree", "manifest", "identity",
            fixture.Root, ConductorAutonomyPolicy.Permissive) with { HolderContractVersion = 0 };
        ConductorGroupedGateAttemptCoordinator.Save(legacy);
        WriteLegacySettlement(AcceptanceHolderBinding.For(legacy), 0, null, null, legacy.StartedAt);
        Assert.True(coordinator.ReservesCapacity(legacy)); // a pending launch cannot be sealed by operator proof
        legacy = legacy with { Outcome = "LaunchFailed", ReconciledAt = legacy.StartedAt };
        ConductorGroupedGateAttemptCoordinator.Save(legacy);
        Assert.False(coordinator.ReservesCapacity(legacy));
        Assert.Equal("LaunchFailed", ConductorGroupedGateAttemptCoordinator.Read(legacy.MetadataPath).Outcome);
    }

    [Fact]
    public void FailedOsLaunch_PublishesCancellationAndForbidsAClaim()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        lease.CancelUnstartedLaunch(fixture.Binding, fixture.FirstGeneration);
        Assert.False(lease.Observe(fixture.Binding).ReservesCapacity);
        Assert.Equal("process-not-started", Assert.Single(lease.Read(fixture.Binding).Receipts).Reason);
        Assert.Throws<InvalidOperationException>(() => lease.Claim(fixture.Binding, fixture.Holder));
    }

    [Fact]
    public void UnclaimedDeadLaunch_IsSealedBeforeLateClaim()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        Assert.False(lease.ReconcileUnclaimed(fixture.Binding, fixture.Holder));
        fixture.HolderAlive = false;
        Assert.True(lease.ReconcileUnclaimed(fixture.Binding, fixture.Holder));
        Assert.False(lease.Observe(fixture.Binding).ReservesCapacity);
        Assert.Throws<InvalidOperationException>(() => lease.Claim(fixture.Binding, fixture.Holder));
        Assert.Equal(0, fixture.HolderLaunches);
    }

    [Fact]
    public void StopBeforeChildClaim_CancelsExactLaunchAndPreventsDelayedExecution()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        Assert.False(lease.Detach(fixture.Binding, fixture.FirstGeneration));
        Assert.True(lease.Cancel(fixture.Binding, fixture.FirstGeneration, "stop-before-claim", fixture.Holder));
        Assert.Equal(1, fixture.UnclaimedStops);
        Assert.Equal("Cancelled", lease.Read(fixture.Binding).Phase);
        Assert.False(lease.Observe(fixture.Binding).ReservesCapacity);
        Assert.Throws<InvalidOperationException>(() => lease.Claim(fixture.Binding, fixture.Holder));
        Assert.Equal(0, fixture.HolderLaunches);
    }

    [Fact]
    public void NativeRecycledIdentity_WithAbsentJob_DoesNotStopUnrelatedLiveProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var current = AcceptanceHolderLease.CurrentGeneration();
        var old = current with { StartedAt = current.StartedAt.AddDays(-1) };
        var status = AcceptanceHolderIdentity.Classify(old.ProcessId, old.StartedAt, old.ImagePath);
        Assert.Equal(SpawnTrackedProcessStatus.DeadOrRecycled, status);
        var observation = DurableAcceptanceJob.Observe($"Local\\absent-acceptance-{Guid.NewGuid():N}", old, status, cancel: true);
        // A missing job without a bound settlement receipt cannot prove descendants exited.
        Assert.False(observation.Settled, observation.Detail);
        Assert.False(observation.HolderMatches);
        Assert.Equal("job-open-error:2", observation.Detail);
        Assert.Equal(SpawnTrackedProcessStatus.LiveMatch,
            AcceptanceHolderIdentity.Classify(current.ProcessId, current.StartedAt, current.ImagePath));
    }

    [Fact]
    public void OwnershipArtifact_DoesNotOverwriteChildResultDuringWriterCustody()
    {
        using var fixture = new Fixture();
        var lease = fixture.Lease();
        lease.Reserve(fixture.Binding, fixture.FirstGeneration);
        using var containment = lease.Claim(fixture.Binding, fixture.Holder);
        var resultPath = fixture.Binding.MetadataPath + ".result";
        File.WriteAllText(resultPath, "passed-verdict");
        using var writer = StorageRetentionMaintenance.AcquireAttemptWriterLease(fixture.Root);
        Assert.True(lease.Detach(fixture.Binding, fixture.FirstGeneration));
        fixture.FirstGenerationAlive = false;
        Assert.True(lease.Adopt(fixture.Binding, fixture.SecondGeneration));
        Assert.Equal("passed-verdict", File.ReadAllText(resultPath));
        Assert.Equal("Adopted", fixture.Lease().Read(fixture.Binding).Phase);
    }

    [Fact]
    public void SettledHolderWithoutResultRecordsFirstTransientDeath()
    {
        using var fixture = new Fixture();
        var goal = new AgentOrchestratorKernel().CreateGoal("Settled holder retry");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", "main");
        var first = SettlementCoordinator(fixture, fixture.FirstGeneration);
        var started = first.Evaluate(candidate, ConductorAutonomyPolicy.Permissive,
            (_, _, _, _, _) => throw new InvalidOperationException("Unexpected verifier execution."));
        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
        Assert.Equal("Claimed", fixture.Lease().Read(fixture.Binding).Phase);
        fixture.ActiveShards = 2;
        first.PrepareHolderDetach();
        Assert.Equal("Detached", fixture.Lease().Read(fixture.Binding).Phase);
        fixture.FirstGenerationAlive = false;
        var successor = SettlementCoordinator(fixture, fixture.SecondGeneration);
        Assert.Single(successor.GetAllCapacityReservingAttempts());
        Assert.Equal(fixture.SecondGeneration, fixture.Lease().Read(fixture.Binding).Generation);
        fixture.HolderAlive = false;
        Assert.Single(successor.GetAllCapacityReservingAttempts());
        Assert.False(File.Exists(started.Attempt!.ResultPath));

        fixture.ActiveShards = 0;
        Assert.Empty(successor.GetAllCapacityReservingAttempts());
        Assert.Equal("Released", fixture.Lease().Read(fixture.Binding).Phase);
        var terminal = ReadSettlementAttempt(started.Attempt);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, terminal.Outcome);
        Assert.StartsWith("holder-tree-settled:", terminal.Detail);
        Assert.Equal(1, terminal.TransientFailureCount);
        Assert.True(ConductorParallelAcceptanceAttemptCoordinator.IsTransientTerminalFailure(terminal));
    }

    [Fact]
    public void CancelledHolderWithoutResultRecordsTransientDeath()
    {
        using var fixture = new Fixture();
        var goal = new AgentOrchestratorKernel().CreateGoal("Cancelled holder retry");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", "main");
        var coordinator = SettlementCoordinator(fixture, fixture.FirstGeneration);
        var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Permissive,
            (_, _, _, _, _) => throw new InvalidOperationException("Unexpected verifier execution."));
        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
        Assert.Equal("Claimed", fixture.Lease().Read(fixture.Binding).Phase);
        Assert.True(fixture.Lease().Cancel(fixture.Binding, fixture.FirstGeneration, "test cancellation"));
        Assert.Equal(1, fixture.CancelledJobs);
        Assert.Equal("Cancelled", fixture.Lease().Read(fixture.Binding).Phase);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, ReadSettlementAttempt(started.Attempt!).Outcome);
        Assert.False(File.Exists(started.Attempt!.ResultPath));

        Assert.Empty(coordinator.GetAllCapacityReservingAttempts());
        var terminal = ReadSettlementAttempt(started.Attempt);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, terminal.Outcome);
        Assert.Equal("holder-tree-settled:Cancelled", terminal.Detail);
        Assert.Equal(1, terminal.TransientFailureCount);
    }

    [Fact]
    public void ConsecutiveSettledDeathsIncrementTheCandidateCount()
    {
        using var fixture = new Fixture();
        var goal = new AgentOrchestratorKernel().CreateGoal("Consecutive holder deaths");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", "main");
        var coordinator = SettlementCoordinator(fixture, fixture.FirstGeneration);
        var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Permissive,
            (_, _, _, _, _) => throw new InvalidOperationException("Unexpected verifier execution."));
        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
        var current = started.Attempt!;
        var earlier = current with
        {
            AttemptId = "earlier",
            MetadataPath = Path.Combine(Path.GetDirectoryName(current.MetadataPath)!, "earlier.attempt.json"),
            StartedAt = current.StartedAt.AddMinutes(-1),
            CompletedAt = current.StartedAt.AddSeconds(-1),
            ReconciledAt = current.StartedAt.AddSeconds(-1),
            ExecutionProtocol = "in-process",
            HolderContractVersion = 0,
            HolderCandidateKey = null,
            Outcome = ConductorParallelAcceptanceAttemptOutcome.ProcessDied,
            Detail = "holder-tree-settled:Cancelled",
            TransientFailureCount = 2
        };
        File.WriteAllText(earlier.MetadataPath,
            JsonSerializer.Serialize(earlier, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(current.CandidateKey, ReadSettlementAttempt(earlier).CandidateKey);
        Assert.NotNull(ReadSettlementAttempt(earlier).ReconciledAt);
        Assert.False(File.Exists(current.ResultPath));
        fixture.HolderAlive = false;
        fixture.ActiveShards = 0;

        Assert.Empty(coordinator.GetAllCapacityReservingAttempts());
        var terminal = ReadSettlementAttempt(current);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, terminal.Outcome);
        Assert.StartsWith("holder-tree-settled:", terminal.Detail);
        Assert.Equal(3, terminal.TransientFailureCount);
    }

    [Fact]
    public void SettledHolderWithResultFileKeepsOutcomeAndCount()
    {
        using var fixture = new Fixture();
        var goal = new AgentOrchestratorKernel().CreateGoal("Published holder result");
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [], "branch", "main");
        var coordinator = SettlementCoordinator(fixture, fixture.FirstGeneration);
        var started = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Permissive,
            (_, _, _, _, _) => throw new InvalidOperationException("Unexpected verifier execution."));
        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, started.Kind);
        var before = started.Attempt! with { TransientFailureCount = 2 };
        File.WriteAllText(before.MetadataPath,
            JsonSerializer.Serialize(before, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, ReadSettlementAttempt(before).Outcome);
        File.WriteAllText(before.ResultPath, "{}");
        Assert.True(File.Exists(before.ResultPath));
        fixture.HolderAlive = false;
        fixture.ActiveShards = 0;

        Assert.Empty(coordinator.GetAllCapacityReservingAttempts());
        Assert.Equal("Released", fixture.Lease().Read(fixture.Binding).Phase);
        var after = ReadSettlementAttempt(before);
        Assert.Equal(before.Outcome, after.Outcome);
        Assert.Equal(before.TransientFailureCount, after.TransientFailureCount);

        // Removing the result proves the guarded writer is armed for this same settled holder.
        File.Delete(before.ResultPath);
        Assert.Empty(coordinator.GetAllCapacityReservingAttempts());
        var withoutResult = ReadSettlementAttempt(before);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, withoutResult.Outcome);
        Assert.Equal(1, withoutResult.TransientFailureCount);
    }

    [Fact]
    public void LegacySettledHolderWithoutResultRecordsTransientDeath()
    {
        using var fixture = new Fixture();
        var directory = Path.Combine(fixture.Root, "goal");
        Directory.CreateDirectory(directory);
        var legacy = new ConductorParallelAcceptanceAttempt("legacy", "goal", "goal", 0,
            "branch", "main", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, fixture.Holder.ProcessId,
            ConductorParallelAcceptanceAttemptOutcome.Running,
            Path.Combine(directory, "out"), Path.Combine(directory, "err"),
            Path.Combine(directory, "exit"), Path.Combine(directory, "heartbeat"),
            Path.Combine(directory, "result"), Path.Combine(directory, "legacy.attempt.json"))
        { OwnerProcessStartedAt = fixture.Holder.StartedAt, OwnerExecutablePath = fixture.Holder.ImagePath };
        File.WriteAllText(legacy.MetadataPath,
            JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(fixture.Root, TrunkBranchName.Default,
            utcNow: () => DateTimeOffset.UnixEpoch.AddDays(1),
            classifyHolder: _ => SpawnTrackedProcessStatus.DeadOrRecycled,
            holderLease: fixture.Lease(), generationIdentity: fixture.FirstGeneration);
        Assert.False(File.Exists(legacy.ResultPath));

        Assert.Empty(coordinator.GetAllCapacityReservingAttempts());
        var terminal = ReadSettlementAttempt(legacy);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, terminal.Outcome);
        Assert.Equal("legacy-holder-settled", terminal.Detail);
        Assert.Equal(1, terminal.TransientFailureCount);
        Assert.True(ConductorParallelAcceptanceAttemptCoordinator.IsTransientTerminalFailure(terminal));
    }

    private static ConductorParallelAcceptanceAttempt ReadSettlementAttempt(ConductorParallelAcceptanceAttempt attempt) =>
        JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(attempt.MetadataPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static ConductorParallelAcceptanceAttemptCoordinator SettlementCoordinator(
        Fixture fixture, SpawnProcessIdentity generation) => new(
        Path.Combine(fixture.Root, "attempts"), "integration",
        isProcessAlive: pid => pid == fixture.Holder.ProcessId && fixture.HolderAlive,
        utcNow: () => DateTimeOffset.UnixEpoch,
        holderLease: fixture.Lease(), generationIdentity: generation,
        launchOwnedProcess: launch =>
        {
            fixture.Binding = AcceptanceHolderBinding.For(launch.Attempt);
            using var claim = fixture.Lease().Claim(fixture.Binding, fixture.Holder);
            return new(fixture.Holder.ProcessId, fixture.Holder.StartedAt, fixture.Holder.ImagePath);
        });

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"holder-lease-{Guid.NewGuid():N}");
        internal AcceptanceHolderBinding Binding { get; set; }
        internal SpawnProcessIdentity FirstGeneration { get; } = new(111, DateTimeOffset.UnixEpoch, "conductor.exe");
        internal SpawnProcessIdentity SecondGeneration { get; } = new(112, DateTimeOffset.UnixEpoch.AddDays(1), "conductor.exe");
        internal SpawnProcessIdentity Holder { get; } = new(222, DateTimeOffset.UnixEpoch, "dotnet.exe");
        internal bool FirstGenerationAlive = true;
        internal bool HolderAlive = true;
        internal bool RecycledPidAlive = true;
        internal int ActiveShards;
        internal int HolderLaunches;
        internal int MaximumConcurrentHolderTrees;
        internal int CancelledJobs;
        internal int UnclaimedStops;
        internal string? Failure;
        private int _clock;
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Binding = new("attempt", Path.Combine(Root, "attempt.attempt.json"), "candidate:branch:main", "slot-0");
        }
        internal AcceptanceHolderLease Lease() => new(
            utcNow: () => DateTimeOffset.UnixEpoch.AddMilliseconds(++_clock),
            classify: identity => identity == Holder
                ? Failure == "identity-unknown" ? SpawnTrackedProcessStatus.Unknown
                    : HolderAlive ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled
                : identity == FirstGeneration && !FirstGenerationAlive
                    ? SpawnTrackedProcessStatus.DeadOrRecycled : SpawnTrackedProcessStatus.LiveMatch,
            attach: name =>
            {
                Assert.Equal(Binding.JobName, name);
                HolderLaunches++;
                MaximumConcurrentHolderTrees = Math.Max(MaximumConcurrentHolderTrees, 1);
                return new EmptyDisposable();
            },
            job: (name, holder, status, cancel) =>
            {
                Assert.Equal(Binding.JobName, name);
                Assert.Equal(Holder, holder);
                if (Failure is not null) return new(false, false, Failure);
                if (cancel) { CancelledJobs++; HolderAlive = false; ActiveShards = 0; }
                return new(status == SpawnTrackedProcessStatus.LiveMatch,
                    !HolderAlive && ActiveShards == 0, $"active-shards:{ActiveShards}");
            },
            stopUnclaimed: identity =>
            {
                Assert.Equal(Holder, identity);
                UnclaimedStops++;
                HolderAlive = false;
                return true;
            });
        internal void TryReplacement(AcceptanceHolderLease lease)
        {
            if (lease.Observe(Binding).ReservesCapacity) return;
            Assert.Equal("Released", lease.Read(Binding).Phase); // publication precedes launch
            MaximumConcurrentHolderTrees = Math.Max(MaximumConcurrentHolderTrees,
                (HolderAlive || ActiveShards > 0 ? 1 : 0) + 1);
            HolderLaunches++;
        }
        public void Dispose() => Directory.Delete(Root, true);
        private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    }
}
