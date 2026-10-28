using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal FailedGoalFindingObservation BuildReceiptClosureOrDeliveryRetry(
        Goal goal,
        TaskSpec task,
        string candidateSha,
        IReadOnlyList<ReviewFinding> findings,
        IReadOnlyList<string> receiptIds,
        string summary)
    {
        FindingReceiptClosureDiagnosis? diagnosis = null;
        if (task.RequiredRole == AgentRole.Tester &&
            (_cohortKernel ?? _conductorTickKernel) is { } kernel)
        {
            if (FindingReceiptClosureCoordinator.TryClose(kernel, goal, task.Id, candidateSha, _utcNow,
                    out diagnosis, out var observation))
                return observation;
        }

        var receiptTask = (_cohortKernel ?? _conductorTickKernel)?.GetTask(goal.Id, task.Id) ?? task;
        var openFindings = ReviewFindings.GetOpenBlockingFindings(
            receiptTask.LastVerification?.MergedReviewFindings ?? findings,
            goal.EffectiveAcceptanceCriteriaCorrections);
        if (task.RequiredRole == AgentRole.Tester && openFindings.Count > 0 &&
            FindingEvidenceReceiptSelector.SuppliedReceiptsPassAtCandidate(receiptTask, candidateSha, receiptIds))
        {
            return PassingFindingEvidenceRouter.BuildDecision(
                goal, task, FindUpstreamDeveloperTask(goal, task), candidateSha, receiptIds, openFindings,
                diagnosis ?? FindingReceiptClosureDiagnosis.Evaluate(goal, receiptTask, candidateSha),
                BuildFailedGoalAttemptIdentity);
        }

        return BuildQualifiedFindingEvidenceDeliveryRetry(goal, task, candidateSha, findings, receiptIds, summary);
    }
}
