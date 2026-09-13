using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static void HandleLifecycleGoal(CliExecutionContext context, IReadOnlyList<string> parts, bool simple)
{
    var commandName = simple ? "lifecycle-simple-goal" : "lifecycle-goal";
    var policy = ResolveCliAutonomyPolicy(parts);
    Console.WriteLine($"Autonomy policy: {policy.Name}");
    policy.ThrowIfDisallowed(AutonomyAction.DispatchStart, commandName);
    CliArgumentParser.RequirePartCount(
        parts,
        2,
        $"{commandName} <objective> --confirm-batch-start [{SubscriptionPromptCostGuard.CliConfirmationFlag}]");
    EnsureCliConfirmation(
        parts,
        "--confirm-batch-start",
        $"{commandName} requires --confirm-batch-start because it starts worker processes.");
    EnsureCliConfirmation(
        parts,
        SubscriptionPromptCostGuard.CliConfirmationFlag,
        $"{commandName} requires {SubscriptionPromptCostGuard.CliConfirmationFlag} because it can start large paid subscription prompts.");

    var objective = parts[1].Trim();
    if (string.IsNullOrWhiteSpace(objective))
    {
        throw new ArgumentException(
            $"Usage: {commandName} <objective> --confirm-batch-start [{SubscriptionPromptCostGuard.CliConfirmationFlag}]");
    }

    var objectivePlan = BuildGoalObjectivePlan(context, objective, simple);
    GoalObjectivePlanner.ThrowIfBlocked(objectivePlan);
    ConsoleViews.PrintGoalObjectivePlan(objectivePlan);
    var sourceBacklogLink = ResolveSourceBacklogItemLink(context, parts, objective);
    PrintClosedSourceBacklogWarning(sourceBacklogLink);

    var existingGoalId = GoalOperationJournal.TryFindLifecycleGoal(context.Workspace.ExecutionDirectory, commandName, objective);
    context.CurrentGoal = existingGoalId is not null &&
        context.Kernel.Goals.FirstOrDefault(goal => goal.Id == existingGoalId) is { } existingGoal
        ? existingGoal
        : simple
            ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, objective, context.Workspace, context.Providers, context.EventWriter)
            : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, objective, context.Workspace, context.Providers, context.EventWriter);
    var goal = context.CurrentGoal;
    ApplySourceBacklogItemLink(context, goal, sourceBacklogLink);
    var goalPrefix = goal.Id.Value[..8];
    Console.WriteLine($"Lifecycle goal: {goal.Id.Value}");
    if (existingGoalId is not null && existingGoalId == goal.Id)
    {
        Console.WriteLine(simple ? "Stage simple-goal: reused existing idempotent goal." : "Stage goal: reused existing idempotent goal.");
        var journal = GoalOperationJournal.Read(context.Workspace.ExecutionDirectory, goal.Id);
        if (goal.Status == GoalStatus.Completed &&
            context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is null &&
            journal.LatestByOperation.Any(entry => entry.Operation == "workspace:remove" && entry.Status == GoalOperationStatus.Completed))
        {
            Console.WriteLine("Lifecycle goal already completed and workspace cleanup is recorded.");
            return;
        }
    }
    else
    {
        GoalOperationJournal.RecordLifecycleGoal(context.Workspace.ExecutionDirectory, goal, commandName, objective);
        GoalOperationJournal.Completed(
            context.Workspace.ExecutionDirectory,
            goal,
            simple ? "lifecycle-simple-goal:create" : "lifecycle-goal:create",
            "Goal created and activated.");
        Console.WriteLine(simple ? "Stage simple-goal: created and activated." : "Stage goal: created and activated.");
    }

    var branch = context.Worktrees.BranchName(goal.Id);
    string workspacePath;
    using (AcquireGoalEvidenceMutationLease(context, goal, "workspace:lifecycle-create"))
    {
        GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "workspace:create", $"branch {branch}");
        workspacePath = context.Worktrees.Ensure(context.Workspace.ExecutionDirectory, goal.Id);
        GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "workspace:create", workspacePath);
    }
    Console.WriteLine($"Stage workspace create: {workspacePath} (branch {branch})");
    EnsureGoalReadinessAllowsStart(context, goal, HasCliConfirmation(parts, "--confirm-readiness-risk"));
    EnsureLifecycleParallelGateAllowsStart(context, goal);

    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "run-goal", "Starting subscription-driven goal loop.");
    RecordPolicyAllowed(context, goal, policy, AutonomyAction.DispatchStart, commandName);
    var runGoalResult = RunGoal(context, goal, allowLargePaidSubscriptionStart: true);
    Console.WriteLine("Stage run-goal:");
    ConsoleViews.PrintRunGoalResult(goal, runGoalResult);
    if (goal.Status != GoalStatus.Verified)
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "run-goal", runGoalResult.StopReason);
        var next = BuildLifecycleRunGoalNextCommand(goalPrefix, runGoalResult);
        Console.WriteLine($"Stage run-goal: stopped. Next: {next}");
        if (runGoalResult.Failure is { } runGoalFailure)
        {
            context.FailAfterCommit(runGoalFailure.Reason);
            return;
        }

        throw new InvalidOperationException($"{commandName} stopped after run-goal. Next: {next}");
    }

    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "run-goal", "Goal reached Verified status.");
    if (!TryEnsurePolicyAllows(context, goal, policy, AutonomyAction.Acceptance, $"{commandName} acceptance", out var acceptancePolicyError))
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "acceptance", acceptancePolicyError);
        var next = $"acceptance {goalPrefix} --autonomy {AutonomyPolicy.SupervisedAuto.Name}";
        Console.WriteLine($"Stage acceptance: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped before acceptance. Next: {next}");
    }

    Console.WriteLine("Stage acceptance:");
    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "acceptance", "Running acceptance evidence and merge.");
    if (!RunAcceptanceWorkspaceMerge(context))
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance did not pass or merge was blocked.");
        var next = $"acceptance {goalPrefix}";
        Console.WriteLine($"Stage acceptance: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped after acceptance. Next: {next}");
    }

    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance passed and merge completed.");
    JournalAutoCloseSourceBacklogItem(context, goal);
    Console.WriteLine("Stage workspace cleanup:");
    context.Kernel.CompleteGoal(goal.Id, "Lifecycle command completed goal after acceptance merge; cleanup deferred to conductor sweep.");
    RecordDeferredGoalCleanup(context, goal, "remove:lifecycle-deferred", commandName);
}

private static RunGoalService.RunGoalResult RunGoal(
    CliExecutionContext context,
    Goal goal,
    bool allowLargePaidSubscriptionStart)
{
    if (context.RunGoalOverride is not null)
    {
        return context.RunGoalOverride(goal).GetAwaiter().GetResult();
    }

    return RunGoalService.RunAsync(
        context.Kernel,
        context.Agents,
        context.WorkerProfiles,
        context.Workspace,
        goal,
        allowLargePaidSubscriptionStart,
        pollInterval: context.RunGoalPollInterval,
        sleep: context.RunGoalSleep,
        providers: context.Providers)
        .GetAwaiter().GetResult();
}

private static void HandleAcceptanceQueue(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var policy = ResolveCliAutonomyPolicy(parts);
    var apply = HasCliConfirmation(parts, "--apply");
    var plan = AcceptanceQueuePlanner.Build(context.Kernel, context.Workspace.ExecutionDirectory, policy);
    ConsoleViews.PrintAcceptanceQueuePlan(plan);
    if (!apply)
    {
        return;
    }

    EnsureCliConfirmation(
        parts,
        "--confirm-acceptance-queue",
        "acceptance-queue --apply requires --confirm-acceptance-queue because it can merge branches and remove workspaces.");

    if (plan.ReadyCount == 0)
    {
        Console.WriteLine("Acceptance queue apply: no ready goals.");
        return;
    }

    foreach (var item in plan.Items.Where(item => item.Disposition == AcceptanceQueueDisposition.Ready))
    {
        var goal = context.Kernel.Goals.First(goal => goal.Id == item.GoalId);
        context.CurrentGoal = goal;
        var goalPrefix = item.GoalPrefix;
        Console.WriteLine($"Acceptance queue goal {goalPrefix}:");

        EnsurePolicyAllows(context, goal, policy, AutonomyAction.Acceptance, "acceptance queue merge");
        GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance queue running evidence and merge.");
        if (!RunAcceptanceWorkspaceMerge(context))
        {
            GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance queue merge blocked.");
            throw new InvalidOperationException($"acceptance-queue stopped at {goalPrefix}: acceptance did not pass or merge was blocked.");
        }

        GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance queue merge completed.");
        context.Kernel.CompleteGoal(goal.Id, "Acceptance queue completed goal after merge; cleanup deferred to conductor sweep.");
        RecordDeferredGoalCleanup(context, goal, "remove:acceptance-queue-deferred", "acceptance-queue");
        context.PersistCheckpoint(context.Kernel);
    }
}

private static bool HandleGoalDrain(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var policy = ResolveCliAutonomyPolicy(parts);
    var drainPolicy = GoalDrainPolicyStore.LoadOrDefault(context.Workspace);
    var apply = HasCliConfirmation(parts, "--apply");
    var costConfirmed = HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag);
    var plan = GoalDrainPlanner.Build(
        context.Kernel,
        context.Agents,
        context.WorkerProfiles,
        context.Workspace,
        policy,
        apply,
        costConfirmed,
        drainPolicy);
    if (!apply)
    {
        ConsoleViews.PrintGoalDrainPlan(plan);
        return false;
    }

    EnsureCliConfirmation(
        parts,
        "--confirm-goal-drain",
        "drain-goals --apply requires --confirm-goal-drain because it can start workers and mutate task state.");
    EnsureCliConfirmation(
        parts,
        "--confirm-batch-start",
        "drain-goals --apply requires --confirm-batch-start because it can start worker processes.");
    policy.ThrowIfDisallowed(AutonomyAction.DispatchStart, "drain-goals");

    var applied = new List<string>();
    var startFailures = new List<string>();
    foreach (var goal in context.Kernel.Goals.ToArray())
    {
        var supervisor = GoalSupervisor.ApplySafe(context.Kernel, goal, context.Agents, context.Workspace, policy);
        applied.AddRange(supervisor.AppliedActions.Select(action => $"{goal.Id.Value[..8]} {action}"));
    }

    var crossGoalPlan = CrossGoalSubscriptionStartPlanner.Build(
        context.Kernel,
        context.Agents,
        context.WorkerProfiles,
        costConfirmed);
    var startableGoalIds = plan.Items
        .Where(item => item.Stage == "subscription-start" && item.CanApply)
        .Select(item => item.GoalPrefix)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var candidate in crossGoalPlan.Candidates.Where(candidate => startableGoalIds.Contains(candidate.GoalPrefix)))
    {
        var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, null, candidate.GoalId);
        EnsureGoalReadinessAllowsStart(context, goal, HasCliConfirmation(parts, "--confirm-readiness-risk"));
        RecordPolicyAllowed(context, goal, policy, AutonomyAction.DispatchStart, "drain-goals");
        SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
            SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
                goal,
                context.Agents,
                context.WorkerProfiles,
                task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(context.Kernel, goal, task, context.Agents)),
            costConfirmed);
        var result = GoalManagementCommandService.StartSubscriptionReadyTasks(
            context.Kernel,
            context.Workspace,
            goal,
            context.Agents,
            context.WorkerProfiles,
            context.Providers);
        var failures = result.Processes.StartFailures ?? [];
        applied.Add($"{goal.Id.Value[..8]} start-subscription-ready dispatches={result.Dispatches.Count} processes={result.Processes.Tasks.Count} startFailures={failures.Count} readyBlocked={result.BlockedDiagnostics.Count}");
        startFailures.AddRange(failures.Select(failure =>
            $"goal={goal.Id.Value[..8]} task={failure.TaskId.Value[..8]} {failure.Reason}"));
    }

    var updated = GoalDrainPlanner.Build(
        context.Kernel,
        context.Agents,
        context.WorkerProfiles,
        context.Workspace,
        policy,
        apply,
        costConfirmed,
        drainPolicy);
    ConsoleViews.PrintGoalDrainPlan(updated, applied);
    if (startFailures.Count > 0)
    {
        context.FailAfterCommit(string.Join(Environment.NewLine, startFailures));
    }

    return applied.Count > 0;
}

private static string BuildLifecycleRunGoalNextCommand(string goalPrefix, RunGoalService.RunGoalResult result)
{
    if (result.BlockingAction?.SuggestedCommand is { Length: > 0 } command)
    {
        return command;
    }

    var next = $"run-goal {goalPrefix} --confirm-batch-start";
    return result.StopReason.Contains(SubscriptionPromptCostGuard.CliConfirmationFlag, StringComparison.OrdinalIgnoreCase)
        ? $"{next} {SubscriptionPromptCostGuard.CliConfirmationFlag}"
        : next;
}

internal static void EnsureGoalReadinessAllowsStart(CliExecutionContext context, Goal goal, bool confirmed)
{
    var sweep = TerminalGoalSweep.Run(
        context.Kernel,
        context.Workspace.ExecutionDirectory,
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
        context.Worktrees.TryResolve);
    if (!readiness.AllowsStart(confirmed))
    {
        ConsoleViews.PrintGoalReadinessPreflight(readiness);
    }

    GoalReadinessPreflight.ThrowIfStartBlocked(readiness, confirmed);
}

private static void EnsureLifecycleParallelGateAllowsStart(CliExecutionContext context, Goal goal)
{
    var plan = CrossGoalSubscriptionStartPlanner.Build(
        context.Kernel,
        context.Agents,
        context.WorkerProfiles,
        costRiskConfirmed: true);
    var decision = plan.ParallelPlan.Decisions.FirstOrDefault(decision =>
        decision.IntentId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase));
    if (decision is null)
    {
        return;
    }

    ConsoleViews.PrintCrossGoalSubscriptionStartPlan(plan);
    if (decision.Disposition == ParallelExecutionDisposition.Concurrent && decision.BatchNumber == 1)
    {
        return;
    }

    var reasons = string.Join("; ", decision.Reasons);
    throw new InvalidOperationException(
        $"Lifecycle start blocked by cross-goal parallel safety gate for goal {goal.Id.Value[..8]}: {decision.Disposition} batch={decision.BatchNumber?.ToString() ?? "none"} ({reasons}). Run cross-goal-start-plan and wait for batch 1 compatibility before lifecycle start.");
}
}
