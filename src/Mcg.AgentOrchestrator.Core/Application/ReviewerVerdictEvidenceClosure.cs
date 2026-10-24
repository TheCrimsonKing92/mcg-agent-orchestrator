namespace Mcg.AgentOrchestrator.Core;

// Owns closure of worker-owned Planner waits from current-candidate Reviewer verdicts.
internal sealed class ReviewerVerdictEvidenceClosure(
    object requestLock,
    IClock clock,
    Action<Goal, TaskId?, ProgressKind, string> append)
{
    internal void Close(Goal goal, TaskSpec reviewer, TaskVerificationRecord verification,
        IEnumerable<HumanInputRequest> requests)
    {
        lock (requestLock)
        {
            var pending = requests.Where(request =>
            {
                var owner = request.EvidenceOwner?.Trim();
                return request.GoalId == goal.Id && !request.IsCompleted &&
                    request.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
                    (string.Equals(owner, "reviewer", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(owner, "worker", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(owner, "developer", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(owner, "tester", StringComparison.OrdinalIgnoreCase));
            }).ToArray();
            foreach (var request in pending)
            {
                var owner = request.EvidenceOwner!.Trim().ToLowerInvariant();
                var reason = GetSkipReason(goal, reviewer, verification, request, out var verdict, out var developer);
                string? reportTests = null;
                if (reason == "verdict-not-met" && owner == "developer" &&
                    ResolveCriterion(goal, request.PlannerIdentity!) is { } criterion)
                {
                    if (DeveloperReportEvidence.TryMatch(criterion, developer!, out reportTests, out var reportReason))
                        reason = null;
                    else
                        reason = reportReason ?? reason;
                }
                var ownerTask = developer;
                if (reason is null && owner == "tester")
                    reason = GetTesterSkipReason(goal, developer!.LastDispatch!.ResultCommit!, out ownerTask);
                if (reason is not null)
                {
                    var prefix = request.Id.Value[..Math.Min(8, request.Id.Value.Length)];
                    append(goal, reviewer.Id, ProgressKind.TaskNote,
                        $"REVIEWER_VERDICT_EVIDENCE_SKIPPED request={prefix} reason={reason}");
                    continue;
                }

                string answer;
                if (reportTests is not null)
                {
                    var log = string.IsNullOrWhiteSpace(developer!.LastVerification!.StandardOutputPath)
                        ? "unavailable" : developer.LastVerification.StandardOutputPath;
                    answer = $"DEVELOPER_REPORT_EVIDENCE task={developer.Id.Value} " +
                        $"criterion_index={request.PlannerIdentity!.CriterionIndex - 1} reviewer_task={reviewer.Id.Value} " +
                        $"candidate={developer.LastDispatch!.ResultCommit} worker_result_log={log} " +
                        $"tests={reportTests[..Math.Min(400, reportTests.Length)]}";
                }
                else
                {
                    var evidence = verdict!.Evidence[..Math.Min(400, verdict.Evidence.Length)];
                    var log = string.IsNullOrWhiteSpace(verification.StandardOutputPath)
                        ? "unavailable" : verification.StandardOutputPath;
                    var ownerFields = owner == "reviewer" ? string.Empty : $" owner={owner} owner_task={ownerTask!.Id.Value}";
                    answer = ($"REVIEWER_VERDICT_EVIDENCE task={reviewer.Id.Value} " +
                        $"criterion_index={verdict.CriterionIndex}{ownerFields} candidate={verification.ReviewedCommit} " +
                        $"worker_result_log={log} evidence={evidence}").Trim();
                }
                request.Complete(answer, clock.UtcNow, origin: HumanInputAnswerOrigin.Worker,
                    briefVersion: goal.AuthoritativeBrief.Version);
                append(goal, request.TaskId, ProgressKind.HumanInputReceived, answer);
            }
        }
    }

    private static string? ResolveCriterion(Goal goal, PlannerEvidenceIdentity identity)
    {
        var version = goal.AuthoritativeRefinedSpecVersion;
        var index = identity.CriterionIndex - 1;
        if (version is null || version.BriefVersion != identity.BriefVersion ||
            index < 0 || index >= version.Spec.AcceptanceCriteria.Count)
            return null;
        var criterion = version.Spec.AcceptanceCriteria[index];
        return string.IsNullOrWhiteSpace(criterion) ? null : criterion;
    }

    private static string? GetSkipReason(Goal goal, TaskSpec reviewer, TaskVerificationRecord verification,
        HumanInputRequest request, out CriterionVerdict? verdict, out TaskSpec? developer)
    {
        verdict = null;
        developer = null;
        if (request.PlannerIdentity is not { } identity)
            return "untyped";
        if (identity.BriefVersion != goal.AuthoritativeBrief.Version ||
            identity.BriefVersion != reviewer.LastDispatch?.BriefVersion)
            return "brief-version-differs";
        if (!WorkerResultBlockers.TryFindPassVerdict(verification) ||
            !WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockers) ||
            blockers != WorkerResultBlockers.BlockersStatus.None)
            return "review-not-pass";

        developer = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer && task.LastDispatch is not null)
            .MaxBy(task => task.LastDispatch!.DispatchedAt);
        var candidate = developer?.LastDispatch?.ResultCommit;
        if (developer is null || developer.Status != WorkTaskStatus.Completed || string.IsNullOrWhiteSpace(candidate))
            return "candidate-unknown";
        if (!string.Equals(verification.ReviewedCommit?.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
            return "candidate-differs";
        if (!WorkerResultBlockers.TryFindCriteriaVerdicts(verification, out var verdicts, out _))
            return "verdict-not-met";
        verdict = verdicts.FirstOrDefault(item => item.CriterionIndex == identity.CriterionIndex - 1 && item.Verdict == "met");
        return verdict is null ? "verdict-not-met" : null;
    }

    private static string? GetTesterSkipReason(Goal goal, string candidate, out TaskSpec? tester)
    {
        tester = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Tester && task.LastDispatch is not null)
            .MaxBy(task => task.LastDispatch!.DispatchedAt);
        if (tester is null)
            return "owner-round-missing";
        if (tester.Status != WorkTaskStatus.Completed)
            return "owner-round-incomplete";
        return string.Equals(tester.LastVerification?.ReviewedCommit?.Trim(), candidate, StringComparison.OrdinalIgnoreCase)
            ? null : "owner-candidate-differs";
    }
}
