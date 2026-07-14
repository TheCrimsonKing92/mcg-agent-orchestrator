namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    public GoalVerificationGate BuildVerificationGate(GoalId goalId)
    {
        var goal = GetGoal(goalId);
        var gates = goal.Tasks.Select(BuildTaskVerificationGate).ToList();

        return new GoalVerificationGate(
            goal.Id,
            goal.Objective,
            goal.Status,
            gates.All(gate => gate.GateStatus == VerificationGateStatus.Passed),
            gates);
    }

    public GoalVerificationWorklist BuildVerificationWorklist(GoalId goalId)
    {
        var gate = BuildVerificationGate(goalId);
        var items = gate.Tasks
            .Where(task => task.GateStatus != VerificationGateStatus.Passed)
            .Select(task => new TaskVerificationWorkItem(
                task.TaskId,
                task.Role,
                task.Description,
                task.TaskStatus,
                task.GateStatus,
                task.Message,
                BuildVerificationSuggestedAction(task)))
            .ToList();

        return new GoalVerificationWorklist(
            gate.GoalId,
            gate.Objective,
            gate.Status,
            gate.IsSatisfied,
            items.Count,
            items);
    }

    public GoalAcceptanceSummary BuildGoalAcceptanceSummary(GoalId goalId)
    {
        var goal = GetGoal(goalId);
        var gate = BuildVerificationGate(goalId);
        var pendingInput = GetPendingHumanInput(goalId);
        var blockers = new List<GoalAcceptanceBlocker>();

        foreach (var request in pendingInput)
        {
            blockers.Add(new GoalAcceptanceBlocker(
                GoalAcceptanceBlockerKind.PendingHumanInput,
                request.TaskId,
                request.Id,
                request.Question,
                "Answer the pending human input request."));
        }

        foreach (var taskGate in gate.Tasks.Where(task => task.GateStatus != VerificationGateStatus.Passed))
        {
            blockers.Add(new GoalAcceptanceBlocker(
                BuildAcceptanceBlockerKind(taskGate.GateStatus),
                taskGate.TaskId,
                null,
                taskGate.Message,
                BuildVerificationSuggestedAction(taskGate)));
        }

        if (goal.LatestAcceptanceFailure is { } failure)
        {
            var failedChecks = string.Join(", ", failure.FailedChecks);
            var candidate = FormatAcceptanceCandidate(failure);
            blockers.Add(new GoalAcceptanceBlocker(
                GoalAcceptanceBlockerKind.AcceptanceFailed,
                null,
                null,
                $"Latest acceptance failed{candidate} at {failure.OccurredAt:u}: {failedChecks}.",
                $"Rerun acceptance for goal {goal.Id.Value[..8]} after resolving the blocker."));
        }

        return new GoalAcceptanceSummary(
            gate.GoalId,
            gate.Objective,
            gate.Status,
            gate.IsSatisfied && pendingInput.Count == 0 && blockers.Count == 0,
            gate.Tasks.Count,
            gate.Tasks.Count(task => task.GateStatus == VerificationGateStatus.Passed),
            gate.Tasks.Count(task => task.GateStatus != VerificationGateStatus.Passed),
            pendingInput.Count,
            blockers,
            []);
    }

    public GoalHumanInputWorklist BuildHumanInputWorklist(GoalId goalId)
    {
        var goal = GetGoal(goalId);
        var items = GetPendingHumanInput(goalId)
            .Select(request => BuildHumanInputWorkItem(goal, request))
            .ToList();

        return new GoalHumanInputWorklist(
            goal.Id,
            goal.Objective,
            goal.Status,
            items.Count,
            items);
    }

    private static string FormatAcceptanceCandidate(AcceptanceFailureSummary failure)
    {
        if (string.IsNullOrWhiteSpace(failure.BranchHeadSha) && string.IsNullOrWhiteSpace(failure.MainHeadSha))
        {
            return string.Empty;
        }

        return $" for candidate branch={FormatShortSha(failure.BranchHeadSha)} main={FormatShortSha(failure.MainHeadSha)}";
    }

    private static string FormatShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha)
            ? "unknown"
            : sha.Trim()[..Math.Min(12, sha.Trim().Length)];
}
