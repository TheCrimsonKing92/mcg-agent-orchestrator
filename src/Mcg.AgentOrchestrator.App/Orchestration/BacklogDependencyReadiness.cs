using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BacklogReadiness(bool Eligible, bool HasLandedDependency, string Suffix);
internal sealed record BacklogPrerequisiteOwner(Goal? Goal, bool Ambiguous = false);

// Both CLI rendering and board selection use the same first-blocker and landing rules.
internal static class BacklogDependencyReadiness
{
    internal static BacklogReadiness Evaluate(BacklogItem item, Func<string, Goal?> findGoal,
        Func<string, BacklogItem?> findBacklog, Func<string, BacklogPrerequisiteOwner> findOwner,
        Func<Goal, string> landingState)
    {
        BacklogReadiness Blocked(string suffix) => new(false, false, suffix);
        foreach (var dependency in item.Dependencies)
        {
            Goal? goal;
            if (dependency.TargetKind == BacklogDependencyTargetKind.Goal)
                goal = findGoal(dependency.PrerequisiteId);
            else
            {
                var prerequisite = findBacklog(dependency.PrerequisiteId);
                var owner = prerequisite is null ? new BacklogPrerequisiteOwner(null) : findOwner(prerequisite.Id);
                goal = owner.Goal;
                if (owner.Ambiguous)
                    return Blocked($" [Blocked: reason=legacy-owner-ambiguous prerequisite {ShortId(dependency.PrerequisiteId)}]");
                if (goal is null)
                    return Blocked($" [Blocked: waiting on open prerequisite {ShortId(dependency.PrerequisiteId)}]");
            }
            if (goal is null)
                return Blocked($" [Blocked: missing prerequisite {ShortId(dependency.PrerequisiteId)}]");
            var state = landingState(goal);
            if (state is "Merged" or "Recorded" or "CleanedUp") continue;
            if (goal.Status is GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded)
                return Blocked($" [Blocked: dependency-terminal-without-landing {ShortId(goal.Id.Value)} state={goal.Status}]");
            return Blocked($" [Blocked: waiting on active prerequisite goal {ShortId(goal.Id.Value)}]");
        }
        return new(true, item.Dependencies.Count != 0,
            item.Dependencies.Count == 0 ? "" : " [Ready: dependencies landed]");
    }

    private static string ShortId(string id) => id.Length <= 8 ? id : id[..8];
}
