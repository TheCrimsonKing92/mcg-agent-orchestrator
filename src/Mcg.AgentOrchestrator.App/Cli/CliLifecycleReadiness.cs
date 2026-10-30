using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliLifecycleReadiness
{
    internal static void EnsureAllowsStart(CliExecutionContext context, Goal goal, bool confirmed)
    {
        var sweep = TerminalGoalSweep.Run(
            context.Kernel,
            context.Workspace.ExecutionDirectory, context.Workspace.IntegrationBranch,
            goal.Id,
            cleanupHooks: context.CleanupContext.Hooks,
            orchestratorDirectory: context.Workspace.OrchestratorDirectory);
        ConsoleViews.PrintTerminalGoalSweep(sweep);
        TerminalGoalSweepAttention.Surface(context.Kernel, sweep, context.Workspace.OrchestratorDirectory, goal.Id);
        goal = context.Kernel.GetGoal(goal.Id);
        if (context.CurrentGoal?.Id == goal.Id)
        {
            context.CurrentGoal = goal;
        }

        var readiness = GoalReadinessPreflight.Build(
            goal,
            context.Agents,
            context.Workspace.ExecutionDirectory,
            context.WorkerProfiles,
            context.Worktrees.TryResolve,
            context.Kernel.Goals,
            projectHomeDirectory: context.Workspace.ProjectHomeDirectoryOrNull);
        if (!readiness.AllowsStart(confirmed))
        {
            GoalReadinessPreflightTextView.PrintGoalReadinessPreflight(readiness);
        }

        GoalReadinessPreflight.ThrowIfStartBlocked(readiness, confirmed);
    }

    internal static void EnsureBeforeCreation(CliExecutionContext context, Goal goal, bool confirmed)
    {
        var readiness = GoalReadinessPreflight.Build(goal, context.Agents,
            context.Workspace.ExecutionDirectory, context.WorkerProfiles,
            context.Worktrees.TryResolve, context.Kernel.Goals,
            projectHomeDirectory: context.Workspace.ProjectHomeDirectoryOrNull);
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
            GoalReadinessPreflightTextView.PrintGoalReadinessPreflight(readiness);
        GoalReadinessPreflight.ThrowIfStartBlocked(readiness, confirmed);
    }
}
