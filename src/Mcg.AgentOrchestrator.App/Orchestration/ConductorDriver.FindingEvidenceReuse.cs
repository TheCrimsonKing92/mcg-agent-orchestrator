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
                HasEverCommittedOutput(candidate));
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
        FindingEvidenceRequest request,
        string candidateSha,
        out FindingEvidenceOutcome? reusableOutcome,
        out FindingEvidenceReceipt? reusableReceipt)
    {
        reusableOutcome = null;
        reusableReceipt = null;
        if (candidateSha == "unavailable")
        {
            return false;
        }

        var identity = BuildFindingEvidenceIdentity(request);
        var validOutcomesByReceiptId = requestingTask.VerificationHistory
            .SelectMany(verification => verification.MergedReviewFindings ?? [])
            .Select(finding => finding.EvidenceOutcome)
            .OfType<FindingEvidenceOutcome>()
            .Where(outcome =>
                outcome is { Honoured: true, ReceiptId.Length: > 0, ResultReason: FindingEvidenceOutcomeReason.ValidEvidence })
            .GroupBy(outcome => outcome.ReceiptId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var currentCandidateReceipts = requestingTask.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .Where(receipt => string.Equals(
                receipt.CandidateSha,
                candidateSha,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (currentCandidateReceipts.Length == 0)
        {
            return false;
        }

        // The newest receipt covering a selection is authoritative. Do not let an earlier green
        // result hide a later candidate-red, incomplete, or rejected receipt at the same SHA.
        var latestReceipts = request.Selections
            .Select(selection => currentCandidateReceipts.LastOrDefault(receipt =>
                (receipt.Request.Selections ?? []).Contains(selection)))
            .ToArray();
        if (latestReceipts.Any(item => item is null) ||
            latestReceipts.Any(item => !IsReusableGreenReceipt(
                item!,
                candidateSha,
                validOutcomesByReceiptId)))
        {
            return false;
        }

        var greenReceipts = latestReceipts
            .Select(item => item!)
            .DistinctBy(receipt => receipt.ReceiptId, StringComparer.Ordinal)
            .ToArray();
        reusableReceipt = greenReceipts.LastOrDefault(receipt =>
            HasReusableRequestIdentity(receipt, identity));
        if (reusableReceipt is not null)
        {
            reusableOutcome = validOutcomesByReceiptId[reusableReceipt.ReceiptId];
            return true;
        }

        var constituentReceipts = greenReceipts;
        var receiptId = CreateConstituentReuseReceiptId(candidateSha, identity, constituentReceipts);
        var armsByIdentity = constituentReceipts
            .SelectMany(receipt => receipt.Arms ?? [])
            .GroupBy(arm => new { arm.Arm, arm.Sha });
        if (armsByIdentity.Any(group => group
                .Select(arm => new { arm.Disposition, arm.Accepted, arm.Passed })
                .Distinct()
                .Skip(1)
                .Any()))
        {
            return false;
        }

        reusableReceipt = new FindingEvidenceReceipt(
            receiptId,
            candidateSha,
            request,
            Accepted: true,
            Passed: true,
            Summary: "Reused green focused evidence from constituent receipts: " +
                string.Join(", ", constituentReceipts.Select(receipt => receipt.ReceiptId)),
            Arms: armsByIdentity
                .Select(group => group.Last())
                .ToArray(),
            RequestDispositions: []);
        reusableOutcome = new FindingEvidenceOutcome(
            Honoured: true,
            ReceiptId: receiptId,
            ResultReason: FindingEvidenceOutcomeReason.ValidEvidence);
        return true;
    }

    private static bool IsReusableGreenReceipt(
        FindingEvidenceReceipt receipt,
        string candidateSha,
        IReadOnlyDictionary<string, FindingEvidenceOutcome> validOutcomesByReceiptId) =>
        validOutcomesByReceiptId.ContainsKey(receipt.ReceiptId) &&
        receipt is { Accepted: true, Passed: true } &&
        (receipt.Arms ?? []).Any(arm =>
            arm is
            {
                Arm: FindingEvidenceArm.Candidate,
                Disposition: FindingEvidenceArmDisposition.Green,
                Accepted: true,
                Passed: true
            } &&
            string.Equals(arm.Sha, candidateSha, StringComparison.OrdinalIgnoreCase));

    private static bool HasReusableRequestIdentity(FindingEvidenceReceipt receipt, string identity) =>
        string.Equals(BuildFindingEvidenceIdentity(receipt.Request), identity, StringComparison.Ordinal) ||
        (receipt.RequestDispositions ?? []).Any(disposition =>
            string.Equals(disposition.RequestIdentity, identity, StringComparison.Ordinal) &&
            disposition.Disposition.StartsWith("executed-", StringComparison.Ordinal));

    private static bool HasEverCommittedOutput(TaskSpec task) =>
        task.VerificationHistory.Any(verification => verification.HasCommittedChanges) ||
        task.DispatchHistory.Any(dispatch =>
            !string.IsNullOrWhiteSpace(dispatch.ResultCommit) &&
            (string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
             !string.Equals(dispatch.BaseCommit, dispatch.ResultCommit, StringComparison.OrdinalIgnoreCase)));

    private static string CreateConstituentReuseReceiptId(
        string candidateSha,
        string requestIdentity,
        IReadOnlyList<FindingEvidenceReceipt> constituents)
    {
        var payload = string.Join(
            "\u001f",
            candidateSha,
            requestIdentity,
            string.Join("\u001e", constituents
                .Select(receipt => receipt.ReceiptId)
                .OrderBy(receiptId => receiptId, StringComparer.Ordinal)));
        return "finding-evidence-reuse-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant()[..24];
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
