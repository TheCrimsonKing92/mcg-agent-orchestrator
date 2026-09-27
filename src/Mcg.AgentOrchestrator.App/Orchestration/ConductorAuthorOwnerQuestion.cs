using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorAuthorOwnerQuestion(
    string GoalId, OperatorAnswerTargetKind TargetKind, string TargetId,
    string Question, string Recommendation, string Reason)
{
    internal string Format() =>
        $"author-owner-question item={TargetKind}:{TargetId} reason={Reason} " +
        $"question={Question} recommendation={Recommendation}";

    internal void Raise(AgentOrchestratorKernel kernel, Goal goal,
        IGoalLifecycleEventWriter lifecycle, ConductEventLogWriter conduct,
        DateTimeOffset now, ISet<GoalId> changed)
    {
        var text = Format();
        var state = goal.Status == GoalStatus.AcceptanceFailed
            ? GoalLifecycleState.AcceptanceFailed : GoalLifecycleState.Failed;
        var observation = kernel.ObserveGoalHold(goal.Id, "author-owner-question", text, now,
            TimeSpan.MaxValue, $"author-owner-question:{TargetKind}:{TargetId}");
        if (observation.StateChanged) changed.Add(goal.Id);
        conduct.Append("goal-escalation", goal.Id.Value, text);
        lifecycle.AppendGoalEscalated(goal.Id, state, goal.Status, text, "author-owner-question");
    }
}
