using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Consumes already reproduced, candidate-bound failures. It neither attributes failures nor runs evidence.
internal static class AttributedFailureDeveloperRoute
{
    internal static AttributedFailure? ReadAcceptanceFailure(AcceptanceFailureSummary? summary)
    {
        if (summary is null || string.IsNullOrWhiteSpace(summary.BranchHeadSha)) return null;
        var evidence = (summary.CheckAttributions ?? []).Select(check => check.Evidence)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (evidence.Length != 1 || evidence[0] is not { } detail) return null;

        var cohort = Regex.Match(detail,
            @"\Acohort-attribution=\S+ cohort-partition=(?<receipt>\S+) reproduced=(?<tests>[^\r\n]*)\z");
        if (cohort.Success)
            return new("cohort", cohort.Groups["receipt"].Value, summary.BranchHeadSha,
                cohort.Groups["tests"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(test => test.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        var train = Regex.Match(detail,
            @"\Amerge train RED attributed \((?<source>ownership|reach|path|recorded)\) receipt (?<receipt>\S+): (?<tests>[^\r\n]+)\z");
        return train.Success
            ? new("train-" + train.Groups["source"].Value, train.Groups["receipt"].Value, summary.BranchHeadSha,
                train.Groups["tests"].Value.Split(',').Select(test => test.Trim())
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
            : null;
    }

    internal static AttributedFailure? ReadTesterReceipt(Goal goal, TaskSpec tester, string? candidateSha)
    {
        if (tester.RequiredRole != AgentRole.Tester || tester.Status != WorkTaskStatus.Failed ||
            string.IsNullOrWhiteSpace(candidateSha) ||
            !WorkerResultBlockers.TryGetTestsStatus(tester.LastVerification, out var status) ||
            status != WorkerResultBlockers.TestsStatus.Fail) return null;

        var receiptIds = ReviewFindings.GetOpenBlockingFindings(
                tester.LastVerification?.MergedReviewFindings ?? [], goal.EffectiveAcceptanceCriteriaCorrections)
            .Where(finding => finding.EvidenceOutcome is { Honoured: true, ReceiptId: not null })
            .Select(finding => finding.EvidenceOutcome!.ReceiptId!).Distinct(StringComparer.Ordinal).ToArray();
        if (receiptIds.Length != 1) return null;
        var receipt = tester.VerificationHistory.SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .LastOrDefault(item => item.ReceiptId == receiptIds[0] &&
                string.Equals(item.CandidateSha, candidateSha, StringComparison.Ordinal));
        if (receipt is not { Accepted: true, Arms: { } arms } ||
            arms.Any(arm => arm.Disposition is FindingEvidenceArmDisposition.ApparatusFailure or
                FindingEvidenceArmDisposition.Inconclusive ||
                arm.Arm == FindingEvidenceArm.Baseline && arm.Disposition == FindingEvidenceArmDisposition.Red)) return null;
        var candidates = arms.Where(arm => arm.Arm == FindingEvidenceArm.Candidate).ToArray();
        if (candidates.Length != 1 || candidates[0] is not
            { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red, FailingTestIdentities.Count: > 0 } red ||
            !string.Equals(red.Sha, candidateSha, StringComparison.Ordinal)) return null;
        return new("tester-receipt", receipt.ReceiptId, receipt.CandidateSha, red.FailingTestIdentities);
    }

    internal static AttributedFailureRouteDecision Decide(Goal goal, AttributedFailure? failure, string? currentCandidate)
    {
        if (failure is null) return AttributedFailureRouteDecision.Decline("no-attributed-failure");
        var tests = failure.FailingTests.Select(test => test.Trim()).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (tests.Length == 0 || tests.Any(test => test.Length == 0 ||
                test.Any(character => char.IsWhiteSpace(character) || character is ',' or ';')))
            return AttributedFailureRouteDecision.Decline("unrepresentable-failing-tests");
        if (string.IsNullOrWhiteSpace(currentCandidate) ||
            !string.Equals(failure.CandidateSha, currentCandidate, StringComparison.Ordinal))
            return AttributedFailureRouteDecision.Decline("candidate-mismatch");
        if (goal.LatestAcceptanceFailure is { } recorded && (recorded.IsEnvironmentalApparatus ||
            recorded.CheckAttributions?.Any(check => check.Cause != AcceptanceFailureCause.NotClassified) == true))
            return AttributedFailureRouteDecision.Decline("classified-acceptance-failure");
        var developer = goal.Tasks.LastOrDefault(task => task.RequiredRole == AgentRole.Developer &&
            task.Status == WorkTaskStatus.Completed &&
            (task.VerificationHistory.Any(verification => verification.HasCommittedChanges) ||
             task.DispatchHistory.Any(dispatch => !string.IsNullOrWhiteSpace(dispatch.ResultCommit) &&
                 !string.Equals(dispatch.ResultCommit, dispatch.BaseCommit, StringComparison.OrdinalIgnoreCase))));
        if (developer is null) return AttributedFailureRouteDecision.Decline("no-committed-developer");
        if (goal.Tasks.Any(task => task.LastProcess is { IsRunning: true }))
            return AttributedFailureRouteDecision.Decline("worker-still-running");

        var signature = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(failure.CandidateSha + "\n" + string.Join(',', tests)))).ToLowerInvariant()[..16];
        if (goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetried &&
                evt.Message.StartsWith("ATTRIBUTED_FAILURE_ROUTE ", StringComparison.Ordinal))
            .Select(evt => evt.Message.Split('\n')[0].TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(tokens => tokens.Contains("candidate_sha=" + failure.CandidateSha, StringComparer.Ordinal) &&
                tokens.Contains("signature=" + signature, StringComparer.Ordinal)))
            return AttributedFailureRouteDecision.Decline("already-routed");
        return new(developer.Id, BuildFeedback(goal, failure with { FailingTests = tests }, signature), signature, null);
    }

    internal static string BuildFeedback(Goal goal, AttributedFailure failure, string signature) =>
        string.Join(Environment.NewLine,
            new[]
            {
                $"ATTRIBUTED_FAILURE_ROUTE source={failure.Source} candidate_sha={failure.CandidateSha} receipt_id={failure.ReceiptId} signature={signature}",
                "failing_tests=" + string.Join(',', failure.FailingTests)
            }.Concat(goal.RefinedSpec?.AcceptanceCriteria ?? [])
            .Append("1. Read the named failing tests at this candidate and decide whether the code or the test is wrong. " +
                "2. Fix the code, or fix the test so it asserts the intended behavior; never weaken, loosen or remove an assertion to make it pass. " +
                "3. List every failing test's class in `tests: deferred - ` so the conductor runs it."));
}
