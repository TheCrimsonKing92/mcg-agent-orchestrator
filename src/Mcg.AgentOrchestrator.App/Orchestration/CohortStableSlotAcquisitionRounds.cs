using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Busy slots are admission pressure, before the gate has produced an outcome.
internal sealed class CohortStableSlotAcquisitionRounds
{
    internal const string DeferredVerdict = "deferred-slots-busy";
    internal static readonly TimeSpan InterRoundDelay = TimeSpan.FromSeconds(10);
    internal static readonly ConductorGroupedGateOutcome DeferredGroupedOutcome = new(DeferredVerdict, "");
    private readonly int _roundCount;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal CohortStableSlotAcquisitionRounds(int roundCount,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(roundCount, 1);
        _roundCount = roundCount;
        _delay = delay ?? Task.Delay;
    }

    internal DotnetBuildEnvironmentLease? Acquire(string cohortId,
        Func<CancellationToken, DotnetBuildEnvironmentLease> acquireRound,
        CancellationToken cancellationToken = default)
    {
        for (var round = 1; round <= _roundCount; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return acquireRound(cancellationToken); }
            catch (DotnetBuildSlotsBusyException busy)
            {
                if (round == _roundCount)
                {
                    Console.WriteLine($"COHORT_GATE_SLOTS_DEFERRED cohort={cohortId} rounds={round} " +
                        $"busySlots={DotnetBuildEnvironmentManager.FormatBusySlots(busy.SlotsBusy.BusySlots)}");
                    return null;
                }
            }
            _delay(InterRoundDelay, cancellationToken).GetAwaiter().GetResult();
        }
        throw new InvalidOperationException("Cohort slot rounds ended without acquisition or deferral.");
    }

    internal static string HolderLabel(IReadOnlyList<AcceptanceCohortMemberBinding> bindings) =>
        $"cohort-gate:goal-{string.Join('+', bindings.Select(member => member.GoalId.Value[..8]))}";

    internal static ConductorAcceptanceCohortRunResult DeferredRun(
        IReadOnlyList<Goal> goals, ConductorAutonomyPolicy policy, string identity) => new(
        null,
        goals.ToDictionary(goal => goal.Id.Value, goal => new ConductorAdvanceResult(
            goal.Id.Value, goal.Id.Value[..8], policy.Name,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified,
                "Cohort gate deferred while stable build slots are busy.", identity)), StringComparer.Ordinal),
        $"outcome={DeferredVerdict} cohort={identity}");
}
