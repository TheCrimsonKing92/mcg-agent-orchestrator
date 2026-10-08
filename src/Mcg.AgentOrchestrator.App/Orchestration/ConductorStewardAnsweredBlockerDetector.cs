using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The kernel owns clarification state; this detector only reads it for the current candidate.
internal sealed class ConductorStewardAnsweredBlockerDetector(
    Func<Goal, IReadOnlyList<HumanInputRequest>> requests)
{
    internal IReadOnlyList<ConductorStewardTrigger> Detect(Goal goal,
        Func<ConductorStewardTriggerKind, string, string, bool>? needsInspection = null)
    {
        if (goal.IsTerminal) return [];
        var developer = goal.Tasks.LastOrDefault(task => task.RequiredRole == AgentRole.Developer);
        if (developer is null || developer.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Failed))
            return [];
        var sha = developer.LastDispatch?.ResultCommit ?? developer.LastDispatch?.BaseCommit;
        const ConductorStewardTriggerKind kind = ConductorStewardTriggerKind.ReviewerOrTesterBlockerWithAnswer;
        if (string.IsNullOrWhiteSpace(sha) ||
            !(needsInspection?.Invoke(kind, developer.Id.Value, sha) ?? true)) return [];

        var failedTasks = AgentOrchestratorKernel.BuildVerificationWorklist(goal).Items
            .Where(item => item.GateStatus == VerificationGateStatus.FailedVerification)
            .Select(item => item.TaskId).ToHashSet();
        var answers = requests(goal);
        var eligible = new List<ConductorStewardTrigger>();
        foreach (var task in goal.Tasks)
        {
            if (task.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester) ||
                task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Failed) ||
                !failedTasks.Contains(task.Id) || task.LastVerification is not { } verification)
                continue;
            var blockerSha = task.LastDispatch?.BaseCommit ?? task.LastDispatch?.ResultCommit;
            if (!string.Equals(sha, blockerSha, StringComparison.OrdinalIgnoreCase)) continue;
            string blocker;
            var hasBlocker = task.RequiredRole == AgentRole.Reviewer
                ? WorkerResultBlockers.TryFindHardFailureBlocker(verification, out blocker)
                : WorkerResultBlockers.TryFindTesterWorkerResultBlocker(task, verification, out blocker);
            if (!hasBlocker) continue;

            var request = answers.Where(request => request.GoalId == goal.Id && request.TaskId == task.Id &&
                    request.Kind == HumanWaitKind.SpecClarification && request.IsCompleted &&
                    !request.WasDismissed && request.SupersededByRequestId is null &&
                    !request.IsSyntheticParkedHumanWaitCompletion && !string.IsNullOrWhiteSpace(request.Answer) &&
                    request.AuthoritativeAnswer?.AnsweredAt > verification.CompletedAt)
                .MaxBy(request => request.AuthoritativeAnswer!.AnsweredAt);
            if (request is null) continue;
            var output = verification.AuthoritativeStandardOutput ?? verification.StandardOutput;
            var start = output.LastIndexOf("WORKER_RESULT:", StringComparison.Ordinal);
            if (start < 0) continue;
            var end = output.IndexOf("END_WORKER_RESULT", start, StringComparison.Ordinal);
            var workerResult = end < 0 ? output[start..] : output[start..(end + "END_WORKER_RESULT".Length)];
            eligible.Add(new ConductorStewardTrigger(goal.Id.Value, developer.Id.Value, sha, kind,
                request.AuthoritativeAnswer!.AnsweredAt,
                $"{task.RequiredRole} task {task.Id.Value} blocker: {blocker}\nClarification {request.Id.Value} answer:\n{request.Answer}",
                workerResult,
                [$"blocker-task={task.Id.Value}", $"blocker-role={task.RequiredRole}", $"blocker={blocker}",
                    $"clarification-request={request.Id.Value}", $"clarification-answer={request.Answer}", $"candidate-sha={sha}"],
                goal.RefinedSpec?.AcceptanceCriteria.ToArray() ?? []));
        }
        // One route identity targets the last Developer, even if both downstream roles are blocked.
        var latest = eligible.MaxBy(trigger => trigger.OccurredAt);
        return latest is null ? [] : [latest];
    }
}
