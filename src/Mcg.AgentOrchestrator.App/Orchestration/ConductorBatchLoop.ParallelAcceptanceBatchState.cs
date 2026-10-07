using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    // Mutable locals for one batch, assigned by the phase that originally declared them.
    private sealed class ParallelAcceptanceBatchState
    {
        public int ConfiguredAcceptanceWidth;
        public Dictionary<string, ParallelLandingOutcome> Results = null!;
        public int DeferredByAdmission;
        public IReadOnlyList<Goal> OrderedEligible = null!;
        public bool CapacityStateUnavailable;
        public List<ConductorParallelAcceptanceAttempt> LiveAttempts = null!;
        public List<ConductorParallelAcceptanceCandidate> ActiveCandidates = null!;
        public HashSet<string> ActiveAttemptIds = null!;
        public HashSet<int> ActiveAttemptSlotIndexes = null!;
        public ConductorAcceptanceCapacitySnapshot ActiveCohortCapacity = null!;
        public LiveAcceptanceCensus AcceptanceCensus = null!;
        public HashSet<string> LiveAttemptGoalIds = null!;
        public Goal[] CohortEligible = null!;
        public ConductorSpeculativeAcceptanceCandidate[] ProductionCandidates = null!;
        public ParallelAcceptanceOldestWaiterObservation OldestWaiterObservation;
        public Goal? TransientRetryPriorityGoal;
        public Goal? InteractionOnlyPriorityGoal;
        public bool GroupedAdmissionOpen;
        public ConductorAcceptanceCohortFairnessPriority? ForcedCohortPriority;
    }
}
