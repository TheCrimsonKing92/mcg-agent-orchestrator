using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private ConductorAdvanceResult? TryDisposeWithinAttemptRerunApparatus(
        Goal goal,
        string goalPrefix,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance)
    {
        var disposition = _apparatusRedGate?.ClassifyWithinAttemptRerun(goal, acceptance);
        if (disposition is ApparatusRedDisposition.BoundExhausted bound)
        {
            var partitions = acceptance.RequiredUnmetCriteria
                .Where(check => check.WithinAttemptRerun is not null)
                .Select(check => $"{check.Name} ({check.WithinAttemptRerun!.FailedPredicate})");
            var boundReason =
                $"{WithinAttemptRerunApparatusClassifier.BoundExhaustedToken}: " +
                $"apparatus regate budget {bound.RegateCount}/{bound.RegateCap} exhausted for " +
                $"{string.Join(", ", partitions)}; each in-attempt rerun passed. " +
                "This is an infrastructure failure, not a criteria failure. " +
                "Repair the apparatus or confirm acceptance-retry; no worker was reopened.";
            var result = Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, boundReason);
            return result with
            {
                Outcome = ((ConductorAdvanceOutcome.Escalated)result.Outcome) with
                {
                    Decision = AcceptanceApparatusDispositionPolicy.Evaluate(
                        new AcceptanceApparatusDispositionFacts(goal.Id.Value, AcceptanceApparatusDisposition.WithinAttemptRerunBoundExhausted)
                        {
                            BranchHeadSha = acceptance.BranchHeadSha,
                            MainHeadSha = acceptance.MainHeadSha,
                            EvidenceKind = bound.EvidenceKind,
                            RegateCount = bound.RegateCount,
                            RegateCap = bound.RegateCap,
                            TestIdentities = AcceptanceApparatusDispositionFacts.CanonicalTestIdentities(bound.TestIdentities),
                            Reason = boundReason
                        }).ToRecord()
                }
            };
        }

        if (disposition is not ApparatusRedDisposition.Regate regate)
        {
            return null;
        }

        RestoreVerifiedAfterAcceptanceClassification(
            goal,
            $"Acceptance apparatus ({regate.EvidenceKind}); restored Verified for regate " +
            $"{regate.RegateOrdinal}/{regate.RegateCap}.");
        _apparatusRedGate!.RecordRegate(goal, regate);
        var observedHeads = _resolveAcceptanceHeads(goal);
        var branchHeadSha = acceptance.BranchHeadSha ?? observedHeads.BranchHeadSha;
        var mainHeadSha = acceptance.MainHeadSha ?? observedHeads.MainHeadSha;
        WriteApparatusRedRegateJournal(
            goal, branchHeadSha, mainHeadSha, regate,
            acceptance.RequiredUnmetCriteria.Select(check => check.Name).ToArray());
        var reason =
            $"Acceptance apparatus ({regate.EvidenceKind}) for " +
            $"{string.Join(", ", regate.TestIdentities)}: the in-attempt rerun passed. " +
            $"Regating on the next conduct tick ({regate.RegateOrdinal}/{regate.RegateCap}); " +
            "no worker was reopened.";
        return MakeResult(
            goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Held(
                GoalLifecycleState.Verified,
                reason,
                StableIdentity:
                    $"acceptance-apparatus-rerun-pass:{branchHeadSha ?? "unknown"}:" +
                    $"{mainHeadSha ?? "unknown"}:{regate.RegateOrdinal}")
            {
                Decision = AcceptanceApparatusDispositionPolicy.Evaluate(
                    new AcceptanceApparatusDispositionFacts(goal.Id.Value, AcceptanceApparatusDisposition.WithinAttemptRerunRegateHold)
                    {
                        BranchHeadSha = branchHeadSha,
                        MainHeadSha = mainHeadSha,
                        EvidenceKind = regate.EvidenceKind,
                        RegateOrdinal = regate.RegateOrdinal,
                        RegateCap = regate.RegateCap,
                        TestIdentities = AcceptanceApparatusDispositionFacts.CanonicalTestIdentities(regate.TestIdentities),
                        Reason = reason
                    }).ToRecord()
            });
    }
}
