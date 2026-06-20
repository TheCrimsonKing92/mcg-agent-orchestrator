using System.Text.Json;
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
        IOperatorChannel? channel = null)
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
            return ExecuteAcceptanceOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
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
                    var sweptCount = new BackgroundDispatchRunner().SweepExitedProcesses(kernel);

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
                    var anySave = shouldSave || sweptCount > 0;
                    return Task.FromResult((anySave, anySave));
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
            "operator-listen" or "operator-channel" => true,
            // Backlog commands operate solely on the independent BacklogStore, never the orchestrator
            // kernel/state.db. Running them with an empty kernel — no state load, no write lock, no
            // process sweep — keeps them fully concurrent with a running conductor instead of
            // contending on the per-tick write transaction. A future backlog command that DOES touch
            // the kernel must NOT be listed here.
            "backlog-list" or "backlog-add" or "backlog-show" or "backlog-close" or
            "backlog-reopen" or "backlog-import" or "backlog-view" or
            "firewall-setup" or "stable-slot-dotnet" => true,
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
        return args.Count > 0 &&
            (args[0].Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("accept", StringComparison.OrdinalIgnoreCase));
    }

    // Runs a conductor loop outside the single wrapping state transaction, committing each tick's
    // progress via an independent SaveAsync (passed to the loop as PersistCheckpoint). This makes a
    // started dispatch durable the moment its tick completes — so a stopped/killed/long-running loop
    // never loses dispatch records, and reconcile can recognize a finished worker instead of
    // re-dispatching it. A pre-loop sweep and a final save mirror the transactional path's bookkeeping.
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
        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        new BackgroundDispatchRunner().SweepExitedProcesses(kernel);
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

        // Final checkpoint so the loop's terminal state is durable even if the last tick made no progress.
        Persist(kernel);
        return shouldSave;
    }

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

    private static bool ExecuteAcceptanceOutsideTransaction(
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
            finalizeAcceptanceMerge: Finalize);

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

    private static string BuildGoalFingerprint(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        var snapshot = kernel.ExportSnapshot().Goals.FirstOrDefault(goal => goal.Id == goalId.Value)
            ?? throw new InvalidOperationException($"Goal '{goalId.Value}' no longer exists; retry acceptance.");
        return JsonSerializer.Serialize(snapshot);
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
