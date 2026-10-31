using System.Text.RegularExpressions;
using static Mcg.AgentOrchestrator.Core.DispatchFailureClassifier;

namespace Mcg.AgentOrchestrator.Core;

internal static class DispatchCompletionEvidence
{
    private static readonly Regex PassedCountPattern = new(
        @"\bPassed:\s*[1-9]\d*\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // Common test-runner phrasings the colon form misses: "4 passed", "4 tests passed",
    // "4 of 4 passed", and "tests: pass" / "tests pass". Test runners and agents report results
    // these ways far more often than "Passed: N", so recognising them prevents false "no evidence"
    // failures on genuinely-verified work (the acceptance gate remains the authoritative check).
    private static readonly Regex PassedWordPattern = new(
        @"\b[1-9]\d*\s+(?:[\w.-]+\s+){0,3}passed\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TestsPassPattern = new(
        @"\btests?\s*[:=]?\s*pass(?:ed|es|ing)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FractionPattern = new(
        @"\b\d+\s*/\s*\d+\b",
        RegexOptions.CultureInvariant);
    private static readonly Regex ExitCodeZeroPattern = new(
        @"\bexit code\s*0\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CommitShaPattern = new(
        @"\b[0-9a-f]{7,64}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool HasDispatchResultCommitEvidence(TaskSpec task)
    {
        if (task.LastDispatch is not { } dispatch ||
            string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
            string.IsNullOrWhiteSpace(dispatch.ResultCommit))
        {
            return false;
        }

        return !string.Equals(dispatch.BaseCommit, dispatch.ResultCommit, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasVerificationEvidence(string standardOutput, string standardError)
    {
        var output = $"{standardOutput}\n{standardError}";
        if (output.Contains("test run successful", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hasVerificationTerm =
            output.Contains("test", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("suite", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("verification", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("smoke", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("check", StringComparison.OrdinalIgnoreCase);

        return hasVerificationTerm &&
            (PassedCountPattern.IsMatch(output) ||
             PassedWordPattern.IsMatch(output) ||
             TestsPassPattern.IsMatch(output) ||
             FractionPattern.IsMatch(output) ||
             ExitCodeZeroPattern.IsMatch(output));
    }

    internal static bool HasVerificationEvidence(TaskVerificationRecord verification) =>
        DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true)
            .Any(line => HasVerificationEvidence(line, string.Empty));

    internal static bool HasArtifactEvidence(bool workerResultPresent, bool hasCommittedChanges) =>
        workerResultPresent || hasCommittedChanges;

    internal static bool HasDispatchCompletionEvidence(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges)
    {
        if (hasCommittedChanges)
        {
            return true;
        }

        if (HasPassingVerificationEvidence(verification))
        {
            return true;
        }

        if (HasWorkerResultDeferral(verification))
        {
            return true;
        }

        if (workerResultPresent)
        {
            return CanCompleteWithoutChangeEvidence(task, verification) &&
                HasPopulatedStandardOutput(verification);
        }

        return !IsTransientEmptyOutputDispatchFlake(verification);
    }

    private static bool CanCompleteWithoutChangeEvidence(TaskSpec task, TaskVerificationRecord verification) =>
        DispatchRoleOutputCapabilities.TryGet(task.RequiredRole, out var capability) && capability switch
        {
            DispatchRoleOutputCapability.ReadOnly => true,
            DispatchRoleOutputCapability.VerificationOnly => HasAcceptableStructuredTestsForCompletion(verification),
            _ => false
        };

    private static bool HasPassingVerificationEvidence(TaskVerificationRecord verification) =>
        HasVerificationEvidence(verification) &&
        !HasFailingTestsForCompatibility(verification);

    private static bool HasPopulatedStandardOutput(TaskVerificationRecord verification) =>
        verification.HeartbeatStandardOutputBytes > 0 ||
        !string.IsNullOrWhiteSpace(verification.StandardOutput) ||
        HasStandardOutputFileBytes(verification);

    private static bool HasSubstantiveWorkerEvidence(bool workerResultPresent, bool hasCommittedChanges) =>
        workerResultPresent && hasCommittedChanges;

    internal static bool IsRetryRoundWithoutCommitOrDeferral(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges) =>
        verification.Succeeded &&
        workerResultPresent &&
        !hasCommittedChanges &&
        task.RequiredRole == AgentRole.Developer &&
        (task.CriterionRetryCount > 0 || task.CriterionRetryFeedback.Count > 0) &&
        !HasWorkerResultDeferral(verification);

    internal static bool IsRetryRoundVerifiedNoNewCommit(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges) =>
        verification.Succeeded &&
        workerResultPresent &&
        !hasCommittedChanges &&
        task.RequiredRole == AgentRole.Developer &&
        (task.CriterionRetryCount > 0 || task.CriterionRetryFeedback.Count > 0) &&
        HasVerifiedNoNewCommitWorkerResult(task, verification);

    internal static bool IsDeveloperVerifiedNoChangeRound(
        TaskSpec task,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges)
    {
        var dispatch = task.LastDispatch;
        var integrationSatisfied = dispatch?.PreDispatchIntegrationReceipt?.Matches(
            dispatch.GoalId,
            task,
            dispatch.BaseCommit) == true;
        var earlyConvergenceSatisfied = dispatch?.ContextPackageReceipt?.HasEarlyConvergenceEvidenceFor(
            dispatch.BaseCommit) == true;
        if (task.RequiredRole != AgentRole.Developer ||
            hasCommittedChanges ||
            !workerResultPresent ||
            !WasRedispatchedByAnyRoute(task) ||
            string.IsNullOrWhiteSpace(dispatch?.BaseCommit) ||
            (!integrationSatisfied && !earlyConvergenceSatisfied) ||
            !HasPopulatedStandardOutput(verification) ||
            !WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockersStatus) ||
            blockersStatus != WorkerResultBlockers.BlockersStatus.None ||
            WorkerResultBlockers.TryFindBlocker(verification, out _) ||
            !WorkerResultBlockers.TryGetTestsStatus(verification, out var testsStatus) ||
            testsStatus != WorkerResultBlockers.TestsStatus.Pass ||
            HasStructuredFailingTests(verification) ||
            (integrationSatisfied &&
             (!WorkerResultBlockers.TryFindTests(verification, out var tests) ||
              !FreshVerificationEvidence.HasNonZeroTestCount(tests))))
        {
            return false;
        }

        if (integrationSatisfied && verification.Succeeded)
        {
            return true;
        }

        return DispatchRejectionDiagnosticMarker.TryParse(
                verification.StandardError,
                out var verificationRecognized,
                out var reason,
                out var postDispatchCommits,
                out var changedPaths) &&
            verificationRecognized &&
            string.Equals(reason, DispatchRejectionDiagnosticMarker.NoChangeEvidence, StringComparison.Ordinal) &&
            postDispatchCommits == 0 &&
            (string.IsNullOrWhiteSpace(changedPaths) ||
             string.Equals(changedPaths, "none", StringComparison.OrdinalIgnoreCase)) &&
            TryGetOrchestratorAuthoredFailure(verification, out var authoredFailure) &&
            string.Equals(
                authoredFailure.Rule.Token,
                TaskOutcomeRules.RequiredFileChangeEvidenceMissing.Token,
                StringComparison.Ordinal);
    }

    internal static bool IsVerifiedNoChangeRoundWithRecognisedEvidence(
        OrchestratorAuthoredFailure authoredFailure,
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges) =>
        !hasCommittedChanges &&
        workerResultPresent &&
        HasPopulatedStandardOutput(verification) &&
        string.Equals(authoredFailure.Rule.Token, TaskOutcomeRules.RequiredFileChangeEvidenceMissing.Token, StringComparison.Ordinal) &&
        DispatchRejectionDiagnosticMarker.TryParse(verification.StandardError, out var verificationRecognized, out _, out _, out _) &&
        verificationRecognized &&
        WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockersStatus) &&
        blockersStatus == WorkerResultBlockers.BlockersStatus.None &&
        !WorkerResultBlockers.TryFindBlocker(verification, out _) &&
        WorkerResultBlockers.TryGetTestsStatus(verification, out var testsStatus) &&
        testsStatus == WorkerResultBlockers.TestsStatus.Pass &&
        !HasStructuredFailingTests(verification);

    // The verified-no-change allowance is gated on re-dispatch, not on the reason for it. Operator
    // recovery and upstream bounces record LatestRetryAt without touching criterion-retry state.
    private static bool WasRedispatchedByAnyRoute(TaskSpec task) =>
        task.LatestRetryAt is not null ||
        task.CriterionRetryCount > 0 ||
        task.CriterionRetryFeedback.Count > 0;

    private static bool HasVerifiedNoNewCommitWorkerResult(TaskSpec task, TaskVerificationRecord verification) =>
        HasPopulatedStandardOutput(verification) &&
        WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockersStatus) &&
        blockersStatus == WorkerResultBlockers.BlockersStatus.None &&
        WorkerResultBlockers.TryGetTestsStatus(verification, out var testsStatus) &&
        testsStatus == WorkerResultBlockers.TestsStatus.Pass &&
        !WorkerResultBlockers.TryFindBlocker(verification, out _) &&
        !HasStructuredFailingTests(verification) &&
        TryGetKnownWorkerResultCommitSha(task, verification, out _);

    internal static bool TryGetKnownWorkerResultCommitSha(
        TaskSpec task,
        TaskVerificationRecord verification,
        out string commitSha)
    {
        commitSha = string.Empty;
        if (!DispatchClassifierReceipt.TryGetWorkerResultFieldValue(verification, "commit", out var commitValue) ||
            DispatchClassifierReceipt.IsNoWorkerResultBlockersValue(commitValue))
        {
            return false;
        }

        var match = CommitShaPattern.Match(commitValue);
        if (!match.Success)
        {
            return false;
        }

        var candidate = match.Value;
        if (MatchesKnownDispatchCommit(task.LastDispatch?.BaseCommit, candidate) ||
            MatchesKnownDispatchCommit(task.LastDispatch?.ResultCommit, candidate))
        {
            commitSha = candidate;
            return true;
        }

        return false;
    }

    private static bool MatchesKnownDispatchCommit(string? knownCommit, string candidate)
    {
        if (string.IsNullOrWhiteSpace(knownCommit))
        {
            return false;
        }

        var known = knownCommit.Trim();
        return known.StartsWith(candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(known, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasWorkerResultDeferral(TaskVerificationRecord verification) =>
        HasWorkerResultDeferral(
            DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: false));

    internal static bool HasWorkerResultDeferral(IEnumerable<string> evidenceLines)
    {
        var retainedLines = evidenceLines.ToArray();
        return (WorkerResultBlockers.TryGetTestsStatus(string.Join('\n', retainedLines), out var testsStatus) &&
                testsStatus == WorkerResultBlockers.TestsStatus.Deferred) ||
            (DispatchClassifierReceipt.TryGetWorkerResultFieldValue(retainedLines, "deferrals", out var deferrals) &&
             !DispatchClassifierReceipt.IsNoWorkerResultBlockersValue(deferrals));
    }

    internal static bool HasGreenCommittedWorkerResultEvidence(
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges) =>
        workerResultPresent &&
        hasCommittedChanges &&
        HasPopulatedStandardOutput(verification) &&
        WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockersStatus) &&
        blockersStatus == WorkerResultBlockers.BlockersStatus.None &&
        WorkerResultBlockers.TryGetTestsStatus(verification, out var testsStatus) &&
        testsStatus is WorkerResultBlockers.TestsStatus.Pass or WorkerResultBlockers.TestsStatus.NotRun &&
        !WorkerResultBlockers.TryFindBlocker(verification, out _) &&
        !HasStructuredFailingTests(verification);

    private static bool HasStructuredFailingTests(TaskVerificationRecord verification) =>
        WorkerResultBlockers.TryGetTestsStatus(verification, out var status) &&
        status == WorkerResultBlockers.TestsStatus.Fail;

    private static bool HasFailingTestsForCompatibility(TaskVerificationRecord verification) =>
        HasStructuredFailingTests(verification) ||
        (!WorkerResultBlockers.TryGetTestsStatus(verification, out _) &&
         WorkerResultBlockers.TryFindFailingTests(verification, out _));

    private static bool HasAcceptableStructuredTestsForCompletion(TaskVerificationRecord verification) =>
        WorkerResultBlockers.TryGetTestsStatus(verification, out var status) &&
        status is WorkerResultBlockers.TestsStatus.Pass or WorkerResultBlockers.TestsStatus.NotRun;

    internal static bool HasWorkerEvidenceThatOutranksSubscriptionLimit(
        TaskVerificationRecord verification,
        bool workerResultPresent,
        bool hasCommittedChanges) =>
        WorkerResultBlockers.TryFindBlocker(verification, out _) ||
        HasSubstantiveWorkerEvidence(workerResultPresent, hasCommittedChanges);
}
