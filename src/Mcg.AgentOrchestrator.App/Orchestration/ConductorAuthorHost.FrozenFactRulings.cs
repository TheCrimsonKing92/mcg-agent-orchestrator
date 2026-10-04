using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorAuthorHost
{
    private void SubmitRuling(AgentOrchestratorKernel kernel, Goal goal,
        ConductorAuthorItem item, FrozenFactRuling ruling, HashSet<GoalId> changed)
    {
        var rendered = ruling.Render();
        var reason = ConductorAuthorFrozenFactRulingCheck.Evaluate(item, ruling, _workingDirectory(goal));
        reason ??= item.ForkKind == AcceptanceCriterionFeasibility.ForkKind
            ? "acceptance-weakening"
            : ConductorAuthorOwnerClassCheck.Evaluate(item.Question, rendered, ruling.EvidenceReferences);
        if (reason is not null)
        {
            Escalate(kernel, goal, item, item.Question, rendered, reason, changed);
            _claims.Complete(item.Identity, "owner-question");
            return;
        }
        SubmitAnswer(item, rendered, ruling.EvidenceReferences, null, "frozen-fact-ruling");
    }
}
