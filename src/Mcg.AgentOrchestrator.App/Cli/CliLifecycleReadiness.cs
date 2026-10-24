using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliLifecycleReadiness
{
    internal static void EnsureBeforeCreation(CliExecutionContext context, Goal goal, bool confirmed)
    {
        var readiness = GoalReadinessPreflight.Build(goal, context.Agents,
            context.Workspace.ExecutionDirectory, context.WorkerProfiles,
            context.Worktrees.TryResolve, context.Kernel.Goals);
        // Workspace creation is the next owned step. Validate every other blocker before
        // committing a new goal or publishing epic membership. The full workspace check
        // still runs after Ensure, before native admission.
        var findings = readiness.Findings.Where(finding => finding.Kind != "workspace-missing").ToArray();
        readiness = readiness with
        {
            Findings = findings,
            HasHardBlockers = findings.Any(finding => finding is
                { Severity: GoalReadinessSeverity.Blocker, CanOverride: false })
        };
        if (!readiness.AllowsStart(confirmed))
            ConsoleViews.PrintGoalReadinessPreflight(readiness);
        GoalReadinessPreflight.ThrowIfStartBlocked(readiness, confirmed);
    }
}
