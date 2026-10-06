using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DeveloperDeferredNoChangeQualifier
{
    internal const string StructuredDeclarationMarker = "DEFERRED_NO_CHANGE_STRUCTURED_DECLARATION";
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
        var hasExplicitRationale = classifier.HasExplicitNoChangeRationale(standardOutput, string.Empty);
        var filesLine = string.Empty;
        var structuredDeclaration = !hasExplicitRationale &&
            TryReadStructuredDeclaration(standardOutput, out filesLine);
        if (!hasExplicitRationale && !structuredDeclaration)
            return Decline("no-rationale", out declineCode);
        if (!WorkerResultParser.TryParseResult(standardOutput, out var result, out _))
            return Decline("no-worker-result", out declineCode);
        if (result.BlockersStatus != WorkerResultParser.BlockersStatus.None)
            return Decline("blockers", out declineCode);
        if (result.TestsStatus != WorkerResultParser.TestsStatus.Deferred ||
            !result.Fields.TryGetValue("tests", out var testsField))
            return Decline("tests-not-deferred", out declineCode);

        var rationale = RationaleLine.Match(standardOutput);
        if (!rationale.Success && !structuredDeclaration) return Decline("no-rationale", out declineCode);
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
            if (required.Length == 0 && findingClasses.Length == 0 && !NewestRetryIsEvidenceUnusable(goal, task) &&
                !AnswerTriggeredRound(goal, task))
                return Decline("no-finding-classes", out declineCode);
        }
        if (required.Any(name => !IsDeclared(name)))
            return Decline("failing-test-class-undeclared", out declineCode);
        if (findingClasses.Any(name => !IsDeclared(name)))
            return Decline("finding-class-undeclared", out declineCode);

        outcome = new DeferredNoChangeOutcome(candidate, classes,
            rationale.Success ? rationale.Value.Trim() : filesLine);
        return true;

        bool IsDeclared(string name) => classes.Any(declared =>
            string.Equals(declared, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(declared, name.Split(['.', '+']).Last(), StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TryReadStructuredDeclaration(string standardOutput, out string filesLine)
    {
        filesLine = string.Empty;
        if (!WorkerResultParser.TryParseResult(standardOutput, out var result, out _) ||
            !HasExactValue("files", "none") || !HasExactValue("commit", "none") ||
            !HasExactValue("assigned_scope_complete", "true") ||
            result.BlockersStatus != WorkerResultParser.BlockersStatus.None ||
            result.TestsStatus != WorkerResultParser.TestsStatus.Deferred ||
            !result.Fields.TryGetValue("tests", out var tests) ||
            DeveloperDeferredTestClassNames.Parse(tests).Count == 0)
            return false;

        // Match the parser's authoritative final block and last-writer-wins field rule,
        // retaining the raw line so Core can find the rationale verbatim in stdout.
        var lines = standardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.FindLastIndex(lines, line =>
            WorkerResultParser.IsOpener(WorkerResultLineUnwrap.Unwrap(line.Trim())));
        if (start < 0) return false;
        var end = Array.FindIndex(lines, start + 1, line =>
            WorkerResultParser.IsEndMarker(WorkerResultLineUnwrap.Unwrap(line.Trim())));
        if (end < 0)
        {
            end = Array.FindIndex(lines, start + 1, line =>
                string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'));
            if (end < 0) end = lines.Length;
        }
        for (var i = start + 1; i < end; i++)
        {
            var line = WorkerResultLineUnwrap.Unwrap(lines[i].Trim());
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0 && string.Equals(WorkerResultParser.NormalizeKey(line[..separator]),
                    "files", StringComparison.OrdinalIgnoreCase))
                filesLine = lines[i].Trim();
        }
        return filesLine.Length > 0;

        bool HasExactValue(string key, string expected) => result.Fields.TryGetValue(key, out var value) &&
            string.Equals(value.Trim(), expected, StringComparison.OrdinalIgnoreCase);
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

    private static bool AnswerTriggeredRound(Goal goal, TaskSpec task)
    {
        if (task.LastDispatch is not { } current) return false;
        var newestRetry = goal.Timeline.Where(item => item.TaskId == task.Id &&
                item.Kind is ProgressKind.TaskRetried or ProgressKind.TaskRetryFeedbackUpdated)
            .Select(item => (DateTimeOffset?)item.OccurredAt).Max();
        return goal.Timeline.Any(item => item.TaskId == task.Id &&
            item.Kind == ProgressKind.HumanInputReceived &&
            (newestRetry is null || item.OccurredAt > newestRetry.Value) &&
            item.OccurredAt <= current.DispatchedAt);
    }

    private static string DeclaringClass(string identity)
    {
        var withoutCase = identity.Trim().TrimEnd('.').Split('(', 2)[0];
        var lastDot = withoutCase.LastIndexOf('.');
        return lastDot > 0 ? withoutCase[..lastDot] : string.Empty;
    }
}
