using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique artifact roots and injected process, containment and clock boundaries.
public sealed class AcceptanceHolderLeaseFaultHoldTests
{
    [Fact]
    public void ParallelDetach_MismatchedBinding_HoldsWithoutChangingArtifacts()
    {
        using var fixture = new Fixture();
        var attempt = fixture.AddParallel();
        var binding = AcceptanceHolderBinding.For(attempt);
        fixture.Mismatch(binding);
        var leaseBytes = File.ReadAllBytes(binding.LeasePath);
        var metadataBytes = File.ReadAllBytes(attempt.MetadataPath);

        fixture.Parallel.PrepareHolderDetach();

        Assert.Equal(attempt.AttemptId, Assert.Single(fixture.Parallel.GetAllCapacityReservingAttempts()).AttemptId);
        AssertHold(fixture, binding, "lease-binding-mismatch", "candidate:other-candidate");
        AssertUnchanged(fixture, binding, leaseBytes, metadataBytes, attempt.ExitCodePath);
    }

    [Theory]
    [InlineData("candidate", "lease-binding-mismatch")]
    [InlineData("phase", "lease-malformed")]
    public void GroupedDetach_InvalidLease_HoldsWithoutChangingArtifacts(string defect, string reason)
    {
        using var fixture = new Fixture();
        var attempt = fixture.AddGrouped();
        var binding = AcceptanceHolderBinding.For(attempt);
        if (defect == "candidate") fixture.Mismatch(binding);
        else fixture.Rewrite(binding, fixture.Lease.Read(binding) with { Phase = "Bogus" });
        var leaseBytes = File.ReadAllBytes(binding.LeasePath);
        var metadataBytes = File.ReadAllBytes(attempt.MetadataPath);

        fixture.Grouped.PrepareHolderDetach();

        Assert.True(fixture.Grouped.ReservesCapacity(attempt));
        AssertHold(fixture, binding, reason, defect == "candidate" ? "candidate:other-candidate" : "phase:Bogus");
        AssertUnchanged(fixture, binding, leaseBytes, metadataBytes, attempt.ExitCodePath);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Census_MalformedJson_HoldsCapacity(bool parallel, bool completeIdentity)
    {
        using var fixture = new Fixture();
        AcceptanceHolderBinding binding;
        string exitPath;
        Func<bool> reserves;
        if (parallel)
        {
            var attempt = fixture.AddParallel();
            if (!completeIdentity)
            {
                attempt = attempt with { OwnerProcessStartedAt = null, OwnerExecutablePath = null };
                File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            binding = AcceptanceHolderBinding.For(attempt);
            exitPath = attempt.ExitCodePath;
            reserves = () => Assert.Single(fixture.Parallel.GetAllCapacityReservingAttempts()).AttemptId == attempt.AttemptId;
        }
        else
        {
            var attempt = fixture.AddGrouped();
            if (!completeIdentity)
            {
                attempt = attempt with { OwnerProcessStartedAt = null, OwnerExecutablePath = null };
                ConductorGroupedGateAttemptCoordinator.Save(attempt);
            }
            binding = AcceptanceHolderBinding.For(attempt);
            exitPath = attempt.ExitCodePath;
            reserves = () => fixture.Grouped.ReservesCapacity(attempt);
        }
        File.WriteAllText(binding.LeasePath, "{malformed");
        var leaseBytes = File.ReadAllBytes(binding.LeasePath);
        var metadataBytes = File.ReadAllBytes(binding.MetadataPath);

        Assert.True(reserves());
        Assert.True(reserves()); // The same read fault is reported once across repeated census calls.

        AssertHold(fixture, binding, "lease-malformed", "unreadable");
        AssertUnchanged(fixture, binding, leaseBytes, metadataBytes, exitPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HolderMismatch_ReadPaths_HoldWithoutSettlement(bool parallel)
    {
        using var fixture = new Fixture();
        AcceptanceHolderBinding binding;
        string exitPath;
        Action readPaths;
        if (parallel)
        {
            var attempt = fixture.AddParallel();
            binding = AcceptanceHolderBinding.For(attempt);
            exitPath = attempt.ExitCodePath;
            readPaths = () =>
            {
                Assert.Equal(attempt.AttemptId, Assert.Single(fixture.Parallel.GetAllCapacityReservingAttempts()).AttemptId);
                fixture.Parallel.PrepareHolderDetach();
            };
        }
        else
        {
            var attempt = fixture.AddGrouped();
            binding = AcceptanceHolderBinding.For(attempt);
            exitPath = attempt.ExitCodePath;
            readPaths = () =>
            {
                fixture.Grouped.PrepareHolderDetach();
                // Refuse reaches StopHolder directly, independently of the detach pre-check.
                Assert.False(fixture.Grouped.Refuse(attempt, "candidate"));
                Assert.False(fixture.Grouped.ShouldAdopt(attempt));
                Assert.Equal(attempt, fixture.Grouped.Adopt(attempt));
                Assert.True(fixture.Grouped.ReservesCapacity(attempt));
            };
        }
        var otherHolder = fixture.Holder with { ProcessId = 333, StartedAt = fixture.Holder.StartedAt.AddDays(1) };
        fixture.Rewrite(binding, fixture.Lease.Read(binding) with { Holder = otherHolder });
        var leaseBytes = File.ReadAllBytes(binding.LeasePath);
        var metadataBytes = File.ReadAllBytes(binding.MetadataPath);

        readPaths();

        AssertHold(fixture, binding, "lease-binding-mismatch", "pid:333");
        var hold = Assert.Single(fixture.Holds());
        Assert.Contains($"expected=attempt:{binding.AttemptId},slot:{binding.Slot},candidate:{binding.Candidate}" +
            $",pid:222,start:{fixture.Holder.StartedAt:O}", hold);
        Assert.Contains($"recorded=attempt:{binding.AttemptId},slot:{binding.Slot},candidate:{binding.Candidate}" +
            $",pid:333,start:{otherHolder.StartedAt:O}", hold);
        AssertUnchanged(fixture, binding, leaseBytes, metadataBytes, exitPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Detach_FirstLeaseFault_StillDetachesLaterHolder(bool parallel)
    {
        using var fixture = new Fixture();
        AcceptanceHolderBinding first;
        AcceptanceHolderBinding second;
        Action detach;
        if (parallel)
        {
            _ = fixture.AddParallel();
            _ = fixture.AddParallel();
            var attempts = fixture.Parallel.GetUnreconciledAttempts(fixture.GoalIds);
            Assert.Equal(2, attempts.Count);
            first = AcceptanceHolderBinding.For(attempts[0]);
            second = AcceptanceHolderBinding.For(attempts[1]);
            detach = fixture.Parallel.PrepareHolderDetach;
        }
        else
        {
            _ = fixture.AddGrouped();
            _ = fixture.AddGrouped();
            var attempts = fixture.Grouped.ReadAll().ToArray();
            Assert.Equal(2, attempts.Length);
            first = AcceptanceHolderBinding.For(attempts[0]);
            second = AcceptanceHolderBinding.For(attempts[1]);
            detach = fixture.Grouped.PrepareHolderDetach;
        }
        fixture.Mismatch(first);

        detach();

        AssertHold(fixture, first, "lease-binding-mismatch", "candidate:other-candidate");
        Assert.Equal("Detached", fixture.Lease.Read(second).Phase);
        Assert.Equal(0, fixture.Cancels);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Detach_HealthyClaimedLease_DetachesWithoutFaultHold(bool parallel)
    {
        using var fixture = new Fixture();
        var binding = parallel ? AcceptanceHolderBinding.For(fixture.AddParallel()) :
            AcceptanceHolderBinding.For(fixture.AddGrouped());

        if (parallel) fixture.Parallel.PrepareHolderDetach();
        else fixture.Grouped.PrepareHolderDetach();

        Assert.Equal("Detached", fixture.Lease.Read(binding).Phase);
        Assert.Empty(fixture.Holds());
        Assert.Equal(0, fixture.Cancels);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Census_HealthyReservedLease_KeepsLiveHolderCapacity(bool parallel)
    {
        using var fixture = new Fixture { ClaimAtLaunch = false };
        AcceptanceHolderBinding binding;
        if (parallel)
        {
            var attempt = fixture.AddParallel();
            binding = AcceptanceHolderBinding.For(attempt);
            Assert.Equal(attempt.AttemptId, Assert.Single(fixture.Parallel.GetAllCapacityReservingAttempts()).AttemptId);
        }
        else
        {
            var attempt = fixture.AddGrouped();
            binding = AcceptanceHolderBinding.For(attempt);
            Assert.True(fixture.Grouped.ReservesCapacity(attempt));
        }

        Assert.Equal("Reserved", fixture.Lease.Read(binding).Phase);
        var observation = fixture.Lease.Observe(binding);
        Assert.True(observation.ReservesCapacity);
        Assert.Null(observation.Fault);
        Assert.DoesNotContain(fixture.Holds(), line => line.Contains("lease-binding-mismatch") || line.Contains("lease-malformed"));
        Assert.Equal(0, fixture.Cancels);
    }

    [Fact]
    public void Read_InvalidLease_PreservesExceptionsAndObservationReason()
    {
        using var fixture = new Fixture();
        var binding = AcceptanceHolderBinding.For(fixture.AddParallel());
        fixture.Mismatch(binding);
        var error = Assert.Throws<InvalidDataException>(() => fixture.Lease.Read(binding));
        Assert.Equal("Acceptance holder lease binding or phase changed.", error.Message);
        Assert.Equal("lease-binding-mismatch", AcceptanceHolderLeaseFault.From(error, binding)!.Reason);

        File.WriteAllText(binding.LeasePath, "{malformed");
        Assert.Throws<JsonException>(() => fixture.Lease.Read(binding));
        var observation = fixture.Lease.Observe(binding);
        Assert.Equal("holder-lease-unavailable:JsonException", observation.Reason);
        Assert.Equal("lease-malformed", observation.Fault!.Reason);
        Assert.Null(AcceptanceHolderLeaseFault.From(new IOException(), binding));
        Assert.Null(AcceptanceHolderLeaseFault.From(new InvalidOperationException(), binding));
    }

    private static void AssertHold(Fixture fixture, AcceptanceHolderBinding binding, string reason, string recorded)
    {
        var hold = Assert.Single(fixture.Holds());
        Assert.Contains($"reason={reason}", hold);
        Assert.Contains($"expected=attempt:{binding.AttemptId},slot:{binding.Slot},candidate:{binding.Candidate}", hold);
        var recordedText = hold[(hold.IndexOf(" recorded=", StringComparison.Ordinal) + " recorded=".Length)..];
        Assert.Contains(" recorded=", hold);
        Assert.Contains(recorded, recordedText);
        Assert.DoesNotContain(' ', recordedText);
    }

    private static void AssertUnchanged(Fixture fixture, AcceptanceHolderBinding binding,
        byte[] leaseBytes, byte[] metadataBytes, string exitPath)
    {
        Assert.Equal(leaseBytes, File.ReadAllBytes(binding.LeasePath));
        Assert.Equal(metadataBytes, File.ReadAllBytes(binding.MetadataPath));
        Assert.False(File.Exists(exitPath));
        Assert.Equal(0, fixture.Cancels);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"holder-fault-{Guid.NewGuid():N}");
        private readonly List<string> _events = [];
        private readonly string _parallelLog;
        internal SpawnProcessIdentity Holder { get; } = new(222, DateTimeOffset.UnixEpoch, "dotnet.exe");
        private SpawnProcessIdentity Generation { get; } = new(111, DateTimeOffset.UnixEpoch, "conductor.exe");
        internal AcceptanceHolderLease Lease { get; }
        internal ConductorParallelAcceptanceAttemptCoordinator Parallel { get; }
        internal ConductorGroupedGateAttemptCoordinator Grouped { get; }
        internal List<string> GoalIds { get; } = [];
        internal bool ClaimAtLaunch { get; init; } = true;
        internal int Cancels { get; private set; }
        private int _groupedCount;

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            _parallelLog = Path.Combine(_root, "events.log");
            Lease = new(utcNow: () => DateTimeOffset.UnixEpoch,
                classify: _ => SpawnTrackedProcessStatus.LiveMatch,
                attach: _ => new EmptyDisposable(),
                job: (_, _, _, cancel) =>
                {
                    if (cancel) Cancels++;
                    return new(true, cancel, "injected-job");
                },
                stopUnclaimed: _ => { Cancels++; return true; });
            Parallel = new(Path.Combine(_root, "parallel"), "integration",
                utcNow: () => DateTimeOffset.UnixEpoch, isProcessAlive: _ => true,
                holderLease: Lease, generationIdentity: Generation,
                conductEventLogWriter: new ConductEventLogWriter(_parallelLog, utcNow: () => DateTimeOffset.UnixEpoch),
                launchOwnedProcess: launch =>
                {
                    Claim(AcceptanceHolderBinding.For(launch.Attempt));
                    return new(Holder.ProcessId, Holder.StartedAt, Holder.ImagePath);
                });
            Grouped = new(Path.Combine(_root, "grouped"),
                utcNow: () => DateTimeOffset.UnixEpoch.AddMinutes(_groupedCount), isProcessAlive: _ => true,
                holderLease: Lease, generationIdentity: Generation, eventSink: _events.Add,
                launch: attempt =>
                {
                    Claim(AcceptanceHolderBinding.For(attempt));
                    return new(Holder.ProcessId, Holder.StartedAt, Holder.ImagePath);
                });
        }

        private void Claim(AcceptanceHolderBinding binding)
        {
            if (!ClaimAtLaunch) return;
            using var claim = Lease.Claim(binding, Holder);
        }

        internal ConductorParallelAcceptanceAttempt AddParallel()
        {
            var goal = new AgentOrchestratorKernel().CreateGoal("Holder fault");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, GoalIds.Count, [], "branch", "main");
            GoalIds.Add(goal.Id.Value);
            var decision = Parallel.Evaluate(candidate, ConductorAutonomyPolicy.Permissive,
                (_, _, _, _, _) => throw new InvalidOperationException("Verifier belongs to the owned child."));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, decision.Kind);
            Assert.NotNull(decision.Attempt);
            Assert.NotNull(decision.Attempt.HolderCandidateKey);
            return decision.Attempt;
        }

        internal ConductorGroupedGateAttempt AddGrouped()
        {
            _groupedCount++;
            var attempt = Grouped.Create("cohort", [], "main", "tree", "manifest", $"identity-{_groupedCount}",
                _root, ConductorAutonomyPolicy.Permissive);
            Grouped.Launch(attempt);
            return ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath);
        }

        internal void Mismatch(AcceptanceHolderBinding binding)
        {
            var record = Lease.Read(binding);
            var other = binding with { Candidate = "other-candidate" };
            Rewrite(binding, record with { Binding = other, JobName = other.JobName });
        }

        internal void Rewrite(AcceptanceHolderBinding binding, AcceptanceHolderLeaseRecord record) =>
            File.WriteAllText(binding.LeasePath, JsonSerializer.Serialize(record));

        internal string[] Holds()
        {
            var parallel = File.Exists(_parallelLog) ? File.ReadAllLines(_parallelLog).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("detail").GetString()!;
            }) : [];
            return _events.Concat(parallel).Where(line => line.Contains("ACCEPTANCE_HOLDER_HOLD", StringComparison.Ordinal)).ToArray();
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
        private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    }
}
