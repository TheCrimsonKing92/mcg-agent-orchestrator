using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class LandingExecutor
{
    private static void RecordSuccessfulPromotion(OrchestratorWorkspace workspace,
        IReadOnlyList<(GoalId GoalId, string CandidateRevision)> members,
        string priorMainRevision, string promotedMainRevision, string provenance, IClock? clock)
    {
        // An ancestor branch can be reconciled without a new main promotion.
        // Do not fabricate a landing instant or resample a recovery clock.
        if (string.Equals(priorMainRevision, promotedMainRevision, StringComparison.OrdinalIgnoreCase))
            return;
        var at = (clock ?? new SystemClock()).UtcNow;
        var facts = members.Select(member => new GoalPromotionFact(member.GoalId.Value,
            member.CandidateRevision, priorMainRevision, promotedMainRevision, at, provenance)).ToArray();
        // No schema bootstrap here: the conductor owns numbered migrations. On
        // failure recovery anchors remain; recovery retains known facts and leaves
        // an unrecorded crash-gap time unknown rather than resampling its clock.
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).RecordPromotionFacts(facts);
    }
}
