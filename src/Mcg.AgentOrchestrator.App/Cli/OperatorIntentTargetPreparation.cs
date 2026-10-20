using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class OperatorIntentTargetPreparation
{
    internal static bool PrepareOperatorIntentTarget(
        CliExecutionContext context, AgentOrchestratorKernel kernel, GoalId goalId,
        BackgroundDispatchRunner runner,
        IDictionary<string, ConductorGoalReloadObservation> observations,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null)
    {
        observations[goalId.Value] = new ConductorGoalReloadObservation.NotObserved();
        var loaded = context.LoadOperatorIntentTargets([goalId]);
        var snapshot = loaded.ExportSnapshot();
        var stored = snapshot.Goals.SingleOrDefault(goal => goal.Id == goalId.Value);
        if (stored is null)
        {
            observations[goalId.Value] = new ConductorGoalReloadObservation.Missing();
            return false;
        }
        if (stored.Status == GoalStatus.Completed || GoalStatusSemantics.ExcludesFromConductorWorkingSet(stored.Status))
        {
            kernel.ReplaceGoalStateWithSnapshot(stored,
                snapshot.HumanInputRequests.Where(request => request.GoalId == goalId.Value).ToArray());
            observations[goalId.Value] = new ConductorGoalReloadObservation.Terminal(stored.Status);
            return false;
        }

        kernel.ReplaceGoalStateWithSnapshot(stored,
            snapshot.HumanInputRequests.Where(request => request.GoalId == goalId.Value).ToArray());
        return PrepareLoadedOperatorIntentTarget(context, kernel, kernel.GetGoal(goalId), runner, gitRunner);
    }

    internal static bool PrepareLoadedOperatorIntentTarget(
        CliExecutionContext context, AgentOrchestratorKernel kernel, Goal target, BackgroundDispatchRunner runner,
        Func<string, IReadOnlyList<string>, GitCli.GitResult>? gitRunner = null)
    {
        new GoalDispatchOperations().RefreshDispatches(kernel, target, runner);
        runner.SweepExitedProcesses(kernel, target.Id);
        StaleDispatchProcessReconciler.Reconcile(kernel, runner, target, StaleDispatchProcessReconciler.AssignedOnly);

        // These recoveries belong to ordinary maintenance. Refreshing a target does not grant
        // permission to bypass interrupted-work or landing/evidence reconciliation.
        if (target.Tasks.Any(task =>
                task.PendingInterruptedWorkCheckpoint is not null ||
                (task.Status == WorkTaskStatus.Cancelled && task.LastProcess?.WasCancelledByConductor == true) ||
                (task.Status == WorkTaskStatus.Failed && task.LastProcess is { } process &&
                    (process.WasGracefullyDetachedByConductor || process.ExitArtifactOrigin == DispatchExitArtifactOrigin.Synthetic))))
            return false;

        if (target.Status == GoalStatus.Verified ||
            (!TerminalGoalSweep.IsTerminalSweepStatus(target.Status) && target.Tasks.All(task =>
                (task.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled or WorkTaskStatus.Failed) &&
                task.LastProcess is not { IsRunning: true })))
        {
            gitRunner ??= GoalGitFactIndex.GitRunner;
            GitCli.GitResult ReadGit(string directory, IReadOnlyList<string> args)
            {
                var result = gitRunner(directory, args);
                return result.DrainTimedOut || !result.ProcessStarted ? result with { ExitCode = -1 } : result;
            }
            var probe = ReadGit(context.Workspace.ExecutionDirectory, ["rev-parse", "--is-inside-work-tree"]);
            // An explicit non-repository result retains non-git command support. An unreadable
            // repository, failed launch or incomplete output is not evidence that landing is absent.
            if (probe.ExitCode != 0)
                return probe.ProcessStarted && !probe.DrainTimedOut && probe.ExitCode == 128 &&
                    probe.Error.StartsWith("fatal: not a git repository", StringComparison.OrdinalIgnoreCase);
            if (!probe.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
            var facts = GoalGitFactIndex.Build(context.Workspace.ExecutionDirectory, context.Workspace.IntegrationBranch,
                gitRunner: ReadGit);
            if (!facts.IsAvailable) return false;
            var branch = facts.BuildGoalBranchFacts(target);
            if (branch.BranchAlreadyLanded || branch.ContentState == GoalBranchContentState.Inconclusive)
                return false;
            if (target.Status == GoalStatus.Verified && !branch.HasGoalBranch &&
                target.OutstandingCriterionEvidenceObligations.Any(item =>
                    item.Owner == CriterionEvidenceOwner.Acceptance && item.State == CriterionEvidenceState.Pending &&
                    item.RequiredScope == CriterionEvidenceScopes.FullAcceptanceGate))
                return false;
            var evidence = GoalIntegrationEvidenceResolver.Build(
                context.Workspace.ExecutionDirectory, context.Workspace.IntegrationBranch, facts.MainSha, ReadGit);
            if (evidence.TryResolve(target.Id, out _) || !evidence.IsAvailable) return false;
        }
        return true;
    }
}
