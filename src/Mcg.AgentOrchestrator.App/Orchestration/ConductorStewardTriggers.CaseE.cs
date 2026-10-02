using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorStewardTriggerDetector
{
    // AutoReviewRetryConvergenceBriefBuilder emits this marker for Reviewer finding routing.
    private static readonly Regex ReviewerFindingRetry = new(
        @"\Aauto-review-retry round \d+ convergence brief: Reviewer task (?<reviewer>[a-fA-F0-9]{8}) verdict=needs-work; retry upstream Developer task\.",
        RegexOptions.CultureInvariant);

    // Detection and consequential close admission use this same state predicate.
    internal static bool IsCaseETask(Goal goal, TaskSpec task, string? currentHead)
    {
        var dispatch = task.LastDispatch;
        var verification = task.LastVerification;
        if (goal.IsTerminal || task != goal.Tasks.LastOrDefault(item => item.RequiredRole == AgentRole.Developer) ||
            task.RequiredRole != AgentRole.Developer || task.Status != WorkTaskStatus.Failed ||
            task.LatestRetryAt is null || task.LatestRetryInherited || dispatch is null || verification is null ||
            string.IsNullOrWhiteSpace(currentHead) || string.IsNullOrWhiteSpace(dispatch.BaseCommit) ||
            !string.Equals(currentHead, dispatch.BaseCommit, StringComparison.OrdinalIgnoreCase) ||
            verification.HasCommittedChanges ||
            (!string.IsNullOrWhiteSpace(dispatch.ResultCommit) &&
             !string.Equals(dispatch.ResultCommit, dispatch.BaseCommit, StringComparison.OrdinalIgnoreCase)))
            return false;

        var retry = ReviewerRetry(goal, task);
        if (retry is null || retry.OccurredAt < task.LatestRetryAt || retry.OccurredAt > dispatch.DispatchedAt ||
            TryFindConfirmedRed(goal, currentHead, out _, out _)) return false;

        var output = verification.AuthoritativeStandardOutput;
        // A reported commit contradicts the no-commit premise even on a rejection path.
        if (output is not null && Regex.IsMatch(ExtractWorkerResult(output), @"^commit:\s*(?!none\s*$)\S+",
                RegexOptions.Multiline | RegexOptions.CultureInvariant)) return false;
        if (IsNoCommitRejection(verification)) return true;
        if (!verification.WorkerResultPresent || output is null ||
            !output.Contains("WORKER_RESULT:", StringComparison.Ordinal) ||
            !output.Contains("END_WORKER_RESULT", StringComparison.Ordinal)) return false;
        var result = ExtractWorkerResult(output);
        return WorkerResultBlockers.TryGetBlockersStatus(result, out var blockers) &&
               blockers == WorkerResultBlockers.BlockersStatus.Present &&
               Regex.IsMatch(result, @"^commit:\s*none\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    }

    private static ProgressEvent? ReviewerRetry(Goal goal, TaskSpec task) =>
        goal.Timeline.LastOrDefault(item => item.TaskId == task.Id && item.Kind == ProgressKind.TaskRetried) is { } retry &&
        ReviewerFindingRetry.IsMatch(retry.Message) ? retry : null;

    internal static TaskSpec? ResolveCaseEReviewer(Goal goal, TaskSpec developer)
    {
        var retry = ReviewerRetry(goal, developer);
        if (retry is null) return null;
        var prefix = ReviewerFindingRetry.Match(retry.Message).Groups["reviewer"].Value;
        var matches = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Reviewer &&
            task.Id.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private void DetectCaseE(Goal goal,
        Func<ConductorStewardTriggerKind, string, string, bool>? needsInspection,
        List<ConductorStewardTrigger> result)
    {
        if (caseDSources is null) return;
        var developer = goal.Tasks.LastOrDefault(task => task.RequiredRole == AgentRole.Developer);
        if (developer is null || ReviewerRetry(goal, developer) is null) return;
        var head = caseDSources.ResolveHead(goal);
        if (!IsCaseETask(goal, developer, head) ||
            !(needsInspection?.Invoke(ConductorStewardTriggerKind.DeveloperReviewerFindingNoCommit,
                developer.Id.Value, head!) ?? true)) return;
        var retry = ReviewerRetry(goal, developer)!;
        var reviewerPrefix = ReviewerFindingRetry.Match(retry.Message).Groups["reviewer"].Value;
        result.Add(new ConductorStewardTrigger(goal.Id.Value, developer.Id.Value, head!,
            ConductorStewardTriggerKind.DeveloperReviewerFindingNoCommit,
            developer.LastVerification!.DispatchStartedAt ?? developer.LastDispatch!.DispatchedAt,
            retry.Message, ExtractWorkerResult(developer.LastVerification.AuthoritativeStandardOutput ??
                developer.LastVerification.StandardOutput),
            [$"reviewer-task={reviewerPrefix}", $"dispatch-base={head}"],
            goal.RefinedSpec?.AcceptanceCriteria.ToArray() ?? []));
    }
}
