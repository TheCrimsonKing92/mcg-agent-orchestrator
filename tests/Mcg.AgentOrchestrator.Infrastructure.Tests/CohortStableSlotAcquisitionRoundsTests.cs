using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

public sealed class CohortStableSlotAcquisitionRoundsTests : DotnetBuildEnvironmentManagerRootedTestBase
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StartsOnFirstAcquiredRound(int successfulRound)
    {
        using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
            TimeSpan.Zero, storageRoot: StorageRoot);
        var events = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var rounds = new CohortStableSlotAcquisitionRounds(3, (duration, token) =>
        {
            Assert.Equal(CohortStableSlotAcquisitionRounds.InterRoundDelay, duration);
            Assert.Equal(cancellation.Token, token);
            events.Add("delay");
            return Task.CompletedTask;
        });
        var acquisitions = 0;

        var acquired = rounds.Acquire("cohort", token =>
        {
            Assert.Equal(cancellation.Token, token);
            events.Add("acquire");
            if (++acquisitions < successfulRound) throw Busy();
            return lease;
        }, cancellation.Token);

        Assert.Same(lease, acquired);
        Assert.Equal(successfulRound, acquisitions);
        Assert.Equal(Enumerable.Range(1, successfulRound).SelectMany(round =>
            round == successfulRound ? new[] { "acquire" } : new[] { "acquire", "delay" }), events);
    }

    [Fact]
    public void AllBusyRoundsDeferAndNameFinalHolders()
    {
        var acquisitions = 0;
        var delays = 0;
        var rounds = new CohortStableSlotAcquisitionRounds(3, (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });
        DotnetBuildEnvironmentLease? lease = null;
        var output = AsyncLocalConsoleRouter.Capture(() => lease = rounds.Acquire("same-cohort", _ =>
        {
            acquisitions++;
            throw new DotnetBuildSlotsBusyException(new("first-available-stable-slot",
                [new(0, 123) { HolderLabel = $"acceptance-attempt:round-{acquisitions}" }]));
        }));

        Assert.Null(lease);
        Assert.Equal(3, acquisitions);
        Assert.Equal(2, delays);
        Assert.Contains("COHORT_GATE_SLOTS_DEFERRED cohort=same-cohort rounds=3", output);
        Assert.Contains("slot-0:pid-123:holder-acceptance-attempt:round-3", output);
    }

    [Fact]
    public void CancellationDuringDelayStopsFurtherAcquisition()
    {
        var acquisitions = 0;
        using var cancellation = new CancellationTokenSource();
        var rounds = new CohortStableSlotAcquisitionRounds(3, (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(token);
        });

        Assert.ThrowsAny<OperationCanceledException>(() => rounds.Acquire("cohort", _ =>
        {
            acquisitions++;
            throw Busy();
        }, cancellation.Token));
        Assert.Equal(1, acquisitions);
    }

    [Fact]
    public void NonBusyFailurePropagatesWithoutDelay()
    {
        var failure = new IOException("gate storage unavailable");
        var rounds = new CohortStableSlotAcquisitionRounds(3, (_, _) =>
            throw new InvalidOperationException("A non-busy failure must not retry."));
        Assert.Same(failure, Assert.Throws<IOException>(() => rounds.Acquire("cohort", _ => throw failure)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CohortStableSlotAcquisitionRounds(0));
    }

    [Fact]
    public void DeferredRunRecordsBuildSlotsBusyDecisionWithoutGateReceipt()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Cohort member", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);

        var run = CohortStableSlotAcquisitionRounds.DeferredRun([goal], ConductorAutonomyPolicy.Permissive, "same-cohort");

        Assert.Null(run.Receipt);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(Assert.Single(run.MemberResults).Value.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal("same-cohort", held.StableIdentity);
        Assert.NotNull(held.Decision);
        Assert.Equal(VerifiedAdmissionPolicy.StageName, held.Decision.Stage);
        Assert.Equal(nameof(VerifiedAdmissionAction.Hold), held.Decision.Action);
        Assert.Equal("gate-start-build-slots-busy", held.Decision.DiscriminatingEvidence);
        var facts = VerifiedAdmissionFacts.FromRecordedFacts(held.Decision.Facts);
        Assert.Equal(goal.Status, facts.GoalStatus);
        Assert.Equal("build-slots-busy", facts.GateStartDeferral);
        Assert.Contains("cohort=same-cohort", facts.BuildSlotsBusyDetail);
        Assert.Equal(held.Reason, VerifiedAdmissionPolicy.Evaluate(facts).Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OneReservationStaysHeldAcrossAllRoundsAndDelays(int successfulRound)
    {
        var clock = new RecordingTimeProvider();
        var events = new List<string>();
        AcceptanceSlotPriorityReservation? reservation = null;
        var takes = 0;
        Task Delay(TimeSpan duration, CancellationToken token)
        {
            Assert.Equal(CohortStableSlotAcquisitionRounds.InterRoundDelay, duration);
            AssertReservationHeld(reservation!);
            events.Add("delay");
            clock.Advance(duration);
            return Task.CompletedTask;
        }
        var rounds = new CohortStableSlotAcquisitionRounds(3, Delay, StorageRoot, clock, () =>
        {
            takes++;
            return reservation = AcceptanceSlotPriorityReservation.Take(StorageRoot, clock,
                CohortStableSlotAcquisitionRounds.MaximumReservationHold, Delay);
        });
        var acquisitions = 0;

        using var acquired = rounds.Acquire("cohort", _ =>
        {
            events.Add("acquire");
            AssertReservationHeld(reservation!);
            if (++acquisitions < successfulRound) throw Busy();
            return DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.Zero, storageRoot: StorageRoot);
        });

        Assert.NotNull(acquired);
        Assert.Equal(successfulRound, acquisitions);
        Assert.Equal(Enumerable.Range(1, successfulRound).SelectMany(round =>
            round == successfulRound ? new[] { "acquire" } : new[] { "acquire", "delay" }), events);
        Assert.Equal(1, takes);
        Assert.Equal(1, reservation!.ReleaseCount);
        Assert.Equal(0, reservation.HeldSlotCount);
        using var nextReservation = AcceptanceSlotPriorityReservation.Take(StorageRoot, clock,
            CohortStableSlotAcquisitionRounds.MaximumReservationHold, Delay);
        Assert.Equal(2, nextReservation.HeldSlotCount);
    }

    [Theory]
    [InlineData("gave-up")]
    [InlineData("cancelled")]
    [InlineData("exception")]
    public void EveryUnsuccessfulExitReleasesReservations(string exit)
    {
        var clock = new RecordingTimeProvider();
        AcceptanceSlotPriorityReservation? reservation = null;
        using var cancellation = new CancellationTokenSource();
        Task Delay(TimeSpan duration, CancellationToken token)
        {
            AssertReservationHeld(reservation!);
            if (exit == "cancelled")
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            }
            clock.Advance(duration);
            return Task.CompletedTask;
        }
        var rounds = new CohortStableSlotAcquisitionRounds(3, Delay, StorageRoot, clock, () =>
            reservation = AcceptanceSlotPriorityReservation.Take(StorageRoot, clock,
                CohortStableSlotAcquisitionRounds.MaximumReservationHold, Delay));
        var failure = new IOException("acquisition failed");
        DotnetBuildEnvironmentLease Acquire(CancellationToken token)
        {
            AssertReservationHeld(reservation!);
            if (exit == "exception") throw failure;
            throw Busy();
        }

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            if (exit == "cancelled")
                Assert.ThrowsAny<OperationCanceledException>(() => rounds.Acquire("cohort", Acquire, cancellation.Token));
            else if (exit == "exception")
                Assert.Same(failure, Assert.Throws<IOException>(() => rounds.Acquire("cohort", Acquire)));
            else
                Assert.Null(rounds.Acquire("cohort", Acquire));
        });

        Assert.Equal(1, reservation!.ReleaseCount);
        Assert.Equal(0, reservation.HeldSlotCount);
        Assert.Contains($"outcome={(exit == "exception" ? "gave-up" : exit)}", output);
        AssertWorkersAcquire();
    }

    [Fact]
    public void ReservationMaximumDoesNotExceedDocumentedRoundBound()
    {
        var documentedBound = TimeSpan.FromTicks(3 * CohortStableSlotAcquisitionRounds.InterRoundDelay.Ticks +
            DotnetBuildEnvironmentManager.DefaultSlotBusyPollTimeout.Ticks);
        Assert.True(CohortStableSlotAcquisitionRounds.MaximumReservationHold <= documentedBound);
        Assert.True(CohortStableSlotAcquisitionRounds.MaximumReservationHold > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiryReleasesSlotsAndRemainingRoundsContinue(bool acquireAfterExpiry)
    {
        var clock = new RecordingTimeProvider();
        AcceptanceSlotPriorityReservation? reservation = null;
        var acquisitions = 0;
        Task Delay(TimeSpan duration, CancellationToken token)
        {
            clock.Advance(duration);
            return Task.CompletedTask;
        }
        var rounds = new CohortStableSlotAcquisitionRounds(3, Delay, StorageRoot, clock, () =>
            reservation = AcceptanceSlotPriorityReservation.Take(StorageRoot, clock,
                CohortStableSlotAcquisitionRounds.MaximumReservationHold, Delay));
        DotnetBuildEnvironmentLease? acquired = null;
        var output = AsyncLocalConsoleRouter.Capture(() => acquired = rounds.Acquire("train", _ =>
        {
            acquisitions++;
            if (acquisitions == 1)
            {
                clock.Advance(CohortStableSlotAcquisitionRounds.MaximumReservationHold);
                throw Busy();
            }
            Assert.True(reservation!.IsExpired);
            Assert.Equal(0, reservation.HeldSlotCount);
            AssertWorkersAcquire();
            if (!acquireAfterExpiry) throw Busy();
            return DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.Zero, storageRoot: StorageRoot);
        }));
        using var acquiredLease = acquired;

        Assert.Equal(acquireAfterExpiry ? 2 : 3, acquisitions);
        Assert.Equal(1, reservation!.ReleaseCount);
        Assert.Contains($"outcome={(acquireAfterExpiry ? "acquired" : "expired")}", output);
    }

    [Theory]
    [InlineData(0, "cohort", "gave-up", 20_000)]
    [InlineData(1, "cohort", "acquired", 0)]
    [InlineData(2, "merge-train", "acquired", 10_000)]
    public void WaitEmitsOneConsoleLineAndJournalEvent(int successfulRound, string identity,
        string outcome, int waitMs)
    {
        var clock = new RecordingTimeProvider();
        var round = 0;
        var rounds = new CohortStableSlotAcquisitionRounds(3, (duration, _) =>
        {
            clock.Advance(duration);
            return Task.CompletedTask;
        }, StorageRoot, clock);
        DotnetBuildEnvironmentLease? acquired = null;
        var output = AsyncLocalConsoleRouter.Capture(() => acquired = rounds.Acquire(identity, _ =>
        {
            round++;
            if (round == successfulRound)
                return DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                    TimeSpan.Zero, storageRoot: StorageRoot);
            throw new DotnetBuildSlotsBusyException(new("first-available-stable-slot",
                [new(0, 123) { LeaseId = $"goal-first-{round}" }, new(1, 456) { LeaseId = $"goal-second-{round}" }]));
        }));
        using var acquiredLease = acquired;

        var line = Assert.Single(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(GateSlotWaitRecord.EventName + " ", StringComparison.Ordinal)));
        var journal = Assert.Single(File.ReadAllLines(Path.Combine(StorageRoot.RootPath, "build-slots", "lease.journal.jsonl"))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(entry => entry.GetProperty("event").GetString() == GateSlotWaitRecord.EventName));
        Assert.Contains($"gate={identity} outcome={outcome}", line);
        Assert.Contains($"waitMs={waitMs} reservedSlots=2", line);
        Assert.Equal(identity, journal.GetProperty("leaseId").GetString());
        Assert.Equal(outcome, journal.GetProperty("decision").GetString());
        Assert.Contains($"waitMs={waitMs} reservedSlots=2", journal.GetProperty("reason").GetString());
        var busy = successfulRound == 1 ? "none" : "goal-first-1|goal-second-1";
        Assert.Contains($"busyLeases={busy}", line);
        Assert.Contains($"busyLeases={busy}", journal.GetProperty("reason").GetString());
    }

    [Fact]
    public void WaitRecordsRealWorkerLeaseIdsBeforeTheReleasedSlotAcquires()
    {
        var clock = new RecordingTimeProvider();
        var worker0 = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 0)
            with { LeaseId = "goal-original0" };
        var worker1 = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, 1)
            with { LeaseId = "goal-original1" };
        using var holder0 = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(worker0, timeout: TimeSpan.Zero);
        using var holder1 = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(worker1, timeout: TimeSpan.Zero);
        var rounds = new CohortStableSlotAcquisitionRounds(3, (duration, _) =>
        {
            holder0.Dispose();
            clock.Advance(duration);
            return Task.CompletedTask;
        }, StorageRoot, clock);
        DotnetBuildEnvironmentLease? acquired = null;

        var output = AsyncLocalConsoleRouter.Capture(() => acquired = rounds.Acquire("real-cohort", _ =>
            RootedDotnetBuildEnvironmentManager.TryAcquireFirstAvailableStableSlotExecutionLock(
                StorageRoot, TimeSpan.Zero, timeProvider: clock, sleep: clock.Advance) switch
            {
                DotnetBuildLeaseAcquisition.Acquired success => success.Lease,
                DotnetBuildLeaseAcquisition.SlotsBusy busy => throw new DotnetBuildSlotsBusyException(busy),
                _ => throw new InvalidOperationException("Unexpected build lock outcome.")
            }));
        using var acquiredLease = acquired;

        Assert.NotNull(acquired);
        Assert.Equal(0, acquired.Environment.BuildPermitIndex);
        var line = Assert.Single(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(GateSlotWaitRecord.EventName + " ", StringComparison.Ordinal)));
        var journal = Assert.Single(File.ReadAllLines(Path.Combine(StorageRoot.RootPath, "build-slots", "lease.journal.jsonl"))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(entry => entry.GetProperty("event").GetString() == GateSlotWaitRecord.EventName));
        Assert.Contains("outcome=acquired", line);
        Assert.Contains("busyLeases=goal-original0|goal-original1", line);
        Assert.Contains("waitMs=10000", line);
        Assert.Equal("acquired", journal.GetProperty("decision").GetString());
        Assert.Contains("busyLeases=goal-original0|goal-original1", journal.GetProperty("reason").GetString());
        Assert.Contains("waitMs=10000", journal.GetProperty("reason").GetString());
    }

    [Fact]
    public void ImmediateAcquisitionRecordsZeroWaitDespiteSetupWork()
    {
        var clock = new RecordingTimeProvider();
        var rounds = new CohortStableSlotAcquisitionRounds(3, (_, _) =>
            throw new InvalidOperationException("Unexpected round delay."), StorageRoot, clock);
        DotnetBuildEnvironmentLease? acquired = null;
        var output = AsyncLocalConsoleRouter.Capture(() => acquired = rounds.Acquire("cohort", _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.Zero, storageRoot: StorageRoot);
        }));
        using var acquiredLease = acquired;

        Assert.NotNull(acquired);
        Assert.Contains("outcome=acquired busyLeases=none waitMs=0", output);
    }

    private void AssertReservationHeld(AcceptanceSlotPriorityReservation reservation)
    {
        Assert.Equal(2, reservation.HeldSlotCount);
        using var competitor = AcceptanceSlotPriorityReservation.Take(StorageRoot, new RecordingTimeProvider(),
            TimeSpan.FromSeconds(50), (_, _) => throw new InvalidOperationException("Unexpected reservation wait."));
        Assert.Equal(0, competitor.HeldSlotCount);
    }

    private void AssertWorkersAcquire()
    {
        for (var slot = 0; slot < DotnetBuildEnvironmentManager.StableSlotCount; slot++)
        {
            var environment = RootedDotnetBuildEnvironmentManager.CreateStableSlotAttempt(StorageRoot, slot);
            using var worker = DotnetBuildEnvironmentManager.AcquireLeaseExecutionPermit(environment,
                timeProvider: new RecordingTimeProvider(), sleep: _ => throw new InvalidOperationException("Unexpected wait."),
                timeout: TimeSpan.Zero);
            Assert.True(worker.IsExecutionLockHeld);
        }
    }

    private static DotnetBuildSlotsBusyException Busy() => new(new("first-available-stable-slot", []));
}
