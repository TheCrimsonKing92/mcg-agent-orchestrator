using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    private static List<CollaborationItem> OpenClarifications(CollaborationItemStore store)
    {
        return store.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(IsSpecClarification)
            .ToList();
    }

    internal static List<CollaborationItem> AllClarifications(CollaborationItemStore store)
    {
        return store.ListAsync().GetAwaiter().GetResult()
            .Where(IsSpecClarification)
            .ToList();
    }

    internal static List<CollaborationItem> AllClarificationsForGoal(CollaborationItemStore store, Goal goal) =>
        AllClarifications(store)
            .Where(item => string.Equals(item.GoalId, goal.Id.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();

    internal static bool IsSpecClarification(CollaborationItem item) =>
        !string.IsNullOrWhiteSpace(item.CorrelationKey) &&
        item.CorrelationKey!.StartsWith("spec-clarification:", StringComparison.Ordinal);

    internal static Goal ResolveAttentionGoal(AgentOrchestratorKernel kernel, string goalPrefix) =>
        AttentionReadGoalSelection.Resolve(kernel, goalPrefix);

    private static CollaborationItem ResolveAttentionItemById(
        IReadOnlyList<CollaborationItem> items,
        string itemId)
    {
        var matches = items
            .Where(item => CollaborationItemLifecycle.IsReachUpType(item.Type))
            .Where(item => item.Id.StartsWith(itemId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"No attention item matches id '{itemId}'."),
            _ => throw new ArgumentException(
                $"Attention item id '{itemId}' is ambiguous ({matches.Count} matches); use the full item id from `attention show`.")
        };
    }

    internal static IReadOnlyList<HumanInputRequest> HumanWaitsForAttention(
        AgentOrchestratorKernel kernel, GoalId? goalId, bool includeHistory) =>
        AttentionHumanWaitQuery.Read(kernel, goalId, includeHistory);

    internal static IReadOnlyList<CollaborationItem> CollaborationItemsForAttention(
        IReadOnlyList<CollaborationItem> items,
        AgentOrchestratorKernel kernel,
        GoalId? goalId,
        bool includeHistory)
    {
        return items
            .Where(item => CollaborationItemLifecycle.IsReachUpType(item.Type))
            .Where(item => goalId is null || string.Equals(item.GoalId, goalId.Value, StringComparison.OrdinalIgnoreCase))
            .Where(item => includeHistory || item.GoalId is null || AttentionHumanWaitQuery.IsLiveAttentionGoal(kernel, new GoalId(item.GoalId)))
            .ToList();
    }

    private static int ResolveParkedAttentionItems(AgentOrchestratorKernel kernel, CollaborationItemStore store)
    {
        var resolved = 0;
        foreach (var goal in kernel.Goals.Where(goal => goal.Status == GoalStatus.Parked))
        {
            var resolution = goal.Timeline
                .LastOrDefault(evt =>
                    evt.Kind == ProgressKind.GoalPolicyDecision &&
                    evt.Message.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase))
                ?.Message ?? "Goal parked.";
            resolved += store.ResolveOpenForGoalAsync(goal.Id.Value, resolution).GetAwaiter().GetResult();
        }

        return resolved;
    }

    private static bool IsAttentionAllFlag(string value) =>
        value.Equals("--all", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--include-parked", StringComparison.OrdinalIgnoreCase);

    private static (bool IncludeHistory, string? GoalPrefix) ParseAttentionShowArgs(IReadOnlyList<string> parts)
    {
        var args = parts.Skip(2).Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        var includeHistory = args.RemoveAll(IsAttentionAllFlag) > 0;
        if (args.Count == 0)
        {
            return (includeHistory, null);
        }

        if (args[0].Equals("--goal", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 2)
            {
                throw new ArgumentException("Usage: attention show [--all|--include-parked] [--goal] <goal-id-prefix>");
            }

            return (includeHistory, args[1]);
        }

        return (includeHistory, args[0]);
    }

    private static CollaborationItem ResolveClarificationByShortId(
        IReadOnlyList<CollaborationItem> clarifications,
        IReadOnlyList<CollaborationItem> identityUniverse,
        string id,
        string notFoundMessage,
        string ambiguousMessage)
    {
        return TryResolveClarificationByShortId(
                clarifications,
                identityUniverse,
                id,
                ambiguousMessage)
            ?? throw new ArgumentException(notFoundMessage);
    }

    private static CollaborationItem? TryResolveClarificationByShortId(
        IReadOnlyList<CollaborationItem> clarifications,
        IReadOnlyList<CollaborationItem> identityUniverse,
        string id,
        string ambiguousMessage)
    {
        var exactCorrelationMatch = clarifications
            .FirstOrDefault(c => string.Equals(c.CorrelationKey, id, StringComparison.OrdinalIgnoreCase));
        if (exactCorrelationMatch is not null)
            return exactCorrelationMatch;

        var matches = clarifications
            .Where(c =>
                c.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase) ||
                ClarificationId(c, identityUniverse).StartsWith(id, StringComparison.OrdinalIgnoreCase) ||
                ClarificationTopicId(c.CorrelationKey!).StartsWith(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count > 1)
            throw new ArgumentException(string.Format(ambiguousMessage, matches.Count));

        return matches.Count == 1 ? matches[0] : null;
    }

    private static CollaborationItem? ResolveClarificationByExactShortId(
        IReadOnlyList<CollaborationItem> clarifications,
        IReadOnlyList<CollaborationItem> identityUniverse,
        string id,
        string ambiguousMessage)
    {
        var matches = clarifications
            .Where(c =>
                string.Equals(c.CorrelationKey, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ClarificationId(c, identityUniverse), id, StringComparison.OrdinalIgnoreCase) ||
                c.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count > 1)
            throw new ArgumentException(string.Format(ambiguousMessage, matches.Count));

        return matches.Count == 1 ? matches[0] : null;
    }

    // Use the complete trailing topic segment when its shape is unambiguous to the CLI parser. For a true
    // topic collision or a single-token topic, display the full correlation key as the stable escape hatch.
    // The identity universe includes resolved history, so answering a peer never changes the remaining id.
    internal static string ClarificationId(
        CollaborationItem clarification,
        IReadOnlyList<CollaborationItem> identityUniverse)
    {
        var correlationKey = clarification.CorrelationKey!;
        var topicId = ClarificationTopicId(correlationKey);
        var collides = identityUniverse.Any(other =>
            !string.Equals(other.CorrelationKey, correlationKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                ClarificationTopicId(other.CorrelationKey!),
                topicId,
                StringComparison.OrdinalIgnoreCase));
        return collides || !IsSelfDescribingClarificationTopicId(topicId) ? correlationKey : topicId;
    }

    private static string ClarificationTopicId(string correlationKey) =>
        correlationKey[(correlationKey.LastIndexOf(':') + 1)..];

    private static bool IsSelfDescribingClarificationTopicId(string topicId) =>
        (topicId.Length <= 8 && topicId.All(Uri.IsHexDigit)) ||
        topicId.Contains('-', StringComparison.Ordinal);

    private static bool? TryExecuteSystemCommand(string command, IReadOnlyList<string> parts, CliExecutionContext context)
    {
        switch (command)
        {
            case "acceptance-engine":
            {
                var circuit = PostLandingCanaryFactory.CreateCircuit(context.Workspace);
                if (parts.Count == 1 ||
                    (parts.Count == 2 && parts[1].Equals("status", StringComparison.OrdinalIgnoreCase)))
                {
                    var health = circuit.Read();
                    var decision = AcceptanceEngineAcceptanceGate.Decide(
                        health.Health,
                        AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy);
                    Console.WriteLine(
                        $"Acceptance engine: {health.Health}; landing={health.LandingSha ?? "none"}; " +
                        $"reason={health.FailureReason ?? "none"}; receipt={health.ReceiptReference ?? "none"}; " +
                        $"decision=\"{decision.Reason}\"");
                    return false;
                }

                if (parts.Count >= 3 && parts[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    var note = string.Join(' ', parts.Skip(2));
                    var health = circuit.Clear(note);
                    Console.WriteLine(
                        $"Acceptance engine circuit cleared at {health.UpdatedAt:O}. Acceptance and landing may resume.");
                    return false;
                }

                throw new ArgumentException(
                    "Usage: acceptance-engine status | acceptance-engine clear <repair-or-operator-note>");
            }

            case "gate-status":
                GateStatusRemoteWait.PrintText(context.Workspace);
                return false;

            case "run-events-maintenance":
                HandleRunEventsMaintenance(parts, context);
                return false;

            case "state-db-maintenance":
                HandleStateDatabaseMaintenance(parts, context);
                return false;

            case "run-event":
            {
                if (parts.Count is < 3 or > 5 ||
                    !parts[1].Equals("show", StringComparison.OrdinalIgnoreCase) ||
                    !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
                    sequence < 1)
                {
                    throw new ArgumentException(CliCommandHelp.RunEventUsage);
                }

                var format = "text";
                if (parts.Count > 3)
                {
                    if (parts.Count != 5 ||
                        !parts[3].Equals("--format", StringComparison.OrdinalIgnoreCase) ||
                        (!parts[4].Equals("json", StringComparison.OrdinalIgnoreCase) &&
                         !parts[4].Equals("text", StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new ArgumentException(CliCommandHelp.RunEventUsage);
                    }

                    format = parts[4].ToLowerInvariant();
                }

                var record = new SqliteRunEventStore(context.Workspace.RunEventStorePath)
                    .ReadBySequenceAsync(sequence)
                    .GetAwaiter()
                    .GetResult();
                if (record is null)
                {
                    throw new InvalidOperationException($"Run event run-event:{sequence} was not found.");
                }

                if (format == "json")
                {
                    Console.WriteLine(JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                }
                else
                {
                    Console.WriteLine($"run-event:{record.Sequence}");
                    Console.WriteLine($"occurred-at: {record.OccurredAt:O}");
                    Console.WriteLine($"event-type: {record.EventType}");
                    Console.WriteLine($"goal: {record.GoalId ?? "none"}");
                    Console.WriteLine($"operation: {record.Operation ?? "none"}");
                    Console.WriteLine($"status: {record.Status ?? "none"}");
                    Console.WriteLine($"detail: {record.Detail ?? "none"}");
                    Console.WriteLine($"payload-json: {record.PayloadJson ?? "none"}");
                }

                return false;
            }

            case "cleanup-status":
                PrintCleanupStatus(context.Workspace.ExecutionDirectory, context.CleanupContext.Hooks);
                return false;

            case "attention":
            {
                var store = context.IsReadOnlyQuery
                    ? CollaborationItemStore.OpenExisting(context.Workspace.OrchestratorDirectory)
                    : CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory);

                // `attention dismiss --item <item-id>` resolves one item, including cross-goal items.
                // The legacy goal-prefix selector remains available for bulk cleanup.
                if (parts.Count > 1 && parts[1].Equals("dismiss", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Count == 4 && parts[2].Equals("--item", StringComparison.OrdinalIgnoreCase))
                    {
                        var item = ResolveAttentionItemById(
                            store.ListAsync().GetAwaiter().GetResult(),
                            parts[3]);
                        if (CollaborationItemLifecycle.IsTerminal(item.Status))
                        {
                            throw new InvalidOperationException(
                                $"Attention item '{parts[3]}' matched, but status is '{item.Status}'; nothing was dismissed.");
                        }

                        if (!store.TryResolveByIdAsync(item.Id, "dismissed by operator").GetAwaiter().GetResult())
                        {
                            throw new InvalidOperationException(
                                $"Attention item '{parts[3]}' matched, but it was no longer open when dismissal was applied; nothing was dismissed.");
                        }

                        Console.WriteLine($"Dismissed attention item '{item.Id}'.");
                        return false;
                    }

                    var explicitGoal = parts.Count == 4 && parts[2].Equals("--goal", StringComparison.OrdinalIgnoreCase);
                    if ((parts.Count != 3 || parts[2].StartsWith("--", StringComparison.Ordinal)) && !explicitGoal)
                        throw new ArgumentException(CliCommandHelp.AttentionUsage);

                    var goalPrefix = explicitGoal ? parts[3] : parts[2];
                    var goal = ResolveAttentionGoal(context.Kernel, goalPrefix);
                    var open = store.GetAttentionQueueAsync().GetAwaiter().GetResult()
                        .Where(item => string.Equals(item.GoalId, goal.Id.Value, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (open.Count == 0)
                    {
                        Console.WriteLine(
                            $"Dismissed 0 of 0 open attention item(s) for goal '{goal.Id.Value}'.");
                        return false;
                    }

                    var dismissed = open.Count(item =>
                        store.TryResolveByIdAsync(item.Id, "dismissed by operator").GetAwaiter().GetResult());
                    if (dismissed == 0)
                    {
                        throw new InvalidOperationException(
                            $"Goal '{goal.Id.Value}' matched {open.Count} open attention item(s), but none remained eligible for dismissal.");
                    }

                    Console.WriteLine($"Dismissed {dismissed} of {open.Count} open attention item(s) for goal '{goal.Id.Value}'.");
                    return false;
                }

                // `attention show [--goal] <goal-id-prefix>`: list open typed human waits plus the same
                // nonterminal Decision/Clarification/Verify queue the board counts. Spec-clarification
                // rows keep short ids so `attention answer` still works. `--all` remains history.
                if (parts.Count > 1 && parts[1].Equals("show", StringComparison.OrdinalIgnoreCase))
                {
                    var showMigratedHumanWaits = context.IsReadOnlyQuery ? 0 : context.Kernel.SweepParkedGoalHumanWaits();
                    var showMigratedCollaborationItems = context.IsReadOnlyQuery ? 0 : ResolveParkedAttentionItems(context.Kernel, store);
                    var (includeHistory, goalPrefix) = ParseAttentionShowArgs(parts);
                    var changed = showMigratedHumanWaits > 0 || showMigratedCollaborationItems > 0;

                    if (goalPrefix is null)
                    {
                        PostLandingDutiesTextView.Print(context.Kernel.Goals);
                        var showWaits = HumanWaitsForAttention(context.Kernel, null, includeHistory);
                        if (showWaits.Count > 0 || context.Kernel.Goals.Any(goal => goal.GateTestObligations.Count > 0))
                        {
                            ConsoleViews.PrintHumanWaits(showWaits, DateTimeOffset.UtcNow, includeHistory, context.Kernel.Goals);
                        }
                        if (showWaits.Count > 0)
                        {
                            if (includeHistory)
                            {
                                var allItems = store.ListAsync().GetAwaiter().GetResult();
                                ConsoleViews.PrintCollaborationItems(
                                    CollaborationItemsForAttention(allItems, context.Kernel, null, includeHistory),
                                    includeHistory);
                            }

                            return changed;
                        }

                        var globalItems = includeHistory
                            ? store.ListAsync().GetAwaiter().GetResult()
                            : store.GetAttentionQueueAsync().GetAwaiter().GetResult();
                        var globalQueue = CollaborationItemsForAttention(globalItems, context.Kernel, null, includeHistory);
                        var allClarifications = AllClarifications(store);
                        ConsoleViews.PrintCollaborationItems(
                            globalQueue,
                            includeHistory,
                            item => IsSpecClarification(item) ? ClarificationId(item, allClarifications) : null);
                        return changed;
                    }

                    var goal = ResolveAttentionGoal(context.Kernel, goalPrefix);
                    PostLandingDutiesTextView.Print([goal]);
                    var clarificationIdentityUniverse = AllClarifications(store)
                        .Where(item => string.Equals(item.GoalId, goal.Id.Value, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var waitsForGoal = HumanWaitsForAttention(context.Kernel, goal.Id, includeHistory);
                    if (waitsForGoal.Count > 0 || goal.GateTestObligations.Count > 0)
                        ConsoleViews.PrintHumanWaits(waitsForGoal, DateTimeOffset.UtcNow, includeHistory, [goal]);

                    if (includeHistory)
                    {
                        var allItems = store.ListAsync().GetAwaiter().GetResult();
                        var historyItems = CollaborationItemsForAttention(allItems, context.Kernel, goal.Id, includeHistory);
                        if (waitsForGoal.Count > 0)
                        {
                            ConsoleViews.PrintCollaborationItems(
                                historyItems,
                                includeHistory,
                                item => IsSpecClarification(item) ? ClarificationId(item, clarificationIdentityUniverse) : null);
                            return changed;
                        }

                        if (historyItems.Count == 0)
                        {
                            Console.WriteLine($"No open attention items for goal {goal.Id.Value}.");
                            return changed;
                        }

                        ConsoleViews.PrintCollaborationItems(
                            historyItems,
                            includeHistory,
                            item => IsSpecClarification(item) ? ClarificationId(item, clarificationIdentityUniverse) : null);
                        return changed;
                    }

                    var scopedItems = store.GetAttentionQueueAsync().GetAwaiter().GetResult()
                        .Where(item => string.Equals(item.GoalId, goal.Id.Value, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (scopedItems.Count == 0)
                    {
                        if (waitsForGoal.Count == 0)
                            Console.WriteLine($"No open attention items for goal {goal.Id.Value}.");
                        return changed;
                    }

                    var printedClarification = false;
                    foreach (var item in scopedItems)
                    {
                        if (IsSpecClarification(item))
                        {
                            Console.WriteLine($"[{ClarificationId(item, clarificationIdentityUniverse)}] {item.Subject}");
                            if (!string.IsNullOrWhiteSpace(item.Body))
                                Console.WriteLine($"    {item.Body}");
                            printedClarification = true;
                            continue;
                        }

                        Console.WriteLine($"[{item.Id[..8]}] ({item.Type}) {item.Subject}");
                    }

                    if (printedClarification)
                    {
                        Console.WriteLine($"Answer with: attention answer {goal.Id.Value[..8]} <id> <answer> or --text-file <path> (ids are stable; answering one does not renumber the rest)");
                    }

                    return changed;
                }

                // `attention answer [<goal-id-prefix>] <id> <answer...>` resolves either identity printed by
                // `attention show`: collaboration clarifications retain precedence, then typed human waits.
                if (parts.Count > 1 && parts[1].Equals("answer", StringComparison.OrdinalIgnoreCase))
                {
                    return HandleAttentionAnswer(parts, context, store);
                }

                var migratedHumanWaits = context.IsReadOnlyQuery ? 0 : context.Kernel.SweepParkedGoalHumanWaits();
                var migratedCollaborationItems = context.IsReadOnlyQuery ? 0 : ResolveParkedAttentionItems(context.Kernel, store);
                var waits = HumanWaitsForAttention(context.Kernel, null, includeHistory: false);
                PostLandingDutiesTextView.Print(context.Kernel.Goals);
                if (waits.Count > 0 || context.Kernel.Goals.Any(goal => goal.GateTestObligations
                        .Any(item => item.BriefVersion == goal.AuthoritativeBrief.Version)))
                {
                    ConsoleViews.PrintHumanWaits(waits, DateTimeOffset.UtcNow, goals: context.Kernel.Goals);
                }
                if (waits.Count > 0)
                {
                    return migratedHumanWaits > 0 || migratedCollaborationItems > 0;
                }

                var queue = CollaborationItemsForAttention(
                    store.GetAttentionQueueAsync().GetAwaiter().GetResult(),
                    context.Kernel,
                    null,
                    includeHistory: false);
                ConsoleViews.PrintAttentionQueue(queue);
                return migratedHumanWaits > 0 || migratedCollaborationItems > 0;
            }

            case "doctor":
                HealthTextView.PrintHealth(OrchestratorHealthInspector.InspectCurrentEnvironment(new AgentCatalog(context.Agents), context.WorkerProfiles));
                return false;

            case "repo-process-info":
                RepoProcessCliCommand.PrintInfo(parts, Console.Out);
                return false;

            case "host-exclusions":
                if (CliHostExclusionsCommand.Run(parts) != 0)
                    throw new CliExitException(1);
                return false;

            case "repo-process-stop":
                if (!RepoProcessCliCommand.Stop(parts, Console.Out))
                {
                    throw new CliExitException(1);
                }
                return false;

            case "stable-slot-dotnet":
                CliStableSlotDotnetCommand.Run(parts, context);
                return false;

            case "tenant":
                ConsoleViews.PrintTenant(context.Workspace);
                return false;

            case "project":
                ProjectCliCommand.Execute(
                    parts,
                    OrchestratorProjectRegistry.CreateDefault(),
                    context.Workspace.RootDirectory,
                    activeProjectOverride: null);
                return false;

            case "trial-compare":
                TrialCompareCliCommand.Execute(
                    parts,
                    new SystemTrialRootHost(),
                    context.Workspace.TrialComparisonReceiptDirectory,
                    Console.Out,
                    selector => ResolveHistoricalTrial(context, selector));
                return false;

            case "hermes-acp-trial":
                HermesAcpCliCommand.ExecuteAsync(parts, Console.Out, Console.Error)
                    .GetAwaiter()
                    .GetResult();
                return false;

            case "hermes-acp-verify-identity":
                HermesAcpCliCommand.ExecuteIdentityVerificationAsync(parts, Console.Out)
                    .GetAwaiter()
                    .GetResult();
                return false;

            case "architecture":
                ConsoleViews.PrintArchitecture(BuildCliArchitectureReport(context));
                return false;

            case "provider-smoke":
                var smokeArgs = parts.Skip(1)
                    .Where(part => !part.Equals("--confirm-all", StringComparison.OrdinalIgnoreCase))
                    .Where(part => !part.Equals("--confirm-paid-smoke", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var confirmAllSmoke = parts.Any(part => part.Equals("--confirm-all", StringComparison.OrdinalIgnoreCase));
                var confirmPaidSmoke = parts.Any(part => part.Equals("--confirm-paid-smoke", StringComparison.OrdinalIgnoreCase));
                if (smokeArgs.Count > 2)
                {
                    throw new ArgumentException("Usage: provider-smoke [openai|anthropic|ollama] [--confirm-paid-smoke] [task-number], or provider-smoke all --confirm-all [task-number]. Omit the target for local Ollama only.");
                }

                var smokeTarget = smokeArgs.Count > 0 ? smokeArgs[0] : ProviderSmokeRunner.DefaultTarget;
                if (smokeTarget.Equals("all", StringComparison.OrdinalIgnoreCase) && !confirmAllSmoke)
                {
                    throw new InvalidOperationException(ProviderSmokeRunner.BuildBroadSmokeConfirmationMessage("--confirm-all"));
                }

                if (ProviderSmokeRunner.RequiresPaidConfirmation(smokeTarget) && !confirmAllSmoke && !confirmPaidSmoke)
                {
                    throw new InvalidOperationException(ProviderSmokeRunner.BuildPaidSmokeConfirmationMessage("--confirm-paid-smoke"));
                }

                TaskSpec? smokeTask = null;
                if (smokeArgs.Count > 1)
                {
                    context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
                    smokeTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, smokeArgs[1]);
                }

                var smokeEvidence = ProviderSmokeRunner.RunProviderSmoke(smokeTarget);
                if (smokeTask is not null)
                {
                    context.Kernel.RecordTaskVerification(
                        context.CurrentGoal!.Id,
                        smokeTask.Id,
                        new TaskVerificationRecord(
                            $"provider-smoke {smokeTarget}",
                            context.Workspace.RootDirectory,
                            0,
                            smokeEvidence,
                            string.Empty,
                            DateTimeOffset.UtcNow,
                            FullStandardOutput: smokeEvidence,
                            FullStandardError: string.Empty));
                    TaskTextView.PrintTask(context.CurrentGoal, smokeTask);
                    return true;
                }

                return false;

            case "model-outcomes":
                ModelOutcomeBoundModels.Print(context.Kernel.BuildModelOutcomeScorecard(), context.Workspace);
                return false;

            case "durations":
            {
                var since = CliSinceArgument.ParseOptional(GetFlagValue(parts, "--since"));
                ConsoleViews.PrintTaskDurationStats(
                    context.Kernel.BuildTaskDurationStats(HasCliConfirmation(parts, "--by-model"), since),
                    since,
                    context.Kernel.BuildTaskDurationTrend(since));
                return false;
            }

            case "dispatch-value":
            {
                var since = CliSinceArgument.ParseOptional(GetFlagValue(parts, "--since"));
                ConsoleViews.PrintDispatchValueReport(context.Kernel.BuildDispatchValueReport(since));
                return false;
            }

            case "loop-health":
            {
                int? lastN = null;
                var lastNStr = GetFlagValue(parts, "--last");
                if (lastNStr is not null && int.TryParse(lastNStr, out var parsedN) && parsedN > 0)
                {
                    lastN = parsedN;
                }
                ConsoleViews.PrintLoopHealthReport(context.Kernel.BuildLoopHealthReport(lastN));
                return false;
            }

            case "provenance":
            {
                var dogfoodPath = Path.Combine(context.Workspace.RootDirectory, "DOGFOOD_LOG.md");
                var dogfoodText = File.Exists(dogfoodPath) ? File.ReadAllText(dogfoodPath) : string.Empty;
                var executionDirectory = context.Workspace.ExecutionDirectory;
                var snapshot = context.Kernel.BuildProvenanceReport(
                    dogfoodText,
                    sha => GitCommitShaExists(executionDirectory, sha));
                ConsoleViews.PrintProvenanceReport(snapshot);
                if (snapshot.UnbackedGoalCount > 0)
                {
                    throw new InvalidOperationException(
                        $"Provenance check failed: {snapshot.UnbackedGoalCount} completed goal(s) are unbacked (missing verification receipts).");
                }
                if (snapshot.UnbackedCommitShas.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Provenance check failed: {snapshot.UnbackedCommitShas.Count} commit SHA(s) referenced in DOGFOOD_LOG.md are absent from git history.");
                }
                return false;
            }

            case "monitor-goal":
                throw new InvalidOperationException(CliMonitorGoalQueryCommand.WriterPathRefusal);

            default:
                return null;
        }
    }

    private static void HandleRunEventsMaintenance(IReadOnlyList<string> parts, CliExecutionContext context)
    {
        var retentionDays = GetFlagValue(parts, "--tick-max-age-days") is { } daysValue
            ? ParsePositiveInteger(daysValue, "--tick-max-age-days")
            : 7;
        var keepRows = GetFlagValue(parts, "--keep-tick-rows") is { } keepValue
            ? ParsePositiveInteger(keepValue, "--keep-tick-rows")
            : RunEventMaintenanceOptions.Default.MinConductorTickRowsToKeep;
        var payloadMaxBytes = GetFlagValue(parts, "--payload-max-bytes") is { } payloadValue
            ? ParsePositiveInteger(payloadValue, "--payload-max-bytes")
            : RunEventMaintenanceOptions.Default.MaxConductorTickPayloadBytes;
        var batchSize = GetFlagValue(parts, "--batch-size") is { } batchValue
            ? ParsePositiveInteger(batchValue, "--batch-size")
            : RunEventMaintenanceOptions.Default.DeleteBatchSize;
        var legacyPurge = HasCliConfirmation(parts, "--legacy-purge-oversized-ticks");
        var vacuum = HasCliConfirmation(parts, "--vacuum");
        var store = new SqliteRunEventStore(
            context.Workspace.RunEventStorePath,
            ensureSchema: !File.Exists(context.Workspace.RunEventStorePath));
        SqliteMaintenanceLease? vacuumLease = null;
        if (vacuum && !SqliteMaintenanceLease.TryAcquireExclusive(
                SqliteMaintenanceLease.ForDatabase(context.Workspace.RunEventStorePath), out vacuumLease))
        {
            throw new InvalidOperationException(
                "Full run-events VACUUM was refused because active conductor work holds the maintenance lease.");
        }

        using var heldVacuumLease = vacuumLease;
        var options = new RunEventMaintenanceOptions(
            TimeSpan.FromDays(retentionDays),
            keepRows,
            vacuum,
            MaxConductorTickPayloadBytes: payloadMaxBytes,
            DeleteBatchSize: batchSize,
            LegacyOversizedConductorTickPurge: legacyPurge,
            OfflineVacuumAuthorized: vacuum);
        var result = store.MaintainAsync(options)
            .GetAwaiter()
            .GetResult();

        var mode = legacyPurge ? "legacy-purge" : "manual";
        var status = result.Disposition.ToString().ToLowerInvariant();
        var receipt = RunEventMaintenanceCadence.FormatReceipt(mode, options, result);
        Console.WriteLine(receipt);
        try
        {
            new ConductEventLogWriter(context.Workspace.ConductEventsLogPath)
                .Append("run-events-maintenance", null, receipt);
        }
        catch
        {
        }

        Console.WriteLine($"run-events-maintenance status={status}");
        Console.WriteLine($"db={context.Workspace.RunEventStorePath}");
        Console.WriteLine($"mode={mode} tickMaxAgeDays={retentionDays} keepTickRows={keepRows} payloadMaxBytes={payloadMaxBytes} batchSize={Math.Clamp(batchSize, 1, 1000)}");
        Console.WriteLine($"conductorTickRowsDeleted={result.ConductorTickRowsDeleted}");
        Console.WriteLine($"agedConductorTickRowsDeleted={result.AgedConductorTickRowsDeleted}");
        Console.WriteLine($"oversizedConductorTickRowsDeleted={result.OversizedConductorTickRowsDeleted}");
        Console.WriteLine($"deletedPayloadBytesEstimate={result.DeletedPayloadBytesEstimate}");
        Console.WriteLine($"maxRowsDeletedInTransaction={result.MaxRowsDeletedInTransaction}");
        Console.WriteLine($"durationMs={(long)result.Duration.TotalMilliseconds}");
        Console.WriteLine($"bytesBefore={result.BytesBefore} bytesAfter={result.BytesAfter}");
        Console.WriteLine($"vacuumRequested={result.VacuumRequested} vacuumCompleted={result.VacuumCompleted} vacuumDeferred={result.VacuumDeferred}");
        if (!string.IsNullOrWhiteSpace(result.DeferredReason))
        {
            Console.WriteLine($"deferredReason={result.DeferredReason}");
        }

        RunEventMaintenanceCadence.TryAppendRunEventReceipt(
            store,
            mode,
            options,
            result,
            DateTimeOffset.UtcNow);
    }

    private static void HandleStateDatabaseMaintenance(
        IReadOnlyList<string> parts,
        CliExecutionContext context) =>
        HandleStateDatabaseMaintenance(
            parts,
            context,
            GateHeartbeatArtifacts.ReadStableSlots().Any(status => !status.IsAvailable),
            conversionUtcNow: null);

    internal static void HandleStateDatabaseMaintenance(
        IReadOnlyList<string> parts,
        CliExecutionContext context,
        bool activeGate,
        DateTimeOffset? conversionUtcNow)
    {
        if (parts.Count < 2 ||
            (!parts[1].Equals("plan", StringComparison.OrdinalIgnoreCase) &&
             !parts[1].Equals("execute", StringComparison.OrdinalIgnoreCase) &&
             !parts[1].Equals("convert-copy", StringComparison.OrdinalIgnoreCase) &&
             !parts[1].Equals("convert-live", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(CliCommandHelp.StateDatabaseMaintenanceUsage);
        }

        var databasePath = context.Workspace.SqliteStatePath;
        var activeDispatchOrGate = activeGate || context.Kernel.Goals.Any(goal =>
            goal.Tasks.Any(task =>
                task.Status == WorkTaskStatus.Running ||
                task.LastProcess is { IsRunning: true }));
        if (parts[1].Equals("plan", StringComparison.OrdinalIgnoreCase))
        {
            var leasePath = SqliteMaintenanceLease.ForDatabase(databasePath);
            var activeConductor = !SqliteMaintenanceLease.TryAcquireExclusive(leasePath, out var probeLease);
            probeLease?.Dispose();
            var metrics = StateDatabaseMaintenance.Probe(databasePath);
            var plan = StateDatabaseMaintenance.Plan(metrics, activeConductor, activeDispatchOrGate);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"STATE_DB_MAINTENANCE_PLAN decision={plan.Decision} reason={plan.Reason} activeConductor={activeConductor} activeDispatchOrGate={activeDispatchOrGate} pageCount={metrics.PageCount} pageSize={metrics.PageSize} freelistCount={metrics.FreelistCount} reclaimableBytes={metrics.ReclaimableBytes} databaseBytes={metrics.DatabaseBytes} journalMode={metrics.JournalMode} autoVacuum={metrics.AutoVacuumMode}"));
            return;
        }

        if (parts[1].Equals("convert-copy", StringComparison.OrdinalIgnoreCase) ||
            parts[1].Equals("convert-live", StringComparison.OrdinalIgnoreCase))
        {
            var conversionOptions = ResolveStateDatabaseConversionOptions(parts, activeDispatchOrGate, conversionUtcNow);
            var conversion = StateDatabaseOfflineConversion.ExecuteAsync(
                    databasePath,
                    conversionOptions)
                .GetAwaiter()
                .GetResult();
            WriteStateDatabaseMaintenanceReceipt(context, conversion.FormatReceipt(), "state-db-offline-conversion");
            return;
        }

        if (!HasCliConfirmation(parts, "--confirm-offline"))
            throw new ArgumentException(CliCommandHelp.StateDatabaseMaintenanceUsage);

        var result = StateDatabaseMaintenance.ExecuteAsync(databasePath, activeDispatchOrGate)
            .GetAwaiter()
            .GetResult();
        WriteStateDatabaseMaintenanceReceipt(context, result.FormatReceipt(), "state-db-maintenance");
    }

    internal static StateDatabaseOfflineConversionOptions ResolveStateDatabaseConversionOptions(
        IReadOnlyList<string> parts,
        bool activeDispatchOrGate,
        DateTimeOffset? utcNow = null)
    {
        var createCopy = parts.Count > 1 &&
            parts[1].Equals("convert-copy", StringComparison.OrdinalIgnoreCase);
        var replaceLive = parts.Count > 1 &&
            parts[1].Equals("convert-live", StringComparison.OrdinalIgnoreCase);
        var outputPath = GetFlagValue(parts, "--output");
        if ((!createCopy && !replaceLive) ||
            !HasCliConfirmation(parts, "--confirm-offline") ||
            (createCopy && string.IsNullOrWhiteSpace(outputPath)) ||
            (replaceLive && (!HasCliConfirmation(parts, "--confirm-live-replacement") || outputPath is not null)))
        {
            throw new ArgumentException(CliCommandHelp.StateDatabaseMaintenanceUsage);
        }

        return new StateDatabaseOfflineConversionOptions(
            createCopy
                ? StateDatabaseOfflineConversionMode.CreateCopy
                : StateDatabaseOfflineConversionMode.ReplaceLive,
            CopyOutputPath: outputPath,
            ExplicitlyAuthorized: true,
            ActiveDispatchOrGate: activeDispatchOrGate,
            UtcNow: utcNow);
    }

    private static void WriteStateDatabaseMaintenanceReceipt(
        CliExecutionContext context,
        string receipt,
        string eventKind)
    {
        Console.WriteLine(receipt);
        try
        {
            new ConductEventLogWriter(context.Workspace.ConductEventsLogPath)
                .Append(eventKind, null, receipt);
        }
        catch
        {
        }
    }

    private static bool GitCommitShaExists(string executionDirectory, string sha) =>
        GitCli.Run(executionDirectory, "cat-file", "-e", $"{sha}^{{commit}}").Succeeded;

    internal static bool ShouldBuildStableSlotMtpProject(bool noBuild, bool artifactsExist) =>
        CliStableSlotDotnetCommand.ShouldBuildMtpProject(noBuild, artifactsExist);

    internal static string ResolveStableSlotResultsDirectory(
        string? configuredResultsDirectory,
        string workspaceRoot,
        string defaultResultsDirectory) =>
        CliStableSlotDotnetCommand.ResolveResultsDirectory(configuredResultsDirectory, workspaceRoot, defaultResultsDirectory);

    internal static IReadOnlyList<string> TranslateStableSlotMtpFilter(string filter) =>
        CliStableSlotDotnetCommand.TranslateMtpFilter(filter);

    internal static int RunStableSlotProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool configureDotnetEnvironment) =>
        CliStableSlotDotnetCommand.RunProcess(fileName, arguments, workingDirectory, configureDotnetEnvironment);

    private static void PrintCleanupStatus(
        string executionDirectory,
        GoalWorktreeCleanupHooks hooks)
    {
        var debts = GoalWorktrees.ListCleanupDebt(executionDirectory, hooks);
        if (debts.Count == 0)
        {
            Console.WriteLine("Cleanup status: no pending cleanup debt.");
            return;
        }

        Console.WriteLine("Cleanup status:");
        foreach (var debt in debts)
        {
            var escalated = debt.EscalatedAtUtc is null
                ? "no"
                : debt.EscalatedAtUtc.Value.ToString("O", CultureInfo.InvariantCulture);
            Console.WriteLine(
                $"  path=\"{debt.Path}\" age={FormatCleanupStatusDuration(debt.Age)} reason={debt.Reason} " +
                $"last_operation={debt.LastOperation} skip_until_utc={debt.SkipUntilUtc:O} " +
                $"remaining_wait={FormatCleanupStatusDuration(debt.RemainingWait)} " +
                $"consecutive_failures={debt.ConsecutiveFailureCount} escalated={escalated}");
        }
    }

    private static string FormatCleanupStatusDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}.{duration:hh\\:mm\\:ss}"
            : duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private static HistoricalTrialReplayResolution ResolveHistoricalTrial(
        CliExecutionContext context,
        HistoricalTrialSelector selector)
    {
        AgentOrchestratorKernel kernel;
        try
        {
            kernel = context.ReloadKernel([selector.GoalId]);
        }
        catch (InvalidOperationException ex)
        {
            throw new TrialComparisonUnavailableException(
                TrialComparisonUnavailableReason.HistoricalGoalNotFound,
                $"Historical goal '{selector.GoalId}' could not be loaded from durable state: {ex.Message}");
        }

        return HistoricalTrialReplayResolver.Resolve(kernel, selector);
    }

    private static DistributedArchitectureReport BuildCliArchitectureReport(CliExecutionContext context)
    {
        return DistributedArchitectureReport.Create(
            context.Workspace,
            context.Agents,
            context.WorkerProfiles);
    }
}
