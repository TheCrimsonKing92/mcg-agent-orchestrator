namespace Mcg.AgentOrchestrator.Core;

// Owns the read-only projection of goal and stream verification receipts into Reviewer briefs.
internal static class ReviewerEvidenceBriefSection
{
    private const int ReviewerExecutedTestEvidenceMaxLines = 12;

    internal static IReadOnlyList<string> BuildSliceBatchParentReviewBriefBlock(Goal goal, TaskSpec task, IEnumerable<Goal> goals)
    {
        if (task.RequiredRole != AgentRole.Reviewer || goal.SliceBatchParentId is not null)
        {
            return [];
        }

        // GoalCreated is durable across snapshot reloads. Stable sorting preserves creation order
        // for planner nodes created in the same clock tick.
        var children = goals
            .Where(child => child.SliceBatchParentId == goal.Id)
            .OrderBy(child => child.Timeline.FirstOrDefault(evt => evt.Kind == ProgressKind.GoalCreated)?.OccurredAt
                ?? child.MetadataCreatedAt)
            .ToArray();
        if (children.Length == 0)
        {
            return [];
        }

        var lines = new List<string>
        {
            "## Slice-Batch Decomposition Plan and Stream Review Receipts",
            $"Parent objective: {goal.Objective}",
            "Child objectives in plan order:"
        };
        for (var index = 0; index < children.Length; index++)
        {
            lines.Add($"- {index + 1}. {children[index].Id.Value}: {children[index].Objective}");
        }

        foreach (var child in children)
        {
            lines.Add(string.Empty);
            lines.Add($"### Child {child.Id.Value}");
            lines.Add($"Objective: {child.Objective}");
            var reviewer = child.Tasks.LastOrDefault(candidate => candidate.RequiredRole == AgentRole.Reviewer);
            lines.Add("Pre-review evidence receipt:");
            if (reviewer?.PreReviewEvidenceReceipt is { } receipt)
            {
                lines.Add($"Disposition={receipt.Disposition}; reviewer_round={receipt.ReviewerRound}; " +
                    $"candidate_sha={receipt.CandidateSha}; selected={receipt.SelectedFocusedTests.Count}; " +
                    $"passed={receipt.PassedCheckCount}; failed={receipt.FailedCheckCount}.");
                lines.Add($"Recorded at: {receipt.RecordedAt:u}");
                lines.Add($"Selected focused tests: {string.Join(", ", receipt.SelectedFocusedTests)}");
                lines.Add($"Mapping reason: {receipt.MappingReason}");
                foreach (var check in receipt.Checks)
                {
                    lines.Add($"- {check.Name}: passed={check.Passed}; exit={check.ExitCode?.ToString() ?? "none"}; " +
                        $"command={check.Command}; artifact={check.ArtifactPath ?? "none"}");
                    foreach (var path in check.TestResultPaths ?? [])
                    {
                        lines.Add($"  Test result: {path}");
                    }
                }

                lines.Add($"Failing tests: {string.Join(", ", receipt.FailingTestIdentities)}");
                foreach (var advisory in receipt.Advisories ?? [])
                {
                    lines.Add($"Advisory: {advisory}");
                }

                foreach (var timeout in receipt.EvidenceTimeoutChecks ?? [])
                {
                    lines.Add($"Evidence timeout check: {timeout}");
                }

                lines.Add($"Evidence pointer: {receipt.EvidencePointer ?? "none"}");
            }
            else
            {
                lines.Add("(none recorded)");
            }

            lines.Add("Last Reviewer result:");
            var verification = reviewer?.VerificationHistory.OrderByDescending(item => item.CompletedAt).FirstOrDefault();
            if (verification is not null)
            {
                var verdict = TryGetWorkerResultField(verification, "verdict", out var value) ? value : "(none recorded)";
                lines.Add($"result {(verification.Succeeded ? "pass" : "fail")} (exit {verification.ExitCode}); " +
                    $"completed {verification.CompletedAt:u}; reviewed commit {verification.ReviewedCommit ?? "none"}; verdict {verdict}");
                // Keep findings as well as the verdict: a whole-goal Reviewer needs the recorded
                // summary itself, not just a success flag for each stream.
                lines.Add($"Stdout: {verification.AuthoritativeStandardOutput ?? verification.StandardOutput}");
                lines.Add($"Stderr: {verification.AuthoritativeStandardError ?? verification.StandardError}");
            }
            else
            {
                lines.Add("(none recorded)");
            }
        }

        lines.Add(string.Empty);
        return lines;
    }

    internal static IReadOnlyList<string> BuildReviewerExecutedTestEvidenceBriefBlock(Goal goal, TaskSpec task)
    {
        if (task.RequiredRole != AgentRole.Reviewer)
        {
            return [];
        }

        var lines = new List<string>();
        if (task.PreReviewEvidenceReceipt is { } preReview)
        {
            lines.Add("## Current-HEAD Pre-Review Evidence (conductor-owned)");
            lines.Add(
                $"Disposition={preReview.Disposition}; reviewer_round={preReview.ReviewerRound}; " +
                $"candidate_sha={preReview.CandidateSha}; selected={preReview.SelectedFocusedTests.Count}; " +
                $"passed={preReview.PassedCheckCount}; failed={preReview.FailedCheckCount}.");
            lines.Add($"Mapping reason: {PromptContextFormatter.TrimPromptBlock(preReview.MappingReason)}");
            foreach (var check in preReview.Checks)
            {
                lines.Add(
                    $"- {check.Name}: passed={check.Passed}; exit={check.ExitCode?.ToString() ?? "none"}; " +
                    $"command={PromptContextFormatter.TrimPromptBlock(check.Command)}; artifact={check.ArtifactPath ?? "none"}");
            }

            if (preReview.FailingTestIdentities.Count > 0)
            {
                lines.Add($"Failing tests: {string.Join(", ", preReview.FailingTestIdentities)}");
            }

            foreach (var advisory in preReview.Advisories ?? [])
            {
                lines.Add($"Advisory: {PromptContextFormatter.TrimPromptBlock(advisory)}");
            }

            lines.Add($"Evidence pointer: {preReview.EvidencePointer ?? "none"}");
            lines.Add(string.Empty);
        }

        var receipts = goal.Tasks
            .Where(candidate => candidate.Id != task.Id)
            .SelectMany(candidate => candidate.VerificationHistory.Select(verification => new
            {
                Task = candidate,
                Verification = verification
            }))
            .OrderByDescending(item => item.Verification.CompletedAt)
            .ToList();

        lines.Add("## Executed Test Evidence");
        lines.Add($"Reviewer is read-only; use these existing verification receipts before asking for reruns. Newest first; capped at {ReviewerExecutedTestEvidenceMaxLines} receipt line(s).");

        if (receipts.Count == 0)
        {
            lines.Add("No executed test evidence exists for this goal yet.");
            lines.Add(string.Empty);
            return lines;
        }

        var emitted = 0;
        foreach (var receipt in receipts)
        {
            if (emitted >= ReviewerExecutedTestEvidenceMaxLines)
            {
                break;
            }

            lines.Add(FormatReviewerVerificationReceipt(goal, receipt.Task, receipt.Verification));
            emitted++;

            if (emitted >= ReviewerExecutedTestEvidenceMaxLines)
            {
                break;
            }

            if (WorkerResultBlockers.TryFindTests(receipt.Verification, out var tests))
            {
                lines.Add(FormatReviewerWorkerResultTestsReceipt(goal, receipt.Task, receipt.Verification, tests));
                emitted++;
            }
        }

        if (receipts.Count > 0 && emitted >= ReviewerExecutedTestEvidenceMaxLines)
        {
            var omitted = receipts.Sum(item => WorkerResultBlockers.TryFindTests(item.Verification, out _) ? 2 : 1) - emitted;
            if (omitted > 0)
            {
                lines.Add($"- Omitted {omitted} older executed-test evidence line(s).");
            }
        }

        lines.Add(string.Empty);
        return lines;
    }

    private static string FormatReviewerVerificationReceipt(
        Goal goal,
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        var status = verification.Succeeded ? "pass" : "fail";
        var freshness = DescribeVerificationFreshness(task, verification);
        var paths = DescribeVerificationArtifactPaths(verification);
        var evidence = DescribeVerificationOutputEvidence(verification);
        return $"- {verification.CompletedAt:u}; provenance: Task {TaskDisplayNumber.Resolve(goal, task.Id)} {task.RequiredRole} verification; result: {status} (exit {verification.ExitCode}); command/run context: {PromptContextFormatter.TrimPromptBlock(verification.Command)} @ {PromptContextFormatter.TrimPromptBlock(verification.WorkingDirectory)}; freshness: {freshness}{paths}{evidence}";
    }

    private static string FormatReviewerWorkerResultTestsReceipt(
        Goal goal,
        TaskSpec task,
        TaskVerificationRecord verification,
        string tests)
    {
        return $"- {verification.CompletedAt:u}; provenance: Task {TaskDisplayNumber.Resolve(goal, task.Id)} {task.RequiredRole} WORKER_RESULT tests; tests: {PromptContextFormatter.TrimPromptBlock(tests)}; freshness: {DescribeVerificationFreshness(task, verification)}";
    }

    private static string DescribeVerificationFreshness(TaskSpec task, TaskVerificationRecord verification)
    {
        if (TryGetWorkerResultField(verification, "commit", out var workerResultCommit) &&
            !IsNoneValue(workerResultCommit))
        {
            return $"verified commit {workerResultCommit}";
        }

        if (!string.IsNullOrWhiteSpace(task.LastDispatch?.ResultCommit))
        {
            return $"verified commit {task.LastDispatch.ResultCommit.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(task.LastDispatch?.BaseCommit))
        {
            return $"base commit {task.LastDispatch.BaseCommit.Trim()}";
        }

        return "unknown commit";
    }

    private static string DescribeVerificationArtifactPaths(TaskVerificationRecord verification)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(verification.StandardOutputPath))
        {
            paths.Add($"stdout {verification.StandardOutputPath.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardErrorPath))
        {
            paths.Add($"stderr {verification.StandardErrorPath.Trim()}");
        }

        return paths.Count == 0
            ? string.Empty
            : $"; artifacts: {string.Join(", ", paths)}";
    }

    private static string DescribeVerificationOutputEvidence(TaskVerificationRecord verification)
    {
        var summaries = new List<string>();
        if (!string.IsNullOrWhiteSpace(verification.StandardOutput))
        {
            summaries.Add($"stdout {TailPreferredSingleLine(verification.StandardOutput, 240)}");
        }

        if (!string.IsNullOrWhiteSpace(verification.StandardError))
        {
            summaries.Add($"stderr {TailPreferredSingleLine(verification.StandardError, 240)}");
        }

        return summaries.Count == 0
            ? string.Empty
            : $"; evidence: {string.Join(" | ", summaries)}";
    }

    private static string TailPreferredSingleLine(string text, int maxChars)
    {
        var normalized = text.Trim().ReplaceLineEndings(" ");
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return $"...[truncated {normalized.Length - maxChars} chars before evidence tail]...{normalized[^maxChars..]}";
    }

    private static bool TryGetWorkerResultField(TaskVerificationRecord verification, string fieldName, out string value)
    {
        foreach (var line in (verification.StandardOutput + Environment.NewLine + verification.StandardError)
            .Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].TrimStart('-', ' ').Trim();
            if (string.Equals(key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                value = trimmed[(separator + 1)..].Trim();
                return value.Length > 0;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool IsNoneValue(string value) =>
        string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase);
}
