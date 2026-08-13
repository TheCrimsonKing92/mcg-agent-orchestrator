using System.Text.Json;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record TransientSqliteLoadHold(
    int SqliteErrorCode,
    int SqliteExtendedErrorCode,
    string AttemptCount,
    string ElapsedMilliseconds,
    string Operation)
{
    internal static TransientSqliteLoadHold From(SqliteException exception, string operation) => new(
        exception.SqliteErrorCode,
        exception.SqliteExtendedErrorCode,
        exception.Data["Mcg.AttemptCount"]?.ToString() ?? "unknown",
        exception.Data["Mcg.ElapsedMilliseconds"]?.ToString() ?? "unknown",
        operation);
}

internal static class CliPersistentStateRunner
{
    internal enum OperatorIntentSubmissionSource
    {
        Cli,
        Discord
    }

    internal readonly record struct OperatorIntentAttribution(
        string Actor,
        string Channel,
        string AuthenticationAssurance);

    // The conduct-loop fast path queries goal metadata every tick. Every fourth tick, hydrate
    // currently Parked goals as a safety-net sweep so non-metadata unpark side effects cannot strand
    // a parked goal indefinitely; at the default 15s watch cadence this is roughly one minute.
    internal const int ParkedGoalSafetyNetSweepCadenceTicks = 4;

    internal static bool IsParkedGoalSafetyNetSweepTick(int tickNumber) =>
        tickNumber > 0 && tickNumber % ParkedGoalSafetyNetSweepCadenceTicks == 0;

    private sealed record GoalScopedTaskMutationResult(
        bool ShouldSave,
        IReadOnlyList<AgentDefinition> Agents,
        WorkerProfileCatalog WorkerProfiles,
        Goal? CurrentGoal,
        GoalStateSnapshot State,
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
        IGoalAcceptanceVerifier? acceptanceVerifier = null,
        OperatorIntentSubmissionSource operatorIntentSubmissionSource = OperatorIntentSubmissionSource.Cli)
    {
        using var writeOperationTag = SqliteOrchestratorStateRepository.UseWriteOperationTag(
            $"cli:{(args.Count == 0 ? "repl" : args[0].Trim().ToLowerInvariant())}");
        DrainAcceptanceRetryAuditOutbox(stateRepository, workspace);

        if (IsOperatorIntentStatusCommand(args))
        {
            PrintOperatorIntentStatus(args, workspace);
            return false;
        }

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

        // Dependency-aware backlog commands read goal identities and landing state but mutate only
        // BacklogStore. Hydrate state outside the generic write transaction so they can resolve goal
        // prerequisites without taking a state.db writer lock or persisting an unchanged kernel.
        if (RequiresKernelBacklogState(args))
        {
            return ExecuteCommandWithoutTransaction(
                args,
                stateRepository,
                workspace,
                ref agents,
                providers,
                ref workerProfiles,
                ref currentGoal,
                channel);
        }

        if (IsMetadataOnlyListing(args))
        {
            var summaries = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult();
            ConsoleViews.PrintGoals(summaries);
            ConsoleViews.PrintCleanupDebtWarning(GoalWorktrees.ListCleanupDebt(workspace.ExecutionDirectory));
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

        if (IsGoalCreateDeliveryRetryCommand(args))
        {
            return ExecuteGoalCreateDeliveryRetry(args, stateRepository, workspace, ref currentGoal);
        }

        if (IsGoalCreateCommand(args))
        {
            return ExecuteGoalCreateOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
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
            CliCommandHelp.ThrowIfInvalidFlags(args);
            if (IsInboxBackedGoalScopedTaskMutationCommand(args))
            {
                return SubmitGoalScopedTaskOperatorIntent(
                    args,
                    stateRepository,
                    workspace,
                    agents,
                    providers,
                    workerProfiles,
                    ref currentGoal,
                    channel,
                    operatorIntentSubmissionSource);
            }

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
        string? postCommitFailure = null;
        var isAcceptanceRetry = args.Count > 0 &&
            args[0].Equals("acceptance-retry", StringComparison.OrdinalIgnoreCase);
        bool changed;
        if (isAcceptanceRetry &&
            stateRepository is IOrchestratorStateOutboxRepository outboxRepository)
        {
            changed = outboxRepository.TransactWithOutboxAsync(
                    (kernel, _) =>
                    {
                        var outboxMessages = new List<OrchestratorStateOutboxMessage>();
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
                            () => stateRepository.LoadAsync().GetAwaiter().GetResult(),
                            registerStateOutboxMessage: outboxMessages.Add,
                            registerPostCommitFailure: failure => postCommitFailure ??= failure);

                        nextAgents = commandAgents;
                        nextWorkerProfiles = commandProfiles;
                        nextCurrentGoal = commandGoal;
                        var mustCommit = shouldSave || postCommitFailure is not null;
                        return Task.FromResult((
                            mustCommit,
                            mustCommit,
                            (IReadOnlyList<OrchestratorStateOutboxMessage>)outboxMessages));
                    })
                .GetAwaiter()
                .GetResult();
            DrainAcceptanceRetryAuditOutbox(stateRepository, workspace);
        }
        else
        {
            if (isAcceptanceRetry)
            {
                throw new InvalidOperationException(
                    "acceptance-retry requires a state repository with durable outbox support.");
            }

            changed = stateRepository.TransactAsync(
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
                            () => stateRepository.LoadAsync().GetAwaiter().GetResult(),
                            registerPostCommitFailure: failure => postCommitFailure ??= failure);

                        nextAgents = commandAgents;
                        nextWorkerProfiles = commandProfiles;
                        nextCurrentGoal = commandGoal;
                        var mustCommit = shouldSave || postCommitFailure is not null;
                        return Task.FromResult((mustCommit, mustCommit));
                    })
                .GetAwaiter()
                .GetResult();
        }

        agents = nextAgents;
        workerProfiles = nextWorkerProfiles;
        currentGoal = nextCurrentGoal;
        if (postCommitFailure is not null)
        {
            throw new InvalidOperationException(postCommitFailure);
        }

        return changed;
    }

    private static void DrainAcceptanceRetryAuditOutbox(
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace)
    {
        if (stateRepository is not IOrchestratorStateOutboxRepository outboxRepository)
        {
            return;
        }

        IReadOnlyList<OrchestratorStateOutboxMessage> messages;
        try
        {
            messages = outboxRepository
                .ListOutboxMessagesAsync(GoalOperationJournal.AcceptanceRetryAuditOutboxKind)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            WriteAcceptanceRetryDrainWarning(
                "could not list pending messages",
                ex);
            return;
        }

        if (messages.Count == 0)
        {
            return;
        }

        AgentOrchestratorKernel kernel;
        try
        {
            kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            WriteAcceptanceRetryDrainWarning(
                "could not load goal state for pending messages",
                ex);
            return;
        }

        foreach (var message in messages)
        {
            try
            {
                AcceptanceRetryAuditPayload? appliedPayload = null;
                Goal? appliedGoal = null;
                OrchestratorStateOutboxProcessingResult? processingResult = null;
                var claimed = outboxRepository.TryProcessOutboxMessageAsync(
                        message.Id,
                        (claimedMessage, _) =>
                        {
                            try
                            {
                                var payload = GoalOperationJournal.DeserializeAcceptanceRetryAuditMessage(claimedMessage);
                                var goal = kernel.GetGoal(new GoalId(payload.GoalId));
                                appliedPayload = payload;
                                appliedGoal = goal;
                                processingResult = GoalOperationJournal.ApplyAcceptanceRetryAuditMessage(
                                    workspace,
                                    goal,
                                    claimedMessage);
                                return Task.FromResult(processingResult);
                            }
                            catch (JsonException ex)
                            {
                                processingResult = OrchestratorStateOutboxProcessingResult.Quarantined(
                                    $"Invalid acceptance-retry audit payload: {ex.Message}");
                                return Task.FromResult(processingResult);
                            }
                            catch (KeyNotFoundException ex)
                            {
                                processingResult = OrchestratorStateOutboxProcessingResult.Quarantined(
                                    $"Acceptance-retry audit targets a missing goal: {ex.Message}");
                                return Task.FromResult(processingResult);
                            }
                            catch (ArgumentException ex)
                            {
                                processingResult = OrchestratorStateOutboxProcessingResult.Quarantined(
                                    $"Acceptance-retry audit contains an invalid goal id: {ex.Message}");
                                return Task.FromResult(processingResult);
                            }
                        })
                    .GetAwaiter()
                    .GetResult();

                if (!claimed)
                {
                    continue;
                }

                if (processingResult?.Disposition == OrchestratorStateOutboxDisposition.Quarantine)
                {
                    Console.Error.WriteLine(
                        $"Warning: acceptance-retry audit outbox message '{message.Id}' was quarantined: " +
                        processingResult.Detail);
                    continue;
                }

                if (appliedPayload is not null && appliedGoal is not null)
                {
                    Console.WriteLine(
                        $"Acceptance retry scheduled: goal={appliedGoal.Id.Value[..8]} state={appliedGoal.Status} " +
                        $"operator-regate={appliedPayload.OperatorRegateCount}/{Goal.OperatorAcceptanceRegateCap}; " +
                        "next conductor tick will re-run acceptance.");
                }
            }
            catch (Exception ex)
            {
                WriteAcceptanceRetryDrainWarning(
                    $"message '{message.Id}' remains pending",
                    ex);
            }
        }
    }

    private static void WriteAcceptanceRetryDrainWarning(string detail, Exception exception) =>
        Console.Error.WriteLine(
            $"Warning: acceptance-retry audit drain {detail}; unrelated command will continue. " +
            $"{exception.GetType().Name}: {exception.Message}");

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
            // instead of contending on the per-tick write transaction. Dependency-aware add/list/show
            // commands intentionally hydrate state through RequiresKernelBacklogState.
            "backlog-add" when !HasFlag(args, "--depends-on") => true,
            "backlog-update" or "backlog-annotate" or "backlog-close" or
            "backlog-supersede" or "backlog-unsupersede" or "backlog-link" or "backlog-reopen" or "backlog-view" or
            "cleanup-status" or
            "repo-process-info" or "repo-process-stop" or "stable-slot-dotnet" or
            "gate-status" or "acceptance-engine" or "run-event" or
            "project" => true,
            _ => false,
        };
    }

    internal static bool RequiresKernelBacklogState(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return false;

        return args[0].ToLowerInvariant() switch
        {
            "backlog-list" or "backlog-show" or "backlog-depends" => true,
            "backlog-add" => HasFlag(args, "--depends-on"),
            _ => false
        };
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(arg => arg.Equals(flag, StringComparison.OrdinalIgnoreCase));

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

    internal static bool IsInboxBackedGoalScopedTaskMutationCommand(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        args[0].ToLowerInvariant() is
            OperatorIntentVerbs.Progress or
            OperatorIntentVerbs.Retry or
            OperatorIntentVerbs.VerifyManual;

    private static bool IsOperatorIntentStatusCommand(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        args[0].Equals("operator-intent-status", StringComparison.OrdinalIgnoreCase);

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
        return args.Count > 0 &&
            (args[0].Equals("backlog-intake", StringComparison.OrdinalIgnoreCase) ||
             (args[0].Equals("goal", StringComparison.OrdinalIgnoreCase) &&
              args.Any(arg => arg.Equals("--from-backlog", StringComparison.OrdinalIgnoreCase))));
    }

    internal static bool IsGoalCreateCommand(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        args[0].Equals("goal", StringComparison.OrdinalIgnoreCase) &&
        !args.Any(arg => arg.Equals("--from-backlog", StringComparison.OrdinalIgnoreCase));

    internal static bool IsGoalCreateDeliveryRetryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        args[0].Equals("goal-delivery-retry", StringComparison.OrdinalIgnoreCase);

    internal static bool HasStateDbMigrationAuthority(IReadOnlyList<string> args)
    {
        return IsConductLoop(args) || IsBacklogIntakeCommand(args);
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
            "revise" => HasFlag(args, "--history"),
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
        using var conductLoopLease = ConductorLoopLeaseController.Acquire(workspace.OrchestratorDirectory);
        var operatorIntentStore = SqliteOperatorIntentStore.ForDirectories(
            workspace.OrchestratorDirectory,
            workspace.LogDirectory);
        AgentOrchestratorKernel LoadLoopKernel() => LoadLoopKernelForGoals([]);

        AgentOrchestratorKernel LoadLoopKernelForGoals(IReadOnlyCollection<string> trackedGoalIds) =>
            LoadConductLoopKernel(
                stateRepository,
                operatorIntentStore.ListActionableGoalIdsAsync().GetAwaiter().GetResult()
                    .Concat(trackedGoalIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                workspace.ExecutionDirectory);
        var startupStopPath = Path.Combine(workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName);
        TransientSqliteLoadHold? initialConductLoopLoadHold = null;
        var kernel = LoadInitialConductLoopKernelWithTransientHold(
            LoadLoopKernel,
            workspace,
            stopRequested: () => File.Exists(startupStopPath),
            onHoldExhausted: hold => initialConductLoopLoadHold = hold);
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
                kernel = LoadLoopKernel();
                tickBaselines = kernel.ExportSnapshot().Goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
            }

            GoalWorktreeOrphanSweepScheduler.SweepIfDue(workspace.ExecutionDirectory, sweepKernel);
        }
        catch (Exception ex)
        {
            EmitPreLoopJanitorialFailure(workspace, ex);
        }

        var loopCurrentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);
        currentGoal = loopCurrentGoal;

        void Persist(AgentOrchestratorKernel checkpoint) =>
            PersistGoals(checkpoint, checkpoint.Goals.Select(goal => goal.Id).ToArray());

        void PersistGoals(AgentOrchestratorKernel checkpoint, IReadOnlyCollection<GoalId> changedGoalIds)
        {
            var requests = BuildCheckpointRequests(checkpoint, changedGoalIds, containTransientBaselineLoads: false, out _);
            if (requests.Length == 0) return;

            var results = stateRepository.SaveGoalSnapshotsWithMergeAsync(requests, CancellationToken.None).GetAwaiter().GetResult();
            ApplyDurableResults(checkpoint, results);
        }

        IReadOnlyList<GoalSnapshotCheckpointResult> CheckpointGoals(
            AgentOrchestratorKernel checkpoint,
            IReadOnlyCollection<GoalId> changedGoalIds)
        {
            var requests = BuildCheckpointRequests(
                checkpoint,
                changedGoalIds,
                containTransientBaselineLoads: true,
                out var baselineLoadHolds);
            if (requests.Length == 0)
                return baselineLoadHolds;

            if (!stateRepository.SupportsGoalCheckpointContainment)
            {
                var legacyResults = stateRepository.SaveGoalSnapshotsWithMergeAsync(requests, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                ApplyDurableResults(checkpoint, legacyResults);
                return baselineLoadHolds.Concat(legacyResults.Select(result => new GoalSnapshotCheckpointResult(
                    result.GoalId,
                    GoalSnapshotCheckpointDisposition.Durable,
                    result,
                    "state",
                    "unknown",
                    "legacy-checkpoint"))).ToArray();
            }

            var results = stateRepository.CheckpointGoalSnapshotsAsync(requests, CancellationToken.None).GetAwaiter().GetResult();
            ApplyDurableResults(
                checkpoint,
                results.Where(result => result.IsDurable && result.SaveResult is not null)
                    .Select(result => result.SaveResult!)
                    .ToArray());
            return baselineLoadHolds.Concat(results).ToArray();
        }

        GoalSnapshotSaveRequest[] BuildCheckpointRequests(
            AgentOrchestratorKernel checkpoint,
            IReadOnlyCollection<GoalId> changedGoalIds,
            bool containTransientBaselineLoads,
            out IReadOnlyList<GoalSnapshotCheckpointResult> baselineLoadHolds)
            => BuildConductLoopCheckpointRequests(
                checkpoint,
                changedGoalIds,
                tickBaselines,
                stateRepository,
                workspace.SqliteStatePath,
                containTransientBaselineLoads,
                out baselineLoadHolds);

        void ApplyDurableResults(AgentOrchestratorKernel checkpoint, IReadOnlyList<GoalSnapshotSaveResult> results)
        {
            var persistedTerminalGoalIds = new List<GoalId>();
            var persistedTerminalGoalIdValues = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in results)
            {
                if (result.PersistedSnapshot is not null)
                {
                    if (IsConductLoopTerminalStatus(result.PersistedSnapshot.Status.ToString()))
                    {
                        persistedTerminalGoalIds.Add(new GoalId(result.GoalId));
                        persistedTerminalGoalIdValues.Add(result.GoalId);
                        tickBaselines.Remove(result.GoalId);
                    }
                    else
                    {
                        tickBaselines[result.GoalId] = result.PersistedSnapshot;
                    }
                }

                if (result.Disposition is GoalSnapshotSaveDisposition.Merged or GoalSnapshotSaveDisposition.Skipped)
                {
                    Console.WriteLine(FormatTickMergeReceipt(result));
                }
            }

            checkpoint.EvictTerminalGoalAggregates(persistedTerminalGoalIds);
            if (loopCurrentGoal is not null && persistedTerminalGoalIdValues.Contains(loopCurrentGoal.Id.Value))
            {
                loopCurrentGoal = null;
            }
        }

        var shouldSave = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref loopCurrentGoal,
            channel,
            LoadLoopKernel,
            Persist,
            persistGoalKernel: PersistGoals,
            releaseConductLoopLease: conductLoopLease.Release,
            reacquireConductLoopLease: conductLoopLease.Reacquire,
            reloadResolvedParkedHumanWaitKernel: () => LoadConductLoopResolvedParkedHumanWaitKernel(stateRepository),
            reloadParkedGoalSafetyNetKernel: () => LoadConductLoopParkedGoalSafetyNetKernel(stateRepository),
            reloadKernelForGoals: LoadLoopKernelForGoals,
            checkpointGoalKernel: CheckpointGoals,
            initialConductLoopLoadHold: initialConductLoopLoadHold);

        // A successful handoff has transferred the lease and authority to the successor. All incumbent
        // tick state was persisted before handoff; do not write once the successor owns the loop.
        if (conductLoopLease.IsHeld)
            _ = CheckpointGoals(kernel, kernel.Goals.Select(goal => goal.Id).ToArray());
        currentGoal = loopCurrentGoal;
        return shouldSave;
    }

    internal static GoalSnapshotSaveRequest[] BuildConductLoopCheckpointRequests(
        AgentOrchestratorKernel checkpoint,
        IReadOnlyCollection<GoalId> changedGoalIds,
        IReadOnlyDictionary<string, GoalSnapshot> tickBaselines,
        ITransactionalOrchestratorStateRepository stateRepository,
        string databasePath,
        bool containTransientBaselineLoads,
        out IReadOnlyList<GoalSnapshotCheckpointResult> baselineLoadHolds)
    {
        if (changedGoalIds.Count == 0)
        {
            baselineLoadHolds = [];
            return [];
        }

        var changed = changedGoalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        var checkpointSnapshot = checkpoint.ExportSnapshot();
        var humanInputByGoal = checkpointSnapshot.HumanInputRequests
            .Where(request => changed.Contains(request.GoalId))
            .GroupBy(request => request.GoalId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<HumanInputRequestSnapshot>)group.ToArray(),
                StringComparer.Ordinal);
        var requests = new List<GoalSnapshotSaveRequest>(changedGoalIds.Count);
        var holds = new List<GoalSnapshotCheckpointResult>();
        foreach (var goal in checkpointSnapshot.Goals.Where(goal => changed.Contains(goal.Id)))
        {
            GoalSnapshot baseline;
            if (tickBaselines.TryGetValue(goal.Id, out var known))
            {
                baseline = known;
            }
            else
            {
                try
                {
                    baseline = stateRepository.LoadGoalAsync(new GoalId(goal.Id), CancellationToken.None)
                        .GetAwaiter()
                        .GetResult() ?? goal;
                }
                catch (SqliteException ex) when (
                    containTransientBaselineLoads &&
                    SqliteOrchestratorStateRepository.IsTransientLock(ex))
                {
                    var operation = $"loop:tick/LoadGoalAsync({goal.Id[..8]})";
                    var hold = TransientSqliteLoadHold.From(ex, operation);
                    holds.Add(new GoalSnapshotCheckpointResult(
                        goal.Id,
                        GoalSnapshotCheckpointDisposition.Held,
                        SaveResult: null,
                        Store: "state",
                        DatabasePath: databasePath,
                        Operation: operation,
                        SqliteErrorCode: hold.SqliteErrorCode,
                        SqliteExtendedErrorCode: hold.SqliteExtendedErrorCode,
                        AttemptCount: int.TryParse(hold.AttemptCount, out var attempt) ? attempt : 1,
                        ElapsedMilliseconds: double.TryParse(
                            hold.ElapsedMilliseconds,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var elapsed) ? elapsed : 0));
                    continue;
                }
            }

            requests.Add(new GoalSnapshotSaveRequest(
                baseline,
                goal,
                humanInputByGoal.GetValueOrDefault(goal.Id, [])));
        }

        baselineLoadHolds = holds;
        return requests.ToArray();
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

    internal static AgentOrchestratorKernel LoadInitialConductLoopKernelWithTransientHold(
        Func<AgentOrchestratorKernel> load,
        OrchestratorWorkspace workspace,
        Func<bool>? stopRequested = null,
        Action<TimeSpan>? holdDelay = null,
        TimeSpan? holdInterval = null,
        int maxHoldRetries = 1,
        Action<TransientSqliteLoadHold>? onHoldExhausted = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(workspace);
        var delay = holdInterval ?? TimeSpan.FromSeconds(1);
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(holdInterval));
        if (maxHoldRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxHoldRetries));

        var eventWriter = new ConductEventLogWriter(workspace.ConductEventsLogPath);
        var maxLoadAttempts = checked(maxHoldRetries + 1);
        int? lastSqliteErrorCode = null;
        int? lastSqliteExtendedErrorCode = null;
        string? lastAttempt = null;
        string? lastElapsedMilliseconds = null;
        for (var holdCycle = 1; holdCycle <= maxLoadAttempts; holdCycle++)
        {
            try
            {
                var kernel = load();
                if (holdCycle > 1)
                {
                    var recovered =
                        $"LOOP_LOAD_RECOVERED store=state database={SanitizeConductToken(workspace.SqliteStatePath)} " +
                        $"operation=loop:startup/load sqlite_code={lastSqliteErrorCode} " +
                        $"sqlite_extended_code={lastSqliteExtendedErrorCode} attempt={lastAttempt} hold_cycle={holdCycle - 1} " +
                        $"elapsed_ms={lastElapsedMilliseconds} disposition=recovered";
                    Console.WriteLine(recovered);
                    eventWriter.Append("loop-load-recovered", null, recovered);
                }
                return kernel;
            }
            catch (SqliteException ex) when (SqliteOrchestratorStateRepository.IsTransientLock(ex))
            {
                var hold = TransientSqliteLoadHold.From(ex, "loop:startup/load");
                var attempt = hold.AttemptCount;
                var elapsed = hold.ElapsedMilliseconds;
                lastSqliteErrorCode = ex.SqliteErrorCode;
                lastSqliteExtendedErrorCode = ex.SqliteExtendedErrorCode;
                lastAttempt = attempt;
                lastElapsedMilliseconds = elapsed;
                var held =
                    $"LOOP_LOAD_HOLD store=state database={SanitizeConductToken(workspace.SqliteStatePath)} " +
                    $"operation=loop:startup/load sqlite_code={ex.SqliteErrorCode} sqlite_extended_code={ex.SqliteExtendedErrorCode} " +
                    $"attempt={attempt} hold_cycle={holdCycle} elapsed_ms={elapsed} disposition=exhausted-held holder=unknown";
                Console.WriteLine(held);
                eventWriter.Append("loop-load-hold", null, held);
                if (stopRequested?.Invoke() == true)
                    return new AgentOrchestratorKernel();

                if (holdCycle == maxLoadAttempts)
                {
                    onHoldExhausted?.Invoke(hold);
                    return new AgentOrchestratorKernel();
                }

                if (holdDelay is null)
                    Thread.Sleep(delay);
                else
                    holdDelay(delay);
            }
        }

        throw new InvalidOperationException("The bounded startup load policy exhausted without returning a disposition.");
    }

    internal static bool TryReloadConductLoopKernel(
        Func<AgentOrchestratorKernel> reload,
        OrchestratorWorkspace workspace,
        ConductEventLogWriter eventWriter,
        ref TransientSqliteLoadHold? holdEpisode,
        out AgentOrchestratorKernel? kernel)
    {
        ArgumentNullException.ThrowIfNull(reload);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(eventWriter);
        try
        {
            kernel = reload();
            if (holdEpisode is not null)
            {
                var recovered =
                    $"TICK_LOAD_RECOVERED store=state database={SanitizeConductToken(workspace.SqliteStatePath)} " +
                    $"operation=loop:tick/reload sqlite_code={holdEpisode.SqliteErrorCode} " +
                    $"sqlite_extended_code={holdEpisode.SqliteExtendedErrorCode} attempt=1 " +
                    $"elapsed_ms={holdEpisode.ElapsedMilliseconds} disposition=recovered";
                Console.WriteLine(recovered);
                eventWriter.Append("tick-load-recovered", null, recovered);
                holdEpisode = null;
            }
            return true;
        }
        catch (SqliteException ex) when (SqliteOrchestratorStateRepository.IsTransientLock(ex))
        {
            holdEpisode = TransientSqliteLoadHold.From(ex, "loop:tick/reload");
            var held =
                $"TICK_LOAD_HOLD store=state database={SanitizeConductToken(workspace.SqliteStatePath)} " +
                $"operation={holdEpisode.Operation} sqlite_code={holdEpisode.SqliteErrorCode} " +
                $"sqlite_extended_code={holdEpisode.SqliteExtendedErrorCode} attempt={holdEpisode.AttemptCount} " +
                $"elapsed_ms={holdEpisode.ElapsedMilliseconds} disposition=exhausted-held holder=unknown";
            Console.WriteLine(held);
            eventWriter.Append("tick-load-hold", null, held);
            kernel = null;
            return false;
        }
    }

    private static string SanitizeConductToken(string value) =>
        value.Replace(' ', '_').Replace('\t', '_').Replace('\n', '_').Replace('\r', '_');

    internal static AgentOrchestratorKernel LoadConductLoopKernel(
        ITransactionalOrchestratorStateRepository stateRepository,
        IReadOnlyCollection<string>? additionalHydratedGoalIds = null,
        string? executionDirectory = null)
    {
        var summaries = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult();
        var terminalSummaries = summaries
            .Where(summary => IsConductLoopTerminalStatus(summary.Status))
            .ToArray();
        var terminalDependencyMetadata = terminalSummaries
            .Select(summary => ReadConductLoopDependencyMetadata(summary, executionDirectory))
            .ToDictionary(metadata => metadata.GoalId, StringComparer.Ordinal);
        var hydratedIds = summaries
            .Where(summary =>
                !IsConductLoopTerminalStatus(summary.Status) &&
                !summary.Status.Equals(GoalStatus.Parked.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(summary => new GoalId(summary.Id))
            .Concat((additionalHydratedGoalIds ?? [])
                .Select(id => new GoalId(id)))
            .Distinct()
            .ToArray();
        var kernel = stateRepository.LoadGoalsAsync(hydratedIds).GetAwaiter().GetResult();
        kernel.MarkKnownDependencyGoalStatuses(summaries.Select(summary =>
            new KeyValuePair<GoalId, string>(
                new GoalId(summary.Id),
                terminalDependencyMetadata.TryGetValue(summary.Id, out var metadata)
                    ? metadata.Status
                    : summary.Status)));
        kernel.MarkKnownCompletedDependencyGoals(terminalDependencyMetadata.Values
            .Where(metadata => metadata.IsLanded)
            .Select(metadata => new GoalId(metadata.GoalId)));

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
        var missingDependencyMetadata = missingDependencySummaries
            .Select(summary => ReadConductLoopDependencyMetadata(summary, executionDirectory))
            .ToArray();
        kernel.MarkKnownDependencyGoalStatuses(missingDependencyMetadata.Select(metadata =>
            new KeyValuePair<GoalId, string>(new GoalId(metadata.GoalId), metadata.Status)));
        var completedDependencyIds = missingDependencyMetadata
            .Where(metadata => metadata.IsLanded)
            .Select(metadata => new GoalId(metadata.GoalId))
            .ToArray();
        kernel.MarkKnownCompletedDependencyGoals(completedDependencyIds);
        return kernel;
    }

    internal static AgentOrchestratorKernel LoadConductLoopParkedGoalSafetyNetKernel(
        ITransactionalOrchestratorStateRepository stateRepository)
    {
        var parkedIds = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(summary => summary.Status.Equals(GoalStatus.Parked.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(summary => new GoalId(summary.Id))
            .ToArray();
        return parkedIds.Length == 0
            ? new AgentOrchestratorKernel()
            : stateRepository.LoadGoalsAsync(parkedIds).GetAwaiter().GetResult();
    }

    internal static AgentOrchestratorKernel LoadConductLoopResolvedParkedHumanWaitKernel(
        ITransactionalOrchestratorStateRepository stateRepository)
    {
        var parkedIds = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult()
            .Where(summary => summary.Status.Equals(GoalStatus.Parked.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(summary => new GoalId(summary.Id))
            .ToArray();
        if (parkedIds.Length == 0)
        {
            return new AgentOrchestratorKernel();
        }

        var resolvedIds = stateRepository.ListGoalIdsWithCompletedHumanInputAsync(parkedIds)
            .GetAwaiter()
            .GetResult();
        return resolvedIds.Count == 0
            ? new AgentOrchestratorKernel()
            : stateRepository.LoadGoalsAsync(resolvedIds).GetAwaiter().GetResult();
    }

    private static AgentOrchestratorKernel LoadConductLoopSweepKernel(
        ITransactionalOrchestratorStateRepository stateRepository,
        AgentOrchestratorKernel workingSetKernel,
        string executionDirectory,
        GoalId? onlyGoalId)
    {
        var candidates = ResolveTerminalSweepCandidateIds(stateRepository, executionDirectory, onlyGoalId)
            .Where(id => workingSetKernel.Goals.FirstOrDefault(goal => goal.Id == id) is not { IsMetadataOnly: false })
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

    internal static void PersistSweepChanges(
        AgentOrchestratorKernel sweepKernel,
        ITransactionalOrchestratorStateRepository stateRepository,
        IReadOnlyCollection<GoalId> changedGoalIds)
    {
        if (changedGoalIds.Count == 0)
        {
            return;
        }

        foreach (var goalId in changedGoalIds)
        {
            PersistSingleGoalSnapshot(stateRepository, sweepKernel, goalId);
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
        status.Equals(GoalStatus.Failed.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Cancelled.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Superseded.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Retired", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("CleanedUp", StringComparison.OrdinalIgnoreCase);

    private static ConductLoopDependencyMetadata ReadConductLoopDependencyMetadata(
        GoalSummary summary,
        string? executionDirectory)
    {
        if (executionDirectory is null || !IsConductLoopTerminalStatus(summary.Status))
        {
            return new ConductLoopDependencyMetadata(summary.Id, summary.Status, IsLanded: false);
        }

        var journal = GoalOperationJournal.Read(executionDirectory, new GoalId(summary.Id));
        var isLanded = GoalOperationJournal.HasDurableLandingIntent(journal);
        var status = !isLanded &&
            GoalOperationJournal.HasRetiredTerminalDisposition(journal) &&
            !IsConductLoopTerminalWithoutLandingStatus(summary.Status)
                ? "Retired"
                : summary.Status;
        return new ConductLoopDependencyMetadata(summary.Id, status, isLanded);
    }

    private static bool IsConductLoopTerminalWithoutLandingStatus(string status) =>
        status.Equals(GoalStatus.Failed.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Cancelled.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Superseded.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Retired", StringComparison.OrdinalIgnoreCase);

    private sealed record ConductLoopDependencyMetadata(
        string GoalId,
        string Status,
        bool IsLanded);

    private static TerminalGoalMetadata ToTerminalGoalMetadata(GoalSummary summary)
    {
        var status = Enum.TryParse<GoalStatus>(summary.Status, ignoreCase: true, out var parsed)
            ? parsed
            : GoalStatus.Completed;
        var title = string.IsNullOrWhiteSpace(summary.Objective)
            ? summary.Id
            : summary.Objective;
        var terminatedAt = summary.TerminatedAt ?? TryParseTimestamp(summary.UpdatedAt);
        return new TerminalGoalMetadata(
            new GoalId(summary.Id),
            status,
            title,
            summary.ResultCommit,
            summary.CreatedAt,
            terminatedAt);
    }

    private static DateTimeOffset? TryParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

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

    private static bool SubmitGoalScopedTaskOperatorIntent(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel,
        OperatorIntentSubmissionSource submissionSource)
    {
        var target = CliArgumentParser.ParseGoalScopedTaskTargetArgs(args);
        var hasInlineGoalPrefix = HasInlineGoalPrefixForGoalScopedTaskMutation(stateRepository, target.Parts);
        var preparedCommand = CliCommandHandlers.PrepareGoalScopedTaskMutationCommand(target, hasInlineGoalPrefix, workspace);
        var goalId = ResolveGoalScopedTaskMutationGoalId(stateRepository, currentGoal?.Id.Value, preparedCommand);
        var snapshot = stateRepository.LoadGoalAsync(goalId).GetAwaiter().GetResult()
            ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
        var kernel = KernelFromGoalSnapshot(snapshot, []);
        var goal = kernel.GetGoal(goalId);
        var context = new CliExecutionContext(
            kernel,
            workspace,
            providers,
            agents,
            workerProfiles,
            goal,
            channel);
        var task = CliCommandHandlers.ResolveGoalScopedTaskMutationTarget(preparedCommand, context);
        EnsureGoalScopedTaskMutationTargetMatchesSelector(preparedCommand, goal, task);

        object payload = preparedCommand.Command switch
        {
            OperatorIntentVerbs.Progress => new ProgressOperatorIntentPayload(
                preparedCommand.ProgressStatus
                    ?? throw new InvalidOperationException("Prepared progress command is missing a status."),
                preparedCommand.Text
                    ?? throw new InvalidOperationException("Prepared progress command is missing text.")),
            OperatorIntentVerbs.Retry => BuildRetryPayload(preparedCommand),
            OperatorIntentVerbs.VerifyManual => new ManualVerificationOperatorIntentPayload(
                preparedCommand.ManualVerification
                    ?? throw new InvalidOperationException("Prepared verify-manual command is missing verification evidence.")),
            _ => throw new InvalidOperationException(
                $"Goal-scoped mutation '{preparedCommand.Command}' is not backed by the operator intent inbox.")
        };

        var intentId = Guid.NewGuid().ToString("N");
        var idempotencyKey = ResolveFlagValue(args, "--idempotency-key") ?? intentId;
        var payloadFiles = ResolveFlagValue(args, "--text-file") is { } payloadFile
            ? new[] { Path.GetFullPath(payloadFile) }
            : [];
        var attribution = ResolveOperatorIntentAttribution(args, submissionSource);
        var intent = new OperatorIntentRecord(
            intentId,
            idempotencyKey,
            preparedCommand.Command,
            goal.Id.Value,
            task.Id.Value,
            JsonSerializer.Serialize(payload, payload.GetType(), OperatorIntentJson.Options),
            payloadFiles,
            Actor: attribution.Actor,
            Channel: attribution.Channel,
            AuthenticationAssurance: attribution.AuthenticationAssurance,
            CreatedAt: DateTimeOffset.UtcNow);
        var persisted = SqliteOperatorIntentStore
            .ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .EnqueueAsync(intent)
            .GetAwaiter()
            .GetResult();
        currentGoal = goal;
        Console.WriteLine(
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} " +
            $"selector={preparedCommand.SuppliedGoalSelector ?? "current"} goal={goal.Id.Value} " +
            $"task={task.Id.Value} status={persisted.Status}; poll with operator-intent-status {persisted.Id}.");
        if (!ConductorLoopLease.IsActive(workspace.OrchestratorDirectory))
        {
            Console.WriteLine(ConductorLoopLease.InactiveWarning);
        }

        return false;
    }

    internal static OperatorIntentAttribution ResolveOperatorIntentAttribution(
        IReadOnlyList<string> args,
        OperatorIntentSubmissionSource submissionSource)
    {
        var actor = ResolveFlagValue(args, "--operator-actor");
        return submissionSource switch
        {
            OperatorIntentSubmissionSource.Cli => new OperatorIntentAttribution(
                actor ?? "operator",
                "cli",
                "local-process"),
            OperatorIntentSubmissionSource.Discord => new OperatorIntentAttribution(
                actor ?? throw new ArgumentException(
                    "Discord operator intent submissions require an authenticated --operator-actor."),
                "discord",
                "discord-operator-allowlist"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(submissionSource),
                submissionSource,
                "Unsupported operator intent submission source.")
        };
    }

    private static RetryOperatorIntentPayload BuildRetryPayload(
        CliCommandHandlers.GoalScopedTaskMutationCommand command)
    {
        command.RetryPolicy.ThrowIfDisallowed(AutonomyAction.Retry, OperatorIntentVerbs.Retry);
        return new RetryOperatorIntentPayload(
            command.Text ?? throw new InvalidOperationException("Prepared retry command is missing text."),
            command.RetryRoundKind,
            command.RetryPolicy.Name);
    }

    private static void PrintOperatorIntentStatus(
        IReadOnlyList<string> args,
        OrchestratorWorkspace workspace)
    {
        if (args.Count != 2)
        {
            throw new ArgumentException("Usage: operator-intent-status <intent-id>");
        }

        var databasePath = Path.Combine(
            workspace.OrchestratorDirectory,
            SqliteOperatorIntentStore.DatabaseFileName);
        if (!File.Exists(databasePath))
        {
            throw new KeyNotFoundException($"Operator intent '{args[1]}' was not found.");
        }

        var intent = SqliteOperatorIntentStore
            .OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory)
            .GetAsync(args[1])
            .GetAwaiter()
            .GetResult()
            ?? throw new KeyNotFoundException($"Operator intent '{args[1]}' was not found.");
        Console.WriteLine(
            $"Operator intent {intent.Id}: verb={intent.Verb} goal={intent.GoalId[..Math.Min(8, intent.GoalId.Length)]} " +
            $"task={(intent.TaskId is null ? "none" : intent.TaskId[..Math.Min(8, intent.TaskId.Length)])} " +
            $"status={intent.Status} actor={intent.Actor} channel={intent.Channel} auth={intent.AuthenticationAssurance} " +
            $"outcome={intent.Outcome ?? "pending"}");
    }

    private static string? ResolveFlagValue(IReadOnlyList<string> args, string flag)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{flag} requires a value.");
            }

            return args[index + 1];
        }

        return null;
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
        var target = CliArgumentParser.ParseGoalScopedTaskTargetArgs(args);
        var hasInlineGoalPrefix = HasInlineGoalPrefixForGoalScopedTaskMutation(stateRepository, target.Parts);
        var preparedCommand = CliCommandHandlers.PrepareGoalScopedTaskMutationCommand(target, hasInlineGoalPrefix, workspace);
        var goalId = ResolveGoalScopedTaskMutationGoalId(stateRepository, currentGoal?.Id.Value, preparedCommand);
        var commandAgents = agents;
        var commandProfiles = workerProfiles;

        var result = stateRepository.TransactGoalStateAsync(
                $"cli:{args[0].ToLowerInvariant()}",
                goalId,
                (state, cancellationToken) =>
                {
                    if (state is null)
                    {
                        throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
                    }

                    var kernel = KernelFromGoalSnapshot(state.Goal, state.HumanInputRequests);
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
                    var updatedState = outcome.ShouldSave ? ExportGoalStateSnapshot(kernel, goalId) : state;
                    var transactionResult = new GoalScopedTaskMutationResult(
                        outcome.ShouldSave,
                        transactionAgents,
                        transactionProfiles,
                        transactionCurrentGoal,
                        updatedState,
                        outcome);
                    return Task.FromResult((outcome.ShouldSave, updatedState, transactionResult));
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

    private static bool ExecuteGoalCreateOutsideTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null)
    {
        if (stateRepository is not IOrchestratorStateOutboxRepository outboxRepository)
        {
            throw new InvalidOperationException(
                "Goal creation requires a state repository with durable outbox support.");
        }

        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);
        GoalSnapshot? committedSnapshot = null;
        var deferredEventWriter = new DeferredGoalLifecycleEventWriter();
        var deferredCollaborationWriter = new DeferredGoalCreationCollaborationWriter(
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));

        void FinalizeGoalCreation(Goal goal)
        {
            var preparedSnapshot = kernel.ExportGoalSnapshot(goal.Id);
            GoalCreationSideEffectDelivery.BeforeStateCommit?.Invoke(goal.Id);
            var deliveryMessage = GoalCreationSideEffectDelivery.CreateMessage(
                goal.Id,
                deferredCollaborationWriter.SnapshotEffects(),
                deferredEventWriter.SnapshotEffects());
            outboxRepository.TransactWithOutboxAsync(
                    (currentKernel, _) =>
                    {
                        ValidateGoalCreationPreconditions(currentKernel, preparedSnapshot, workspace);
                        currentKernel.ReplaceGoalWithSnapshot(preparedSnapshot);
                        return Task.FromResult((
                            ShouldSave: true,
                            Result: true,
                            OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[deliveryMessage]));
                    })
                .GetAwaiter()
                .GetResult();
            committedSnapshot = preparedSnapshot;
            try
            {
                _ = DeliverGoalCreationSideEffects(outboxRepository, workspace, kernel, goal.Id);
                deferredCollaborationWriter.CompleteDelivery();
                deferredEventWriter.CompleteDeliveryTo(
                    new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, kernel: kernel));
            }
            catch (Exception ex)
            {
                throw GoalCreationDeliveryIncomplete(goal.Id, ex);
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
            () => stateRepository.LoadAsync().GetAwaiter().GetResult(),
            finalizeGoalCreation: FinalizeGoalCreation,
            eventWriter: deferredEventWriter,
            refinementCollaborationItemRaise: deferredCollaborationWriter.RaiseAsync);

        if (committedSnapshot is null)
        {
            throw new InvalidOperationException("GOAL_CREATE_COMMIT_MISSING reason=finalizer-not-invoked");
        }

        if (currentGoal is not null)
        {
            var finalSnapshot = kernel.ExportGoalSnapshot(currentGoal.Id);
            if (!string.Equals(
                    JsonSerializer.Serialize(committedSnapshot),
                    JsonSerializer.Serialize(finalSnapshot),
                    StringComparison.Ordinal))
            {
                PersistPostCreationGoalChanges(stateRepository, committedSnapshot, finalSnapshot);
            }
        }

        return shouldSave;
    }

    private static bool ExecuteGoalCreateDeliveryRetry(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref Goal? currentGoal)
    {
        if (args.Count != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            throw new InvalidOperationException("Usage: goal-delivery-retry <goal-id-or-prefix>");
        }

        if (stateRepository is not IOrchestratorStateOutboxRepository outboxRepository)
        {
            throw new InvalidOperationException(
                "goal-delivery-retry requires a state repository with durable outbox support.");
        }

        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        var goal = OrchestratorEntityResolver.ResolveGoal(kernel, currentGoal, args[1]);
        try
        {
            var result = DeliverGoalCreationSideEffects(outboxRepository, workspace, kernel, goal.Id);
            currentGoal = goal;
            Console.WriteLine(
                $"GOAL_CREATE_DELIVERY_COMPLETE goal={goal.Id.Value} disposition={result.Disposition.ToString().ToLowerInvariant()}");
            return false;
        }
        catch (Exception ex)
        {
            throw GoalCreationDeliveryIncomplete(goal.Id, ex);
        }
    }

    private static GoalCreationDeliveryResult DeliverGoalCreationSideEffects(
        IOrchestratorStateOutboxRepository outboxRepository,
        OrchestratorWorkspace workspace,
        AgentOrchestratorKernel kernel,
        GoalId goalId)
    {
        OrchestratorStateOutboxProcessingResult? processingResult = null;
        var claimed = outboxRepository.TryProcessOutboxMessageAsync(
                GoalCreationSideEffectDelivery.MessageId(goalId.Value),
                async (message, cancellationToken) =>
                {
                    try
                    {
                        var receipt = GoalCreationSideEffectDelivery.Deserialize(message);
                        if (!receipt.GoalId.Equals(goalId.Value, StringComparison.Ordinal) ||
                            kernel.Goals.All(candidate => candidate.Id != goalId))
                        {
                            processingResult = OrchestratorStateOutboxProcessingResult.Quarantined(
                                $"Goal-creation delivery receipt targets missing or mismatched goal '{receipt.GoalId}'.");
                            return processingResult;
                        }

                        await GoalCreationSideEffectDelivery.DeliverAsync(
                            receipt,
                            workspace,
                            kernel,
                            cancellationToken).ConfigureAwait(false);
                        processingResult = OrchestratorStateOutboxProcessingResult.Completed;
                        return processingResult;
                    }
                    catch (JsonException ex)
                    {
                        processingResult = OrchestratorStateOutboxProcessingResult.Quarantined(
                            $"Invalid goal-creation delivery receipt: {ex.Message}");
                        return processingResult;
                    }
                })
            .GetAwaiter()
            .GetResult();

        if (!claimed)
            return new GoalCreationDeliveryResult(goalId.Value, GoalCreationDeliveryDisposition.AlreadyDelivered);

        if (processingResult?.Disposition == OrchestratorStateOutboxDisposition.Quarantine)
        {
            throw new InvalidOperationException(
                $"GOAL_CREATE_DELIVERY_INVALID goal={goalId.Value} detail={processingResult.Detail}");
        }

        return new GoalCreationDeliveryResult(goalId.Value, GoalCreationDeliveryDisposition.Delivered);
    }

    private static InvalidOperationException GoalCreationDeliveryIncomplete(GoalId goalId, Exception exception) =>
        new(
            $"GOAL_CREATE_DELIVERY_INCOMPLETE goal={goalId.Value} " +
            $"retry=\"goal-delivery-retry {goalId.Value}\" detail={exception.Message}",
            exception);

    private static void ValidateGoalCreationPreconditions(
        AgentOrchestratorKernel currentKernel,
        GoalSnapshot preparedSnapshot,
        OrchestratorWorkspace workspace)
    {
        if (currentKernel.Goals.Any(goal => goal.Id.Value == preparedSnapshot.Id))
        {
            throw GoalCreatePreconditionChanged("prepared-goal-id-exists", ("goal", preparedSnapshot.Id));
        }

        var preparedDependencies = (preparedSnapshot.DependsOn ?? [])
            .ToHashSet(StringComparer.Ordinal);
        if (preparedDependencies.Any(id => currentKernel.Goals.All(goal => goal.Id.Value != id)))
        {
            throw GoalCreatePreconditionChanged("dependency-target-missing", ("goal", preparedSnapshot.Id));
        }

        if (preparedSnapshot.SourceBacklogItemId is not { } backlogItemId)
        {
            return;
        }

        var backlogItem = new BacklogStore(workspace.BacklogStorePath)
            .GetByIdPrefixAsync(backlogItemId)
            .GetAwaiter()
            .GetResult();
        if (backlogItem is null || !string.Equals(backlogItem.Id, backlogItemId, StringComparison.Ordinal))
        {
            throw GoalCreatePreconditionChanged("source-backlog-missing", ("backlogItem", backlogItemId));
        }

        if (currentKernel.FindGoalBySourceBacklogItemId(backlogItemId) is { } competingGoal)
        {
            throw GoalCreatePreconditionChanged(
                "source-backlog-consumed",
                ("backlogItem", backlogItemId),
                ("competingGoal", competingGoal.Id.Value));
        }

        var currentDependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in backlogItem.Dependencies)
        {
            var dependencyGoal = dependency.TargetKind == BacklogDependencyTargetKind.Goal
                ? currentKernel.Goals.FirstOrDefault(goal => goal.Id.Value == dependency.PrerequisiteId)
                : currentKernel.FindGoalBySourceBacklogItemId(dependency.PrerequisiteId);
            if (dependencyGoal is null)
            {
                throw GoalCreatePreconditionChanged(
                    "dependency-target-missing",
                    ("backlogItem", backlogItemId),
                    ("dependency", dependency.PrerequisiteId));
            }

            currentDependencies.Add(dependencyGoal.Id.Value);
        }

        if (!currentDependencies.SetEquals(preparedDependencies))
        {
            throw GoalCreatePreconditionChanged(
                "source-backlog-dependencies-changed",
                ("backlogItem", backlogItemId));
        }
    }

    private static void PersistPostCreationGoalChanges(
        ITransactionalOrchestratorStateRepository stateRepository,
        GoalSnapshot committedSnapshot,
        GoalSnapshot finalSnapshot)
    {
        var committedJson = JsonSerializer.Serialize(committedSnapshot);
        stateRepository.TransactGoalAsync(
                new GoalId(committedSnapshot.Id),
                (currentSnapshot, _) =>
                {
                    if (currentSnapshot is null ||
                        !string.Equals(JsonSerializer.Serialize(currentSnapshot), committedJson, StringComparison.Ordinal))
                    {
                        throw GoalCreatePreconditionChanged(
                            "created-goal-concurrently-modified",
                            ("goal", committedSnapshot.Id));
                    }

                    return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, bool Result)>(
                        (true, finalSnapshot, true));
                })
            .GetAwaiter()
            .GetResult();
    }

    private static InvalidOperationException GoalCreatePreconditionChanged(
        string reason,
        params (string Key, string Value)[] context)
    {
        var suffix = context.Length == 0
            ? string.Empty
            : " " + string.Join(' ', context.Select(item => $"{item.Key}={item.Value}"));
        return new InvalidOperationException($"GOAL_CREATE_PRECONDITION_CHANGED reason={reason}{suffix}");
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
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var refreshUsage = CliCommandHelp.RefreshDispatchSyntax;
        var refreshOptions = command.Equals("refresh-dispatch", StringComparison.OrdinalIgnoreCase)
            ? CliArgumentParser.ParseRefreshDispatchOptions(args, refreshUsage)
            : null;
        var commandArgs = refreshOptions?.TargetParts ?? args;
        if (command.Equals("reconcile", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteGlobalProcessReconcile(args, stateRepository, workspace, ref currentGoal);
        }

        var goalId = ResolveSingleGoalCommandGoalId(stateRepository, currentGoal?.Id.Value, ResolveProcessRefreshGoalPrefix(commandArgs));
        var kernel = LoadSingleGoalKernel(stateRepository, goalId);
        currentGoal = ResolveCurrentGoal(kernel, goalId.Value);

        var candidates = CaptureRunningProcessIdentities(kernel);
        var runner = new BackgroundDispatchRunner();

        switch (command)
        {
            case "refresh-dispatch":
                var refreshTarget = ResolveDispatchCommandTask(commandArgs, kernel, currentGoal, refreshUsage);
                currentGoal = refreshTarget.Goal;
                runner.RefreshLatestProcess(kernel, refreshTarget.Goal.Id, refreshTarget.Task.Id);
                ConsoleViews.PrintRefreshDispatchResult(refreshTarget.Goal, refreshTarget.Task, refreshOptions!);
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

        var transactionResult = stateRepository.TransactGoalStateAsync(
                goalId,
                (state, _) =>
                {
                    if (state is null)
                    {
                        throw new InvalidOperationException($"Goal '{goalId.Value}' no longer exists; retry refresh.");
                    }

                    var transactionKernel = KernelFromGoalSnapshot(state.Goal, state.HumanInputRequests);
                    var appliedCount = ApplyRefreshResults(transactionKernel, results);
                    var updatedState = appliedCount > 0 ? ExportGoalStateSnapshot(transactionKernel, goalId) : state;
                    return Task.FromResult<(bool ShouldSave, GoalStateSnapshot? NewState, (int Applied, GoalStateSnapshot State) Result)>(
                        (appliedCount > 0, updatedState, (appliedCount, updatedState)));
                })
            .GetAwaiter()
            .GetResult();

        currentGoal = KernelFromGoalSnapshot(
            transactionResult.State.Goal,
            transactionResult.State.HumanInputRequests).GetGoal(goalId);
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
        var initialGoalSnapshot = ExportGoalSnapshot(kernel, goalId);
        var initialGoalJson = JsonSerializer.Serialize(initialGoalSnapshot);

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
        var acceptanceGuardAborted = false;

        void Persist(AgentOrchestratorKernel checkpoint) =>
            PersistSingleGoalSnapshot(stateRepository, checkpoint, goalId);

        void PersistCurrentGoal(AgentOrchestratorKernel checkpoint, GoalId goalId)
        {
            PersistSingleGoalSnapshot(stateRepository, checkpoint, goalId);
        }

        AcceptanceMergeGuardPreflightResult PrepareAcceptanceMergeGuard(AcceptanceMergeGuardPreflightRequest request)
            => PrepareAcceptanceMergeGuardState(stateRepository, initialGoalSnapshot, kernel, request);

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
                        var currentGuard = AcceptanceMergeGuard.Capture(transactionKernel, request.GoalId);
                        if (AcceptanceMergeGuard.Compare(request.ExpectedGuard, currentGuard) is { } stateMismatch)
                        {
                            var guardedResult = GuardedAcceptanceAbort(
                                request.GoalId,
                                stateMismatch,
                                request.PassingGateReceiptRecorded);
                            return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, (AcceptanceMergeCommitResult Result, GoalSnapshot Snapshot) Result)>(
                                (false, snapshot, (guardedResult, snapshot)));
                        }

                        var currentHead = ResolveWorktreeHead(workspace.ExecutionDirectory, request.GoalId);
                        if (AcceptanceMergeGuard.CompareWorktreeHead(request.TestedWorktreeHead, currentHead) is { } headMismatch)
                        {
                            var guardedResult = GuardedAcceptanceAbort(
                                request.GoalId,
                                headMismatch,
                                request.PassingGateReceiptRecorded);
                            return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, (AcceptanceMergeCommitResult Result, GoalSnapshot Snapshot) Result)>(
                                (false, snapshot, (guardedResult, snapshot)));
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
            acceptanceGuardAborted = transactionResult.Result.GuardAborted;
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
            phaseTimings: phaseTimings,
            registerAcceptanceGuardAbort: () => acceptanceGuardAborted = true,
            prepareAcceptanceMergeGuard: PrepareAcceptanceMergeGuard);

        currentGoal = updatedCurrentGoal;

        if (acceptanceFinalStatePersisted)
        {
            if (!acceptanceGuardAborted)
            {
                PersistIfTargetGoalChangedSinceLoad(kernel, goalId, initialGoalJson);
            }
            return shouldSave;
        }

        if (acceptanceGuardAborted)
        {
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

    private static AcceptanceMergeGuardPreflightResult PrepareAcceptanceMergeGuardState(
        ITransactionalOrchestratorStateRepository stateRepository,
        GoalSnapshot initialSnapshot,
        AgentOrchestratorKernel proposedKernel,
        AcceptanceMergeGuardPreflightRequest request)
    {
        var initialKernel = KernelFromGoalSnapshot(initialSnapshot);
        var initialGuard = AcceptanceMergeGuard.Capture(initialKernel, request.GoalId);
        if (AcceptanceMergeGuard.Compare(initialGuard, request.ProposedGuard) is null)
        {
            var currentSnapshot = stateRepository.LoadGoalAsync(request.GoalId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"Goal '{request.GoalId.Value}' no longer exists; acceptance cannot continue.");
            var currentGuard = AcceptanceMergeGuard.Capture(KernelFromGoalSnapshot(currentSnapshot), request.GoalId);
            return new AcceptanceMergeGuardPreflightResult(
                currentGuard,
                AcceptanceMergeGuard.Compare(request.ProposedGuard, currentGuard));
        }

        return stateRepository.TransactGoalAsync(
                request.GoalId,
                (snapshot, _) =>
                {
                    if (snapshot is null)
                    {
                        throw new InvalidOperationException($"Goal '{request.GoalId.Value}' no longer exists; acceptance cannot continue.");
                    }

                    var currentKernel = KernelFromGoalSnapshot(snapshot);
                    var currentGuard = AcceptanceMergeGuard.Capture(currentKernel, request.GoalId);
                    if (AcceptanceMergeGuard.Compare(request.ProposedGuard, currentGuard) is null)
                    {
                        return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, AcceptanceMergeGuardPreflightResult Result)>(
                            (false, snapshot, new AcceptanceMergeGuardPreflightResult(currentGuard, null)));
                    }

                    if (AcceptanceMergeGuard.Compare(initialGuard, currentGuard) is { } concurrentMismatch)
                    {
                        return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, AcceptanceMergeGuardPreflightResult Result)>(
                            (false, snapshot, new AcceptanceMergeGuardPreflightResult(currentGuard, concurrentMismatch)));
                    }

                    ReplayAcceptancePreflightRepairs(initialSnapshot, proposedKernel, currentKernel, request.GoalId);
                    var preparedGuard = AcceptanceMergeGuard.Capture(currentKernel, request.GoalId);
                    if (AcceptanceMergeGuard.Compare(request.ProposedGuard, preparedGuard) is { } repairMismatch)
                    {
                        return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, AcceptanceMergeGuardPreflightResult Result)>(
                            (false, snapshot, new AcceptanceMergeGuardPreflightResult(preparedGuard, repairMismatch)));
                    }

                    var preparedSnapshot = ExportGoalSnapshot(currentKernel, request.GoalId);
                    return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, AcceptanceMergeGuardPreflightResult Result)>(
                        (true, preparedSnapshot, new AcceptanceMergeGuardPreflightResult(preparedGuard, null)));
                })
            .GetAwaiter()
            .GetResult();
    }

    private static void ReplayAcceptancePreflightRepairs(
        GoalSnapshot initialSnapshot,
        AgentOrchestratorKernel proposedKernel,
        AgentOrchestratorKernel currentKernel,
        GoalId goalId)
    {
        var initialKernel = KernelFromGoalSnapshot(initialSnapshot);
        var initialGoal = initialKernel.GetGoal(goalId);
        var proposedGoal = proposedKernel.GetGoal(goalId);
        var currentGoal = currentKernel.GetGoal(goalId);

        foreach (var proposedTask in proposedGoal.Tasks)
        {
            var initialTask = initialGoal.Tasks.Single(task => task.Id == proposedTask.Id);
            if (!SameAcceptancePreflightValue(initialTask.LastVerification, proposedTask.LastVerification) &&
                proposedTask.LastVerification is { } verification)
            {
                currentKernel.RecordTaskVerification(goalId, proposedTask.Id, verification);
            }
        }

        if (initialGoal.Status == GoalStatus.Completed && proposedGoal.Status == GoalStatus.Verified)
        {
            currentKernel.NormalizePrematureCompletedGoalToVerified(
                goalId,
                "acceptance: persisted the Completed-to-Verified repair before verification.");
        }
        else if (initialGoal.Status == GoalStatus.Verifying && proposedGoal.Status == GoalStatus.Verified)
        {
            currentKernel.ReconcileGoalAcceptanceVerified(
                goalId,
                "acceptance: persisted the Verifying-to-Verified repair before verification.");
        }
        else if (proposedGoal.Status == GoalStatus.Verified && currentKernel.GetGoal(goalId).Status != GoalStatus.Verified)
        {
            currentKernel.ReconcileGoalVerificationStatus(
                goalId,
                "acceptance: persisted task verification repairs before verification.");
        }

        if (proposedGoal.LatestAcceptanceFailure is null &&
            initialGoal.LatestAcceptanceFailure is not null &&
            SameAcceptancePreflightValue(currentGoal.LatestAcceptanceFailure, initialGoal.LatestAcceptanceFailure))
        {
            currentKernel.ClearAcceptanceFailure(goalId);
        }
    }

    private static bool SameAcceptancePreflightValue<T>(T left, T right) =>
        string.Equals(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right), StringComparison.Ordinal);

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
        var initialGoalSnapshots = kernel.ExportSnapshot().Goals.ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
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
            acceptanceVerifier: acceptanceVerifier,
            prepareAcceptanceMergeGuard: request => PrepareAcceptanceMergeGuardState(
                stateRepository,
                initialGoalSnapshots[request.GoalId.Value],
                kernel,
                request));

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

    private static AcceptanceMergeCommitResult GuardedAcceptanceAbort(
        GoalId goalId,
        AcceptanceMergeGuardMismatch mismatch,
        bool passingGateReceiptRecorded)
    {
        var reason = AcceptanceMergeGuard.BuildAbortMessage(goalId, mismatch, passingGateReceiptRecorded);
        return new AcceptanceMergeCommitResult(false, reason, mismatch);
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

        if (parts[0].Equals("revise", StringComparison.OrdinalIgnoreCase))
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
        CliCommandHandlers.GoalScopedTaskMutationCommand command)
    {
        if (command.SuppliedGoalSelector is { } selector)
        {
            return ResolveSingleGoalCommandGoalId(stateRepository, currentGoalId, selector);
        }

        return ResolveSingleGoalCommandGoalId(stateRepository, currentGoalId, idOrPrefix: null);
    }

    private static void EnsureGoalScopedTaskMutationTargetMatchesSelector(
        CliCommandHandlers.GoalScopedTaskMutationCommand command,
        Goal goal,
        TaskSpec task)
    {
        if (command.SuppliedGoalSelector is { } selector &&
            !goal.Id.Value.StartsWith(selector, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Parsed goal selector '{selector}' resolved to unexpected goal '{goal.Id.Value}'.");
        }

        if (!goal.Tasks.Any(candidate => candidate.Id == task.Id))
        {
            throw new InvalidOperationException(
                $"Resolved task '{task.Id.Value}' does not belong to goal '{goal.Id.Value}'.");
        }
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
        value.Equals("--backlog-coverage", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--backlog-item", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--pipeline", StringComparison.OrdinalIgnoreCase) ||
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
        var kernel = stateRepository.LoadGoalsAsync([goalId], cancellationToken).GetAwaiter().GetResult();
        if (!kernel.Goals.Any(goal => goal.Id == goalId))
        {
            throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
        }

        return kernel;
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

    private static GoalStateSnapshot ExportGoalStateSnapshot(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        var snapshot = kernel.ExportSnapshot();
        var goal = snapshot.Goals.FirstOrDefault(goal => goal.Id == goalId.Value)
            ?? throw new InvalidOperationException($"Goal '{goalId.Value}' no longer exists.");
        var humanInputRequests = snapshot.HumanInputRequests
            .Where(request => string.Equals(request.GoalId, goalId.Value, StringComparison.Ordinal))
            .ToArray();
        return new GoalStateSnapshot(goal, humanInputRequests);
    }

    internal static void PersistSingleGoalSnapshot(
        ITransactionalOrchestratorStateRepository stateRepository,
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        CancellationToken cancellationToken = default)
    {
        var state = ExportGoalStateSnapshot(kernel, goalId);
        stateRepository.TransactGoalStateAsync(
                goalId,
                (stored, _) =>
                {
                    if (stored is null)
                    {
                        throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
                    }

                    return Task.FromResult((
                        true,
                        (GoalStateSnapshot?)MergeHumanInputCheckpoint(stored, state),
                        true));
                },
                cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private static GoalStateSnapshot MergeHumanInputCheckpoint(
        GoalStateSnapshot stored,
        GoalStateSnapshot current)
    {
        var requests = stored.HumanInputRequests
            .ToDictionary(request => request.Id, StringComparer.Ordinal);
        foreach (var request in current.HumanInputRequests)
        {
            if (!requests.TryGetValue(request.Id, out var existing) ||
                request.IsCompleted && !existing.IsCompleted)
            {
                requests[request.Id] = request;
            }
        }

        return new GoalStateSnapshot(current.Goal, requests.Values.ToArray());
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
