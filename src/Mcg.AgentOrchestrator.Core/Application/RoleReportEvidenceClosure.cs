namespace Mcg.AgentOrchestrator.Core;

// Owns closure of current-brief role reports, with Developer classes bound to landing gate evidence.
internal sealed class RoleReportEvidenceClosure(
    object requestLock,
    IClock clock,
    Action<Goal, TaskId?, ProgressKind, string> append)
{
    internal IReadOnlyDictionary<HumanInputRequestId, string> Close(Goal goal,
        IEnumerable<HumanInputRequest> requests, string landingCandidateSha,
        Func<string, string?> passedGateSourceForClass)
    {
        lock (requestLock)
        {
            var skipped = new Dictionary<HumanInputRequestId, string>();
            foreach (var request in requests.Where(request => request.GoalId == goal.Id && !request.IsCompleted &&
                request.Kind == HumanWaitKind.ProspectiveAcceptanceEvidence &&
                string.Equals(request.PlannerEvidence?.Availability, "post-implementation", StringComparison.OrdinalIgnoreCase))
                .ToArray())
            {
                var owner = request.EvidenceOwner?.Trim().ToLowerInvariant();
                if (owner is not ("developer" or "tester" or "reviewer")) continue;
                var role = owner switch
                {
                    "developer" => AgentRole.Developer,
                    "tester" => AgentRole.Tester,
                    _ => AgentRole.Reviewer
                };
                var task = Latest(goal, role);
                string? tests = null;
                string? gate = null;
                var reason = request.PlannerIdentity?.BriefVersion != goal.AuthoritativeBrief.Version
                    ? "brief-version-differs"
                    : owner == "developer"
                        ? DeveloperSkipReason(goal, request, task, passedGateSourceForClass, out tests, out gate)
                        : RoundSkipReason(goal, task, role);
                if (reason is not null)
                {
                    skipped.Add(request.Id, reason);
                    var prefix = $"ROLE_REPORT_EVIDENCE_SKIPPED request={request.Id.Value[..Math.Min(8, request.Id.Value.Length)]} ";
                    var note = $"{prefix}reason={reason}";
                    var latest = goal.Timeline.LastOrDefault(entry => entry.Kind == ProgressKind.TaskNote &&
                        entry.TaskId == request.TaskId && entry.Message.StartsWith(prefix, StringComparison.Ordinal));
                    if (latest?.Message != note)
                        append(goal, request.TaskId, ProgressKind.TaskNote, note);
                    continue;
                }

                var ownerTask = task!;
                var commit = role == AgentRole.Developer ? ownerTask.LastDispatch?.ResultCommit : ownerTask.LastVerification?.ReviewedCommit;
                var log = ownerTask.LastVerification?.StandardOutputPath;
                var answer = $"ROLE_REPORT_EVIDENCE owner={owner} task={ownerTask.Id.Value} " +
                    $"criterion={request.PlannerIdentity!.CriterionIndex} candidate={Available(commit)} " +
                    $"landing_candidate={landingCandidateSha} worker_result_log={Available(log)}";
                if (owner == "developer") answer += $" tests={tests} gate={gate}";
                if (owner == "reviewer") answer += " verdict=pass";
                request.Complete(answer, clock.UtcNow, origin: HumanInputAnswerOrigin.Worker,
                    briefVersion: goal.AuthoritativeBrief.Version);
                append(goal, request.TaskId, ProgressKind.HumanInputReceived, answer);
            }
            return skipped;
        }
    }

    private static string? DeveloperSkipReason(Goal goal, HumanInputRequest request, TaskSpec? developer,
        Func<string, string?> passedGateSourceForClass, out string? tests, out string? gate)
    {
        tests = gate = null;
        var version = goal.AuthoritativeRefinedSpecVersion;
        var identity = request.PlannerIdentity!;
        var index = identity.CriterionIndex - 1;
        var criterion = version is not null && version.BriefVersion == identity.BriefVersion &&
            index >= 0 && index < version.Spec.AcceptanceCriteria.Count ? version.Spec.AcceptanceCriteria[index] : null;
        var requiredField = criterion is null ? null : DeveloperReportEvidence.FindRequiredField(criterion);
        if (requiredField is null) return "not-a-report-criterion";
        if (developer?.Status != WorkTaskStatus.Completed) return "owner-round-incomplete";
        if (!DeveloperReportEvidence.TryReadReport(developer, out var report)) return "developer-report-missing";
        var names = DeveloperDeferredTestClassNames.Parse(report).Order(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return "developer-report-missing";
        var required = DeveloperDeferredTestClassNames.Parse(requiredField);
        if (required.Count > 0 && !new HashSet<string>(required, StringComparer.Ordinal).SetEquals(names))
            return "developer-report-differs";
        var sources = names.Select(name => (Name: name, Source: passedGateSourceForClass(name))).ToArray();
        var missing = sources.Where(item => item.Source is null).Select(item => item.Name).ToArray();
        if (missing.Length > 0) return $"class-not-in-passed-gate:{string.Join(",", missing)}";
        tests = report;
        gate = string.Join(",", sources.Select(item => $"{item.Name}@{item.Source}"));
        return null;
    }

    private static string? RoundSkipReason(Goal goal, TaskSpec? task, AgentRole role)
    {
        if (task?.Status != WorkTaskStatus.Completed || task.LastVerification is not { } verification ||
            !WorkerResultBlockers.TryFindPassVerdict(verification))
            return "owner-round-incomplete";
        if (role == AgentRole.Reviewer && (!WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockers) ||
            blockers != WorkerResultBlockers.BlockersStatus.None))
            return "owner-round-incomplete";
        var candidate = Latest(goal, AgentRole.Developer)?.LastDispatch?.ResultCommit;
        return !string.IsNullOrWhiteSpace(candidate) && !string.IsNullOrWhiteSpace(verification.ReviewedCommit) &&
            string.Equals(verification.ReviewedCommit.Trim(), candidate, StringComparison.OrdinalIgnoreCase)
                ? null : "owner-candidate-differs";
    }

    private static TaskSpec? Latest(Goal goal, AgentRole role) => goal.Tasks
        .Where(task => task.RequiredRole == role && task.LastDispatch is not null)
        .MaxBy(task => task.LastDispatch!.DispatchedAt);

    private static string Available(string? value) => string.IsNullOrWhiteSpace(value) ? "unavailable" : value;
}
