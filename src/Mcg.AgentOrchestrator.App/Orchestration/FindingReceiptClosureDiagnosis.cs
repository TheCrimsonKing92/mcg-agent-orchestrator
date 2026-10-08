using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A closure attempt is all-or-nothing for one Tester task. This is its sole condition evaluator.
internal sealed class FindingReceiptClosureDiagnosis
{
    internal const string Closable = "closable";
    internal const string TaskNotTester = "task-not-tester";
    internal const string TaskCompleted = "task-completed";
    internal const string TesterProcessRunning = "tester-process-running";
    internal const string VerificationMissing = "verification-missing";
    internal const string WorkerResultMissing = "worker-result-missing";
    internal const string FindingContractViolation = "finding-contract-violation";
    internal const string VerificationNotSucceeded = "verification-not-succeeded";
    internal const string HumanInputQuestion = "human-input-question";
    internal const string BlockersUnreadable = "blockers-unreadable";
    internal const string WorkerBlockersReported = "worker-blockers-reported";
    internal const string FailingTestsReported = "failing-tests-reported";
    internal const string TestsInconclusive = "tests-inconclusive";
    internal const string ReviewedCommitMissing = "reviewed-commit-missing";
    internal const string CandidateShaInvalid = "candidate-sha-invalid";
    internal const string ReviewedCommitDiffers = "reviewed-commit-differs";
    internal const string NoOpenBlockingFindings = "no-open-blocking-findings";
    internal const string FindingCategoryNotTestEvidence = "finding-category-not-test-evidence";
    internal const string EvidenceOutcomeNotHonoured = "evidence-outcome-not-honoured";
    internal const string EvidenceOutcomeInvalid = "evidence-outcome-invalid";
    internal const string ReceiptIdMissing = "receipt-id-missing";
    internal const string ReceiptNotFound = "receipt-not-found";
    internal const string ReceiptNotPassing = "receipt-not-passing";
    internal const string ReceiptCandidateDiffers = "receipt-candidate-differs";
    internal const string CandidateArmMissing = "candidate-arm-missing";
    internal const string CandidateArmNotGreen = "candidate-arm-not-green";
    internal const string HumanInputRequestOpen = "human-input-request-open";

    private FindingReceiptClosureDiagnosis(string code, string sentence,
        IReadOnlyList<(ReviewFinding Finding, FindingEvidenceReceipt Receipt)>? findings = null)
    {
        Code = code;
        Sentence = sentence;
        ClosableFindings = findings ?? [];
    }

    internal string Code { get; }
    internal string Sentence { get; }
    internal bool IsClosable => Code == Closable;
    internal IReadOnlyList<(ReviewFinding Finding, FindingEvidenceReceipt Receipt)> ClosableFindings { get; }
    internal string Describe() => $"{Code}: {Sentence}";

    internal static FindingReceiptClosureDiagnosis Evaluate(
        Goal goal, TaskSpec task, string candidateSha, bool blockingHumanInputOpen = false)
    {
        static FindingReceiptClosureDiagnosis Fail(string code, string sentence) => new(code, sentence);
        if (task.RequiredRole != AgentRole.Tester)
            return Fail(TaskNotTester, $"Task {task.Id} has role {task.RequiredRole}.");
        if (task.Status == WorkTaskStatus.Completed)
            return Fail(TaskCompleted, $"Tester task {task.Id} is already Completed.");
        if (task.LastProcess is { IsRunning: true })
            return Fail(TesterProcessRunning, $"Tester task {task.Id} still has a running process.");
        if (task.LastVerification is not { } verification)
            return Fail(VerificationMissing, $"Tester task {task.Id} has no last verification.");
        if (!verification.WorkerResultPresent)
            return Fail(WorkerResultMissing, $"Tester task {task.Id} has no worker result in its last verification.");
        if (verification.ReviewFindingContractViolation is not null)
            return Fail(FindingContractViolation, $"Tester task {task.Id} has finding contract violation {verification.ReviewFindingContractViolation}.");
        if (!verification.Succeeded)
            return Fail(VerificationNotSucceeded, $"Tester task {task.Id} verification did not succeed (exit {verification.ExitCode}).");
        if (!string.IsNullOrWhiteSpace(verification.HumanInputQuestion))
            return Fail(HumanInputQuestion, $"Tester task {task.Id} reported human input question {verification.HumanInputQuestion}.");
        if (!WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockers))
            return Fail(BlockersUnreadable, $"Tester task {task.Id} has no readable blockers status.");
        if (blockers != WorkerResultBlockers.BlockersStatus.None)
            return Fail(WorkerBlockersReported, $"Tester task {task.Id} reported blockers status {blockers}.");
        if (WorkerResultBlockers.TryFindFailingTests(verification, out var failingTests))
            return Fail(FailingTestsReported, $"Tester task {task.Id} reported failing tests {failingTests}.");
        if (WorkerResultBlockers.TryGetTestsStatus(verification, out var tests) &&
            tests == WorkerResultBlockers.TestsStatus.Inconclusive)
            return Fail(TestsInconclusive, $"Tester task {task.Id} reported tests status {tests}.");
        if (!ConductorGitRevisionReader.IsValid(verification.ReviewedCommit))
            return Fail(ReviewedCommitMissing, $"Tester reviewed commit '{verification.ReviewedCommit}' is missing or invalid.");
        if (!ConductorGitRevisionReader.IsValid(candidateSha))
            return Fail(CandidateShaInvalid, $"Candidate '{candidateSha}' is invalid.");
        var reviewedSha = verification.ReviewedCommit!.Trim();
        if (!string.Equals(reviewedSha, candidateSha.Trim(), StringComparison.OrdinalIgnoreCase))
            return Fail(ReviewedCommitDiffers, $"Tester reviewed {reviewedSha} but the candidate is {candidateSha.Trim()}.");

        var open = ReviewFindings.GetOpenBlockingFindings(
            verification.MergedReviewFindings ?? [], goal.EffectiveAcceptanceCriteriaCorrections);
        if (open.Count == 0)
            return Fail(NoOpenBlockingFindings, $"Tester task {task.Id} has no open blocking findings.");
        var selected = new List<(ReviewFinding, FindingEvidenceReceipt)>();
        foreach (var finding in open)
        {
            if (finding.Category != FindingCategory.TestEvidence)
                return Fail(FindingCategoryNotTestEvidence, $"Finding {finding.StableId} has category {finding.Category}.");
            if (finding.EvidenceOutcome is not { Honoured: true } outcome)
                return Fail(EvidenceOutcomeNotHonoured, $"Finding {finding.StableId} has no honoured evidence outcome.");
            if (outcome.ResultReason != FindingEvidenceOutcomeReason.ValidEvidence)
                return Fail(EvidenceOutcomeInvalid, $"Finding {finding.StableId} has evidence outcome {outcome.ResultReason}.");
            if (outcome.ReceiptId is not { Length: > 0 } receiptId)
                return Fail(ReceiptIdMissing, $"Finding {finding.StableId} has no receipt id.");
            var receipt = task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? [])
                .LastOrDefault(candidate => string.Equals(candidate.ReceiptId, receiptId, StringComparison.Ordinal));
            if (receipt is null)
                return Fail(ReceiptNotFound, $"Finding {finding.StableId} receipt {receiptId} is absent from Tester history.");
            if (!receipt.Accepted || !receipt.Passed)
                return Fail(ReceiptNotPassing, $"Finding {finding.StableId} receipt {receiptId} has accepted={receipt.Accepted}, passed={receipt.Passed}.");
            if (!ConductorGitRevisionReader.IsValid(receipt.CandidateSha) ||
                !string.Equals(receipt.CandidateSha.Trim(), reviewedSha, StringComparison.OrdinalIgnoreCase))
                return Fail(ReceiptCandidateDiffers, $"Finding {finding.StableId} receipt {receiptId} candidate '{receipt.CandidateSha}' differs from reviewed {reviewedSha} or is invalid.");
            if (receipt.Arms is not { } arms || !arms.Any(arm => arm.Arm == FindingEvidenceArm.Candidate))
                return Fail(CandidateArmMissing, $"Finding {finding.StableId} receipt {receiptId} has no Candidate arm.");
            var invalidArm = arms.FirstOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate &&
                (arm.Disposition != FindingEvidenceArmDisposition.Green || !arm.Accepted || !arm.Passed ||
                 !string.Equals(arm.Sha?.Trim(), reviewedSha, StringComparison.OrdinalIgnoreCase)));
            if (invalidArm is not null)
                return Fail(CandidateArmNotGreen, $"Finding {finding.StableId} receipt {receiptId} Candidate arm has disposition={invalidArm.Disposition}, accepted={invalidArm.Accepted}, passed={invalidArm.Passed}, sha='{invalidArm.Sha}' instead of green at {reviewedSha}.");
            selected.Add((finding, receipt));
        }
        if (blockingHumanInputOpen)
            return Fail(HumanInputRequestOpen, $"Tester task {task.Id} has an open blocking human input request.");
        return new(Closable, $"All {selected.Count} blocking findings have passing Candidate arms at {reviewedSha}.", selected);
    }
}
