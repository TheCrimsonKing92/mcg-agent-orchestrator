using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A declared brief is authoritative; otherwise the newest resolvable Planner plan is final.
internal static class NegativeControlRevertSetResolver
{
    public static IReadOnlyList<string>? Resolve(Goal goal)
    {
        var brief = NegativeControlRevertDeclaration.Parse(goal.Objective);
        if (brief.Declared)
            return brief.Rejection is null ? brief.Paths : null;

        foreach (var task in goal.Tasks.Reverse().Where(task =>
                     task.RequiredRole == AgentRole.Planner && task.Status == WorkTaskStatus.Completed))
        {
            foreach (var verification in task.VerificationHistory.Reverse())
            {
                try
                {
                    if (!WorkerArtifactWriter.TryResolveDurablePlannerPlan(verification, out var plan, out _))
                        continue;

                    var declaration = NegativeControlRevertDeclaration.Parse(plan);
                    return declaration is { Declared: true, Rejection: null } ? declaration.Paths : null;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return null;
    }
}
