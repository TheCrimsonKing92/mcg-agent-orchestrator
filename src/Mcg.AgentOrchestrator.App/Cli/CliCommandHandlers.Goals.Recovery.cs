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
// Deterministic recovery: one `recover <goal> <note>` owns the multi-step "unblock" dances the
// operator used to memorize. It answers any open human-input requests (which `SubmitHumanInput`
// flips to Running), normalizes stuck/orphaned tasks to Failed so `RetryTask` accepts them, then
// retries them back to a dispatchable state — all with the single operator note. Genuinely running
// tasks (a live process) are left alone.
private static bool HandleRecover(CliExecutionContext context, IReadOnlyList<string> parts)
{
    if (parts.Count < 3)
    {
        throw new ArgumentException("Usage: recover <goal-prefix> <note> | recover <goal-prefix> --text-file <path>");
    }

    var policy = ResolveCliAutonomyPolicy(parts);
    context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
    var goal = context.CurrentGoal;
    var note = ResolveTextArgument(parts, inlineIndex: 2, "recover <goal-prefix> <note> | recover <goal-prefix> --text-file <path>", "--text-file");
    EnsurePolicyAllows(context, goal, policy, AutonomyAction.Retry, "recover");

    var sweep = TerminalGoalSweep.Run(
        context.Kernel,
        context.Workspace.ExecutionDirectory,
        goal.Id,
        orchestratorDirectory: context.Workspace.OrchestratorDirectory);
    ConsoleViews.PrintTerminalGoalSweep(sweep);
    TerminalGoalSweepAttention.Surface(context.Kernel, sweep, context.Workspace.OrchestratorDirectory, goal.Id);
    goal = context.Kernel.GetGoal(goal.Id);
    context.CurrentGoal = goal;
    var actions = 0;
    if (sweep.Changed)
    {
        actions++;
    }

    if (context.Kernel.NormalizeGoalLifecycleState(goal.Id, $"recover: normalized terminal goal with non-terminal task(s); {note}"))
    {
        Console.WriteLine("recover: normalized terminal goal with non-terminal task(s) to Active.");
        actions++;
    }

    var worktreeRecovery = GoalRecoveryPlanner.Build(
        context.Kernel,
        goal,
        context.Workspace.ExecutionDirectory,
        includeCleanupBackoff: false);
    var worktreeBlocksDiagnosis = worktreeRecovery.WorktreeDirty == true ||
        !string.IsNullOrWhiteSpace(worktreeRecovery.WorktreeStatusError);
    if (worktreeRecovery.WorktreeDirty == true)
    {
        var paths = worktreeRecovery.WorktreeDirtyPaths.Take(5).ToArray();
        var omitted = worktreeRecovery.WorktreeDirtyPaths.Count > paths.Length
            ? $", +{worktreeRecovery.WorktreeDirtyPaths.Count - paths.Length} more"
            : string.Empty;
        Console.WriteLine(
            $"recover: dirty worktree blocks Developer/Tester dispatch; paths=[{string.Join(", ", paths)}{omitted}]. " +
            "Inspect and preserve real work before cleaning; recover will not delete, stash, ignore, or commit these paths.");
    }
    else if (!string.IsNullOrWhiteSpace(worktreeRecovery.WorktreeStatusError))
    {
        Console.WriteLine(
            $"recover: worktree cleanliness unavailable ({worktreeRecovery.WorktreeStatusError}); " +
            "lifecycle/task desync recovery is unsafe until git status succeeds.");
    }

    foreach (var request in context.Kernel.GetPendingHumanInput(goal.Id).ToList())
    {
        context.Kernel.SubmitHumanInput(request.Id, note);
        Console.WriteLine($"recover: answered human-input request {request.Id.Value[..8]}.");
        actions++;
    }

    var alreadyReset = new HashSet<TaskId>();
    foreach (var task in goal.Tasks)
    {
        if (task.Status is WorkTaskStatus.Completed)
        {
            continue;
        }

        if (task.LastProcess is { CompletedAt: null } && task.LastVerification is null &&
            DispatchRecoveryView.Evaluate(goal, task) is { } recoveryDecision)
        {
            PrintRecoverDispatchRecovery(task, goal, recoveryDecision);
            if (recoveryDecision.Action is DispatchRecoveryAction.Hold or DispatchRecoveryAction.ClassifyBlocker)
            {
                continue;
            }

            var runner = new BackgroundDispatchRunner();
            var outcome = runner.ReconcileLatestProcess(context.Kernel, goal.Id, task.Id);
            runner.ApplyRefreshOutcomeAndWriteDiagnostics(context.Kernel, goal.Id, task.Id, outcome);
            goal = context.Kernel.GetGoal(goal.Id);
            context.CurrentGoal = goal;
            var refreshedTask = goal.Tasks.First(candidate => candidate.Id == task.Id);
            actions++;

            if (outcome.AutoRequeueDisposition is { ShouldRequeue: true })
            {
                Console.WriteLine($"recover: reset task {ConsoleViews.GetTaskDisplayNumber(goal, refreshedTask.Id)} to dispatchable.");
                alreadyReset.Add(refreshedTask.Id);
                actions++;
            }
            else if (recoveryDecision.Action == DispatchRecoveryAction.MarkStale &&
                outcome.Verification is { } refreshVerification &&
                DispatchRecoveryPolicy.IsStaleDispatchRetryVerification(refreshVerification))
            {
                context.Kernel.RetryTask(
                    goal.Id,
                    refreshedTask.Id,
                    note,
                    invalidateDownstream: !HasRunningDownstreamTask(goal, refreshedTask),
                    retryCause: RetryCause.EnvironmentApparatusFailure);
                Console.WriteLine($"recover: reset task {ConsoleViews.GetTaskDisplayNumber(goal, refreshedTask.Id)} to dispatchable.");
                alreadyReset.Add(refreshedTask.Id);
                actions++;
            }

            continue;
        }

        var stuck = task.Status is WorkTaskStatus.Failed or WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman or WorkTaskStatus.Cancelled ||
            task.LastVerification is { Succeeded: false } ||
            task.SubscriptionRetryAfter is not null;
        if (!stuck)
        {
            continue;
        }

        if (task.Status == WorkTaskStatus.Cancelled)
        {
            context.Kernel.RequeueInterruptedDispatch(goal.Id, task.Id, note);
            Console.WriteLine($"recover: requeued interrupted task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} to dispatchable.");
            alreadyReset.Add(task.Id);
            actions++;
            continue;
        }

        // RetryTask refuses Running/WaitingForHuman; normalize to Failed first (the dance's middle step).
        if (task.Status is WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman)
        {
            context.Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, note);
        }

        context.Kernel.RetryTask(
            goal.Id,
            task.Id,
            note,
            invalidateDownstream: !HasRunningDownstreamTask(goal, task),
            retryCause: RetryCause.Unknown);
        Console.WriteLine($"recover: reset task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} to dispatchable.");
        alreadyReset.Add(task.Id);
        actions++;
    }

    // Detect lifecycle/task desync: a task is Assigned with no dispatch evidence while all
    // earlier-stage tasks are Completed. This happens after flake-recovery when the conductor
    // retried the task (leaving it Assigned) but previously set the goal aside with
    // LifecycleEscalation. The 'recover' command never saw a stuck task so it printed
    // "nothing to recover", even though the goal was permanently blocked. Detect and report so
    // the operator knows to re-admit (restart the loop or run 'conduct <goal>').
    foreach (var task in goal.Tasks)
    {
        if (alreadyReset.Contains(task.Id) ||
            (worktreeBlocksDiagnosis && task.RequiredRole is (AgentRole.Developer or AgentRole.Tester)) ||
            task.Status != WorkTaskStatus.Assigned ||
            task.LastProcess is { IsRunning: true } ||
            task.LastDispatch is not null ||
            task.LastProcess is not null ||
            task.LastVerification is not null)
        {
            continue;
        }

        var hasIncompleteEarlierStage = goal.Tasks.Any(candidate =>
            GoalManagementCommandService.IsEarlierSdlcStageOf(candidate.RequiredRole, task.RequiredRole) &&
            candidate.Status != WorkTaskStatus.Completed);
        if (hasIncompleteEarlierStage)
        {
            continue;
        }

        context.Kernel.RetryTask(
            goal.Id,
            task.Id,
            $"recover: re-derived lifecycle state for {task.RequiredRole} task {task.Id.Value[..8]} (Assigned, dispatchable, earlier stages Completed); {note}",
            invalidateDownstream: !HasRunningDownstreamTask(goal, task),
            retryCause: RetryCause.EnvironmentApparatusFailure);
        Console.WriteLine($"recover: task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} {task.RequiredRole} is assigned and dispatchable but has no dispatch record; lifecycle/task desync detected, lifecycle state re-derived. Re-run 'conduct {goal.Id.Value[..8]}' or restart the conductor loop to unblock.");
        actions++;
    }

    goal = context.Kernel.GetGoal(goal.Id);
    var acceptanceInvalidation = AcceptanceAttemptRetryInvalidation.Apply(
        context.Kernel,
        goal,
        new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(context.Workspace.OrchestratorDirectory, "acceptance-gate-attempts"),
            context.Workspace.ExecutionDirectory),
        $"recover invalidated acceptance verification before redispatch; {note}");
    if (acceptanceInvalidation.Changed)
    {
        Console.WriteLine(
            $"recover: invalidated current acceptance attempt={acceptanceInvalidation.AttemptInvalidated.ToString().ToLowerInvariant()} and reopened goal={acceptanceInvalidation.GoalReopened.ToString().ToLowerInvariant()} before redispatch.");
        actions++;
    }

    if (actions == 0 && worktreeBlocksDiagnosis)
    {
        Console.WriteLine("recover: no automatic lifecycle recovery performed while worktree cleanliness blocks diagnosis.");
    }
    else if (actions == 0)
    {
        Console.WriteLine("recover: nothing to recover (no pending input, stuck tasks, or lifecycle/task desync).");
    }

    ConsoleViews.PrintGoal(goal);
    return actions > 0;
}

private static void PrintRecoverDispatchRecovery(TaskSpec task, Goal goal, DispatchRecoveryDecision decision)
{
    var blocker = string.IsNullOrWhiteSpace(decision.Blocker)
        ? string.Empty
        : $" blocker='{decision.Blocker}'";
    Console.WriteLine(
        $"recover: task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} recovery action='{decision.ActionName}' evidence='{decision.EvidencePath}' reason='{decision.Reason}'{blocker}.");
}

private static bool HasRunningDownstreamTask(Goal goal, TaskSpec task) =>
    goal.Tasks.Any(candidate =>
        GoalManagementCommandService.IsEarlierSdlcStageOf(task.RequiredRole, candidate.RequiredRole) &&
        candidate.LastProcess is { IsRunning: true });

// Deterministic verification from git ground truth: when a goal still has un-verified work tasks
// but the goal branch carries committed changes against main on a CLEAN worktree, record the
// verification from that evidence instead of requiring a manual `verify-manual`. The acceptance
// suite + evidence bundle remain the authoritative substance gates downstream (a failed suite or
// a generated/forbidden/no-relevant-change diff still blocks the merge), so this only removes the
// bookkeeping step, never the safety gate.
private static void AutoVerifyFromGitEvidence(CliExecutionContext context, Goal goal)
{
    if (goal.Status == GoalStatus.Completed)
    {
        return;
    }

    var executionDirectory = context.Workspace.ExecutionDirectory;
    var worktree = context.Worktrees.TryResolve(executionDirectory, goal.Id);
    if (worktree is null)
    {
        return;
    }

    // Tasks that failed only because a Low-IL worker could not self-commit under OS confinement
    // (a benign signature — the edits are real, not broken) are eligible for the same git-ground-truth
    // verification as un-run tasks. If their edits still sit uncommitted in the worktree, commit them
    // at Medium so the clean-worktree check below confirms real changes against main. The acceptance
    // suite always runs before any merge, so this only clears bookkeeping — it never lands unproven work.
    var sandboxBlockedIds = goal.Tasks
        .Where(t => t.Status == WorkTaskStatus.Failed &&
            t.LastVerification is { Succeeded: false } verification &&
            DispatchFailureClassifier.Classify(t, verification).Kind == DispatchOutcomeKind.SandboxCommitBlocked)
        .Select(t => t.Id)
        .ToHashSet();

    if (sandboxBlockedIds.Count > 0 && !context.Worktrees.IsWorktreeClean(executionDirectory, goal.Id))
    {
        TryCommitSandboxBlockedEdits(worktree, goal);
    }

    var pending = goal.Tasks
        .Where(t => t.Status is WorkTaskStatus.Assigned or WorkTaskStatus.Running ||
            sandboxBlockedIds.Contains(t.Id))
        .ToList();
    if (pending.Count == 0)
    {
        return;
    }

    if (!context.Worktrees.IsWorktreeClean(executionDirectory, goal.Id) ||
        !context.Worktrees.HasChangesAgainstMain(executionDirectory, goal.Id))
    {
        return;
    }

    var note =
        $"Auto-verified from git ground truth: committed changes on {context.Worktrees.BranchName(goal.Id)} " +
        "against main on a clean worktree. The acceptance suite and evidence bundle are the authoritative gates.";
    foreach (var task in pending)
    {
        context.Kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, note, worktree, DateTimeOffset.UtcNow));
        Console.WriteLine($"Auto-verified task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} from git ground truth.");
    }
}

// Commit a sandbox-blocked worker's uncommitted edits on the orchestrator's behalf (Medium
// integrity, so .git is writable). Plain `add -A` honours the worktree's .mcg-sandbox exclude.
private static void TryCommitSandboxBlockedEdits(string worktreePath, Goal goal)
{
    if (!GitCli.Run(worktreePath, "add", "-A").Succeeded)
    {
        return;
    }

    var commit = GitCli.Run(
        worktreePath,
        "commit",
        "-m",
        $"Orchestrator-committed sandbox-blocked worker edits for goal {goal.Id.Value}");
    if (commit.Succeeded)
    {
        Console.WriteLine(
            $"Committed sandbox-blocked worker edits for goal {goal.Id.Value[..8]} (worker could not self-commit under the sandbox).");
    }
}
}
