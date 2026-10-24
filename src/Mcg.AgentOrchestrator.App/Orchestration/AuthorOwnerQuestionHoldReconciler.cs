using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AuthorOwnerQuestionHoldReconciler
{
    internal static void Reconcile(AgentOrchestratorKernel kernel, ICollaborationItemStore collaboration,
        ConductEventLogWriter conduct, string? onlyGoalId, ISet<GoalId> changed)
    {
        foreach (var goal in kernel.Goals.ToArray())
        {
            if ((onlyGoalId is not null && goal.Id.Value != onlyGoalId) ||
                goal.IsTerminal || goal.Status == GoalStatus.Parked) continue;

            var hold = goal.CurrentHold;
            if (AuthorOwnerQuestionHold.TryParse(hold) is not { } question) continue;

            var resolved = question.TargetKind switch
            {
                nameof(OperatorAnswerTargetKind.Clarification) => collaboration.ListAsync(goal.Id.Value)
                    .GetAwaiter().GetResult().Any(item => item.Id == question.TargetId &&
                        CollaborationItemLifecycle.IsTerminal(item.Status)),
                nameof(OperatorAnswerTargetKind.HumanInput) => kernel.HumanInputRequests.Any(request =>
                    request.GoalId == goal.Id && request.Id.Value == question.TargetId && request.IsCompleted),
                _ => false
            };
            if (!resolved) continue;

            kernel.ClearGoalHold(goal.Id);
            changed.Add(goal.Id);
            conduct.Append("owner-question-hold-reconciled", goal.Id.Value,
                $"item={question.TargetKind}:{question.TargetId} target=resolved " +
                $"hold_started={hold!.StartedAt.ToString("O", CultureInfo.InvariantCulture)}");
        }
    }
}
