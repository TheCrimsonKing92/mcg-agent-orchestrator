using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DeveloperDeferredNoChangeQualifier
{
    private static readonly Regex RationaleLine = new(
        @"(?im)^\s*(?:NO_CHANGE:|No-change rationale:|No changes needed:)\s*(?<reason>\S[^\r\n]*?)\r?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FailingTests = new(
        @"failing_tests=(?<identities>[^\s;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static bool TryQualify(
        Goal goal,
        TaskSpec task,
        string head,
        bool clean,
        bool hasRelevantCommitAfterDispatch,
        string standardOutput,
        string standardError,
        WorkerDispatchCompletionClassifier classifier,
        out DeferredNoChangeOutcome outcome)
        => TryQualify(goal, task, head, clean, hasRelevantCommitAfterDispatch,
            standardOutput, standardError, classifier, out outcome, out _);

    internal static bool TryQualify(
        Goal goal,
        TaskSpec task,
        string head,
        bool clean,
        bool hasRelevantCommitAfterDispatch,
        string standardOutput,
        string standardError,
        WorkerDispatchCompletionClassifier classifier,
        out DeferredNoChangeOutcome outcome,
        out string? declineCode)
    {
        outcome = default!;
        declineCode = null;
        var candidate = task.LastDispatch?.BaseCommit;
        if (task.RequiredRole != AgentRole.Developer) return Decline("not-developer", out declineCode);
        if (task.LatestRetryAt is null && task.CriterionRetryCount == 0 && task.CriterionRetryFeedback.Count == 0 &&
            !ResumedAfterCommittedDispatchAnswer(goal, task))
            return Decline("not-retry-or-resume", out declineCode);
        if (!clean) return Decline("dirty", out declineCode);
        if (hasRelevantCommitAfterDispatch) return Decline("commit-after-dispatch", out declineCode);
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Regex.IsMatch(candidate, "^[a-fA-F0-9]{40}(?:[a-fA-F0-9]{24})?$") ||
            !string.Equals(candidate, head, StringComparison.OrdinalIgnoreCase))
            return Decline("candidate-not-head", out declineCode);
        if (!classifier.HasExplicitNoChangeRationale(standardOutput, string.Empty))
            return Decline("no-rationale", out declineCode);
        if (!WorkerResultParser.TryParseResult(standardOutput, out var result, out _))
            return Decline("no-worker-result", out declineCode);
        if (result.BlockersStatus != WorkerResultParser.BlockersStatus.None)
            return Decline("blockers", out declineCode);
        if (result.TestsStatus != WorkerResultParser.TestsStatus.Deferred ||
            !result.Fields.TryGetValue("tests", out var testsField))
            return Decline("tests-not-deferred", out declineCode);

        var rationale = RationaleLine.Match(standardOutput);
        if (!rationale.Success) return Decline("no-rationale", out declineCode);
        var classes = DeveloperDeferredTestClassNames.Parse(testsField);
        if (classes.Count == 0) return Decline("no-classes", out declineCode);

        var required = task.CriterionRetryFeedback
            .Append(task.AcceptedRetryFeedback?.Message ?? string.Empty)
            .Concat(goal.Timeline
                .Where(item => item.TaskId == task.Id &&
                    item.OccurredAt >= task.LatestRetryAt &&
                    item.Kind is ProgressKind.TaskRetried or ProgressKind.TaskRetryFeedbackUpdated)
                .Select(item => item.Message))
            .SelectMany(message => FailingTests.Matches(message)
                .SelectMany(match => match.Groups["identities"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries)))
            .Select(DeclaringClass)
            .ToArray();
        if (required.Any(string.IsNullOrEmpty)) return Decline("failing-test-unresolvable", out declineCode);
        string[] findingClasses = [];
        if (task.PendingRetryCause == RetryCause.NewTestFinding)
        {
            findingClasses = goal.Tasks
                .SelectMany(other => other.LastVerification?.MergedReviewFindings ?? [])
                .Where(finding => finding.State == ReviewFindingState.Open &&
                    finding.EvidenceRequest is not null &&
                    finding.Severity == FindingSeverity.Blocking &&
                    ReviewFindingRouting.Project([finding])[0].TargetRole == AgentRole.Developer)
                .SelectMany(finding => finding.EvidenceRequest!.Selections)
                .Select(selection => selection.TestClass).ToArray();
            if (required.Length == 0 && findingClasses.Length == 0 && !NewestRetryIsEvidenceUnusable(goal, task))
                return Decline("no-finding-classes", out declineCode);
        }
        if (required.Any(name => !IsDeclared(name)))
            return Decline("failing-test-class-undeclared", out declineCode);
        if (findingClasses.Any(name => !IsDeclared(name)))
            return Decline("finding-class-undeclared", out declineCode);

        outcome = new DeferredNoChangeOutcome(candidate, classes, rationale.Value.Trim());
        return true;

        bool IsDeclared(string name) => classes.Any(declared =>
            string.Equals(declared, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(declared, name.Split(['.', '+']).Last(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool Decline(string code, out string? declineCode)
    {
        declineCode = code;
        return false;
    }

    private static bool NewestRetryIsEvidenceUnusable(Goal goal, TaskSpec task)
    {
        if (task.LatestRetryAt is not { } retryAt) return false;
        var retry = goal.Timeline.Where(item => item.TaskId == task.Id &&
                item.Kind == ProgressKind.TaskRetried && item.OccurredAt >= retryAt)
            .OrderBy(item => item.OccurredAt).LastOrDefault();
        return retry?.Message.TrimStart().StartsWith(
            "DEFERRED_NO_CHANGE_EVIDENCE_UNUSABLE", StringComparison.Ordinal) == true;
    }

    private static bool ResumedAfterCommittedDispatchAnswer(Goal goal, TaskSpec task)
    {
        if (task.LastDispatch is not { } current) return false;
        var previous = task.DispatchHistory.LastOrDefault(dispatch => dispatch.DispatchedAt < current.DispatchedAt &&
            !string.IsNullOrWhiteSpace(dispatch.BaseCommit));
        if (previous is null || string.IsNullOrWhiteSpace(previous.ResultCommit) ||
            string.Equals(previous.ResultCommit, previous.BaseCommit, StringComparison.OrdinalIgnoreCase))
            return false;

        // Conductor integrations append commits, so the previous ResultCommit is an ancestor
        // of the current BaseCommit by construction; no separate git ancestry query is needed.
        return goal.Timeline.Any(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.HumanInputReceived &&
            item.OccurredAt > previous.DispatchedAt && item.OccurredAt < current.DispatchedAt);
    }

    private static string DeclaringClass(string identity)
    {
        var withoutCase = identity.Trim().TrimEnd('.').Split('(', 2)[0];
        var lastDot = withoutCase.LastIndexOf('.');
        return lastDot > 0 ? withoutCase[..lastDot] : string.Empty;
    }
}
