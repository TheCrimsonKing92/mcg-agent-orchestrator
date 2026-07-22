using System.Text.Json;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliPersistentStateRunner
{
    private sealed record GoalScopedTaskMutationResult(
        bool ShouldSave,
        IReadOnlyList<AgentDefinition> Agents,
        WorkerProfileCatalog WorkerProfiles,
        Goal? CurrentGoal,
        GoalSnapshot Snapshot,
        CliCommandHandlers.GoalScopedTaskMutationOutcome Outcome);

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

        if (SkipsKernelState(args))
        {
            var commandKernel = new AgentOrchestratorKernel();
            Goal? commandCurrentGoal = null;
            return CliCommandDispatcher.ExecuteCommand(
                args,
                commandKernel,
                workspace,
                ref agents,
                providers,
                ref workerProfiles,
                ref commandCurrentGoal,
                channel);
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

        if (IsSingleGoalConductCommand(args))
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
                acceptanceVerifier,
                persistOnlyCurrentGoal: true);
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

        if (IsGoalMarkLandedCommand(args))
        {
            return ExecuteGoalMarkLandedWithPromptBudget(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        if (IsSingleGoalSnapshotCommand(args))
        {
            return ExecuteSingleGoalCommandWithoutTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        if (IsProvenanceCommand(args))
        {
            return ExecuteProvenanceWithoutFullHydration(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        if (IsSingleGoalReportCommand(args))
        {
            return ExecuteSingleGoalCommandWithoutTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        if (IsGoalScopedTaskMutationCommand(args))
        {
            return ExecuteGoalScopedTaskMutationCommand(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        if (IsGoalLifecycleDispositionCommand(args))
        {
            return ExecuteCommandWithoutTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
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
            // These backlog commands operate solely on the independent BacklogStore, never the
            // orchestrator kernel/state.db. Running them with an empty kernel — no state load, no
            // write lock, no process sweep — keeps them fully concurrent with a running conductor
            // instead of contending on the per-tick write transaction. backlog-show intentionally
            // is not listed because it renders linked goals from kernel state.
            "backlog-list" or "backlog-add" or "backlog-update" or "backlog-annotate" or "backlog-close" or
            "backlog-supersede" or "backlog-unsupersede" or "backlog-link" or "backlog-reopen" or "backlog-view" or
            "cleanup-status" or
            "firewall-setup" or "repo-process-info" or "repo-process-stop" or "stable-slot-dotnet" or
            "gate-status" or
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
            "repo-process-info" or
            "repo-process-stop" or
            "monitor-goal" or
            "operator-channel" => false,
            _ => true
        };
    }

    internal static bool IsGoalScopedTaskMutationCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return false;
        }

        return args[0].ToLowerInvariant() switch
        {
            "progress" or
            "verify-manual" or
            "retry" or
            "verification-plan" or
            "note" => true,
            _ => false
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

    internal static bool IsSingleGoalConductCommand(IReadOnlyList<string> args)
    {
        return args.Count > 1 &&
            args[0].Equals("conduct", StringComparison.OrdinalIgnoreCase) &&
            !args[1].StartsWith("--", StringComparison.Ordinal) &&
            !IsConductLoop(args);
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

    internal static bool IsGoalMarkLandedCommand(IReadOnlyList<string> args)
    {
        return args.Count > 0 && args[0].Equals("goal-mark-landed", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSingleGoalSnapshotCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return false;

        return args[0].ToLowerInvariant() switch
        {
            "timeline" or "task-timeline" => true,
            "goal-timing" => args.Count > 1 && !args[1].Equals("--all", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    internal static bool IsProvenanceCommand(IReadOnlyList<string> args) =>
        args.Count == 1 && args[0].Equals("provenance", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSingleGoalReportCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return false;

        return args[0].ToLowerInvariant() switch
        {
            "status" or
            "monitor" or
            "readiness" or
            "goal-recovery" or
            "dogfood-eval" or
            "failure-triage" or
            "retention-plan" or
            "evidence" or
            "goal-changes" or
            "stages" or
            "gates" or
            "verify-needed" or
            "input-needed" or
            "goal-diagnostics" or
            "next" or
            "subscription-plan" or
            "supervisor" or
            "build-lease-cleanup" => true,
            _ => false
        };
    }

    internal static bool IsGoalLifecycleDispositionCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return false;
        }

        return args[0].Equals("park-goal", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("unpark-goal", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("abandon-goal", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ExecuteGoalMarkLandedWithPromptBudget(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        var currentGoalId = currentGoal?.Id.Value;
        GoalId? goalMarkLandedGoalId = currentGoal?.Id;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var slowStep = "state-load";
        using var cancellation = new CancellationTokenSource(CliCommandHandlers.GoalMarkLandedPromptTimeoutMilliseconds);

        try
        {
            var goalId = ResolveSingleGoalCommandGoalId(
                stateRepository,
                currentGoalId,
                GetOptionalArgument(args, "--confirm-goal-mark-landed", "--force"));
            var kernel = LoadSingleGoalKernel(stateRepository, goalId, cancellation.Token);
            currentGoal = ResolveCurrentGoal(kernel, goalId.Value);

            void Persist(AgentOrchestratorKernel checkpoint)
            {
                slowStep = "state-save-commit";
                PersistSingleGoalSnapshot(stateRepository, checkpoint, goalId, cancellation.Token);
                slowStep = "command-handler";
            }

            slowStep = "command-handler";
            var changed = CliCommandDispatcher.ExecuteCommand(
                    args,
                    kernel,
                    workspace,
                    ref agents,
                    providers,
                    ref workerProfiles,
                    ref currentGoal,
                    channel,
                    () => LoadSingleGoalKernel(stateRepository, goalId, cancellation.Token),
                    Persist,
                    goalMarkLandedElapsedMilliseconds: () => elapsed.ElapsedMilliseconds);

            goalMarkLandedGoalId = currentGoal?.Id;
            if (changed)
            {
                Persist(kernel);
            }

            return changed;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (TryReturnSuccessfulDeferredGoalMarkLanded(args, workspace, goalMarkLandedGoalId, slowStep, elapsed.ElapsedMilliseconds))
                return true;

            Console.Error.WriteLine($"goal-mark-landed slow substep: {slowStep} elapsedMs={elapsed.ElapsedMilliseconds}");
            throw new TimeoutException(
                $"goal-mark-landed cleanup exceeded {CliCommandHandlers.GoalMarkLandedPromptTimeoutMilliseconds}ms during substep '{slowStep}'.");
        }
        catch (Exception ex) when (
            IsGoalMarkLandedStateCommitStep(slowStep) &&
            IsGoalMarkLandedDeferredCommitFailure(ex) &&
            TryReturnSuccessfulDeferredGoalMarkLanded(args, workspace, goalMarkLandedGoalId, slowStep, elapsed.ElapsedMilliseconds, ex))
        {
            return true;
        }
    }

    private static bool TryReturnSuccessfulDeferredGoalMarkLanded(
        IReadOnlyList<string> args,
        OrchestratorWorkspace workspace,
        GoalId? goalId,
        string slowStep,
        long elapsedMilliseconds,
        Exception? exception = null)
    {
        if (!IsGoalMarkLandedCommand(args) || !IsGoalMarkLandedStateCommitStep(slowStep) || goalId is null)
            return false;

        if (!HasDurableLandedDeferredCleanupEvidence(workspace.ExecutionDirectory, goalId))
            return false;

        var detail = exception is null ? "commit cancelled after durable landed state" : exception.Message;
        Console.Error.WriteLine(
            $"warning: goal-mark-landed state commit failed after durable landed state and cleanup-needed were recorded; returning success. step={slowStep} elapsedMs={elapsedMilliseconds} detail={detail}");
        return true;
    }

    private static bool IsGoalMarkLandedStateCommitStep(string slowStep) =>
        slowStep.Equals("state-save-commit", StringComparison.OrdinalIgnoreCase);

    private static bool IsGoalMarkLandedDeferredCommitFailure(Exception exception) =>
        exception is TimeoutException or OperationCanceledException ||
        IsTransientSqliteLock(exception) ||
        (exception.InnerException is not null && IsGoalMarkLandedDeferredCommitFailure(exception.InnerException));

    private static bool IsTransientSqliteLock(Exception exception)
    {
        if (exception.GetType().FullName is not "Microsoft.Data.Sqlite.SqliteException")
            return false;

        var code = exception.GetType().GetProperty("SqliteErrorCode")?.GetValue(exception);
        return code is 5 or 6;
    }

    private static bool HasDurableLandedDeferredCleanupEvidence(string executionDirectory, GoalId goalId)
    {
        var journal = GoalOperationJournal.Read(executionDirectory, goalId);
        var hasLanded = journal.LatestByOperation.Any(entry =>
            entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase) &&
            entry.Status == GoalOperationStatus.Completed);
        var hasRecorded = journal.LatestByOperation.Any(entry =>
            entry.Operation.Equals("conductor:record", StringComparison.OrdinalIgnoreCase) &&
            entry.Status == GoalOperationStatus.Completed);
        var hasDeferredCleanup = journal.LatestByOperation.Any(IsDeferredGoalMarkLandedCleanupEvidence);
        return hasLanded && hasRecorded && hasDeferredCleanup;
    }

    private static bool IsDeferredGoalMarkLandedCleanupEvidence(GoalOperationJournalEntry entry) =>
        entry.Operation.Equals("conductor:cleanup", StringComparison.OrdinalIgnoreCase) &&
        entry.Status == GoalOperationStatus.Failed &&
        (entry.Detail?.Contains("Deferred cleanup after", StringComparison.OrdinalIgnoreCase) == true ||
         entry.Detail?.Contains("cleanup-needed", StringComparison.OrdinalIgnoreCase) == true);

    internal static string FormatTickMergeReceipt(GoalSnapshotSaveResult result) =>
        $"TICK_MERGE goal={result.GoalId[..Math.Min(8, result.GoalId.Length)]} disposition={result.Disposition.ToString().ToUpperInvariant()} {result.Message}";

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
        using var conductLoopLease = ConductorLoopLease.Acquire(workspace.OrchestratorDirectory);
        var kernel = LoadConductLoopKernel(stateRepository);
        var tickBaselines = kernel.ExportSnapshot().Goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
        TerminalGoalSweepResult? sweep = null;
        try
        {
            var watchGoalId = ResolveConductWatchGoalId(args, kernel, currentGoal, stateRepository);
            var sweepKernel = LoadConductLoopSweepKernel(stateRepository, kernel, workspace.ExecutionDirectory, watchGoalId);
            sweep = TerminalGoalSweep.Run(sweepKernel, workspace.ExecutionDirectory, watchGoalId);
            var metadataOnlyExcludedGoalCount = CountMetadataOnlyTerminalSweepExclusions(
                stateRepository,
                workspace.ExecutionDirectory,
                watchGoalId);
            if (metadataOnlyExcludedGoalCount > 0)
            {
                sweep = sweep with { ExcludedGoalCount = sweep.ExcludedGoalCount + metadataOnlyExcludedGoalCount };
            }

            ConsoleViews.PrintTerminalGoalSweep(sweep, includeBlockers: ConductLoopWillExitBeforeFirstTick(args, workspace.ExecutionDirectory));
            TerminalGoalSweepAttention.Surface(sweepKernel, sweep, workspace.OrchestratorDirectory, watchGoalId);
            if (sweep.Changed)
            {
                PersistSweepChanges(sweepKernel, stateRepository, sweep.Goals.Select(goal => goal.GoalId).ToArray());
                kernel = LoadConductLoopKernel(stateRepository);
                tickBaselines = kernel.ExportSnapshot().Goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
            }

            GoalWorktreeOrphanSweepScheduler.SweepIfDue(workspace.ExecutionDirectory, sweepKernel);
            RemoteGitMirror.TryStartBackgroundProcessing(sweepKernel, workspace.ExecutionDirectory, watchGoalId);
        }
        catch (Exception ex)
        {
            EmitPreLoopJanitorialFailure(workspace, ex);
        }

        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);

        void Persist(AgentOrchestratorKernel checkpoint) =>
            PersistGoals(checkpoint, checkpoint.Goals.Select(goal => goal.Id).ToArray());

        void PersistGoals(AgentOrchestratorKernel checkpoint, IReadOnlyCollection<GoalId> changedGoalIds)
        {
            if (changedGoalIds.Count == 0) return;

            var changed = changedGoalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            var requests = checkpoint.ExportSnapshot().Goals
                .Where(goal => changed.Contains(goal.Id))
                .Select(goal =>
                {
                    var baseline = tickBaselines.TryGetValue(goal.Id, out var known)
                        ? known
                        : goal;
                    return new GoalSnapshotSaveRequest(baseline, goal);
                })
                .ToArray();

            if (requests.Length == 0) return;

            var results = stateRepository.SaveGoalSnapshotsWithMergeAsync(requests, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var result in results)
            {
                if (result.PersistedSnapshot is not null)
                {
                    tickBaselines[result.GoalId] = result.PersistedSnapshot;
                }

                if (result.Disposition is GoalSnapshotSaveDisposition.Merged or GoalSnapshotSaveDisposition.Skipped)
                {
                    Console.WriteLine(FormatTickMergeReceipt(result));
                }
            }
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
            persistGoalKernel: PersistGoals,
            releaseConductLoopLease: conductLoopLease.Dispose);

        // Final checkpoint so the loop's terminal state is durable even if the last tick made no progress.
        Persist(kernel);
        return shouldSave;
    }

    private static void EmitPreLoopJanitorialFailure(OrchestratorWorkspace workspace, Exception ex)
    {
        var line = $"LOOP_JANITORIAL_FAILED tick=0 phase=pre-loop-sweep exception={ex.GetType().Name} message={SanitizeConductToken(ex.Message)}";
        Console.WriteLine(line);
        Console.Out.Flush();
        try
        {
            new ConductEventLogWriter(workspace.ConductEventsLogPath)
                .Append("loop-janitorial-failure", null, line);
        }
        catch
        {
            // Shared event streaming is advisory; stdout remains the primary conduct log.
        }
    }

    private static string SanitizeConductToken(string value) =>
        value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');

    internal static AgentOrchestratorKernel LoadConductLoopKernel(
        ITransactionalOrchestratorStateRepository stateRepository)
    {
        var summaries = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult();
        var terminalSummaries = summaries
            .Where(summary => IsConductLoopTerminalStatus(summary.Status))
            .ToArray();
        var hydratedIds = summaries
            .Where(summary =>
                !IsConductLoopTerminalStatus(summary.Status) &&
                !summary.Status.Equals(GoalStatus.Parked.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(summary => new GoalId(summary.Id))
            .ToArray();
        var kernel = stateRepository.LoadGoalsAsync(hydratedIds).GetAwaiter().GetResult();
        kernel.MarkKnownDependencyGoalStatuses(summaries.Select(summary =>
            new KeyValuePair<GoalId, string>(new GoalId(summary.Id), summary.Status)));
        kernel.MarkKnownCompletedDependencyGoals(terminalSummaries
            .Select(summary => new GoalId(summary.Id)));

        var loadedIds = hydratedIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
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
        var missingDependencySummaries = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(summary => missingDependencySet.Contains(summary.Id))
            .ToArray();
        kernel.MarkKnownDependencyGoalStatuses(missingDependencySummaries.Select(summary =>
            new KeyValuePair<GoalId, string>(new GoalId(summary.Id), summary.Status)));
        var completedDependencyIds = missingDependencySummaries
            .Where(summary => IsConductLoopTerminalStatus(summary.Status))
            .Select(summary => new GoalId(summary.Id))
            .ToArray();
        kernel.MarkKnownCompletedDependencyGoals(completedDependencyIds);
        return kernel;
    }

    private static AgentOrchestratorKernel LoadConductLoopSweepKernel(
        ITransactionalOrchestratorStateRepository stateRepository,
        AgentOrchestratorKernel workingSetKernel,
        string executionDirectory,
        GoalId? onlyGoalId)
    {
        var candidates = ResolveTerminalSweepCandidateIds(stateRepository, executionDirectory, onlyGoalId)
            .Where(id => !workingSetKernel.Goals.Any(goal => goal.Id == id))
            .ToArray();
        if (candidates.Length == 0)
        {
            return workingSetKernel;
        }

        var terminalKernel = stateRepository.LoadGoalsAsync(candidates).GetAwaiter().GetResult();
        return MergeKernels(workingSetKernel, terminalKernel.ExportSnapshot().Goals);
    }

    private static IReadOnlyList<GoalId> ResolveTerminalSweepCandidateIds(
        ITransactionalOrchestratorStateRepository stateRepository,
        string executionDirectory,
        GoalId? onlyGoalId)
    {
        if (onlyGoalId is not null)
        {
            return [onlyGoalId];
        }

        var summaries = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult();
        return ResolveTerminalSweepCandidateIds(summaries, executionDirectory, onlyGoalId);
    }

    private static IReadOnlyList<GoalId> ResolveTerminalSweepCandidateIds(
        IReadOnlyList<GoalSummary> summaries,
        string executionDirectory,
        GoalId? onlyGoalId)
    {
        if (onlyGoalId is not null)
        {
            return [onlyGoalId];
        }

        var gitFacts = GoalGitFactIndex.Build(executionDirectory);
        return summaries
            .Where(summary => IsConductLoopTerminalStatus(summary.Status))
            .Select(summary => new GoalId(summary.Id))
            .Where(id =>
                gitFacts.HasGoalBranch(GoalWorktrees.BranchName(id)) ||
                GoalWorktrees.TryResolve(executionDirectory, id) is not null ||
                GoalWorktrees.TryGetCleanupBackoff(executionDirectory, id) is not null)
            .Distinct()
            .ToArray();
    }

    private static int CountMetadataOnlyTerminalSweepExclusions(
        ITransactionalOrchestratorStateRepository stateRepository,
        string executionDirectory,
        GoalId? onlyGoalId)
    {
        if (onlyGoalId is not null)
        {
            return 0;
        }

        var summaries = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult();
        var hydratedSweepCandidateIds = ResolveTerminalSweepCandidateIds(summaries, executionDirectory, onlyGoalId)
            .Select(id => id.Value)
            .ToHashSet(StringComparer.Ordinal);
        return summaries
            .Where(summary => IsConductLoopTerminalStatus(summary.Status))
            .Select(summary => summary.Id)
            .Where(id => !hydratedSweepCandidateIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    private static void PersistSweepChanges(
        AgentOrchestratorKernel sweepKernel,
        ITransactionalOrchestratorStateRepository stateRepository,
        IReadOnlyCollection<GoalId> changedGoalIds)
    {
        if (changedGoalIds.Count == 0)
        {
            return;
        }

        var changed = changedGoalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        var snapshots = sweepKernel.ExportSnapshot().Goals
            .Where(goal => changed.Contains(goal.Id))
            .ToArray();
        if (snapshots.Length > 0)
        {
            stateRepository.SaveGoalSnapshotsAsync(snapshots, CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static AgentOrchestratorKernel MergeKernels(
        AgentOrchestratorKernel baseKernel,
        IReadOnlyList<GoalSnapshot> additionalGoals)
    {
        var snapshot = baseKernel.ExportSnapshot();
        var existing = snapshot.Goals.Select(goal => goal.Id).ToHashSet(StringComparer.Ordinal);
        var mergedGoals = snapshot.Goals
            .Concat(additionalGoals.Where(goal => existing.Add(goal.Id)))
            .ToArray();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = mergedGoals });
    }

    private static bool IsConductLoopTerminalStatus(string status) =>
        status.Equals(GoalStatus.Completed.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Cancelled.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Superseded.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Retired", StringComparison.OrdinalIgnoreCase) ||
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

    private static bool ExecuteSingleGoalCommandWithoutTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        var goalId = ResolveSingleGoalCommandGoalId(
            stateRepository,
            currentGoal?.Id.Value,
            ResolveSingleGoalCommandGoalPrefix(args));
        var kernel = LoadSingleGoalKernel(stateRepository, goalId);
        currentGoal = ResolveCurrentGoal(kernel, goalId.Value);

        void Persist(AgentOrchestratorKernel checkpoint) =>
            PersistSingleGoalSnapshot(stateRepository, checkpoint, goalId);

        var shouldSave = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            channel,
            () => LoadSingleGoalKernel(stateRepository, goalId),
            Persist);

        if (shouldSave)
        {
            Persist(kernel);
        }

        return shouldSave;
    }

    private static bool ExecuteGoalScopedTaskMutationCommand(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        var goalId = ResolveGoalScopedTaskMutationGoalId(stateRepository, currentGoal?.Id.Value, args);
        var hasInlineGoalPrefix = HasInlineGoalPrefixForGoalScopedTaskMutation(stateRepository, args);
        var preparedCommand = CliCommandHandlers.PrepareGoalScopedTaskMutationCommand(args, hasInlineGoalPrefix, workspace);
        var commandAgents = agents;
        var commandProfiles = workerProfiles;

        var result = stateRepository.TransactGoalAsync(
                $"cli:{args[0].ToLowerInvariant()}",
                goalId,
                (snapshot, cancellationToken) =>
                {
                    if (snapshot is null)
                    {
                        throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
                    }

                    var humanInputRequests = LoadGoalHumanInputSnapshots(stateRepository, goalId, cancellationToken);
                    var kernel = KernelFromGoalSnapshot(snapshot, humanInputRequests);
                    var transactionAgents = commandAgents;
                    var transactionProfiles = commandProfiles;
                    var transactionCurrentGoal = ResolveCurrentGoal(kernel, goalId.Value);
                    var context = new CliExecutionContext(
                        kernel,
                        workspace,
                        providers,
                        transactionAgents,
                        transactionProfiles,
                        transactionCurrentGoal,
                        channel);
                    var outcome = CliCommandHandlers.ExecuteGoalScopedTaskMutationWithoutRendering(preparedCommand, context);

                    transactionAgents = context.Agents;
                    transactionProfiles = context.WorkerProfiles;
                    transactionCurrentGoal = context.CurrentGoal;
                    var updatedSnapshot = outcome.ShouldSave ? ExportGoalSnapshot(kernel, goalId) : snapshot;
                    var transactionResult = new GoalScopedTaskMutationResult(
                        outcome.ShouldSave,
                        transactionAgents,
                        transactionProfiles,
                        transactionCurrentGoal,
                        updatedSnapshot,
                        outcome);
                    return Task.FromResult((outcome.ShouldSave, updatedSnapshot, transactionResult));
                })
            .GetAwaiter()
            .GetResult();

        agents = result.Agents;
        workerProfiles = result.WorkerProfiles;
        currentGoal = result.CurrentGoal;
        CliCommandHandlers.RenderGoalScopedTaskMutation(result.Outcome);
        return result.ShouldSave;
    }

    private static bool HasInlineGoalPrefixForGoalScopedTaskMutation(
        ITransactionalOrchestratorStateRepository stateRepository,
        IReadOnlyList<string> parts)
    {
        if (parts.Count <= 2 ||
            parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase) ||
            parts[2].StartsWith("--", StringComparison.Ordinal) ||
            (int.TryParse(parts[1], out _) && parts[1].Length < 8))
        {
            return false;
        }

        return stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Any(goal => goal.Id.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase));
    }

    private static bool ExecuteProvenanceWithoutFullHydration(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        var completedGoalIds = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(goal => goal.Status.Equals(GoalStatus.Completed.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(goal => new GoalId(goal.Id))
            .ToArray();
        var kernel = LoadGoalSnapshotsById(stateRepository, completedGoalIds);
        currentGoal = null;

        return CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            channel,
            () => LoadGoalSnapshotsById(stateRepository, completedGoalIds));
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
        var command = args[0].ToLowerInvariant();
        if (command.Equals("reconcile", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteGlobalProcessReconcile(args, stateRepository, workspace, ref currentGoal);
        }

        var goalId = ResolveSingleGoalCommandGoalId(stateRepository, currentGoal?.Id.Value, ResolveProcessRefreshGoalPrefix(args));
        var kernel = LoadSingleGoalKernel(stateRepository, goalId);
        currentGoal = ResolveCurrentGoal(kernel, goalId.Value);

        var candidates = CaptureRunningProcessIdentities(kernel);
        var runner = new BackgroundDispatchRunner();

        switch (command)
        {
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

        var transactionResult = stateRepository.TransactGoalAsync(
                goalId,
                (snapshot, _) =>
                {
                    if (snapshot is null)
                    {
                        throw new InvalidOperationException($"Goal '{goalId.Value}' no longer exists; retry refresh.");
                    }

                    var transactionKernel = KernelFromGoalSnapshot(snapshot);
                    var appliedCount = ApplyRefreshResults(transactionKernel, results);
                    GoalSnapshot? updatedSnapshot = appliedCount > 0 ? ExportGoalSnapshot(transactionKernel, goalId) : snapshot;
                    return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, (int Applied, GoalSnapshot Snapshot) Result)>(
                        (appliedCount > 0, updatedSnapshot, (appliedCount, updatedSnapshot!)));
                })
            .GetAwaiter()
            .GetResult();

        currentGoal = KernelFromGoalSnapshot(transactionResult.Snapshot).GetGoal(goalId);
        return transactionResult.Applied > 0;
    }

    private static bool ExecuteGlobalProcessReconcile(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref Goal? currentGoal)
    {
        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);

        var candidates = CaptureRunningProcessIdentities(kernel);
        var runner = new BackgroundDispatchRunner();
        var reconciled = runner.SweepExitedProcesses(kernel);
        GoalWorktreeOrphanSweepScheduler.SweepIfDue(workspace.ExecutionDirectory, kernel);
        RemoteGitMirror.TryStartBackgroundProcessing(kernel, workspace.ExecutionDirectory);
        Console.WriteLine($"Reconciled dispatches: {reconciled}");

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
        IGoalAcceptanceVerifier? acceptanceVerifier = null,
        bool persistOnlyCurrentGoal = false)
    {
        if (args.Count > 0 && args[0].Equals("acceptance-queue", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteAcceptanceQueueOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel, acceptanceVerifier);
        }

        var phaseTimings = new CliPhaseTimingRecorder("acceptance");
        var resolveStarted = System.Diagnostics.Stopwatch.StartNew();
        var (goalId, resolvedGoalCount) = ResolveAcceptanceGoalId(stateRepository, currentGoal?.Id.Value, ResolveAcceptanceGoalPrefix(args));
        resolveStarted.Stop();
        phaseTimings.Record(
            "startup-goal-resolve",
            resolveStarted.Elapsed,
            ("goal", goalId.Value[..8]),
            ("goalCount", resolvedGoalCount));

        var loadStarted = System.Diagnostics.Stopwatch.StartNew();
        var kernel = LoadSingleGoalKernel(stateRepository, goalId);
        loadStarted.Stop();
        phaseTimings.Record(
            "startup-load-target-goal",
            loadStarted.Elapsed,
            ("goal", goalId.Value[..8]),
            ("goalCount", 1));
        var initialGoalJson = JsonSerializer.Serialize(ExportGoalSnapshot(kernel, goalId));

        var reconcileStarted = System.Diagnostics.Stopwatch.StartNew();
        var targetSweep = TerminalGoalSweep.Run(kernel, workspace.ExecutionDirectory, goalId);
        reconcileStarted.Stop();
        ConsoleViews.PrintTerminalGoalSweep(targetSweep);
        TerminalGoalSweepAttention.Surface(kernel, targetSweep, workspace.OrchestratorDirectory, goalId);
        phaseTimings.Record(
            "reconcile-sweep",
            reconcileStarted.Elapsed,
            ("goal", goalId.Value[..8]),
            ("scope", "target"),
            ("mode", "target-scoped-fast-path"),
            ("goalsWalked", 1),
            ("repairs", targetSweep.Goals.Sum(goal => goal.Repairs.Count)),
            ("blockers", targetSweep.Goals.Sum(goal => goal.Blockers.Count)));

        currentGoal = ResolveCurrentGoal(kernel, goalId.Value);
        var updatedCurrentGoal = currentGoal;
        var acceptanceFinalStatePersisted = false;

        void Persist(AgentOrchestratorKernel checkpoint) =>
            PersistSingleGoalSnapshot(stateRepository, checkpoint, goalId);

        void PersistCurrentGoal(AgentOrchestratorKernel checkpoint, GoalId goalId)
        {
            var snapshot = checkpoint.ExportSnapshot().Goals.FirstOrDefault(goal => goal.Id == goalId.Value)
                ?? throw new InvalidOperationException($"Goal '{goalId.Value}' no longer exists; retry acceptance.");
            stateRepository.SaveGoalSnapshotsAsync([snapshot], CancellationToken.None).GetAwaiter().GetResult();
        }

        AcceptanceMergeCommitResult Finalize(AcceptanceMergeCommitRequest request)
        {
            var transactionResult = stateRepository.TransactGoalAsync(
                    request.GoalId,
                    (snapshot, _) =>
                    {
                        if (snapshot is null)
                        {
                            throw new InvalidOperationException($"Goal '{request.GoalId.Value}' no longer exists; retry acceptance.");
                        }

                        var transactionKernel = KernelFromGoalSnapshot(snapshot);
                        var transactionGoal = transactionKernel.Goals.FirstOrDefault(goal => goal.Id == request.GoalId)
                            ?? throw new InvalidOperationException($"Goal '{request.GoalId.Value}' no longer exists; retry acceptance.");
                        if (transactionGoal.Status != GoalStatus.Verified)
                        {
                            var guardedResult = GuardedAcceptanceFailure(
                                transactionKernel,
                                request.GoalId,
                                $"Goal '{request.GoalId.Value[..8]}' state changed during acceptance verification; retry acceptance.");
                            var guardedSnapshot = ExportGoalSnapshot(transactionKernel, request.GoalId);
                            return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, (AcceptanceMergeCommitResult Result, GoalSnapshot Snapshot) Result)>(
                                (true, guardedSnapshot, (guardedResult, guardedSnapshot)));
                        }

                        var currentFingerprint = BuildGoalFingerprint(transactionKernel, request.GoalId);
                        if (!string.Equals(currentFingerprint, request.ExpectedGoalFingerprint, StringComparison.Ordinal))
                        {
                            var guardedResult = GuardedAcceptanceFailure(
                                transactionKernel,
                                request.GoalId,
                                $"Goal '{request.GoalId.Value[..8]}' state changed during acceptance verification; retry acceptance.");
                            var guardedSnapshot = ExportGoalSnapshot(transactionKernel, request.GoalId);
                            return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, (AcceptanceMergeCommitResult Result, GoalSnapshot Snapshot) Result)>(
                                (true, guardedSnapshot, (guardedResult, guardedSnapshot)));
                        }

                        var currentHead = ResolveWorktreeHead(workspace.ExecutionDirectory, request.GoalId);
                        if (!string.Equals(currentHead, request.TestedWorktreeHead, StringComparison.Ordinal))
                        {
                            var guardedResult = GuardedAcceptanceFailure(
                                transactionKernel,
                                request.GoalId,
                                $"Goal '{request.GoalId.Value[..8]}' worktree changed during acceptance verification; retry acceptance.");
                            var guardedSnapshot = ExportGoalSnapshot(transactionKernel, request.GoalId);
                            return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, (AcceptanceMergeCommitResult Result, GoalSnapshot Snapshot) Result)>(
                                (true, guardedSnapshot, (guardedResult, guardedSnapshot)));
                        }

                        var result = request.Merge();
                        if (result.FastForwarded)
                        {
                            transactionKernel.ClearAcceptanceFailure(request.GoalId);
                            transactionKernel.CompleteGoal(request.GoalId, request.CompletionReason);
                        }
                        else if (result.Message is not null)
                        {
                            transactionKernel.RecordAcceptanceFailure(request.GoalId, ["merge"]);
                        }

                        var updatedSnapshot = ExportGoalSnapshot(transactionKernel, request.GoalId);
                        return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, (AcceptanceMergeCommitResult Result, GoalSnapshot Snapshot) Result)>(
                            (true, updatedSnapshot, (result, updatedSnapshot)));
                    })
                .GetAwaiter()
                .GetResult();
            acceptanceFinalStatePersisted = true;
            updatedCurrentGoal = KernelFromGoalSnapshot(transactionResult.Snapshot).GetGoal(request.GoalId);
            return transactionResult.Result;
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
            Persist,
            finalizeAcceptanceMerge: Finalize,
            acceptanceVerifier: acceptanceVerifier,
            phaseTimings: phaseTimings);

        currentGoal = updatedCurrentGoal;

        if (acceptanceFinalStatePersisted)
        {
            PersistIfTargetGoalChangedSinceLoad(kernel, goalId, initialGoalJson);
            return shouldSave;
        }

        if (shouldSave)
        {
            if (persistOnlyCurrentGoal && currentGoal is not null)
            {
                PersistCurrentGoal(kernel, currentGoal.Id);
            }
            else
            {
                Persist(kernel);
            }
        }
        else
        {
            PersistIfTargetGoalChangedSinceLoad(kernel, goalId, initialGoalJson);
        }

        return shouldSave;

        void PersistIfTargetGoalChangedSinceLoad(AgentOrchestratorKernel checkpoint, GoalId id, string initialJson)
        {
            var currentGoalJson = JsonSerializer.Serialize(ExportGoalSnapshot(checkpoint, id));
            if (!string.Equals(initialJson, currentGoalJson, StringComparison.Ordinal))
            {
                Persist(checkpoint);
            }
        }
    }

    private static bool ExecuteAcceptanceQueueOutsideTransaction(
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
            Persist,
            acceptanceVerifier: acceptanceVerifier);

        if (shouldSave)
        {
            Persist(kernel);
        }

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

    private static AcceptanceMergeCommitResult GuardedAcceptanceFailure(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        string reason)
    {
        kernel.RecordAcceptanceFailure(goalId, [reason]);
        return new AcceptanceMergeCommitResult(false, reason, GuardFailure: true);
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

    private static string? ResolveProcessRefreshGoalPrefix(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0)
        {
            return null;
        }

        if (parts[0].Equals("refresh-dispatches", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Count > 1 && parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
            {
                return parts.Count > 2 ? parts[2] : null;
            }

            return parts.Count > 1 && !parts[1].StartsWith("--", StringComparison.Ordinal) ? parts[1] : null;
        }

        if (!parts[0].Equals("refresh-dispatch", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (parts.Count > 2 && parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
        {
            return parts[2];
        }

        if (parts.Count > 3 && parts[2].Equals("--goal", StringComparison.OrdinalIgnoreCase))
        {
            return parts[3];
        }

        if (parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal))
        {
            return parts[1];
        }

        return null;
    }

    private static string? ResolveAcceptanceGoalPrefix(IReadOnlyList<string> parts) =>
        GetOptionalArgument(parts, "--skip-verify", "--keep-workspace", "--no-record");

    private static string? ResolveSingleGoalCommandGoalPrefix(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0)
        {
            return null;
        }

        if (parts[0].Equals("goal-timing", StringComparison.OrdinalIgnoreCase))
        {
            return parts.Count > 1 ? parts[1] : null;
        }

        if (parts[0].Equals("timeline", StringComparison.OrdinalIgnoreCase))
        {
            return parts.Count > 1 ? parts[1] : null;
        }

        if (parts[0].Equals("task-timeline", StringComparison.OrdinalIgnoreCase))
        {
            var goalFlag = GetFlagValue(parts, "--goal");
            if (!string.IsNullOrWhiteSpace(goalFlag))
            {
                return goalFlag;
            }

            if (parts.Count > 2 && !int.TryParse(parts[1], out _))
            {
                return parts[1];
            }
        }

        if (IsSingleGoalReportCommand(parts))
        {
            return parts[0].ToLowerInvariant() switch
            {
                "next" => GetOptionalArgument(parts, "--full"),
                "goal-changes" => GetOptionalArgument(parts, "--role", "--task", "--committed", "--working", "--all", "--flat", "--json"),
                "failure-triage" => GetOptionalArgument(parts, "--autonomy", "--policy"),
                "supervisor" => GetOptionalArgument(parts, "--apply-safe", "--autonomy", "--policy"),
                "build-lease-cleanup" => GetOptionalArgument(parts, "--confirm-build-lease-cleanup"),
                _ => parts.Count > 1 ? parts[1] : null
            };
        }

        return null;
    }

    private static GoalId ResolveGoalScopedTaskMutationGoalId(
        ITransactionalOrchestratorStateRepository stateRepository,
        string? currentGoalId,
        IReadOnlyList<string> parts)
    {
        if (parts.Count > 2 && parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveSingleGoalCommandGoalId(stateRepository, currentGoalId, parts[2]);
        }

        if (parts.Count > 2 &&
            !parts[2].StartsWith("--", StringComparison.Ordinal) &&
            (!int.TryParse(parts[1], out _) || parts[1].Length >= 8))
        {
            var matches = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult()
                .Where(goal => goal.Id.StartsWith(parts[1], StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 0)
            {
                return matches.Count == 1
                    ? new GoalId(matches[0].Id)
                    : throw new InvalidOperationException($"Goal prefix '{parts[1]}' is ambiguous.");
            }
        }

        return ResolveSingleGoalCommandGoalId(stateRepository, currentGoalId, idOrPrefix: null);
    }

    private static string? GetOptionalArgument(IReadOnlyList<string> parts, params string[] flags)
    {
        for (var i = 1; i < parts.Count; i++)
        {
            var part = parts[i];
            if (flags.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                if (IsCliValueFlag(part))
                {
                    i++;
                }

                continue;
            }

            if (IsCliValueFlag(part))
            {
                i++;
                continue;
            }

            if (part.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            return part;
        }

        return null;
    }

    private static bool IsCliValueFlag(string value) =>
        value.Equals("--goal", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--backlog-item", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--role", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--task", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--autonomy", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--policy", StringComparison.OrdinalIgnoreCase);

    private static GoalId ResolveSingleGoalCommandGoalId(
        ITransactionalOrchestratorStateRepository stateRepository,
        string? currentGoalId,
        string? idOrPrefix)
    {
        if (!string.IsNullOrWhiteSpace(idOrPrefix))
        {
            var matches = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult()
                .Where(goal => goal.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return matches.Count switch
            {
                1 => new GoalId(matches[0].Id),
                0 => throw new KeyNotFoundException($"Goal '{idOrPrefix}' was not found."),
                _ => throw new InvalidOperationException($"Goal prefix '{idOrPrefix}' is ambiguous.")
            };
        }

        if (!string.IsNullOrWhiteSpace(currentGoalId))
        {
            return new GoalId(currentGoalId);
        }

        var latest = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult().FirstOrDefault()
            ?? throw new InvalidOperationException("Create a goal first with: goal <objective>");
        return new GoalId(latest.Id);
    }

    private static (GoalId GoalId, int? GoalCount) ResolveAcceptanceGoalId(
        ITransactionalOrchestratorStateRepository stateRepository,
        string? currentGoalId,
        string? idOrPrefix)
    {
        if (!string.IsNullOrWhiteSpace(idOrPrefix))
        {
            var summaries = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            var matches = summaries
                .Where(goal => goal.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var goalId = matches.Count switch
            {
                1 => new GoalId(matches[0].Id),
                0 => throw new KeyNotFoundException($"Goal '{idOrPrefix}' was not found."),
                _ => throw new InvalidOperationException($"Goal prefix '{idOrPrefix}' is ambiguous.")
            };
            return (goalId, summaries.Count);
        }

        if (!string.IsNullOrWhiteSpace(currentGoalId))
        {
            return (new GoalId(currentGoalId), null);
        }

        var allGoals = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult();
        var latest = allGoals.FirstOrDefault()
            ?? throw new InvalidOperationException("Create a goal first with: goal <objective>");
        return (new GoalId(latest.Id), allGoals.Count);
    }

    private static AgentOrchestratorKernel LoadSingleGoalKernel(
        ITransactionalOrchestratorStateRepository stateRepository,
        GoalId goalId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = stateRepository.LoadGoalAsync(goalId, cancellationToken).GetAwaiter().GetResult()
            ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
        return KernelFromGoalSnapshot(snapshot);
    }

    private static IReadOnlyList<HumanInputRequestSnapshot> LoadGoalHumanInputSnapshots(
        ITransactionalOrchestratorStateRepository stateRepository,
        GoalId goalId,
        CancellationToken cancellationToken = default)
    {
        var kernel = stateRepository.LoadGoalsAsync([goalId], cancellationToken).GetAwaiter().GetResult();
        if (!kernel.Goals.Any(goal => goal.Id == goalId))
        {
            throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
        }

        return kernel.ExportSnapshot().HumanInputRequests;
    }

    private static AgentOrchestratorKernel LoadGoalSnapshotsById(
        ITransactionalOrchestratorStateRepository stateRepository,
        IReadOnlyList<GoalId> goalIds,
        CancellationToken cancellationToken = default)
    {
        var snapshots = new List<GoalSnapshot>(goalIds.Count);
        foreach (var goalId in goalIds)
        {
            var snapshot = stateRepository.LoadGoalAsync(goalId, cancellationToken).GetAwaiter().GetResult();
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(snapshots, []));
    }

    private static AgentOrchestratorKernel KernelFromGoalSnapshot(
        GoalSnapshot snapshot,
        IReadOnlyList<HumanInputRequestSnapshot>? humanInputRequests = null) =>
        AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], humanInputRequests ?? []));

    private static GoalSnapshot ExportGoalSnapshot(AgentOrchestratorKernel kernel, GoalId goalId) =>
        kernel.ExportSnapshot().Goals.FirstOrDefault(goal => goal.Id == goalId.Value)
            ?? throw new InvalidOperationException($"Goal '{goalId.Value}' no longer exists.");

    private static void PersistSingleGoalSnapshot(
        ITransactionalOrchestratorStateRepository stateRepository,
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = ExportGoalSnapshot(kernel, goalId);
        stateRepository.SaveGoalSnapshotsAsync([snapshot], cancellationToken).GetAwaiter().GetResult();
    }

    private static GoalId? ResolveConductWatchGoalId(
        IReadOnlyList<string> args,
        AgentOrchestratorKernel kernel,
        Goal? currentGoal,
        ITransactionalOrchestratorStateRepository? stateRepository = null)
    {
        if (args.Count < 2 ||
            !args[0].Equals("conduct", StringComparison.OrdinalIgnoreCase) ||
            !args.Any(arg => arg.Equals("--watch", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(arg => arg.Equals("--loop", StringComparison.OrdinalIgnoreCase)) ||
            args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        var prefix = args[1];
        var match = kernel.Goals.Where(goal => goal.Id.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (match.Length == 1)
        {
            return match[0].Id;
        }

        if (stateRepository is not null)
        {
            var metadataMatches = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult()
                .Where(goal => goal.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return metadataMatches.Length switch
            {
                1 => new GoalId(metadataMatches[0].Id),
                0 => throw new KeyNotFoundException($"Goal '{prefix}' was not found."),
                _ => throw new InvalidOperationException($"Goal prefix '{prefix}' is ambiguous.")
            };
        }

        return OrchestratorEntityResolver.ResolveGoal(kernel, currentGoal, prefix).Id;
    }

    private static bool ConductLoopWillExitBeforeFirstTick(IReadOnlyList<string> args, string executionDirectory)
    {
        if (args.Count == 0 ||
            !args[0].Equals("conduct", StringComparison.OrdinalIgnoreCase) ||
            !args.Any(arg => arg.Equals("--loop", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (int.TryParse(GetFlagValue(args, "--max-iterations"), out var maxIterations) && maxIterations <= 0)
        {
            return true;
        }

        if (int.TryParse(GetFlagValue(args, "--max-duration"), out var maxDurationSeconds) && maxDurationSeconds <= 0)
        {
            return true;
        }

        return File.Exists(Path.Combine(executionDirectory, ConductorBatchLoop.StopFileName));
    }

    private static string? GetFlagValue(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
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
