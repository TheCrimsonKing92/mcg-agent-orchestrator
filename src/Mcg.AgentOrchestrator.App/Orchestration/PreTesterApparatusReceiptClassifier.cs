using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreTesterApparatusReceiptClassifier
{
    internal static bool IsApparatusUnusable(FocusedEvidenceRunResult? evidence)
    {
        if (evidence is null) return false;
        if (TimedOutSelectionRerunMapping.AllRoundChecks(evidence).Any(check =>
                !check.Passed && AcceptanceFailureClassifications.IsEnvironmentalApparatus(check.FailureClassification)))
            return true;
        var candidate = (evidence.Arms ?? []).Where(arm => arm.Arm == FindingEvidenceArm.Candidate)
            .Select(ConductorDriver.CreateFindingEvidenceArmReceipt).FirstOrDefault();
        return candidate is { Disposition: not FindingEvidenceArmDisposition.Green,
            ExecutedTestCount: null or 0, FailingTestIdentities.Count: 0 };
    }
}
