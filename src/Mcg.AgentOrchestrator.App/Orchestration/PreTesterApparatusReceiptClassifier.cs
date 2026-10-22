using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreTesterApparatusReceiptClassifier
{
    internal static bool IsBuildPhaseOnly(FocusedEvidenceRunResult? evidence)
    {
        if (evidence is null) return false;
        var checks = TimedOutSelectionRerunMapping.AllRoundChecks(evidence);
        return checks.Any(check => !check.Passed) && checks.All(check =>
            (check.Passed || check.FailureClassification == AcceptanceFailureClassifications.BuildPhaseTimeout) &&
            check.ExecutedTestCount is not > 0 && check.FailingTestIdentities is not { Count: > 0 });
    }

    internal static bool IsTransientAttemptOutcome(ConductorParallelAcceptanceAttemptOutcome? outcome) => outcome is
        ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot or
        ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock or
        ConductorParallelAcceptanceAttemptOutcome.Cancelled or
        ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred or
        ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable or
        ConductorParallelAcceptanceAttemptOutcome.StaleCandidate;

    // Receipt validity and failure attribution determine whether a completed run is unusable.
    internal static (string Outcome, FindingEvidenceArmReceipt? Candidate, IReadOnlyList<string> FailingTests)
        ClassifyOutcome(FocusedEvidenceRunResult? evidence, bool completed,
        string? candidateSha, IReadOnlyList<FindingEvidenceSelection> selections,
        Func<IReadOnlyList<string>, bool> everyFailureInsideCandidateChanges)
    {
        var candidate = (evidence?.Arms ?? []).Select(ConductorDriver.CreateFindingEvidenceArmReceipt)
            .FirstOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate &&
                                   string.Equals(arm.Sha, candidateSha, StringComparison.OrdinalIgnoreCase));
        var baseline = (evidence?.Arms ?? []).Select(ConductorDriver.CreateFindingEvidenceArmReceipt)
            .FirstOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
        var failingTests = candidate?.FailingTestIdentities ?? [];
        var candidateRed = completed && evidence!.Accepted &&
                           candidate is { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red };
        var unlistedFailures = failingTests.Where(test => !(PreTesterAlwaysRunGuardTestClasses.IsListed(test) || PreTesterTestSourceGuardClasses.IsCovered(test))).ToArray();
        var actionableRed = candidateRed && failingTests.Count > 0 &&
                            (baseline is null
                                ? unlistedFailures.Length == 0 || everyFailureInsideCandidateChanges(unlistedFailures)
                                : baseline.Disposition == FindingEvidenceArmDisposition.Green);
        var green = completed && evidence!.IsValidEvidence &&
                    candidate is { Accepted: true, Passed: true, Disposition: FindingEvidenceArmDisposition.Green,
                        ExecutedTestCount: > 0 } &&
                    AcceptanceCohortGateEvidence.HasContentBoundGreenTrxEvidence(
                        candidate.TestResultPaths, candidate.ReceiptArtifacts,
                        candidate.ExecutedTestCount!.Value,
                        selections.Select(selection => selection.TestClass).ToHashSet(StringComparer.OrdinalIgnoreCase));
        return (green ? "green" : actionableRed ? "actionable-red" : candidateRed ? "red" : "unusable",
            candidate, failingTests);
    }

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
