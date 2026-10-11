using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Busy slots are admission pressure, before the gate has produced an outcome.
internal sealed class CohortStableSlotAcquisitionRounds
{
    internal const string DeferredVerdict = "deferred-slots-busy";
    internal static readonly TimeSpan InterRoundDelay = TimeSpan.FromSeconds(10);
    /// <summary>Three rounds * the inter-round delay + one stable-slot acquisition timeout (50 seconds).</summary>
    internal static readonly TimeSpan MaximumReservationHold = AcceptanceSlotPriorityReservation.MaximumHoldFor(
        3, InterRoundDelay, DotnetBuildEnvironmentManager.DefaultSlotBusyPollTimeout);
    internal static readonly ConductorGroupedGateOutcome DeferredGroupedOutcome = new(DeferredVerdict, "");
    private readonly int _roundCount;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly DotnetBuildStorageRoot? _storageRoot;
    private readonly TimeProvider _clock;
    private readonly Func<AcceptanceSlotPriorityReservation>? _reserve;

    internal CohortStableSlotAcquisitionRounds(int roundCount,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        DotnetBuildStorageRoot? storageRoot = null,
        TimeProvider? timeProvider = null,
        Func<AcceptanceSlotPriorityReservation>? reserve = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(roundCount, 1);
        _roundCount = roundCount;
        _delay = delay ?? Task.Delay;
        _storageRoot = storageRoot;
        _clock = timeProvider ?? TimeProvider.System;
        _reserve = reserve;
    }

    internal DotnetBuildEnvironmentLease? Acquire(string cohortId,
        Func<CancellationToken, DotnetBuildEnvironmentLease> acquireRound,
        CancellationToken cancellationToken = default)
    {
        var startedAt = _clock.GetUtcNow();
        var outcome = GateSlotWaitRecord.GaveUp;
        IReadOnlyList<string> busyLeaseIds = [];
        DotnetBuildEnvironmentLease? acquired = null;
        AcceptanceSlotPriorityReservation? reservation = null;
        string? errorType = null;
        var waitedAcrossRounds = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_storageRoot is not null)
            {
                reservation = _reserve?.Invoke() ?? AcceptanceSlotPriorityReservation.Take(
                    _storageRoot, _clock, MaximumReservationHold, _delay);
                busyLeaseIds = DotnetBuildEnvironmentManager.ReadStableSlotBusyLeaseIds(_storageRoot);
            }
            for (var round = 1; round <= _roundCount; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reservation?.ObserveExpiry();
                try
                {
                    acquired = acquireRound(cancellationToken);
                    outcome = GateSlotWaitRecord.Acquired;
                    return acquired;
                }
                catch (DotnetBuildSlotsBusyException busy)
                {
                    waitedAcrossRounds = true;
                    if (round == 1 && busyLeaseIds.Count == 0)
                        busyLeaseIds = busy.SlotsBusy.BusySlots.Select(slot => slot.LeaseId)
                            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
                    if (round == _roundCount)
                    {
                        Console.WriteLine($"COHORT_GATE_SLOTS_DEFERRED cohort={cohortId} rounds={round} " +
                            $"busySlots={DotnetBuildEnvironmentManager.FormatBusySlots(busy.SlotsBusy.BusySlots)}");
                        return null;
                    }
                }
                if (reservation is null)
                    _delay(InterRoundDelay, cancellationToken).GetAwaiter().GetResult();
                else
                    reservation.Delay(InterRoundDelay, cancellationToken);
            }
            throw new InvalidOperationException("Cohort slot rounds ended without acquisition or deferral.");
        }
        catch (OperationCanceledException)
        {
            outcome = GateSlotWaitRecord.Cancelled;
            throw;
        }
        catch (Exception error)
        {
            errorType = error.GetType().Name;
            throw;
        }
        finally
        {
            reservation?.ObserveExpiry();
            reservation?.Dispose();
            if (outcome == GateSlotWaitRecord.GaveUp && reservation?.IsExpired == true)
                outcome = GateSlotWaitRecord.Expired;
            var waitDuration = acquired is not null && !waitedAcrossRounds
                ? acquired.SlotWaitDuration ?? TimeSpan.Zero
                : _clock.GetUtcNow() - startedAt;
            new GateSlotWaitRecord(cohortId, busyLeaseIds, waitDuration, outcome,
                reservation?.ReservedSlotCount ?? 0, acquired?.Environment.BuildPermitIndex, errorType).Emit(_storageRoot);
        }
    }

    internal static string HolderLabel(IReadOnlyList<AcceptanceCohortMemberBinding> bindings) =>
        $"cohort-gate:goal-{string.Join('+', bindings.Select(member => member.GoalId.Value[..8]))}";

    internal static ConductorAcceptanceCohortRunResult DeferredRun(
        IReadOnlyList<Goal> goals, ConductorAutonomyPolicy policy, string identity) => new(
        null,
        goals.ToDictionary(goal => goal.Id.Value, goal =>
        {
            var decision = VerifiedAdmissionPolicy.Evaluate(new VerifiedAdmissionFacts(goal.Status)
            {
                GateStartDeferral = "build-slots-busy",
                BuildSlotsBusyDetail = $"Cohort gate deferred while stable build slots are busy. cohort={identity}"
            });
            return new ConductorAdvanceResult(goal.Id.Value, goal.Id.Value[..8], policy.Name,
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, decision.Reason, identity)
                    { Decision = decision.ToRecord() });
        }, StringComparer.Ordinal),
        $"outcome={DeferredVerdict} cohort={identity}");
}
