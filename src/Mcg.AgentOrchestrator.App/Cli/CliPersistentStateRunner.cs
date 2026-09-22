using System.Text.Json;
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

internal sealed class GoalReplacementRetryableTimeoutException(
    Guid requestId,
    string predecessorGoalId,
    string ownerGoalId,
    long claimVersion)
    : InvalidOperationException(
        $"GOAL_REPLACE_RETRYABLE_TIMEOUT requestId={requestId:D} predecessor={predecessorGoalId} owner={ownerGoalId} claimVersion={claimVersion} reason=competing-replacement-timeout")
{
    public Guid RequestId { get; } = requestId;
    public string PredecessorGoalId { get; } = predecessorGoalId;
    public string OwnerGoalId { get; } = ownerGoalId;
    public long ClaimVersion { get; } = claimVersion;
}

internal static partial class CliPersistentStateRunner
{
    internal static Action? AfterGoalReplacementLeaseAcquisitionFailed { get; set; }
    internal static Action? BeforeGoalReplacementFailureClaimObservation { get; set; }

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
        OperatorIntentSubmissionSource operatorIntentSubmissionSource = OperatorIntentSubmissionSource.Cli,
        WorktreeCleanupContext? acceptanceCleanupContext = null,
        Func<TimeSpan?, Action<DotnetBuildStableSlotWait>?, DotnetBuildEnvironmentLease>? stableSlotSelector = null)
    {
        if (CliReadOnlyCommandRunner.TryExecute(args, stateRepository, workspace, providers, channel, ref agents, ref workerProfiles, ref currentGoal, out var readOnlyResult))
            return readOnlyResult;

        using var writeOperationTag = SqliteOrchestratorStateRepository.UseWriteOperationTag(
            $"cli:{(args.Count == 0 ? "repl" : args[0].Trim().ToLowerInvariant())}");
        DrainAcceptanceRetryAuditOutbox(stateRepository, workspace);

        if (IsGoalRefinementWorkCommand(args))
        {
            return ExecuteGoalRefinementWork(
                args,
                stateRepository,
                workspace,
                providers,
                workerProfiles,
                ref currentGoal);
        }

        if (IsOperatorIntentStatusCommand(args))
        {
            CliCriterionEvidenceIntents.PrintStatus(args, workspace);
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
            // Cleanup-debt reads use this command's owner: the injected context, else a configured one.
            var debtHooks = (acceptanceCleanupContext ?? WorktreeCleanupContext.Load(
                attentionStoreDirectory: workspace.OrchestratorDirectory)).Hooks;
            ConsoleViews.PrintCleanupDebtWarning(
                GoalWorktrees.ListCleanupDebt(workspace.ExecutionDirectory, debtHooks));
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
            return ExecuteConductLoopOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel, acceptanceCleanupContext);
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
                persistOnlyCurrentGoal: true,
                cleanupContext: acceptanceCleanupContext,
                stableSlotSelector: stableSlotSelector);
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
                acceptanceVerifier,
                cleanupContext: acceptanceCleanupContext,
                stableSlotSelector: stableSlotSelector);
        }

        if (IsProcessRefreshCommand(args))
        {
            return ExecuteProcessRefreshOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, acceptanceCleanupContext);
        }

        if (IsBacklogIntakeGoalCreationCommand(args) && HasRequestKey(args))
        {
            return ExecuteGoalCreateOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        if (IsBacklogIntakeCommand(args))
        {
            return ExecuteBacklogIntakeOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
        }

        if (IsGoalCreateDeliveryRetryCommand(args))
        {
            return ExecuteGoalCreateDeliveryRetry(args, stateRepository, workspace, ref currentGoal);
        }

        if (IsGoalReplacementCommand(args))
        {
            return ExecuteGoalReplacementOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
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

        if (IsCriterionEvidenceMutationCommand(args))
        {
            return SubmitCriterionEvidenceOperatorIntent(
                args,
                stateRepository,
                workspace,
                ref currentGoal,
                operatorIntentSubmissionSource);
        }

        if (IsGoalLifecycleDispositionCommand(args))
        {
            return ExecuteGoalLifecycleDispositionCommand(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel);
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
        IReadOnlyList<GoalId> pendingRefinementGoalIds = [];
        var isAcceptanceRetry = args.Count > 0 &&
            args[0].Equals("acceptance-retry", StringComparison.OrdinalIgnoreCase);
        bool changed;
        if (stateRepository is IOrchestratorStateOutboxRepository outboxRepository)
        {
            changed = outboxRepository.TransactWithOutboxAsync(
                    (kernel, _) =>
                    {
                        var existingGoalIds = kernel.Goals
                            .Select(goal => goal.Id)
                            .ToHashSet();
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
                        pendingRefinementGoalIds = kernel.Goals
                            .Where(goal => !existingGoalIds.Contains(goal.Id))
                            .Where(GoalRefinementWorkCoordinator.HasPendingWork)
                            .Select(goal => goal.Id)
                            .ToArray();
                        foreach (var goalId in pendingRefinementGoalIds)
                        {
                            var messageId = GoalRefinementWorkCoordinator.MessageId(goalId);
                            if (outboxMessages.All(message => !message.Id.Equals(messageId, StringComparison.Ordinal)))
                                outboxMessages.Add(GoalRefinementWorkCoordinator.CreateMessage(goalId));
                        }
                        var mustCommit = shouldSave || postCommitFailure is not null;
                        return Task.FromResult((
                            mustCommit,
                            mustCommit,
                            (IReadOnlyList<OrchestratorStateOutboxMessage>)outboxMessages));
                    })
                .GetAwaiter()
                .GetResult();
            if (isAcceptanceRetry)
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

        foreach (var goalId in pendingRefinementGoalIds)
            _ = GoalRefinementWorkCoordinator.TryLaunch(workspace, goalId);

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

        if (args.Count >= 2 &&
            args[0].Equals("goals", StringComparison.OrdinalIgnoreCase) &&
            args[1].Equals("--board", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return args[0].ToLowerInvariant() switch
        {
            _ when CliCommandHelp.IsCommandSpecificHelp(args) => true,
            "operator-listen" or "operator-channel" or "goal-intake-status" => true,
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
            "trial-compare" => !TrialCompareCliCommand.RequiresHistoricalState(args),
            "hermes-acp-trial" or "hermes-acp-verify-identity" => true,
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

    internal static bool IsCriterionEvidenceMutationCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].ToLowerInvariant() is
            OperatorIntentVerbs.CriterionEvidenceMap or
            OperatorIntentVerbs.CriterionEvidenceRecord or
            OperatorIntentVerbs.CriterionEvidenceRepair;

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

    internal static bool IsBacklogIntakeGoalCreationCommand(IReadOnlyList<string> args) =>
        IsBacklogIntakeCommand(args) &&
        ((args[0].Equals("goal", StringComparison.OrdinalIgnoreCase) &&
          args.Any(arg => arg.Equals("--from-backlog", StringComparison.OrdinalIgnoreCase))) ||
         args.Any(arg =>
             arg.Equals("--create-goal", StringComparison.OrdinalIgnoreCase) ||
             arg.Equals("--create-simple-goal", StringComparison.OrdinalIgnoreCase)));

    internal static bool IsGoalCreateCommand(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        ((args[0].Equals("goal", StringComparison.OrdinalIgnoreCase) &&
           !args.Any(arg => arg.Equals("--from-backlog", StringComparison.OrdinalIgnoreCase))) ||
          args[0].Equals("simple-goal", StringComparison.OrdinalIgnoreCase));

    internal static bool IsGoalIntakeStatusCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("goal-intake-status", StringComparison.OrdinalIgnoreCase);

    private static bool HasRequestKey(IReadOnlyList<string> args) =>
        args.Any(arg =>
            arg.Equals("--request-key", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--request-key=", StringComparison.OrdinalIgnoreCase));

    internal static bool IsGoalReplacementCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("goal-replace", StringComparison.OrdinalIgnoreCase);

    internal static bool IsGoalCreateDeliveryRetryCommand(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        args[0].Equals("goal-delivery-retry", StringComparison.OrdinalIgnoreCase);

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
        IOperatorChannel? channel = null,
        WorktreeCleanupContext? cleanupContext = null)
    {
        cleanupContext ??= WorktreeCleanupContext.Load(attentionStoreDirectory: workspace.OrchestratorDirectory);
        using var conductLoopLease = ConductorLoopLeaseController.Acquire(workspace.OrchestratorDirectory);
        var operatorIntentStore = SqliteOperatorIntentStore.ForDirectories(
            workspace.OrchestratorDirectory,
            workspace.LogDirectory);
        var tickBaselines = new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal);
        AgentOrchestratorKernel LoadLoopKernel() => LoadLoopKernelForGoals([]);

        AgentOrchestratorKernel LoadLoopKernelForGoals(IReadOnlyCollection<string> trackedGoalIds)
        {
            var loaded = LoadConductLoopKernel(
                stateRepository,
                operatorIntentStore.ListActionableGoalIdsAsync().GetAwaiter().GetResult()
                    .Concat(trackedGoalIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                workspace.ExecutionDirectory);
            CaptureMissingConductLoopTickBaselines(tickBaselines, loaded);
            return loaded;
        }
        var startupStopPath = Path.Combine(workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName);
        TransientSqliteLoadHold? initialConductLoopLoadHold = null;
        var currentGoalAtStartup = currentGoal;
        var kernel = RunConductPreLoopStartup(
            () => conductLoopLease.IsHeld,
            () => LoadInitialConductLoopKernelWithTransientHold(
                LoadLoopKernel,
                workspace,
                stopRequested: () => File.Exists(startupStopPath),
                onHoldExhausted: hold => initialConductLoopLoadHold = hold),
            loaded => $"{ConductorContinuitySupervisor.LoopReadyLinePrefix}lock=acquired state=loaded goals={loaded.Goals.Count}",
            line => EmitPreLoopReadiness(workspace, line),
            RunStartupSweep);

        AgentOrchestratorKernel RunStartupSweep(AgentOrchestratorKernel startupKernel)
        {
            tickBaselines = startupKernel.ExportSnapshot().Goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
            try
            {
                var watchGoalId = ResolveConductWatchGoalId(args, startupKernel, currentGoalAtStartup, stateRepository);
                // Startup and loop share the operation scheduler and its cadence.
                var sweepKernel = LoadConductLoopSweepKernel(
                    stateRepository, startupKernel, workspace.ExecutionDirectory, watchGoalId, cleanupContext.Hooks);
                var sweep = TerminalGoalSweep.Run(
                    sweepKernel,
                    workspace.ExecutionDirectory,
                    watchGoalId,
                    cleanupHooks: cleanupContext.Hooks,
                    orchestratorDirectory: workspace.OrchestratorDirectory);
                var metadataOnlyExcludedGoalCount = CountMetadataOnlyTerminalSweepExclusions(
                    stateRepository, workspace.ExecutionDirectory, watchGoalId, cleanupContext.Hooks);
                if (metadataOnlyExcludedGoalCount > 0)
                {
                    sweep = sweep with { ExcludedGoalCount = sweep.ExcludedGoalCount + metadataOnlyExcludedGoalCount };
                }

                ConsoleViews.PrintTerminalGoalSweep(sweep, includeBlockers: ConductLoopWillExitBeforeFirstTick(args, workspace.ExecutionDirectory));
                TerminalGoalSweepAttention.Surface(sweepKernel, sweep, workspace.OrchestratorDirectory, watchGoalId);
                if (sweep.Changed)
                {
                    PersistSweepChanges(sweepKernel, stateRepository, sweep.Goals.Select(goal => goal.GoalId).ToArray());
                    startupKernel = LoadLoopKernel();
                    tickBaselines = startupKernel.ExportSnapshot().Goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
                }

                cleanupContext.Scheduler.SweepIfDue(workspace.ExecutionDirectory, sweepKernel);
                RemoteGitMirror.TryStartBackgroundProcessing(sweepKernel, workspace.ExecutionDirectory, watchGoalId);
            }
            catch (Exception ex)
            {
                EmitPreLoopJanitorialFailure(workspace, ex);
            }

            return startupKernel;
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

        void PersistCriticalGoals(AgentOrchestratorKernel checkpoint, IReadOnlyCollection<GoalId> changedGoalIds)
            => PersistCriticalGoalSnapshotsOrThrow(
                stateRepository,
                checkpoint,
                changedGoalIds,
                tickBaselines,
                workspace.SqliteStatePath,
                results => ApplyDurableResults(checkpoint, results));

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
                if (RebaseCheckpointAfterDurableSave(checkpoint, result) &&
                    loopCurrentGoal?.Id.Value == result.GoalId)
                {
                    loopCurrentGoal = checkpoint.GetGoal(new GoalId(result.GoalId));
                }
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
            initialConductLoopLoadHold: initialConductLoopLoadHold,
            persistCriticalGoalKernel: PersistCriticalGoals,
            recordDurableGoalBaseline: snapshot => tickBaselines[snapshot.Id] = snapshot,
            cleanupContext: cleanupContext);

        // A successful handoff has transferred the lease and authority to the successor. All incumbent
        // tick state was persisted before handoff; do not write once the successor owns the loop.
        if (conductLoopLease.IsHeld)
            _ = CheckpointGoals(kernel, kernel.Goals.Select(goal => goal.Id).ToArray());
        currentGoal = loopCurrentGoal;
        return shouldSave;
    }

    internal static TState RunConductPreLoopStartup<TState>(
        Func<bool> loopLeaseHeld,
        Func<TState> loadState,
        Func<TState, string> formatReadinessLine,
        Action<string> emitLine,
        Func<TState, TState> runStartupSweep)
    {
        if (!loopLeaseHeld())
        {
            throw new InvalidOperationException("Cannot report conductor readiness without the loop lease.");
        }

        var state = loadState();
        emitLine(formatReadinessLine(state));
        return runStartupSweep(state);
    }

    internal static bool RebaseCheckpointAfterDurableSave(
        AgentOrchestratorKernel checkpoint,
        GoalSnapshotSaveResult result)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(result);

        if (result.PersistedSnapshot is null ||
            result.Disposition is not (GoalSnapshotSaveDisposition.Merged or GoalSnapshotSaveDisposition.Skipped))
        {
            return false;
        }

        // A merge/skip means the store, not the submitted tick snapshot, is authoritative. Keeping
        // the old aggregate alive lets the following tick repeat a side effect for a task that the
        // merged row has already advanced (for example, redispatching a manually verified Tester).
        if (result.PersistedHumanInputRequests is null)
        {
            checkpoint.ReplaceGoalWithSnapshot(result.PersistedSnapshot);
        }
        else
        {
            checkpoint.ReplaceGoalStateWithSnapshot(
                result.PersistedSnapshot,
                result.PersistedHumanInputRequests);
        }
        return true;
    }

    internal static void CaptureMissingConductLoopTickBaselines(
        IDictionary<string, GoalSnapshot> tickBaselines,
        AgentOrchestratorKernel loadedKernel)
    {
        ArgumentNullException.ThrowIfNull(tickBaselines);
        ArgumentNullException.ThrowIfNull(loadedKernel);
        foreach (var goal in loadedKernel.ExportSnapshot().Goals)
            tickBaselines.TryAdd(goal.Id, goal);
    }

    internal static void PersistCriticalGoalSnapshotsOrThrow(
        ITransactionalOrchestratorStateRepository stateRepository,
        AgentOrchestratorKernel checkpoint,
        IReadOnlyCollection<GoalId> changedGoalIds,
        IReadOnlyDictionary<string, GoalSnapshot> tickBaselines,
        string databasePath,
        Action<IReadOnlyList<GoalSnapshotSaveResult>> applyDurableResults)
    {
        ArgumentNullException.ThrowIfNull(stateRepository);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(changedGoalIds);
        ArgumentNullException.ThrowIfNull(tickBaselines);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(applyDurableResults);

        var missingBaselines = changedGoalIds
            .Where(goalId => !tickBaselines.ContainsKey(goalId.Value))
            .Select(goalId => goalId.Value)
            .ToArray();
        if (missingBaselines.Length > 0)
        {
            throw new DispatchCheckpointConflictException(
                $"Critical dispatch checkpoint has no tick baseline for goal(s) {string.Join(',', missingBaselines.Select(id => id[..Math.Min(8, id.Length)]))}; worker start was aborted.");
        }

        var requests = BuildConductLoopCheckpointRequests(
                checkpoint,
                changedGoalIds,
                tickBaselines,
                stateRepository,
                databasePath,
                containTransientBaselineLoads: false,
                out _)
            .Select(request => request with { RejectConflict = true })
            .ToArray();
        if (requests.Length != changedGoalIds.Count)
        {
            throw new InvalidOperationException(
                $"Critical dispatch checkpoint could not resolve every requested goal ({requests.Length}/{changedGoalIds.Count}).");
        }

        var results = stateRepository.SaveGoalSnapshotsWithMergeAsync(requests, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        applyDurableResults(results);

        var conflict = results.FirstOrDefault(result => result.Disposition != GoalSnapshotSaveDisposition.Saved);
        if (conflict is not null)
        {
            throw new DispatchCheckpointConflictException(
                $"Critical dispatch checkpoint rejected stale state for goal {conflict.GoalId[..Math.Min(8, conflict.GoalId.Length)]}; worker start was aborted and the next tick will re-evaluate the authoritative task state.");
        }
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

    internal static void EmitPreLoopReadiness(OrchestratorWorkspace workspace, string line)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        var emittedAt = DateTimeOffset.UtcNow;
        Console.WriteLine($"{line} ts={emittedAt:O}");
        Console.Out.Flush();
        try
        {
            new ConductEventLogWriter(workspace.ConductEventsLogPath)
                .Append("loop-ready", null, line, emittedAt);
        }
        catch
        {
            // Shared event streaming is advisory; stdout remains the primary readiness signal.
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
        GoalId? onlyGoalId,
        GoalWorktreeCleanupHooks cleanupHooks)
    {
        var candidates = ResolveTerminalSweepCandidateIds(stateRepository, executionDirectory, onlyGoalId, cleanupHooks)
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
        GoalId? onlyGoalId,
        GoalWorktreeCleanupHooks cleanupHooks)
    {
        if (onlyGoalId is not null)
        {
            return [onlyGoalId];
        }

        var summaries = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult();
        return ResolveTerminalSweepCandidateIds(summaries, executionDirectory, onlyGoalId, cleanupHooks);
    }

    private static IReadOnlyList<GoalId> ResolveTerminalSweepCandidateIds(
        IReadOnlyList<GoalSummary> summaries,
        string executionDirectory,
        GoalId? onlyGoalId,
        GoalWorktreeCleanupHooks cleanupHooks)
    {
        if (onlyGoalId is not null)
        {
            return [onlyGoalId];
        }

        var gitFacts = GoalGitFactIndex.Build(executionDirectory);
        return summaries
            .Where(summary => IsConductLoopTerminalStatus(summary.Status))
            .Select(summary => new GoalId(summary.Id))
            // Presence-only test: the owner changes no selection, but owns the backoff-read warning.
            .Where(id =>
                gitFacts.HasGoalBranch(GoalWorktrees.BranchName(id)) ||
                GoalWorktrees.TryResolve(executionDirectory, id) is not null ||
                GoalWorktrees.TryGetCleanupBackoff(executionDirectory, id, cleanupHooks) is not null)
            .Distinct()
            .ToArray();
    }

    private static int CountMetadataOnlyTerminalSweepExclusions(
        ITransactionalOrchestratorStateRepository stateRepository,
        string executionDirectory,
        GoalId? onlyGoalId,
        GoalWorktreeCleanupHooks cleanupHooks)
    {
        if (onlyGoalId is not null)
        {
            return 0;
        }

        var summaries = stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult();
        var hydratedSweepCandidateIds = ResolveTerminalSweepCandidateIds(summaries, executionDirectory, onlyGoalId, cleanupHooks)
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

    private static bool IsConductLoopTerminalStatus(string status) => ConductLoopGoalStatus.IsTerminal(status);

    private static ConductLoopDependencyMetadata ReadConductLoopDependencyMetadata(
        GoalSummary summary,
        string? executionDirectory)
    {
        if (executionDirectory is null || !IsConductLoopTerminalStatus(summary.Status))
        {
            return new ConductLoopDependencyMetadata(summary.Id, summary.Status, IsLanded: false);
        }

        var journal = TerminalGoalJournalMetadataCache.Read(executionDirectory, new GoalId(summary.Id));
        var isLanded = journal.IsLanded;
        var status = !isLanded &&
            journal.IsRetiredDisposition &&
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
            OperatorIntentVerbs.VerifyManual => preparedCommand.ManualVerification is { } manual
                ? new ManualVerificationOperatorIntentPayload(Request: new ManualVerificationRequest(
                    manual.ExitCode == 0,
                    manual.ExitCode == 0 ? manual.AuthoritativeStandardOutput : manual.AuthoritativeStandardError,
                    manual.WorkingDirectory))
                : throw new InvalidOperationException("Prepared verify-manual command is missing verification evidence."),
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

    private static bool SubmitCriterionEvidenceOperatorIntent(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref Goal? currentGoal,
        OperatorIntentSubmissionSource submissionSource)
    {
        var goalSelector = ResolveFlagValue(args, "--goal")
            ?? throw new ArgumentException($"{args[0]} requires --goal <goal-prefix>.");
        var goalId = ResolveSingleGoalCommandGoalId(stateRepository, currentGoal?.Id.Value, goalSelector);
        var snapshot = stateRepository.LoadGoalAsync(goalId).GetAwaiter().GetResult()
            ?? throw new KeyNotFoundException($"Goal '{goalId.Value}' was not found.");
        var kernel = KernelFromGoalSnapshot(snapshot, []);
        var goal = kernel.GetGoal(goalId);
        CliCriterionEvidenceIntents.Submit(args, workspace, goal.Id, ResolveOperatorIntentAttribution(args, submissionSource));
        currentGoal = goal;
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
            command.RetryPolicy.Name,
            command.RetryCause);
    }

    private static string? ResolveFlagValue(IReadOnlyList<string> args, string flag) =>
        CliCriterionEvidenceIntents.ResolveFlagValue(args, flag);

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

    private static bool ExecuteGoalReplacementOutsideTransaction(
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
            throw new InvalidOperationException("goal-replace requires a state repository with durable outbox support.");

        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);
        var claimStore = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        if (TryReplayCommittedGoalReplacement(
                args,
                kernel,
                claimStore,
                ref currentGoal,
                out var replayResult))
        {
            return replayResult;
        }
        var predecessorForLease = OrchestratorEntityResolver.ResolveGoal(
            kernel,
            currentGoal,
            args.Count > 1 ? args[1] : null);
        var deferredEventWriter = new DeferredGoalLifecycleEventWriter();
        var deferredCollaborationWriter = new DeferredGoalCreationCollaborationWriter(
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
        string? committedGoalId = null;

        void FinalizeGoalReplacement(Goal preparedGoal, GoalReplacementCommand command)
        {
            GoalRefinementWorkCoordinator.RecordPending(kernel, preparedGoal.Id);
            var preparedSnapshot = kernel.ExportGoalSnapshot(preparedGoal.Id);
            var fingerprintInputs = BuildGoalReplacementFingerprint(command, preparedSnapshot);
            var fingerprint = fingerprintInputs.Fingerprint;
            GoalCreationSideEffectDelivery.BeforeStateCommit?.Invoke(preparedGoal.Id);
            GoalReplacementTransferLeaseAcquisition replacementLeaseAcquisition;
            try
            {
                replacementLeaseAcquisition = AcquireGoalReplacementTransferLease(
                    workspace.SqliteStatePath,
                    command.PredecessorGoalId.Value,
                    () => ResolveObservedClaim(
                        claimStore,
                        stateRepository,
                        preparedSnapshot.SourceBacklogItemId!));
            }
            catch (LegacySourceBacklogOwnerAmbiguousException ambiguous)
            {
                throw RecordLegacyOwnerAmbiguousFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    CreateUnavailableGoalReplacementFacts(predecessorForLease),
                    ambiguous);
            }
            if (replacementLeaseAcquisition.Kind == GoalReplacementTransferLeaseAcquisitionKind.ProtectedEvidenceMutationOwner)
            {
                var failureCode = replacementLeaseAcquisition.FailureCode ?? "evidence-mutation-active";
                var observedClaim = replacementLeaseAcquisition.ObservedClaim;
                if (observedClaim is null)
                {
                    TryRecordGoalReplacementFailure(
                        claimStore,
                        command,
                        preparedSnapshot,
                        fingerprint,
                        GoalReplacementOutcome.PersistenceFailed,
                        observedOwner: null,
                        observedVersion: null,
                        "claim-observation-unavailable",
                        CreateUnavailableGoalReplacementFacts(predecessorForLease));
                    throw new InvalidOperationException(
                        $"GOAL_REPLACE_PERSISTENCE_FAILED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} owner=unknown claimVersion=unknown reason=claim-observation-unavailable");
                }
                if (!string.Equals(observedClaim.OwnerGoalId, command.ExpectedOwnerGoalId, StringComparison.Ordinal) ||
                    observedClaim.Version != command.ExpectedClaimVersion)
                {
                    TryRecordGoalReplacementFailure(
                        claimStore,
                        command,
                        preparedSnapshot,
                        fingerprint,
                        GoalReplacementOutcome.CurrentOwnerConflict,
                        observedClaim.OwnerGoalId,
                        observedClaim.Version,
                        "current-owner-conflict",
                        CreateUnavailableGoalReplacementFacts(predecessorForLease));
                    throw new SourceBacklogClaimConflictException(
                        observedClaim.BacklogItemId,
                        command.PredecessorGoalId.Value,
                        observedClaim.OwnerGoalId,
                        observedClaim.Version);
                }
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.ProtectedOwner,
                    observedClaim.OwnerGoalId,
                    observedClaim.Version,
                    failureCode,
                    CreateUnavailableGoalReplacementFacts(predecessorForLease));
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_PROTECTED_OWNER predecessor={command.PredecessorGoalId.Value} reason={failureCode}");
            }
            if (replacementLeaseAcquisition.Kind == GoalReplacementTransferLeaseAcquisitionKind.CompetingReplacementTimedOut)
            {
                ThrowGoalReplacementTransferLeaseTimeout(
                    command,
                    replacementLeaseAcquisition,
                    (outcome, observedOwner, observedVersion, failureCode) =>
                        TryRecordGoalReplacementFailure(
                            claimStore,
                            command,
                            preparedSnapshot,
                            fingerprint,
                            outcome,
                            observedOwner,
                            observedVersion,
                            failureCode,
                            CreateUnavailableGoalReplacementFacts(predecessorForLease)));
            }
            using var replacementLeaseScope = replacementLeaseAcquisition.Lease
                ?? throw new InvalidOperationException(
                    "GOAL_REPLACE_STATE_INVALID reason=acquired-replacement-lease-missing");
            SourceBacklogClaimSnapshot? observedClaimUnderLease;
            try
            {
                observedClaimUnderLease = ResolveObservedClaim(
                    claimStore,
                    stateRepository,
                    preparedSnapshot.SourceBacklogItemId!);
            }
            catch (LegacySourceBacklogOwnerAmbiguousException ambiguous)
            {
                throw RecordLegacyOwnerAmbiguousFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    CreateUnavailableGoalReplacementFacts(predecessorForLease),
                    ambiguous);
            }
            if (observedClaimUnderLease is null)
            {
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.PersistenceFailed,
                    observedOwner: null,
                    observedVersion: null,
                    "claim-observation-unavailable",
                    CreateUnavailableGoalReplacementFacts(predecessorForLease));
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_PERSISTENCE_FAILED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} owner=unknown claimVersion=unknown reason=claim-observation-unavailable");
            }
            if (!string.Equals(observedClaimUnderLease.OwnerGoalId, command.ExpectedOwnerGoalId, StringComparison.Ordinal) ||
                observedClaimUnderLease.Version != command.ExpectedClaimVersion)
            {
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.CurrentOwnerConflict,
                    observedClaimUnderLease.OwnerGoalId,
                    observedClaimUnderLease.Version,
                    "current-owner-conflict",
                    CreateUnavailableGoalReplacementFacts(predecessorForLease));
                throw new SourceBacklogClaimConflictException(
                    observedClaimUnderLease.BacklogItemId,
                    command.PredecessorGoalId.Value,
                    observedClaimUnderLease.OwnerGoalId,
                    observedClaimUnderLease.Version);
            }
            var eligibilityKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            var eligibilityPredecessor = eligibilityKernel.Goals.SingleOrDefault(goal => goal.Id == command.PredecessorGoalId)
                ?? throw new InvalidOperationException(
                    $"GOAL_REPLACE_VALIDATION_REJECTED predecessor={command.PredecessorGoalId.Value} reason=predecessor-missing");
            var authorizedFacts = GoalReplacementEvidence.Capture(workspace, eligibilityPredecessor);
            var deliveryMessage = GoalCreationSideEffectDelivery.CreateMessage(
                preparedGoal.Id,
                deferredCollaborationWriter.SnapshotEffects(),
                deferredEventWriter.SnapshotEffects());
            var refinementMessage = GoalRefinementWorkCoordinator.CreateMessage(preparedGoal.Id);

            GoalReplacementReceipt receipt;
            try
            {
                // The committed replacement lease serializes every supported mutation of the
                // predecessor's eligibility evidence. Complete the Git/journal validation before
                // opening the state write transaction so a competing replacement can continue
                // polling the lease instead of escaping with SQLITE_BUSY while Git is running.
                var transferAuthority = GoalReplacementEvidence.CreateTransferAuthority(
                    workspace,
                    eligibilityPredecessor,
                    observedClaimUnderLease.BacklogItemId,
                    observedClaimUnderLease.Version,
                    authorizedFacts);
                var validatedFacts = transferAuthority.RevalidateForTransfer();

                using var backlogLease = new BacklogStore(workspace.BacklogStorePath)
                    .TryAcquireOpenItemLease(preparedSnapshot.SourceBacklogItemId!, out var observedBacklogStatus);
                if (backlogLease is null)
                {
                    throw new GoalReplacementValidationException(
                        $"GOAL_REPLACE_VALIDATION_REJECTED backlogItem={preparedSnapshot.SourceBacklogItemId} status={observedBacklogStatus?.ToString() ?? "missing"} reason=source-backlog-not-open",
                        "source-backlog-not-open");
                }

                receipt = outboxRepository.TransactWithOutboxAsync(
                        (currentKernel, _) =>
                        {
                            var existingAudit = claimStore.FindAuditInCurrentTransaction(command.RequestId);
                            if (existingAudit is not null)
                            {
                                if (!string.Equals(existingAudit.Fingerprint, fingerprint, StringComparison.Ordinal))
                                    throw new GoalReplacementIdempotencyConflictException(command.RequestId);
                                if (existingAudit.Outcome != GoalReplacementOutcome.Succeeded ||
                                    existingAudit.SuccessorGoalId is null)
                                {
                                    return Task.FromResult((
                                        ShouldSave: false,
                                        Result: new GoalReplacementReceipt(
                                            existingAudit.Outcome,
                                            command.RequestId,
                                            existingAudit.BacklogItemId,
                                            existingAudit.PredecessorGoalId,
                                            existingAudit.SuccessorGoalId,
                                            existingAudit.ObservedOwnerGoalId,
                                            existingAudit.ObservedClaimVersion,
                                            existingAudit.FailureCode ?? "replayed-failure"),
                                        OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
                                }

                                var replayClaim = claimStore.ResolveOrMaterializeClaim(currentKernel, existingAudit.BacklogItemId);
                                return Task.FromResult((
                                    ShouldSave: false,
                                    Result: new GoalReplacementReceipt(
                                        GoalReplacementOutcome.Replayed,
                                        command.RequestId,
                                        existingAudit.BacklogItemId,
                                        existingAudit.PredecessorGoalId,
                                        existingAudit.SuccessorGoalId,
                                        replayClaim.OwnerGoalId,
                                        replayClaim.Version,
                                        "idempotent-replay"),
                                    OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
                            }

                            var predecessor = currentKernel.Goals.FirstOrDefault(goal => goal.Id == command.PredecessorGoalId)
                                ?? throw new GoalReplacementValidationException(
                                    $"GOAL_REPLACE_VALIDATION_REJECTED predecessor={command.PredecessorGoalId.Value} reason=predecessor-missing",
                                    "predecessor-missing");
                            var backlogItemId = predecessor.SourceBacklogItemId
                                ?? throw new GoalReplacementValidationException(
                                    $"GOAL_REPLACE_VALIDATION_REJECTED predecessor={predecessor.Id.Value} reason=predecessor-has-no-source-backlog",
                                    "predecessor-has-no-source-backlog");
                            var claim = claimStore.ResolveOrMaterializeClaim(currentKernel, backlogItemId);
                            if (!string.Equals(claim.OwnerGoalId, predecessor.Id.Value, StringComparison.Ordinal))
                            {
                                throw new SourceBacklogClaimConflictException(
                                    backlogItemId,
                                    predecessor.Id.Value,
                                    claim.OwnerGoalId,
                                    claim.Version);
                            }
                            GoalCreationPreconditionValidator.Validate(
                                currentKernel,
                                preparedSnapshot,
                                workspace,
                                predecessor.Id.Value);

                            if (!GoalReplacementEvidence.MatchesStateFacts(predecessor, validatedFacts))
                            {
                                throw new GoalReplacementValidationException(
                                    $"GOAL_REPLACE_VALIDATION_REJECTED predecessor={predecessor.Id.Value} reason=eligibility-evidence-changed",
                                    "eligibility-evidence-changed");
                            }
                            var eligibility = SourceBacklogClaimEligibility.Evaluate(command.Disposition, validatedFacts);
                            if (eligibility != GoalReplacementOutcome.Succeeded)
                            {
                                var rejectedAudit = new GoalReplacementAuditSnapshot(
                                    command.RequestId,
                                    fingerprint,
                                    eligibility,
                                    backlogItemId,
                                    predecessor.Id.Value,
                                    null,
                                    command.Disposition,
                                    command.Reason,
                                    predecessor.Status,
                                    null,
                                    claim.Coverage,
                                    "operator",
                                    "cli",
                                    "local-process",
                                    DateTimeOffset.UtcNow,
                                    predecessor.Id.Value,
                                    claim.Version,
                                    claim.OwnerGoalId,
                                    claim.Version,
                                    validatedFacts,
                                    eligibility == GoalReplacementOutcome.ProtectedOwner
                                        ? validatedFacts.IsGitEvidenceAvailable
                                            ? "protected-owner"
                                            : "git-evidence-unavailable"
                                        : "ineligible-disposition",
                                    fingerprintInputs.ObjectiveHash,
                                    fingerprintInputs.OrderedRoles,
                                    fingerprintInputs.AssignedAgents);
                                claimStore.CommitRejectedAttempt(rejectedAudit);
                                return Task.FromResult((
                                    ShouldSave: false,
                                    Result: new GoalReplacementReceipt(
                                        eligibility,
                                        command.RequestId,
                                        backlogItemId,
                                        predecessor.Id.Value,
                                        null,
                                        claim.OwnerGoalId,
                                        claim.Version,
                                        rejectedAudit.FailureCode!),
                                    OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
                            }

                            var now = DateTimeOffset.UtcNow;
                            var audit = new GoalReplacementAuditSnapshot(
                                command.RequestId,
                                fingerprint,
                                GoalReplacementOutcome.Succeeded,
                                backlogItemId,
                                predecessor.Id.Value,
                                preparedSnapshot.Id,
                                command.Disposition,
                                command.Reason,
                                predecessor.Status,
                                preparedSnapshot.Status,
                                claim.Coverage,
                                "operator",
                                "cli",
                                "local-process",
                                now,
                                predecessor.Id.Value,
                                claim.Version,
                                predecessor.Id.Value,
                                claim.Version,
                                validatedFacts,
                                FailureCode: null,
                                ObjectiveHash: fingerprintInputs.ObjectiveHash,
                                OrderedRoles: fingerprintInputs.OrderedRoles,
                                AssignedAgents: fingerprintInputs.AssignedAgents);

                            var transferred = claimStore.CommitReplacement(
                                audit,
                                transferAuthority,
                                validatedFacts,
                                replacementLeaseAcquisition.LeaseOwner
                                    ?? throw new InvalidOperationException(
                                        "GOAL_REPLACE_STATE_INVALID reason=acquired-replacement-lease-owner-missing"));
                            currentKernel.ReplaceGoalWithSnapshot(preparedSnapshot);
                            currentKernel.RecordGoalPolicyDecision(
                                predecessor.Id,
                                $"Source backlog claim replaced by goal {preparedSnapshot.Id}; request={command.RequestId:D} disposition={command.Disposition}.");
                            currentKernel.RecordGoalPolicyDecision(
                                new GoalId(preparedSnapshot.Id),
                                $"Source backlog claim inherited from goal {predecessor.Id.Value}; request={command.RequestId:D} disposition={command.Disposition}.");
                            return Task.FromResult((
                                ShouldSave: true,
                                Result: new GoalReplacementReceipt(
                                    GoalReplacementOutcome.Succeeded,
                                    command.RequestId,
                                    backlogItemId,
                                    predecessor.Id.Value,
                                    preparedSnapshot.Id,
                                    transferred.OwnerGoalId,
                                    transferred.Version,
                                    "replaced"),
                                OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[deliveryMessage, refinementMessage]));
                        })
                    .GetAwaiter()
                    .GetResult();
            }
            catch (SourceBacklogClaimConflictException conflict)
            {
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.CurrentOwnerConflict,
                    conflict.ObservedOwnerGoalId,
                    conflict.ObservedVersion,
                    "current-owner-conflict",
                    authorizedFacts);
                throw;
            }
            catch (GoalReplacementIdempotencyConflictException)
            {
                throw;
            }
            catch (GoalReplacementTransferAuthorityException authorityFailure)
            {
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.ValidationRejected,
                    observedClaimUnderLease.OwnerGoalId,
                    observedClaimUnderLease.Version,
                    authorityFailure.ReasonCode,
                    authorizedFacts,
                    authorityFailure.ObservedFacts);
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_VALIDATION_REJECTED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} reason={authorityFailure.ReasonCode}",
                    authorityFailure);
            }
            catch (GoalReplacementValidationException validation)
            {
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.ValidationRejected,
                    observedClaimUnderLease.OwnerGoalId,
                    observedClaimUnderLease.Version,
                    validation.ReasonCode,
                    authorizedFacts);
                throw;
            }
            catch (InvalidOperationException precondition)
                when (GoalCreationPreconditionValidator.GetReason(precondition) is not null)
            {
                var reasonCode = GoalCreationPreconditionValidator.GetReason(precondition)!;
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.ValidationRejected,
                    observedClaimUnderLease.OwnerGoalId,
                    observedClaimUnderLease.Version,
                    reasonCode,
                    authorizedFacts);
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_VALIDATION_REJECTED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} reason={reasonCode} detail={precondition.Message}",
                    precondition);
            }
            catch (LegacySourceBacklogOwnerAmbiguousException ambiguous)
            {
                throw RecordLegacyOwnerAmbiguousFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    authorizedFacts,
                    ambiguous);
            }
            catch (Exception ex)
            {
                var observedClaim = ResolveObservedClaimAfterReplacementFailure(
                    claimStore,
                    stateRepository,
                    preparedSnapshot.SourceBacklogItemId!);
                var failureCode = observedClaim is null
                    ? "transaction-failed-claim-observation-unavailable"
                    : "transaction-failed";
                TryRecordGoalReplacementFailure(
                    claimStore,
                    command,
                    preparedSnapshot,
                    fingerprint,
                    GoalReplacementOutcome.PersistenceFailed,
                    observedClaim?.OwnerGoalId,
                    observedClaim?.Version,
                    failureCode,
                    authorizedFacts);
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_PERSISTENCE_FAILED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} owner={observedClaim?.OwnerGoalId ?? "unknown"} claimVersion={observedClaim?.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} reason={failureCode} detail={ex.Message}",
                    ex);
            }

            committedGoalId = receipt.SuccessorGoalId;
            if (receipt.Outcome == GoalReplacementOutcome.Succeeded)
            {
                _ = DeliverGoalCreationSideEffects(outboxRepository, workspace, kernel, preparedGoal.Id);
                deferredCollaborationWriter.CompleteDelivery();
                deferredEventWriter.CompleteDeliveryTo(
                    new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, kernel: kernel));
                _ = GoalRefinementWorkCoordinator.TryLaunch(workspace, preparedGoal.Id);
            }
            var outcomeCode = RenderGoalReplacementOutcomeCode(receipt.Outcome);
            var receiptOwner = receipt.OwnerGoalId ?? "unknown";
            var receiptVersion = receipt.ClaimVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
            Console.WriteLine(
                $"GOAL_REPLACE_{outcomeCode} requestId={receipt.RequestId:D} predecessor={receipt.PredecessorGoalId} successor={receipt.SuccessorGoalId} backlogItem={receipt.BacklogItemId} owner={receiptOwner} claimVersion={receiptVersion}");
            if (receipt.Outcome is not (GoalReplacementOutcome.Succeeded or GoalReplacementOutcome.Replayed))
            {
                throw new InvalidOperationException(
                    $"GOAL_REPLACE_{outcomeCode} predecessor={receipt.PredecessorGoalId} backlogItem={receipt.BacklogItemId} owner={receiptOwner} claimVersion={receiptVersion} reason={receipt.ReasonCode}");
            }
        }

        bool shouldSave;
        try
        {
            shouldSave = CliCommandDispatcher.ExecuteCommand(
                args,
                kernel,
                workspace,
                ref agents,
                providers,
                ref workerProfiles,
                ref currentGoal,
                channel,
                () => stateRepository.LoadAsync().GetAwaiter().GetResult(),
                eventWriter: deferredEventWriter,
                refinementCollaborationItemRaise: deferredCollaborationWriter.RaiseAsync,
                finalizeGoalReplacement: FinalizeGoalReplacement);
        }
        catch (Exception ex)
        {
            if (ex is not GoalReplacementRetryableTimeoutException &&
                (!Guid.TryParse(TryGetGoalReplacementFlag(args, "--request-id"), out var failedRequestId) ||
                 claimStore.FindAudit(failedRequestId) is null))
            {
                TryRecordGoalReplacementPreflightFailure(claimStore, args, kernel, workspace, ex);
            }
            throw;
        }

        if (committedGoalId is null)
            throw new InvalidOperationException("GOAL_REPLACE_COMMIT_MISSING reason=finalizer-not-invoked");

        var committedKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = committedKernel.Goals.Single(goal => goal.Id.Value == committedGoalId);
        ConsoleViews.PrintGoal(currentGoal);
        return shouldSave;
    }

    private sealed class GoalReplacementValidationException(string message, string reasonCode)
        : InvalidOperationException(message)
    {
        public string ReasonCode { get; } = reasonCode;
    }

    internal enum GoalReplacementTransferLeaseAcquisitionKind
    {
        Acquired,
        ProtectedEvidenceMutationOwner,
        CompetingReplacementTimedOut
    }

    internal sealed record GoalReplacementTransferLeaseAcquisition(
        GoalReplacementTransferLeaseAcquisitionKind Kind,
        IDisposable? Lease = null,
        string? LeaseOwner = null,
        SourceBacklogClaimSnapshot? ObservedClaim = null,
        string? FailureCode = null);

    internal static void ThrowGoalReplacementTransferLeaseTimeout(
        GoalReplacementCommand command,
        GoalReplacementTransferLeaseAcquisition acquisition,
        Action<GoalReplacementOutcome, string?, long?, string> recordTerminalFailure)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(recordTerminalFailure);
        if (acquisition.Kind != GoalReplacementTransferLeaseAcquisitionKind.CompetingReplacementTimedOut)
        {
            throw new InvalidOperationException(
                $"GOAL_REPLACE_STATE_INVALID reason=non-timeout-lease-resolution kind={acquisition.Kind}");
        }

        var observedClaim = acquisition.ObservedClaim;
        if (observedClaim is null)
        {
            const string failureCode = "claim-observation-unavailable";
            recordTerminalFailure(GoalReplacementOutcome.PersistenceFailed, null, null, failureCode);
            throw new InvalidOperationException(
                $"GOAL_REPLACE_PERSISTENCE_FAILED requestId={command.RequestId:D} predecessor={command.PredecessorGoalId.Value} owner=unknown claimVersion=unknown reason={failureCode}");
        }

        if (!string.Equals(observedClaim.OwnerGoalId, command.ExpectedOwnerGoalId, StringComparison.Ordinal) ||
            observedClaim.Version != command.ExpectedClaimVersion)
        {
            recordTerminalFailure(
                GoalReplacementOutcome.CurrentOwnerConflict,
                observedClaim.OwnerGoalId,
                observedClaim.Version,
                "current-owner-conflict");
            throw new SourceBacklogClaimConflictException(
                observedClaim.BacklogItemId,
                command.PredecessorGoalId.Value,
                observedClaim.OwnerGoalId,
                observedClaim.Version);
        }

        // The claim CAS precondition still holds. The competing lease can disappear after this
        // bounded wait, so do not create a permanent idempotency audit for a retryable timeout.
        throw new GoalReplacementRetryableTimeoutException(
            command.RequestId,
            command.PredecessorGoalId.Value,
            observedClaim.OwnerGoalId,
            observedClaim.Version);
    }

    private static string RenderGoalReplacementOutcomeCode(GoalReplacementOutcome outcome) => outcome switch
    {
        GoalReplacementOutcome.Succeeded => "SUCCEEDED",
        GoalReplacementOutcome.Replayed => "REPLAYED",
        GoalReplacementOutcome.ValidationRejected => "VALIDATION_REJECTED",
        GoalReplacementOutcome.IneligibleDisposition => "INELIGIBLE_DISPOSITION",
        GoalReplacementOutcome.ProtectedOwner => "PROTECTED_OWNER",
        GoalReplacementOutcome.CurrentOwnerConflict => "CURRENT_OWNER_CONFLICT",
        GoalReplacementOutcome.LegacyOwnerAmbiguous => "LEGACY_OWNER_AMBIGUOUS",
        GoalReplacementOutcome.PersistenceFailed => "PERSISTENCE_FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown goal replacement outcome.")
    };

    private static (string Fingerprint, string ObjectiveHash, string OrderedRoles, string AssignedAgents) BuildGoalReplacementFingerprint(
        GoalReplacementCommand command,
        GoalSnapshot preparedSnapshot)
    {
        var normalizedObjective = string.Join(
            '\n',
            command.Objective.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.TrimEnd()))
            .Trim();
        var objectiveHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedObjective)))
            .ToLowerInvariant();
        var orderedRoles = string.Join(',', preparedSnapshot.Tasks.Select(task => task.RequiredRole.ToString()));
        var assignedAgents = string.Join(
            ',',
            preparedSnapshot.Tasks.Select(task => $"{task.RequiredRole}={task.AssignedAgentId ?? "unassigned"}"));
        var fingerprint = ComputeGoalReplacementFingerprint(
            command.PredecessorGoalId.Value,
            preparedSnapshot.SourceBacklogItemId!,
            command.Disposition,
            command.Reason,
            objectiveHash,
            preparedSnapshot.SourceBacklogCoverage ?? SourceBacklogCoverage.Full,
            orderedRoles,
            command.RequestedPipeline,
            command.RequestedAgentOverrides,
            assignedAgents);
        return (fingerprint, objectiveHash, orderedRoles, assignedAgents);
    }

    private static string ComputeGoalReplacementFingerprint(
        string predecessorGoalId,
        string backlogItemId,
        GoalReplacementDisposition disposition,
        string reason,
        string objectiveHash,
        SourceBacklogCoverage coverage,
        string orderedRoles,
        string requestedPipeline,
        string requestedAgentOverrides,
        string assignedAgents) =>
        ComputeGoalReplacementFingerprint(
            predecessorGoalId,
            backlogItemId,
            disposition.ToString(),
            reason,
            objectiveHash,
            coverage,
            orderedRoles,
            requestedPipeline,
            requestedAgentOverrides,
            assignedAgents);

    private static string ComputeGoalReplacementFingerprint(
        string predecessorGoalId,
        string backlogItemId,
        string dispositionMaterial,
        string reason,
        string objectiveHash,
        SourceBacklogCoverage coverage,
        string orderedRoles,
        string requestedPipeline,
        string requestedAgentOverrides,
        string assignedAgents)
    {
        var material = string.Join(
            "|",
            predecessorGoalId,
            backlogItemId,
            dispositionMaterial,
            reason.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
            objectiveHash,
            coverage,
            orderedRoles,
            requestedPipeline,
            requestedAgentOverrides,
            assignedAgents);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private static string ComputeLegacyGoalReplacementFingerprint(
        string predecessorGoalId,
        string backlogItemId,
        GoalReplacementDisposition disposition,
        string reason,
        string objectiveHash,
        SourceBacklogCoverage coverage,
        string orderedRoles)
    {
        var material = string.Join(
            "|",
            predecessorGoalId,
            backlogItemId,
            disposition,
            reason.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
            objectiveHash,
            coverage,
            orderedRoles);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    internal static GoalReplacementTransferLeaseAcquisition AcquireGoalReplacementTransferLease(
        string stateDbPath,
        string predecessorGoalId,
        Func<SourceBacklogClaimSnapshot?> resolveCurrentClaim,
        TimeProvider? timeProvider = null,
        Action<TimeSpan>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(resolveCurrentClaim);
        timeProvider ??= GoalReplacementTransferLeaseClock.Current;
        wait ??= GoalReplacementTransferLeaseClock.CurrentWait;
        var store = new ReconcileSweepRemediationStore(stateDbPath);
        var owner = $"goal-replace:{Environment.ProcessId}:{Guid.NewGuid():N}";
        var deadline = timeProvider.GetUtcNow().AddSeconds(15);
        do
        {
            var lease = store.TryAcquireAcceptanceLease(predecessorGoalId, owner, TimeSpan.FromMinutes(30));
            if (lease is not null)
                return new GoalReplacementTransferLeaseAcquisition(
                    GoalReplacementTransferLeaseAcquisitionKind.Acquired,
                    Lease: lease,
                    LeaseOwner: owner);

            AfterGoalReplacementLeaseAcquisitionFailed?.Invoke();
            var activeOwner = store.TryGetAcceptanceLeaseOwner(predecessorGoalId);
            if (activeOwner is null)
            {
                wait(TimeSpan.FromMilliseconds(10));
                continue;
            }
            if (!activeOwner.StartsWith("goal-replace:", StringComparison.Ordinal))
            {
                var observedClaim = resolveCurrentClaim();
                var confirmedOwner = store.TryGetAcceptanceLeaseOwner(predecessorGoalId);
                if (!string.Equals(activeOwner, confirmedOwner, StringComparison.Ordinal))
                {
                    wait(TimeSpan.FromMilliseconds(10));
                    continue;
                }
                return new GoalReplacementTransferLeaseAcquisition(
                    GoalReplacementTransferLeaseAcquisitionKind.ProtectedEvidenceMutationOwner,
                    ObservedClaim: observedClaim,
                    FailureCode: activeOwner.StartsWith("goal-evidence:", StringComparison.Ordinal)
                        ? "evidence-mutation-active"
                        : "landing-operation-active");
            }
            wait(TimeSpan.FromMilliseconds(10));
        }
        while (timeProvider.GetUtcNow() < deadline);

        return new GoalReplacementTransferLeaseAcquisition(
            GoalReplacementTransferLeaseAcquisitionKind.CompetingReplacementTimedOut,
            ObservedClaim: resolveCurrentClaim());
    }

    private static bool TryReplayCommittedGoalReplacement(
        IReadOnlyList<string> args,
        AgentOrchestratorKernel kernel,
        SourceBacklogClaimStore claimStore,
        ref Goal? currentGoal,
        out bool result)
    {
        result = false;
        if (!args.Any(part => part.Equals("--confirm-goal-replace", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!Guid.TryParse(TryGetGoalReplacementFlag(args, "--request-id"), out var requestId) ||
            claimStore.FindAudit(requestId) is not { } audit)
        {
            return false;
        }

        var predecessorSelector = args.Count > 1 ? args[1].Trim() : string.Empty;
        if (predecessorSelector.Length < 8 ||
            !audit.PredecessorGoalId.StartsWith(predecessorSelector, StringComparison.OrdinalIgnoreCase))
        {
            throw new GoalReplacementIdempotencyConflictException(requestId);
        }

        var briefPath = TryGetGoalReplacementFlag(args, "--brief-file");
        var reasonPath = TryGetGoalReplacementFlag(args, "--reason-file");
        var hasBriefFile = !string.IsNullOrWhiteSpace(briefPath) && File.Exists(briefPath);
        var hasReasonFile = !string.IsNullOrWhiteSpace(reasonPath) && File.Exists(reasonPath);
        var hasDisposition = TryParseGoalReplacementDisposition(
            TryGetGoalReplacementFlag(args, "--disposition"),
            out var disposition);
        if (!hasBriefFile || !hasReasonFile || !hasDisposition)
        {
            var preflightFingerprint = ComputeUnpreparedGoalReplacementFingerprint(
                args,
                audit.PredecessorGoalId,
                audit.BacklogItemId,
                audit.Coverage,
                hasBriefFile ? File.ReadAllText(briefPath!) : string.Empty,
                hasReasonFile ? File.ReadAllText(reasonPath!).Trim() : string.Empty,
                hasDisposition,
                disposition);
            if (!string.Equals(preflightFingerprint, audit.Fingerprint, StringComparison.Ordinal))
                throw new GoalReplacementIdempotencyConflictException(requestId);
            ThrowReplayedGoalReplacementFailure(audit);
        }

        var normalizedObjective = string.Join(
            '\n',
            File.ReadAllText(briefPath).Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.TrimEnd()))
            .Trim();
        var objectiveHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(normalizedObjective)))
            .ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(audit.ObjectiveHash) &&
            !string.Equals(objectiveHash, audit.ObjectiveHash, StringComparison.Ordinal))
        {
            throw new GoalReplacementIdempotencyConflictException(requestId);
        }
        var replayOrderedRoles = audit.OrderedRoles;
        if (string.IsNullOrWhiteSpace(replayOrderedRoles))
        {
            replayOrderedRoles = audit.SuccessorGoalId is { } legacySuccessorGoalId
                ? string.Join(',', kernel.Goals
                    .SingleOrDefault(goal => goal.Id.Value == legacySuccessorGoalId)?.Tasks
                    .Select(task => task.RequiredRole) ?? [])
                : ResolveLegacyGoalReplacementReplayRoles(args);
        }
        if (string.IsNullOrWhiteSpace(replayOrderedRoles))
            throw new GoalReplacementIdempotencyConflictException(requestId);
        var fingerprint = string.IsNullOrWhiteSpace(audit.AssignedAgents)
            ? ComputeLegacyGoalReplacementFingerprint(
                audit.PredecessorGoalId,
                audit.BacklogItemId,
                disposition,
                File.ReadAllText(reasonPath).Trim(),
                objectiveHash,
                audit.Coverage,
                replayOrderedRoles)
            : ComputeGoalReplacementFingerprint(
                audit.PredecessorGoalId,
                audit.BacklogItemId,
                disposition,
                File.ReadAllText(reasonPath).Trim(),
                objectiveHash,
                audit.Coverage,
                replayOrderedRoles,
                NormalizeGoalReplacementPipelineRequest(args),
                BuildGoalReplacementRequestedAgentOverrides(args),
                audit.AssignedAgents);
        if (!string.Equals(fingerprint, audit.Fingerprint, StringComparison.Ordinal))
            throw new GoalReplacementIdempotencyConflictException(requestId);

        if (audit.Outcome != GoalReplacementOutcome.Succeeded || audit.SuccessorGoalId is null)
            ThrowReplayedGoalReplacementFailure(audit);
        var successorGoalId = audit.SuccessorGoalId;

        var successor = kernel.Goals.SingleOrDefault(goal => goal.Id.Value == successorGoalId)
            ?? throw new InvalidOperationException(
                $"GOAL_REPLACE_REPLAY_UNAVAILABLE requestId={requestId:D} successor={successorGoalId} reason=successor-missing");
        var claim = claimStore.ResolveClaim(kernel, audit.BacklogItemId)
            ?? throw new InvalidOperationException(
                $"GOAL_REPLACE_REPLAY_UNAVAILABLE requestId={requestId:D} backlogItem={audit.BacklogItemId} reason=source-claim-missing");
        currentGoal = successor;
        Console.WriteLine(
            $"GOAL_REPLACE_REPLAYED requestId={requestId:D} predecessor={audit.PredecessorGoalId} successor={successorGoalId} backlogItem={audit.BacklogItemId} owner={claim.OwnerGoalId} claimVersion={claim.Version}");
        ConsoleViews.PrintGoal(successor);
        result = true;
        return true;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowReplayedGoalReplacementFailure(GoalReplacementAuditSnapshot audit)
    {
        var failureOutcomeCode = RenderGoalReplacementOutcomeCode(audit.Outcome);
        var failureOwner = audit.ObservedOwnerGoalId ?? "unknown";
        var failureVersion = audit.ObservedClaimVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        Console.WriteLine(
            $"GOAL_REPLACE_{failureOutcomeCode} requestId={audit.RequestId:D} predecessor={audit.PredecessorGoalId} successor={audit.SuccessorGoalId} backlogItem={audit.BacklogItemId} owner={failureOwner} claimVersion={failureVersion}");
        throw new InvalidOperationException(
            $"GOAL_REPLACE_{failureOutcomeCode} predecessor={audit.PredecessorGoalId} backlogItem={audit.BacklogItemId} owner={failureOwner} claimVersion={failureVersion} reason={audit.FailureCode ?? "replayed-failure"}");
    }

    private static string ComputeUnpreparedGoalReplacementFingerprint(
        IReadOnlyList<string> args,
        string predecessorGoalId,
        string backlogItemId,
        SourceBacklogCoverage coverage,
        string objective,
        string reason,
        bool hasValidDisposition,
        GoalReplacementDisposition disposition)
    {
        var normalizedObjective = string.Join(
            '\n',
            objective.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.TrimEnd()))
            .Trim();
        var objectiveHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(normalizedObjective)))
            .ToLowerInvariant();
        var pipeline = NormalizeGoalReplacementPipelineRequest(args);
        var orderedRoles = pipeline.Equals("five-role", StringComparison.OrdinalIgnoreCase)
            ? string.Join(',', new[]
            {
                AgentRole.Researcher,
                AgentRole.Planner,
                AgentRole.Developer,
                AgentRole.Tester,
                AgentRole.Reviewer
            })
            : $"unprepared:{pipeline}";
        var requestedDisposition = TryGetGoalReplacementFlag(args, "--disposition")?.Trim();
        return ComputeGoalReplacementFingerprint(
            predecessorGoalId,
            backlogItemId,
            hasValidDisposition
                ? disposition.ToString()
                : $"invalid:{requestedDisposition?.ToLowerInvariant() ?? "missing"}",
            reason,
            objectiveHash,
            coverage,
            orderedRoles,
            pipeline,
            BuildGoalReplacementRequestedAgentOverrides(args),
            "unprepared");
    }

    private static string ResolveLegacyGoalReplacementReplayRoles(IReadOnlyList<string> args)
    {
        var pipeline = TryGetGoalReplacementFlag(args, "--pipeline");
        if (!string.Equals(pipeline, "five-role", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return string.Join(',', new[]
        {
            AgentRole.Researcher,
            AgentRole.Planner,
            AgentRole.Developer,
            AgentRole.Tester,
            AgentRole.Reviewer
        });
    }

    private static void TryRecordGoalReplacementFailure(
        SourceBacklogClaimStore claimStore,
        GoalReplacementCommand command,
        GoalSnapshot preparedSnapshot,
        string fingerprint,
        GoalReplacementOutcome outcome,
        string? observedOwner,
        long? observedVersion,
        string failureCode,
        GoalReplacementEligibilityFacts eligibilityFacts,
        GoalReplacementEligibilityFacts? observedFacts = null)
    {
        try
        {
            var fingerprintInputs = BuildGoalReplacementFingerprint(command, preparedSnapshot);
            claimStore.RecordFailedAttempt(new GoalReplacementAuditSnapshot(
                command.RequestId,
                fingerprint,
                outcome,
                preparedSnapshot.SourceBacklogItemId!,
                command.PredecessorGoalId.Value,
                null,
                command.Disposition,
                command.Reason,
                eligibilityFacts.Status,
                null,
                preparedSnapshot.SourceBacklogCoverage ?? SourceBacklogCoverage.Full,
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.UtcNow,
                command.ExpectedOwnerGoalId,
                command.ExpectedClaimVersion,
                observedOwner,
                observedVersion,
                observedFacts ?? eligibilityFacts,
                failureCode,
                fingerprintInputs.ObjectiveHash,
                fingerprintInputs.OrderedRoles,
                fingerprintInputs.AssignedAgents));
        }
        catch (GoalReplacementIdempotencyConflictException)
        {
            throw;
        }
        catch (Exception auditFailure)
        {
            throw new InvalidOperationException(
                $"GOAL_REPLACE_AUDIT_UNAVAILABLE requestId={command.RequestId:D} originalOutcome={outcome} reason=durable-receipt-unavailable",
                auditFailure);
        }
    }

    internal static SourceBacklogClaimSnapshot? ResolveObservedClaimAfterReplacementFailure(
        SourceBacklogClaimStore claimStore,
        IOrchestratorStateRepository stateRepository,
        string backlogItemId)
    {
        try
        {
            BeforeGoalReplacementFailureClaimObservation?.Invoke();
            var currentKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            return claimStore.ResolveClaim(currentKernel, backlogItemId);
        }
        catch (LegacySourceBacklogOwnerAmbiguousException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static SourceBacklogClaimSnapshot? ResolveObservedClaim(
        SourceBacklogClaimStore claimStore,
        IOrchestratorStateRepository stateRepository,
        string backlogItemId)
    {
        try
        {
            var currentKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
            return claimStore.ResolveClaim(currentKernel, backlogItemId);
        }
        catch (LegacySourceBacklogOwnerAmbiguousException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static InvalidOperationException RecordLegacyOwnerAmbiguousFailure(
        SourceBacklogClaimStore claimStore,
        GoalReplacementCommand command,
        GoalSnapshot preparedSnapshot,
        string fingerprint,
        GoalReplacementEligibilityFacts eligibilityFacts,
        LegacySourceBacklogOwnerAmbiguousException ambiguous)
    {
        TryRecordGoalReplacementFailure(
            claimStore,
            command,
            preparedSnapshot,
            fingerprint,
            GoalReplacementOutcome.LegacyOwnerAmbiguous,
            string.Join(',', ambiguous.LinkedGoalIds),
            observedVersion: null,
            "legacy-owner-ambiguous",
            eligibilityFacts);
        return new InvalidOperationException(
            $"GOAL_REPLACE_LEGACY_OWNER_AMBIGUOUS requestId={command.RequestId:D} backlogItem={ambiguous.BacklogItemId} linkedGoals={string.Join(',', ambiguous.LinkedGoalIds)} reason=legacy-owner-ambiguous",
            ambiguous);
    }

    private static GoalReplacementEligibilityFacts CreateUnavailableGoalReplacementFacts(Goal predecessor) =>
        new(
            predecessor.Status,
            HasWorkspace: false,
            HasBranch: false,
            HasDispatch: predecessor.Tasks.Any(task => task.LastDispatch is not null),
            HasRepositoryDelta: false,
            IsRunning: predecessor.Tasks.Any(task => task.Status == WorkTaskStatus.Running),
            IsLanded: false,
            IsMerged: false,
            IsRecorded: false,
            IsGitEvidenceAvailable: false);

    private static void TryRecordGoalReplacementPreflightFailure(
        SourceBacklogClaimStore claimStore,
        IReadOnlyList<string> args,
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace,
        Exception exception)
    {
        try
        {
            if (!Guid.TryParse(TryGetGoalReplacementFlag(args, "--request-id"), out var requestId) ||
                args.Count < 2)
            {
                return;
            }

            var predecessorMatches = kernel.Goals
                .Where(goal => goal.Id.Value.Equals(args[1], StringComparison.OrdinalIgnoreCase) ||
                               goal.Id.Value.StartsWith(args[1], StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (predecessorMatches.Length != 1)
            {
                return;
            }

            var predecessor = predecessorMatches[0];
            var hasSourceBacklogItem = predecessor.SourceBacklogItemId is not null;
            var backlogItemId = predecessor.SourceBacklogItemId
                ?? GoalReplacementUnlinkedBacklogItemId(predecessor.Id);
            var requestedDisposition = TryGetGoalReplacementFlag(args, "--disposition")?.Trim();
            var hasValidDisposition = TryParseGoalReplacementDisposition(requestedDisposition, out var disposition);

            var reasonPath = TryGetGoalReplacementFlag(args, "--reason-file");
            var briefPath = TryGetGoalReplacementFlag(args, "--brief-file");
            var hasReasonFile = !string.IsNullOrWhiteSpace(reasonPath) && File.Exists(reasonPath);
            var hasBriefFile = !string.IsNullOrWhiteSpace(briefPath) && File.Exists(briefPath);
            var reason = hasReasonFile ? File.ReadAllText(reasonPath!).Trim() : string.Empty;
            var objective = hasBriefFile ? File.ReadAllText(briefPath!) : string.Empty;
            SourceBacklogClaimSnapshot? claim = null;
            try
            {
                if (hasSourceBacklogItem)
                    claim = claimStore.ResolveClaim(kernel, backlogItemId);
            }
            catch (LegacySourceBacklogOwnerAmbiguousException)
            {
            }

            GoalReplacementEligibilityFacts facts;
            try
            {
                facts = GoalReplacementEvidence.Capture(workspace, predecessor);
            }
            catch
            {
                facts = new GoalReplacementEligibilityFacts(
                    predecessor.Status,
                    HasWorkspace: false,
                    HasBranch: false,
                    HasDispatch: predecessor.Tasks.Any(task => task.LastDispatch is not null),
                    HasRepositoryDelta: false,
                    IsRunning: predecessor.Tasks.Any(task => task.Status == WorkTaskStatus.Running),
                    IsLanded: false,
                    IsMerged: false,
                    IsRecorded: false,
                    IsGitEvidenceAvailable: false);
            }

            var coverage = claim?.Coverage ?? predecessor.SourceBacklogCoverage ?? SourceBacklogCoverage.Full;
            var expectedOwner = claim?.OwnerGoalId ?? predecessor.Id.Value;
            var expectedVersion = claim?.Version ?? 0;
            var outcome = exception switch
            {
                SourceBacklogClaimConflictException => GoalReplacementOutcome.CurrentOwnerConflict,
                LegacySourceBacklogOwnerAmbiguousException => GoalReplacementOutcome.LegacyOwnerAmbiguous,
                _ => GoalReplacementOutcome.ValidationRejected
            };
            var observedOwner = exception is SourceBacklogClaimConflictException conflict
                ? conflict.ObservedOwnerGoalId
                : claim?.OwnerGoalId;
            var observedVersion = exception is SourceBacklogClaimConflictException ownerConflict
                ? ownerConflict.ObservedVersion
                : claim?.Version;
            var failureCode = ResolveGoalReplacementPreflightFailureCode(
                exception,
                hasValidDisposition,
                hasBriefFile,
                hasReasonFile,
                reason);
            var pipeline = NormalizeGoalReplacementPipelineRequest(args);
            var normalizedObjective = string.Join(
                '\n',
                objective.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n')
                    .Select(line => line.TrimEnd()))
                .Trim();
            var objectiveHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(normalizedObjective)))
                .ToLowerInvariant();
            var orderedRoles = pipeline.Equals("five-role", StringComparison.OrdinalIgnoreCase)
                ? string.Join(',', new[]
                {
                    AgentRole.Researcher,
                    AgentRole.Planner,
                    AgentRole.Developer,
                    AgentRole.Tester,
                    AgentRole.Reviewer
                })
                : $"unprepared:{pipeline}";
            const string assignedAgents = "unprepared";
            var requestedAgentOverrides = BuildGoalReplacementRequestedAgentOverrides(args);
            var fingerprint = ComputeGoalReplacementFingerprint(
                predecessor.Id.Value,
                backlogItemId,
                hasValidDisposition
                    ? disposition.ToString()
                    : $"invalid:{requestedDisposition?.ToLowerInvariant() ?? "missing"}",
                reason,
                objectiveHash,
                coverage,
                orderedRoles,
                pipeline,
                requestedAgentOverrides,
                assignedAgents);

            if (claimStore.FindAudit(requestId) is { } existingAudit)
            {
                if (!string.Equals(existingAudit.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new GoalReplacementIdempotencyConflictException(requestId);
                return;
            }

            claimStore.RecordFailedAttempt(new GoalReplacementAuditSnapshot(
                requestId,
                fingerprint,
                outcome,
                backlogItemId,
                predecessor.Id.Value,
                null,
                disposition,
                reason,
                predecessor.Status,
                null,
                coverage,
                "operator",
                "cli",
                "local-process",
                DateTimeOffset.UtcNow,
                expectedOwner,
                expectedVersion,
                observedOwner,
                observedVersion,
                facts,
                failureCode,
                objectiveHash,
                orderedRoles,
                assignedAgents));
        }
        catch (GoalReplacementIdempotencyConflictException)
        {
            throw;
        }
        catch (Exception auditFailure)
        {
            throw new InvalidOperationException(
                $"GOAL_REPLACE_AUDIT_UNAVAILABLE requestId={TryGetGoalReplacementFlag(args, "--request-id") ?? "unknown"} reason=durable-receipt-unavailable",
                auditFailure);
        }
    }

    private static string? TryGetGoalReplacementFlag(IReadOnlyList<string> args, string flag)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }

    private static string GoalReplacementUnlinkedBacklogItemId(GoalId predecessorGoalId) =>
        $"unlinked:{predecessorGoalId.Value}";

    private static string NormalizeGoalReplacementPipelineRequest(IReadOnlyList<string> args)
    {
        var pipeline = TryGetGoalReplacementFlag(args, "--pipeline")?.Trim();
        return pipeline?.ToLowerInvariant() switch
        {
            null or "" or "auto" => "auto",
            "five-role" => "five-role",
            _ => pipeline.ToLowerInvariant()
        };
    }

    private static string BuildGoalReplacementRequestedAgentOverrides(IReadOnlyList<string> args)
    {
        (string Flag, AgentRole Role)[] flags =
        [
            ("--ideation", AgentRole.Ideation),
            ("--researcher", AgentRole.Researcher),
            ("--planner", AgentRole.Planner),
            ("--developer", AgentRole.Developer),
            ("--tester", AgentRole.Tester),
            ("--reviewer", AgentRole.Reviewer)
        ];
        return string.Join(
            ',',
            flags.OrderBy(entry => entry.Role)
                .Select(entry => (entry.Role, AgentId: TryGetGoalReplacementFlag(args, entry.Flag)))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.AgentId))
                .Select(entry => $"{entry.Role}={entry.AgentId}"));
    }

    private static bool TryParseGoalReplacementDisposition(
        string? value,
        out GoalReplacementDisposition disposition)
    {
        disposition = value?.Trim().ToLowerInvariant() switch
        {
            "zero-work-correction" => GoalReplacementDisposition.ZeroWorkCorrection,
            "abandon-failed-attempt" => GoalReplacementDisposition.AbandonFailedAttempt,
            "supersede-unlanded-attempt" => GoalReplacementDisposition.SupersedeUnlandedAttempt,
            _ => GoalReplacementDisposition.Unspecified
        };
        return value?.Trim().ToLowerInvariant() is
            "zero-work-correction" or "abandon-failed-attempt" or "supersede-unlanded-attempt";
    }

    private static string ExtractGoalReplacementFailureCode(string message)
    {
        const string marker = "reason=";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return "validation-rejected";
        start += marker.Length;
        var end = message.IndexOfAny([' ', '\r', '\n'], start);
        return end < 0 ? message[start..] : message[start..end];
    }

    private static string ResolveGoalReplacementPreflightFailureCode(
        Exception exception,
        bool hasValidDisposition,
        bool hasBriefFile,
        bool hasReasonFile,
        string reason)
    {
        if (!hasValidDisposition)
            return "invalid-disposition";
        if (!hasBriefFile)
            return "brief-file-missing";
        if (!hasReasonFile)
            return "reason-file-missing";
        if (string.IsNullOrWhiteSpace(reason))
            return "reason-required";
        return ExtractGoalReplacementFailureCode(exception.Message);
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
        var request = GoalIntakeRequestMapper.Map(args, workspace, agents);
        GoalIntakeRequestStore? requestStore = null;
        GoalIntakeRequestRecord? intakeRecord = null;
        if (request is not null)
        {
            requestStore = new GoalIntakeRequestStore(workspace.SqliteStatePath);
            var reservation = requestStore.Reserve(request.RequestKey, request.Fingerprint);
            intakeRecord = reservation.Record;
            if (reservation.Kind == GoalIntakeReservationKind.Replay)
            {
                ConsoleViews.PrintGoalIntakeReceipt(reservation.Record);
                if (reservation.Record.State == GoalIntakeRequestStates.Created)
                {
                    var replayKernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
                    currentGoal = replayKernel.Goals.FirstOrDefault(goal =>
                        goal.Id.Value.Equals(reservation.Record.GoalId, StringComparison.Ordinal));
                    if (currentGoal is null)
                    {
                        throw new InvalidOperationException(
                            $"GOAL_INTAKE_RECEIPT_INVALID requestKey={request.RequestKey} reason=created-goal-missing");
                    }
                    ConsoleViews.PrintGoal(currentGoal);
                    return false;
                }
                if (reservation.Record.State == GoalIntakeRequestStates.Failed)
                {
                    throw new InvalidOperationException(
                        $"GOAL_INTAKE_REPLAY_FAILED requestKey={request.RequestKey} " +
                        $"failureCode={reservation.Record.FailureCode} detail={reservation.Record.FailureDetail}");
                }

                return false;
            }
        }
        GoalSnapshot? committedSnapshot = null;
        var deferredEventWriter = new DeferredGoalLifecycleEventWriter();
        var deferredCollaborationWriter = new DeferredGoalCreationCollaborationWriter(
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));

        void FinalizeGoalCreation(Goal goal)
        {
            GoalRefinementWorkCoordinator.RecordPending(kernel, goal.Id);
            var preparedSnapshot = kernel.ExportGoalSnapshot(goal.Id);
            GoalCreationSideEffectDelivery.BeforeStateCommit?.Invoke(goal.Id);
            var deliveryMessage = GoalCreationSideEffectDelivery.CreateMessage(
                goal.Id,
                deferredCollaborationWriter.SnapshotEffects(),
                deferredEventWriter.SnapshotEffects());
            var refinementMessage = GoalRefinementWorkCoordinator.CreateMessage(goal.Id);
            outboxRepository.TransactWithOutboxAsync(
                    (currentKernel, _) =>
                    {
                        GoalCreationPreconditionValidator.Validate(currentKernel, preparedSnapshot, workspace);
                        currentKernel.ReplaceGoalWithSnapshot(preparedSnapshot);
                        if (request is not null)
                        {
                            intakeRecord = requestStore!.MarkCreated(
                                request.RequestKey,
                                request.Fingerprint,
                                preparedSnapshot.Id);
                            if (request.Mode.StartsWith("backlog-", StringComparison.Ordinal) &&
                                preparedSnapshot.SourceBacklogItemId is { } sourceBacklogItemId)
                            {
                                new BacklogIntakeRecordStore(workspace.SqliteStatePath)
                                    .MarkGoalCreated(sourceBacklogItemId, preparedSnapshot.Id);
                            }
                        }
                        return Task.FromResult((
                            ShouldSave: true,
                            Result: true,
                            OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)[deliveryMessage, refinementMessage]));
                    })
                .GetAwaiter()
                .GetResult();
            committedSnapshot = preparedSnapshot;
            if (intakeRecord is not null)
                ConsoleViews.PrintGoalIntakeReceipt(intakeRecord);
            try
            {
                _ = DeliverGoalCreationSideEffects(outboxRepository, workspace, kernel, goal.Id);
                deferredCollaborationWriter.CompleteDelivery();
                deferredEventWriter.CompleteDeliveryTo(
                    new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, kernel: kernel));
                _ = GoalRefinementWorkCoordinator.TryLaunch(workspace, goal.Id);
            }
            catch (Exception ex)
            {
                throw GoalCreationDeliveryIncomplete(goal.Id, ex);
            }
        }

        bool shouldSave;
        try
        {
            shouldSave = CliCommandDispatcher.ExecuteCommand(
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
                refinementCollaborationItemRaise: deferredCollaborationWriter.RaiseAsync,
                reportGoalCreationProgress: () =>
                {
                    if (intakeRecord is not null)
                        ConsoleViews.PrintGoalIntakeReceipt(intakeRecord);
                });
            if (committedSnapshot is null)
            {
                throw new InvalidOperationException("GOAL_CREATE_COMMIT_MISSING reason=finalizer-not-invoked");
            }
        }
        catch (InvalidOperationException ex)
            when (request is null &&
                  ex.Message.StartsWith("GOAL_INTAKE_BACKLOG_ALREADY_HAS_GOAL", StringComparison.Ordinal))
        {
            return false;
        }
        catch (Exception ex)
            when (committedSnapshot is null && GoalCreationPreconditionValidator.GetReason(ex) is not null)
        {
            currentGoal = null;
            if (request is not null)
            {
                _ = requestStore!.MarkFailed(
                    request.RequestKey,
                    request.Fingerprint,
                    ResolveGoalIntakeFailureCode(ex),
                    ex.Message);
            }
            throw;
        }
        catch (Exception ex) when (request is not null && committedSnapshot is null)
        {
            _ = requestStore!.MarkFailed(
                request.RequestKey,
                request.Fingerprint,
                ResolveGoalIntakeFailureCode(ex),
                ex.Message);
            throw;
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

    private static bool IsGoalRefinementWorkCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals(GoalRefinementWorkCoordinator.CommandName, StringComparison.OrdinalIgnoreCase);

    private static bool ExecuteGoalRefinementWork(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        if (args.Count is < 2 or > 3 || string.IsNullOrWhiteSpace(args[1]))
            throw new InvalidOperationException($"Usage: {GoalRefinementWorkCoordinator.CommandName} <goal-id> [executor-stamp]");
        if (stateRepository is not IOrchestratorStateOutboxRepository outboxRepository)
            throw new InvalidOperationException("Goal refinement requires durable outbox support.");

        var result = GoalRefinementWorkCoordinator.ProcessAsync(
                outboxRepository,
                workspace,
                providers,
                workerProfiles,
                new GoalId(args[1]),
                rawOutputStamp: args.Count == 3 ? args[2] : null)
            .GetAwaiter()
            .GetResult();
        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = kernel.Goals.FirstOrDefault(goal => goal.Id.Value.Equals(args[1], StringComparison.Ordinal));
        GoalRefinementWorkOutcomeReporter.Report(result, workspace);
        return false;
    }

    private static string ResolveGoalIntakeFailureCode(Exception exception)
    {
        var firstToken = exception.Message
            .Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (firstToken is not null &&
            firstToken.Length <= 100 &&
            firstToken.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_'))
        {
            return firstToken;
        }

        return exception is ArgumentException
            ? "GOAL_INTAKE_ARGUMENT_INVALID"
            : "GOAL_INTAKE_CREATE_FAILED";
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
                        throw GoalCreationPreconditionValidator.Changed(
                            "created-goal-concurrently-modified",
                            ("goal", committedSnapshot.Id));
                    }

                    return Task.FromResult<(bool ShouldSave, GoalSnapshot? NewSnapshot, bool Result)>(
                        (true, finalSnapshot, true));
                })
            .GetAwaiter()
            .GetResult();
    }

    private static bool ExecuteProcessRefreshOutsideTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        WorktreeCleanupContext? cleanupContext = null)
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
            return ExecuteGlobalProcessReconcile(args, stateRepository, workspace, ref currentGoal, cleanupContext);
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
                var refreshed = new GoalDispatchOperations().RefreshDispatches(kernel, refreshGoal);
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
        ref Goal? currentGoal,
        WorktreeCleanupContext? cleanupContext = null)
    {
        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        currentGoal = ResolveCurrentGoal(kernel, currentGoal?.Id.Value);

        var candidates = CaptureRunningProcessIdentities(kernel);
        var runner = new BackgroundDispatchRunner();
        var reconciled = runner.SweepExitedProcesses(kernel);
        cleanupContext ??= WorktreeCleanupContext.Load(attentionStoreDirectory: workspace.OrchestratorDirectory);
        cleanupContext.Scheduler.SweepIfDue(workspace.ExecutionDirectory, kernel);
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
        bool persistOnlyCurrentGoal = false,
        WorktreeCleanupContext? cleanupContext = null,
        Func<TimeSpan?, Action<DotnetBuildStableSlotWait>?, DotnetBuildEnvironmentLease>? stableSlotSelector = null)
    {
        if (args.Count > 0 && args[0].Equals("acceptance-queue", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteAcceptanceQueueOutsideTransaction(args, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, channel, acceptanceVerifier, cleanupContext, stableSlotSelector);
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
        var conductTickBaselines = new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal)
        {
            [initialGoalSnapshot.Id] = initialGoalSnapshot
        };

        var reconcileStarted = System.Diagnostics.Stopwatch.StartNew();
        cleanupContext ??= WorktreeCleanupContext.Load(attentionStoreDirectory: workspace.OrchestratorDirectory);
        var targetSweep = TerminalGoalSweep.Run(
            kernel,
            workspace.ExecutionDirectory,
            goalId,
            cleanupHooks: cleanupContext.Hooks,
            orchestratorDirectory: workspace.OrchestratorDirectory);
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
        var criticalCheckpointRejected = false;

        void Persist(AgentOrchestratorKernel checkpoint) =>
            PersistSingleGoalSnapshot(stateRepository, checkpoint, goalId);

        void PersistCurrentGoal(AgentOrchestratorKernel checkpoint, GoalId goalId)
        {
            PersistSingleGoalSnapshot(stateRepository, checkpoint, goalId);
        }

        void ApplyCriticalDurableResults(
            AgentOrchestratorKernel checkpoint,
            IReadOnlyList<GoalSnapshotSaveResult> results)
        {
            foreach (var result in results)
            {
                _ = RebaseCheckpointAfterDurableSave(checkpoint, result);
                if (result.PersistedSnapshot is not null)
                    conductTickBaselines[result.GoalId] = result.PersistedSnapshot;
            }
        }

        void PersistCriticalCurrentGoal(
            AgentOrchestratorKernel checkpoint,
            IReadOnlyCollection<GoalId> changedGoalIds)
        {
            try
            {
                PersistCriticalGoalSnapshotsOrThrow(
                    stateRepository,
                    checkpoint,
                    changedGoalIds,
                    conductTickBaselines,
                    workspace.SqliteStatePath,
                    results => ApplyCriticalDurableResults(checkpoint, results));
            }
            catch (DispatchCheckpointConflictException)
            {
                // The rejected checkpoint already rebased this kernel to the store's authoritative
                // snapshot. A generic end-of-command write would reopen a second-writer window and
                // could replace a newer operator update with that just-rebased snapshot.
                criticalCheckpointRejected = true;
                throw;
            }
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

                        var outstandingEvidence = transactionKernel.GetGoal(request.GoalId)
                            .GetOutstandingCriterionEvidenceObligations(request.TestedWorktreeHead);
                        if (outstandingEvidence.Count > 0)
                        {
                            var evidenceMismatch = new AcceptanceMergeGuardMismatch(
                                AcceptanceMergeGuardMismatchKind.CriterionEvidence,
                                "Outstanding criterion evidence",
                                "none",
                                string.Join(", ", outstandingEvidence.Select(item => $"{item.Id}:{item.Owner}:{item.State}")));
                            var guardedResult = GuardedAcceptanceAbort(
                                request.GoalId,
                                evidenceMismatch,
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
            prepareAcceptanceMergeGuard: PrepareAcceptanceMergeGuard,
            persistCriticalGoalKernel: PersistCriticalCurrentGoal,
            recordDurableGoalBaseline: snapshot => conductTickBaselines[snapshot.Id] = snapshot,
            stableSlotSelector: stableSlotSelector,
            cleanupContext: cleanupContext);

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
                if (!criticalCheckpointRejected)
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

                    ReplayAcceptancePreflightRepairs(
                        initialSnapshot,
                        proposedKernel,
                        currentKernel,
                        request.GoalId,
                        request.CurrentBranchHeadSha,
                        request.CurrentMainHeadSha);
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

    private static bool ExecuteAcceptanceQueueOutsideTransaction(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null,
        IGoalAcceptanceVerifier? acceptanceVerifier = null,
        WorktreeCleanupContext? cleanupContext = null,
        Func<TimeSpan?, Action<DotnetBuildStableSlotWait>?, DotnetBuildEnvironmentLease>? stableSlotSelector = null)
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
                request),
            cleanupContext: cleanupContext,
            stableSlotSelector: stableSlotSelector);

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
            return CliSingleGoalReportSelector.ResolveGoalPrefix(parts, GetOptionalArgument);
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
