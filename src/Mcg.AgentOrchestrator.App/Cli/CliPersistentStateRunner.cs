using System.Text.Json;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliPersistentStateRunner
{
    public static bool ExecuteCommand(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null,
        IGoalAcceptanceVerifier? acceptanceVerifier = null)
    {
        if (IsModelOutcomesScorecard(args))
        {
            var records = stateRepository.BuildModelOutcomeScorecardAsync().GetAwaiter().GetResult();
            ConsoleViews.PrintModelOutcomeScorecard(records);
            return false;
        }

        if (IsMetadataOnlyListing(args))
        {
            var summaries = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            ConsoleViews.PrintGoals(summaries);
            return false;
        }

        if (IsGoalSubscriptionCommand(args))
        {
            return ExecuteCommandWithoutTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        // Long-running conductor loops persist per tick and must NOT run inside the single wrapping
        // state transaction: that transaction only commits when the command returns, so a watch loop
        // (which may never return) never persists its dispatches, and a killed loop rolls back every
        // dispatch it started — the goal then re-dispatches the same stage forever and can't advance.
        if (IsConductLoop(args))
        {
            return ExecuteConductLoopOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        if (IsAcceptanceCommand(args))
        {
            return ExecuteAcceptanceOutsideTransaction(
                args,
                stateRepository,
                workspace,
                ref agents,
                providers,
                ref workerProfiles,
                ref currentGoal,
                channel,
                acceptanceVerifier);
        }

        if (IsProcessRefreshCommand(args))
        {
            return ExecuteProcessRefreshOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal);
        }

        if (IsBacklogIntakeCommand(args))
        {
            return ExecuteBacklogIntakeOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        if (args.Count > 0 && !ShouldRunInStateTransaction(args[0]))
        {
            return ExecuteCommandWithoutTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        var nextAgents = agents;
        var nextWorkerProfiles = workerProfiles;
        var currentGoalId = currentGoal?.Id.Value;
        Goal? nextCurrentGoal = currentGoal;

        var changed = stateRepository.TransactAsync(
                (kernel, _) =>
                {
                    var commandAgents = nextAgents;
                    var commandProfiles = nextWorkerProfiles;
                    var commandGoal = ResolveCurrentGoal(kernel, currentGoalId);
                    var shouldSave = CliCommandDispatcher.ExecuteCommand(
                        args,
                        kernel,
                        workspace,
                        ref commandAgents,
                        providers,
                        ref commandProfiles,
                        ref commandGoal,
                        channel,
                        () => stateRepository.LoadAsync().GetAwaiter().GetResult());

                    nextAgents = commandAgents;
                    nextWorkerProfiles = commandProfiles;
                    nextCurrentGoal = commandGoal;
                    return Task.FromResult((shouldSave, shouldSave));
                })
            .GetAwaiter()
            .GetResult();

        agents = nextAgents;
        workerProfiles = nextWorkerProfiles;
        currentGoal = nextCurrentGoal;
        return changed;
    }

    // Read-only goal listing: served from indexed metadata (no whole-store hydration, no save, no
    // auto-reconcile sweep). Only the bare `goals` command qualifies — any extra args fall through
    // to the normal transactional path so future `goals <filter>` forms keep working.
    internal static bool IsMetadataOnlyListing(IReadOnlyList<string> args)
    {
        return args.Count == 1 && args[0].Equals("goals", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsGoalSubscriptionCommand(IReadOnlyList<string> args)
    {
        return args.Count > 1 &&
            args[0].Equals("goals", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("subscribe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsModelOutcomesScorecard(IReadOnlyList<string> args)
    {
        return args.Count == 1 && args[0].Equals("model-outcomes", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SkipsKernelState(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return false;

        return args[0].ToLowerInvariant() switch
        {
            _ when CliCommandHelp.IsCommandSpecificHelp(args) => true,
            "operator-listen" or "operator-channel" => true,
            // Backlog commands operate solely on the independent BacklogStore, never the orchestrator
            // kernel/state.db. Running them with an empty kernel — no state load, no write lock, no
            // process sweep — keeps them fully concurrent with a running conductor instead of
            // contending on the per-tick write transaction. A future backlog command that DOES touch
            // the kernel must NOT be listed here.
            "backlog-list" or "backlog-add" or "backlog-show" or "backlog-close" or
            "backlog-reopen" or "backlog-view" or
            "firewall-setup" or "stable-slot-dotnet" or
            "project" => true,
            _ => false,
        };
    }

    private static bool ShouldRunInStateTransaction(string command)
    {
        return command.ToLowerInvariant() switch
        {
            "dashboard" or
            "serve-dashboard" or
            "hosted-dashboard" or
            "simple-hosted-dashboard" or
            "open-dashboard" or
            "monitor-goal" or
            "operator-channel" => false,
            _ => true
        };
    }

    // A conduct command running the batch loop (--loop) or single-goal continuous mode (--watch).
    internal static bool IsConductLoop(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !args[0].Equals("conduct", StringComparison.OrdinalIgnoreCase))
            return false;

        return args.Any(a =>
            a.Equals("--loop", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--watch", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsAcceptanceCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return false;
        }

        return args[0].Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("accept", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("acceptance-queue", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsProcessRefreshCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return false;
        }

        return args[0].Equals("reconcile", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("refresh-dispatch", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("refresh-dispatches", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsBacklogIntakeCommand(IReadOnlyList<string> args)
    {
        return args.Count > 0 && args[0].Equals("backlog-intake", StringComparison.OrdinalIgnoreCase);
    }

    // Runs a conductor loop outside the single wrapping state transaction, committing each tick's
    // progress via an independent SaveAsync (passed to the loop as PersistCheckpoint). This makes a
    // started dispatch durable the moment its tick completes — so a stopped/killed/long-running loop
    // never loses dispatch records, and reconcile can recognize a finished worker instead of
    // re-dispatching it. Reconcile stays owned by the loop/explicit refresh commands instead of being
    // an implicit side effect of every state-mutating command.
    private static bool ExecuteConductLoopOutsideTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        var kernel = LoadConductLoopKernel(stateRepository);
        var sweep = TerminalGoalSweep.Run(kernel, workspace.ExecutionDirectory, ResolveConductWatchGoalId(args, kernel, currentGoal));
        ConsoleViews.PrintTerminalGoalSweep(sweep, includeBlockers: false);
        GoalWorktreeOrphanSweepScheduler.SweepIfDue(workspace.ExecutionDirectory, kernel);
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);

        void Persist(AgentOrchestratorKernel checkpoint) =>
            stateRepository.SaveAsync(checkpoint).GetAwaiter().GetResult();

        void PersistGoal(AgentOrchestratorKernel checkpoint, GoalId changedGoalId)
        {
            var snap = checkpoint.ExportSnapshot().Goals.FirstOrDefault(g => g.Id == changedGoalId.Value);
            if (snap is null) return;
            stateRepository.TransactGoalAsync(
                changedGoalId,
                (_, ct) => Task.FromResult((true, (GoalSnapshot?)snap, true)),
                CancellationToken.None).GetAwaiter().GetResult();
        }

        if (sweep.Changed)
        {
            Persist(kernel);
        }

        var shouldSave = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            channel,
            () => LoadConductLoopKernel(stateRepository),
            Persist,
            persistGoalKernel: PersistGoal);

        // Final checkpoint so the loop's terminal state is durable even if the last tick made no progress.
        Persist(kernel);
        return shouldSave;
    }

    internal static AgentOrchestratorKernel LoadConductLoopKernel(
        ITransactionalOrchestratorStateRepository stateRepository)
    {
        var summaries = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult();
        var eligibleIds = summaries
            .Select(summary => new GoalId(summary.Id))
            .ToArray();
        var kernel = stateRepository.LoadGoalsAsync(eligibleIds).GetAwaiter().GetResult();
        kernel.MarkKnownCompletedDependencyGoals(kernel.Goals
            .Where(goal => goal.Status == GoalStatus.Completed)
            .Select(goal => goal.Id)
            .ToArray());

        var loadedIds = eligibleIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        var missingDependencyIds = kernel.Goals
            .SelectMany(goal => goal.DependsOn)
            .Select(id => id.Value)
            .Where(id => !loadedIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missingDependencyIds.Length == 0)
        {
            return kernel;
        }

        var missingDependencySet = missingDependencyIds.ToHashSet(StringComparer.Ordinal);
        var completedDependencyIds = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(summary => missingDependencySet.Contains(summary.Id) && IsConductLoopTerminalStatus(summary.Status))
            .Select(summary => new GoalId(summary.Id))
            .ToArray();
        kernel.MarkKnownCompletedDependencyGoals(completedDependencyIds);
        return kernel;
    }

    private static bool IsConductLoopTerminalStatus(string status) =>
        status.Equals(GoalStatus.Completed.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals("CleanedUp", StringComparison.OrdinalIgnoreCase);

    private static bool ExecuteCommandWithoutTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);
        var shouldSave = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            channel,
            () => stateRepository.LoadAsync().GetAwaiter().GetResult());

        if (shouldSave)
        {
            stateRepository.SaveAsync(kernel).GetAwaiter().GetResult();
        }

        return shouldSave;
    }

    private static bool ExecuteBacklogIntakeOutsideTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);

        void Persist(AgentOrchestratorKernel checkpoint) =>
            stateRepository.SaveAsync(checkpoint).GetAwaiter().GetResult();

        var shouldSave = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            channel,
            () => stateRepository.LoadAsync().GetAwaiter().GetResult(),
            Persist);

        if (shouldSave)
        {
            Persist(kernel);
        }

        return shouldSave;
    }

    private static bool ExecuteProcessRefreshOutsideTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);

        var candidates = CaptureRunningProcessIdentities(kernel);
        var command = args[0].ToLowerInvariant();
        var runner = new BackgroundDispatchRunner();

        switch (command)
        {
            case "reconcile":
                var reconciled = runner.SweepExitedProcesses(kernel);
                GoalWorktreeOrphanSweepScheduler.SweepIfDue(workspace.ExecutionDirectory, kernel);
                Console.WriteLine($"Reconciled dispatches: {reconciled}");
                break;

            case "refresh-dispatch":
                var refreshTarget = ResolveDispatchCommandTask(args, kernel, currentGoal, "refresh-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number>");
                currentGoal = refreshTarget.Goal;
                runner.RefreshLatestProcess(kernel, refreshTarget.Goal.Id, refreshTarget.Task.Id);
                ConsoleViews.PrintTask(refreshTarget.Goal, refreshTarget.Task);
                break;

            case "refresh-dispatches":
                var refreshGoal = ResolveDispatchCommandGoal(args, kernel, currentGoal, "refresh-dispatches [goal-prefix|--goal <goal-prefix>]");
                currentGoal = refreshGoal;
                var refreshed = GoalManagementCommandService.RefreshDispatches(kernel, refreshGoal);
                ConsoleViews.PrintProcessBatchResult(refreshGoal, refreshed);
                break;

            default:
                throw new ArgumentException($"Unsupported process refresh command: {args[0]}");
        }

        var results = CaptureRefreshResults(kernel, candidates);
        if (results.Count == 0)
        {
            return false;
        }

        var applied = stateRepository.TransactAsync(
                (transactionKernel, _) =>
                {
                    var appliedCount = ApplyRefreshResults(transactionKernel, results);
                    return Task.FromResult((appliedCount > 0, appliedCount));
                })
            .GetAwaiter()
            .GetResult();

        currentGoal = ResolveCurrentGoal(stateRepository.LoadAsync().GetAwaiter().GetResult(), currentGoal?.Id.Value);
        return applied > 0;
    }

    private static bool ExecuteAcceptanceOutsideTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null,
        IGoalAcceptanceVerifier? acceptanceVerifier = null)
    {
        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);

        AcceptanceMergeCommitResult Finalize(AcceptanceMergeCommitRequest request)
        {
            return stateRepository.TransactAsync(
                    (transactionKernel, _) =>
                    {
                        var transactionGoal = transactionKernel.Goals.FirstOrDefault(goal => goal.Id == request.GoalId)
                            ?? throw new InvalidOperationException($"Goal '{request.GoalId.Value}' no longer exists; retry acceptance.");
                        if (transactionGoal.Status != GoalStatus.Completed)
                        {
                            throw new InvalidOperationException(
                                $"Goal '{request.GoalId.Value[..8]}' changed during acceptance verification; retry acceptance.");
                        }

                        var currentFingerprint = BuildGoalFingerprint(transactionKernel, request.GoalId);
                        if (!string.Equals(currentFingerprint, request.ExpectedGoalFingerprint, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Goal '{request.GoalId.Value[..8]}' state changed during acceptance verification; retry acceptance.");
                        }

                        var currentHead = ResolveWorktreeHead(workspace.ExecutionDirectory, request.GoalId);
                        if (!string.Equals(currentHead, request.TestedWorktreeHead, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Goal '{request.GoalId.Value[..8]}' worktree changed during acceptance verification; retry acceptance.");
                        }

                        var result = request.Merge();
                        return Task.FromResult((result.FastForwarded, result));
                    })
                .GetAwaiter()
                .GetResult();
        }

        var shouldSave = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            channel,
            () => stateRepository.LoadAsync().GetAwaiter().GetResult(),
            finalizeAcceptanceMerge: Finalize,
            acceptanceVerifier: acceptanceVerifier);

        return shouldSave;
    }

    private static string? ResolveWorktreeHead(string executionDirectory, GoalId goalId)
    {
        var worktreePath = GoalWorktrees.TryResolve(executionDirectory, goalId);
        if (worktreePath is null)
        {
            return null;
        }

        var head = GitCli.Run(worktreePath, "rev-parse", "HEAD");
        if (!head.Succeeded)
        {
            throw new InvalidOperationException($"Failed to resolve tested worktree HEAD: {head.Error}");
        }

        return head.Output.Trim();
    }

    private static IReadOnlyDictionary<(GoalId GoalId, TaskId TaskId), ProcessRefreshIdentity> CaptureRunningProcessIdentities(
        AgentOrchestratorKernel kernel)
    {
        return kernel.Goals
            .SelectMany(goal => goal.Tasks
                .Where(task => task.LastProcess is { IsRunning: true })
                .Select(task => new
                {
                    GoalId = goal.Id,
                    TaskId = task.Id,
                    Identity = ProcessRefreshIdentity.From(task.LastProcess!)
                }))
            .ToDictionary(item => (item.GoalId, item.TaskId), item => item.Identity);
    }

    private static IReadOnlyList<ProcessRefreshResult> CaptureRefreshResults(
        AgentOrchestratorKernel kernel,
        IReadOnlyDictionary<(GoalId GoalId, TaskId TaskId), ProcessRefreshIdentity> candidates)
    {
        var results = new List<ProcessRefreshResult>();
        foreach (var goal in kernel.Goals)
        {
            foreach (var task in goal.Tasks)
            {
                // Capture a candidate the sweep RECONCILED in place: the exited process keeps its
                // identity (pid/startedAt/exitCodePath) but is no longer running. Skip ones still
                // running (sweep changed nothing) or whose identity differs (a different process).
                if (!candidates.TryGetValue((goal.Id, task.Id), out var identity) ||
                    task.LastProcess is null ||
                    task.LastProcess.IsRunning ||
                    ProcessRefreshIdentity.From(task.LastProcess) != identity)
                {
                    continue;
                }

                results.Add(new ProcessRefreshResult(
                    goal.Id,
                    task.Id,
                    identity,
                    task.LastProcess,
                    task.LastVerification));
            }
        }

        return results;
    }

    private static int ApplyRefreshResults(
        AgentOrchestratorKernel transactionKernel,
        IReadOnlyList<ProcessRefreshResult> results)
    {
        var applied = 0;
        foreach (var result in results)
        {
            var currentTask = transactionKernel.GetTask(result.GoalId, result.TaskId);
            if (currentTask.Status != WorkTaskStatus.Running ||
                currentTask.LastProcess is not { IsRunning: true } currentProcess ||
                ProcessRefreshIdentity.From(currentProcess) != result.ExpectedIdentity)
            {
                continue;
            }

            transactionKernel.RecordTaskProcessRefreshed(
                result.GoalId,
                result.TaskId,
                result.Process,
                result.Verification);
            applied++;
        }

        return applied;
    }

    private static (Goal Goal, TaskSpec Task) ResolveDispatchCommandTask(
        IReadOnlyList<string> parts,
        AgentOrchestratorKernel kernel,
        Goal? currentGoal,
        string usage)
    {
        if (parts.Count < 2)
        {
            throw new ArgumentException($"Usage: {usage}");
        }

        string? goalPrefix = null;
        string taskNumber;
        if (parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Count < 4)
            {
                throw new ArgumentException($"Usage: {usage}");
            }

            goalPrefix = parts[2];
            taskNumber = parts[3];
        }
        else if (parts.Count > 2 && parts[2].Equals("--goal", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Count < 4)
            {
                throw new ArgumentException($"Usage: {usage}");
            }

            goalPrefix = parts[3];
            taskNumber = parts[1];
        }
        else if (parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal))
        {
            goalPrefix = parts[1];
            taskNumber = parts[2];
        }
        else
        {
            taskNumber = parts[1];
        }

        var goal = OrchestratorEntityResolver.ResolveGoal(kernel, currentGoal, goalPrefix);
        return (goal, OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, taskNumber));
    }

    private static Goal ResolveDispatchCommandGoal(
        IReadOnlyList<string> parts,
        AgentOrchestratorKernel kernel,
        Goal? currentGoal,
        string usage)
    {
        string? goalPrefix = null;
        if (parts.Count > 1)
        {
            if (parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Count < 3)
                {
                    throw new ArgumentException($"Usage: {usage}");
                }

                goalPrefix = parts[2];
            }
            else if (!parts[1].StartsWith("--", StringComparison.Ordinal))
            {
                goalPrefix = parts[1];
            }
        }

        return OrchestratorEntityResolver.ResolveGoal(kernel, currentGoal, goalPrefix);
    }

    private static GoalId? ResolveConductWatchGoalId(
        IReadOnlyList<string> args,
        AgentOrchestratorKernel kernel,
        Goal? currentGoal)
    {
        if (args.Count < 2 ||
            !args[0].Equals("conduct", StringComparison.OrdinalIgnoreCase) ||
            !args.Any(arg => arg.Equals("--watch", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(arg => arg.Equals("--loop", StringComparison.OrdinalIgnoreCase)) ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        return OrchestratorEntityResolver.ResolveGoal(kernel, currentGoal, args[1]).Id;
    }

    private sealed record ProcessRefreshIdentity(
        int ProcessId,
        string ExitCodePath,
        DateTimeOffset StartedAt)
    {
        public static ProcessRefreshIdentity From(TaskProcessRecord process) =>
            new(process.ProcessId, process.ExitCodePath, process.StartedAt);
    }

    private sealed record ProcessRefreshResult(
        GoalId GoalId,
        TaskId TaskId,
        ProcessRefreshIdentity ExpectedIdentity,
        TaskProcessRecord Process,
        TaskVerificationRecord? Verification);

    private static string BuildGoalFingerprint(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        var snapshot = kernel.ExportSnapshot().Goals.FirstOrDefault(goal => goal.Id == goalId.Value)
            ?? throw new InvalidOperationException($"Goal '{goalId.Value}' no longer exists; retry acceptance.");
        var landingRelevantState = new
        {
            Tasks = snapshot.Tasks
                .OrderBy(task => task.Id, StringComparer.Ordinal)
                .Select(task => new
                {
                    task.Id,
                    Role = task.RequiredRole,
                    task.Status
                })
        };

        return JsonSerializer.Serialize(landingRelevantState);
    }

    private static Goal? ResolveCurrentGoal(AgentOrchestratorKernel kernel, string? currentGoalId)
    {
        if (!string.IsNullOrWhiteSpace(currentGoalId))
        {
            var match = kernel.Goals.FirstOrDefault(goal => goal.Id.Value.Equals(currentGoalId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return OrchestratorEntityResolver.GetLatestGoal(kernel);
    }
}
