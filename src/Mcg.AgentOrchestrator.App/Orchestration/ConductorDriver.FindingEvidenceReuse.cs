using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private VerifyingFindingTrigger? BuildTesterDeveloperOwnedFindingTrigger(Goal goal, TaskSpec task)
    {
        if (task is not { Status: WorkTaskStatus.Failed, RequiredRole: AgentRole.Tester } ||
            task.LastVerification?.MergedReviewFindings is not { } mergedFindings)
        {
            return null;
        }

        var developerOwnedFindings = ReviewFindingRouting.Project(
                ReviewFindings.GetOpenBlockingFindings(
                    mergedFindings,
                    goal.EffectiveAcceptanceCriteriaCorrections))
            .Where(projection =>
                projection.TargetRole == AgentRole.Developer &&
                projection.Finding.Category is
                    FindingCategory.Correctness or
                    FindingCategory.TestCoverage or
                    FindingCategory.SpecCompliance or
                    FindingCategory.CodeQuality)
            .Select(projection => projection.Finding)
            .ToArray();
        if (developerOwnedFindings.Length == 0)
        {
            return null;
        }

        var upstreamDeveloper = goal.Tasks
            .TakeWhile(candidate => candidate.Id != task.Id)
            .LastOrDefault(candidate =>
                candidate.RequiredRole == AgentRole.Developer &&
                HasCommittedOutput(candidate));
        var finding = string.Join(
            "; ",
            developerOwnedFindings.Select(item =>
                $"stable_id={item.StableId} category={item.Category} description={item.Description}"));
        return new VerifyingFindingTrigger(
            task,
            finding,
            [],
            upstreamDeveloper,
            RequiresCommittedTarget: true);
    }

    private static bool HasCurrentFindingEvidenceReceipt(
        TaskSpec requestingTask,
        ReviewFinding finding,
        FindingEvidenceRequest request,
        string candidateSha,
        string findingRoundFingerprint)
    {
        var receiptId = finding.EvidenceOutcome?.ReceiptId;
        if (string.IsNullOrWhiteSpace(receiptId) || candidateSha == "unavailable")
        {
            return false;
        }

        var identity = BuildFindingEvidenceIdentity(request);
        return requestingTask.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .Any(receipt =>
                string.Equals(receipt.ReceiptId, receiptId, StringComparison.Ordinal) &&
                string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    receipt.FindingRoundFingerprint,
                    findingRoundFingerprint,
                    StringComparison.Ordinal) &&
                (string.Equals(BuildFindingEvidenceIdentity(receipt.Request), identity, StringComparison.Ordinal) ||
                    (receipt.RequestDispositions ?? []).Any(disposition =>
                        string.Equals(disposition.FindingStableId, finding.StableId, StringComparison.Ordinal) &&
                        string.Equals(disposition.RequestIdentity, identity, StringComparison.Ordinal))));
    }

    private void ReattachReusableGreenFindingEvidence(
        Goal goal,
        TaskSpec requestingTask,
        ReviewFinding finding,
        FindingEvidenceOutcome outcome,
        FindingEvidenceReceipt receipt)
    {
        _recordFindingEvidenceOutcome(
            goal.Id, requestingTask.Id, finding.StableId, outcome, receipt);
        _recordFindingEvidenceRequest(
            goal.Id,
            requestingTask.Id,
            $"finding-evidence disposition=reused-green; role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; " +
            $"finding_id={finding.StableId}; candidate_sha={receipt.CandidateSha}; receipt_id={receipt.ReceiptId}");
    }

    private static bool TryGetReusableGreenFindingEvidenceReceipt(
        TaskSpec requestingTask,
        ReviewFinding finding,
        FindingEvidenceRequest request,
        string candidateSha,
        out FindingEvidenceReceipt? reusableReceipt)
    {
        reusableReceipt = null;
        var outcome = finding.EvidenceOutcome;
        var receiptId = outcome?.ReceiptId;
        if (outcome is not { Honoured: true, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence } ||
            string.IsNullOrWhiteSpace(receiptId) ||
            candidateSha == "unavailable")
        {
            return false;
        }

        var identity = BuildFindingEvidenceIdentity(request);
        reusableReceipt = requestingTask.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .LastOrDefault(receipt =>
                string.Equals(receipt.ReceiptId, receiptId, StringComparison.Ordinal) &&
                string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                receipt is { Accepted: true, Passed: true } &&
                (receipt.Arms ?? []).Any(arm =>
                    arm is
                    {
                        Arm: FindingEvidenceArm.Candidate,
                        Disposition: FindingEvidenceArmDisposition.Green,
                        Accepted: true,
                        Passed: true
                    } &&
                    string.Equals(arm.Sha, candidateSha, StringComparison.OrdinalIgnoreCase)) &&
                (receipt.RequestDispositions ?? []).Any(disposition =>
                    string.Equals(disposition.FindingStableId, finding.StableId, StringComparison.Ordinal) &&
                    string.Equals(disposition.RequestIdentity, identity, StringComparison.Ordinal) &&
                    disposition.Disposition.StartsWith("executed-", StringComparison.Ordinal)));
        return reusableReceipt is not null;
    }

    private static bool IsPermanentFindingEvidenceRefusal(FindingEvidenceOutcome outcome)
    {
        if (!string.IsNullOrWhiteSpace(outcome.ReceiptId))
        {
            return false;
        }

        return outcome.Reason switch
        {
            FindingEvidenceNotHonouredReason.Unknown => true, // Permanent default: no typed retry signal exists.
            FindingEvidenceNotHonouredReason.UnsupportedProject => true, // Permanent until the request changes.
            FindingEvidenceNotHonouredReason.UnparseableSelection => true, // Permanent until the request changes.
            FindingEvidenceNotHonouredReason.CandidateShaMissing => false, // Transient: a later candidate may have a sha.
            FindingEvidenceNotHonouredReason.ExecutorUnavailable => false, // Transient: the executor may recover.
            FindingEvidenceNotHonouredReason.SelectionApparatusFailure => false, // Transient: source discovery may recover.
            FindingEvidenceNotHonouredReason.RunFailed => false, // Transient: the focused run may succeed later.
            FindingEvidenceNotHonouredReason.SupersededByActionableRed => true, // Permanent for this unchanged request.
            FindingEvidenceNotHonouredReason.PerRoundCap => true, // Retired persisted disposition; preserve suppression.
            null => true, // Persisted outcomes without a reason are unclassified and fail safe.
            _ => true // Future or unrecognized reasons fail safe against verbatim replay.
        };
    }
}
