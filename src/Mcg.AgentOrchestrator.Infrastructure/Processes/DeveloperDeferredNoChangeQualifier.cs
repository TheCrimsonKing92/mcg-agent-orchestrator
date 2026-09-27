using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DeveloperDeferredNoChangeQualifier
{
    private static readonly Regex RationaleLine = new(
        @"(?im)^\s*(?:NO_CHANGE:|No-change rationale:|No changes needed:)\s*(?<reason>\S[^\r\n]*)$",
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
    {
        outcome = default!;
        var candidate = task.LastDispatch?.BaseCommit;
        if (task.RequiredRole != AgentRole.Developer ||
            (task.LatestRetryAt is null && task.CriterionRetryCount == 0 && task.CriterionRetryFeedback.Count == 0) ||
            !clean || hasRelevantCommitAfterDispatch ||
            string.IsNullOrWhiteSpace(candidate) ||
            !Regex.IsMatch(candidate, "^[a-fA-F0-9]{40}(?:[a-fA-F0-9]{24})?$") ||
            !string.Equals(candidate, head, StringComparison.OrdinalIgnoreCase) ||
            !classifier.HasExplicitNoChangeRationale(standardOutput, standardError) ||
            !(WorkerResultParser.TryParseResult(standardOutput, out var result, out _) ||
              WorkerResultParser.TryParseResult(standardError, out result, out _)) ||
            result.BlockersStatus != WorkerResultParser.BlockersStatus.None ||
            result.TestsStatus != WorkerResultParser.TestsStatus.Deferred ||
            !result.Fields.TryGetValue("tests", out var testsField))
            return false;

        var rationale = RationaleLine.Match(standardOutput);
        if (!rationale.Success) rationale = RationaleLine.Match(standardError);
        if (!rationale.Success) return false;
        var classes = DeveloperDeferredTestClassNames.Parse(testsField);
        if (classes.Count == 0) return false;

        var required = task.CriterionRetryFeedback
            .Append(task.AcceptedRetryFeedback?.Message ?? string.Empty)
            .SelectMany(message => FailingTests.Matches(message)
                .SelectMany(match => match.Groups["identities"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries)))
            .Select(DeclaringClass)
            .Where(name => name.Length > 0)
            .ToArray();
        if (task.PendingRetryCause == RetryCause.NewTestFinding)
        {
            required = required.Concat(goal.Tasks
                .SelectMany(other => other.LastVerification?.MergedReviewFindings ?? [])
                .Where(finding => finding.State == ReviewFindingState.Open &&
                    finding.EvidenceRequest is not null &&
                    finding.Category is FindingCategory.SpecCompliance or FindingCategory.Correctness or
                        FindingCategory.TestCoverage or FindingCategory.CodeQuality or FindingCategory.Unspecified)
                .SelectMany(finding => finding.EvidenceRequest!.Selections)
                .Select(selection => selection.TestClass)).ToArray();
            if (required.Length == 0) return false;
        }
        if (required.Any(name => !classes.Any(declared =>
                string.Equals(declared, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(declared, name.Split(['.', '+']).Last(), StringComparison.OrdinalIgnoreCase))))
            return false;

        outcome = new DeferredNoChangeOutcome(candidate, classes, rationale.Value.Trim());
        return true;
    }

    private static string DeclaringClass(string identity)
    {
        var withoutCase = identity.Trim().TrimEnd('.').Split('(', 2)[0];
        var lastDot = withoutCase.LastIndexOf('.');
        return lastDot > 0 ? withoutCase[..lastDot] : string.Empty;
    }
}
