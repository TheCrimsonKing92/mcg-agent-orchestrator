using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
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

    private static List<CollaborationItem> AllClarifications(CollaborationItemStore store)
    {
        return store.ListAsync().GetAwaiter().GetResult()
            .Where(IsSpecClarification)
            .ToList();
    }

    private static List<CollaborationItem> AllClarificationsForGoal(CollaborationItemStore store, Goal goal) =>
        AllClarifications(store)
            .Where(item => string.Equals(item.GoalId, goal.Id.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static bool IsSpecClarification(CollaborationItem item) =>
        !string.IsNullOrWhiteSpace(item.CorrelationKey) &&
        item.CorrelationKey!.StartsWith("spec-clarification:", StringComparison.Ordinal);

    private static Goal ResolveAttentionGoal(AgentOrchestratorKernel kernel, string goalPrefix)
    {
        var matches = kernel.Goals
            .Where(goal => goal.Id.Value.StartsWith(goalPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException($"No goal found matching prefix '{goalPrefix}'."),
            _ => throw new InvalidOperationException($"Goal prefix '{goalPrefix}' is ambiguous ({matches.Count} matches).")
        };
    }

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

    private static IReadOnlyList<HumanInputRequest> HumanWaitsForAttention(
        AgentOrchestratorKernel kernel,
        GoalId? goalId,
        bool includeHistory)
    {
        return kernel.HumanInputRequests
            .Where(request => goalId is null || request.GoalId == goalId)
            .Where(request => includeHistory || !request.IsCompleted)
            .Where(request => includeHistory || IsLiveAttentionGoal(kernel, request.GoalId))
            .OrderBy(request => request.CreatedAt)
            .ToList();
    }

    private static IReadOnlyList<CollaborationItem> CollaborationItemsForAttention(
        IReadOnlyList<CollaborationItem> items,
        AgentOrchestratorKernel kernel,
        GoalId? goalId,
        bool includeHistory)
    {
        return items
            .Where(item => CollaborationItemLifecycle.IsReachUpType(item.Type))
            .Where(item => goalId is null || string.Equals(item.GoalId, goalId.Value, StringComparison.OrdinalIgnoreCase))
            .Where(item => includeHistory || item.GoalId is null || IsLiveAttentionGoal(kernel, new GoalId(item.GoalId)))
            .ToList();
    }

    private static bool IsLiveAttentionGoal(AgentOrchestratorKernel kernel, GoalId goalId)
    {
        var goal = kernel.Goals.FirstOrDefault(candidate => candidate.Id == goalId);
        return goal is not null && goal.Status is not GoalStatus.Parked
            and not GoalStatus.Completed
            and not GoalStatus.Failed
            and not GoalStatus.Cancelled
            and not GoalStatus.Superseded;
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

        if (matches.Count == 0)
            throw new ArgumentException(notFoundMessage);
        if (matches.Count > 1)
            throw new ArgumentException(string.Format(ambiguousMessage, matches.Count));

        return matches[0];
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
    private static string ClarificationId(
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
                ConsoleViews.PrintGateStatus(GateHeartbeatArtifacts.ReadStableSlots());
                return false;

            case "run-events-maintenance":
                HandleRunEventsMaintenance(parts, context);
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
                PrintCleanupStatus(context.Workspace.ExecutionDirectory, context.CleanupHooks);
                return false;

            case "attention":
            {
                var store = CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory);

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
                    var showMigratedHumanWaits = context.Kernel.SweepParkedGoalHumanWaits();
                    var showMigratedCollaborationItems = ResolveParkedAttentionItems(context.Kernel, store);
                    var (includeHistory, goalPrefix) = ParseAttentionShowArgs(parts);
                    var changed = showMigratedHumanWaits > 0 || showMigratedCollaborationItems > 0;

                    if (goalPrefix is null)
                    {
                        var showWaits = HumanWaitsForAttention(context.Kernel, null, includeHistory);
                        if (showWaits.Count > 0)
                        {
                            ConsoleViews.PrintHumanWaits(showWaits, DateTimeOffset.UtcNow, includeHistory);
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
                    var clarificationIdentityUniverse = AllClarifications(store)
                        .Where(item => string.Equals(item.GoalId, goal.Id.Value, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var waitsForGoal = HumanWaitsForAttention(context.Kernel, goal.Id, includeHistory);
                    if (waitsForGoal.Count > 0)
                        ConsoleViews.PrintHumanWaits(waitsForGoal, DateTimeOffset.UtcNow, includeHistory);

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

                // `attention answer <goal-id-prefix> <id> <answer...>`: resolve one open clarification with a
                // real answer (vs. `dismiss`). The <id> is the stable short id from `attention show` (matched
                // by prefix), so resolving one clarification never shifts the identity of the others. The
                // answer is written into the RefinedSpec question and recorded as a precedent on the next
                // refinement pass (SyncAnsweredClarifications).
                if (parts.Count > 1 && parts[1].Equals("answer", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Count < 4)
                        throw new ArgumentException("Usage: attention answer [<goal-id-prefix>] <id> <answer> | attention answer [<goal-id-prefix>] <id> --text-file <path>");

                    var globalClarifications = OpenClarifications(store);
                    var clarificationIdentityUniverse = AllClarifications(store);
                    // Legacy global syntax wins when the first token is an open clarification id, even if
                    // that token also happens to be a goal prefix.
                    var globalClarification = ResolveClarificationByExactShortId(
                        globalClarifications,
                        clarificationIdentityUniverse,
                        parts[2],
                        $"Id '{parts[2]}' is ambiguous ({{0}} matches); use a goal-scoped id from `attention show <goal-id>` or a full correlation key.");
                    var scoped = globalClarification is null;
                    var goal = scoped ? ResolveAttentionGoal(context.Kernel, parts[2]) : null;

                    if (scoped && parts.Count < 5)
                        throw new ArgumentException("Usage: attention answer [<goal-id-prefix>] <id> <answer> | attention answer [<goal-id-prefix>] <id> --text-file <path>");

                    var id = scoped ? parts[3] : parts[2];
                    var answer = ResolveTextArgument(
                        parts,
                        scoped ? 4 : 3,
                        "attention answer [<goal-id-prefix>] <id> <answer> | attention answer [<goal-id-prefix>] <id> --text-file <path>",
                        "--text-file");
                    var clarification = scoped
                        ? ResolveClarificationByShortId(
                            AllClarificationsForGoal(store, goal!),
                            clarificationIdentityUniverse
                                .Where(item => string.Equals(item.GoalId, goal!.Id.Value, StringComparison.OrdinalIgnoreCase))
                                .ToList(),
                            id,
                            $"Clarification id '{id}' was not found for goal '{goal!.Id.Value}'. Run `attention show {goal.Id.Value[..8]}` to list valid identifiers.",
                            $"Id '{id}' is ambiguous ({{0}} matches); copy a full id from `attention show {goal!.Id.Value[..8]}` or use a full correlation key.")
                        : globalClarification!;

                    if (CollaborationItemLifecycle.IsTerminal(clarification.Status))
                    {
                        throw new InvalidOperationException(
                            $"Clarification '{id}' is already answered — use supersede.");
                    }

                    var refinementService = new GoalRefinementService(
                        context.Providers,
                        ModelFunctionCatalog.Empty,
                        store,
                        new SpecRefinerPrecedentStore(context.Workspace.SpecRefinerPrecedentsPath));
                    var answerGoal = goal ?? context.Kernel.Goals.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id.Value, clarification.GoalId, StringComparison.OrdinalIgnoreCase));
                    var resolved = answerGoal is null
                        ? refinementService.TryResolveOpenClarificationAsync(
                            clarification.CorrelationKey!,
                            answer).GetAwaiter().GetResult()
                        : refinementService.TryResolveOpenClarificationAsync(
                            clarification.CorrelationKey!,
                            answer,
                            answerGoal.AuthoritativeBrief.Version).GetAwaiter().GetResult();
                    Console.WriteLine(resolved
                        ? goal is null
                            ? $"Answered clarification '{id}'."
                            : $"Answered clarification '{id}' for goal '{goal.Id.Value[..8]}'."
                        : goal is null
                            ? $"Failed to resolve clarification '{id}'."
                            : $"Failed to resolve clarification '{id}' for goal '{goal.Id.Value[..8]}'.");
                    return false;
                }

                var migratedHumanWaits = context.Kernel.SweepParkedGoalHumanWaits();
                var migratedCollaborationItems = ResolveParkedAttentionItems(context.Kernel, store);
                var waits = HumanWaitsForAttention(context.Kernel, null, includeHistory: false);
                if (waits.Count > 0)
                {
                    ConsoleViews.PrintHumanWaits(waits, DateTimeOffset.UtcNow);
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
                ConsoleViews.PrintHealth(OrchestratorHealthInspector.InspectCurrentEnvironment(new AgentCatalog(context.Agents), context.WorkerProfiles));
                return false;

            case "repo-process-info":
                RepoProcessCliCommand.PrintInfo(parts, Console.Out);
                return false;

            case "repo-process-stop":
                if (!RepoProcessCliCommand.Stop(parts, Console.Out))
                {
                    throw new CliExitException(1);
                }
                return false;

            case "stable-slot-dotnet":
                RunStableSlotDotnet(parts, context);
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
                    ConsoleViews.PrintTask(context.CurrentGoal, smokeTask);
                    return true;
                }

                return false;

            case "model-outcomes":
                ConsoleViews.PrintModelOutcomeScorecard(context.Kernel.BuildModelOutcomeScorecard());
                return false;

            case "durations":
            {
                var since = ParseDurationsSince(GetFlagValue(parts, "--since"));
                ConsoleViews.PrintTaskDurationStats(
                    context.Kernel.BuildTaskDurationStats(HasCliConfirmation(parts, "--by-model"), since),
                    since,
                    context.Kernel.BuildTaskDurationTrend(since));
                return false;
            }

            case "dispatch-value":
            {
                var since = ParseDurationsSince(GetFlagValue(parts, "--since"));
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

            case "operator-channel":
                return HandleOperatorChannelCommand(parts, context);

            case "operator-control-plane":
                return HandleOperatorControlPlaneCommand(parts, context);

            case "operator-listen":
            {
                var catalog = OperatorChannelStore.Load(context.Workspace.OperatorChannelPath);
                OperatorChannelFactory.WriteStartupConfigurationWarnings(catalog, Console.Error);
                var botToken = OperatorChannelFactory.ResolveBotToken();
                var store = CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory);
                OperatorChannelFactory.DiscordOperatorRuntime? runtime;
                try
                {
                    runtime = OperatorChannelFactory.CreateDiscordRuntime(
                        catalog,
                        context.Workspace.OperatorChannelPath,
                        botToken,
                        store,
                        context.Workspace.OrchestratorDirectory,
                        async (correlationKey, answer, cancellationToken) =>
                        {
                            // operator-listen owns no state write lock (SkipsKernelState), so read the latest
                            // goal snapshot only to stamp answer provenance. The collaboration-store update
                            // clears the clarification in Discord; the conductor applies it to the RefinedSpec.
                            var service = new GoalRefinementService(
                                context.Providers,
                                ModelFunctionCatalogStore.Load(context.Workspace.ModelFunctionCatalogPath),
                                store,
                                new SpecRefinerPrecedentStore(context.Workspace.SpecRefinerPrecedentsPath));
                            var item = (await store.ListAsync(null, cancellationToken))
                                .FirstOrDefault(candidate => string.Equals(
                                    candidate.CorrelationKey,
                                    correlationKey,
                                    StringComparison.Ordinal));
                            GoalSnapshot? snapshot = null;
                            if (!string.IsNullOrWhiteSpace(item?.GoalId))
                            {
                                snapshot = await SqliteOrchestratorStateRepository
                                    .OpenReadOnly(context.Workspace.SqliteStatePath)
                                    .LoadGoalAsync(new GoalId(item.GoalId), cancellationToken);
                            }

                            var briefVersion = snapshot?.BriefVersions?
                                .SingleOrDefault(version => version.IsAuthoritative)
                                ?.Version ?? (snapshot is null ? null : 1);
                            return briefVersion is { } knownBriefVersion
                                ? await service.TryResolveOpenClarificationAsync(
                                    correlationKey,
                                    answer,
                                    knownBriefVersion,
                                    cancellationToken)
                                : await service.TryResolveOpenClarificationAsync(
                                    correlationKey,
                                    answer,
                                    cancellationToken);
                        },
                        (command, cancellationToken) =>
                            DispatchOperatorDecisionCommandAsync(command, context, cancellationToken),
                        itemId => OperatorInbox.AppendAcknowledgement(context.Workspace, itemId),
                        OperatorChannelComposition.BuildGoalStateVersionReader(context.Workspace.OrchestratorDirectory));
                }
                catch (Exception ex) when (DiscordOperatorFaultClassifier.IsAuthError(ex))
                {
                    DiscordOperatorFaultClassifier.DisableForProcess("operator-listen startup", ex);
                    return false;
                }
                if (runtime is null)
                {
                    Console.WriteLine("operator-listen: Discord not configured or MCGO_DISCORD_BOT_TOKEN missing.");
                    return false;
                }

                Console.WriteLine("operator-listen: Discord listener running. Collaboration view refreshes every 15 seconds; progress view refreshes every 15 minutes. Press Ctrl+C to stop.");
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cts.Cancel();
                };
                try
                {
                    var progressLoop = RunOperatorListenLoopAsync(context, store, runtime.ProgressView, cts.Token);
                    var collaborationLoop = RunCollaborationReconcileLoopAsync(runtime.CollaborationView, TimeSpan.FromSeconds(15), cts.Token);
                    Task.WhenAny(progressLoop, collaborationLoop).GetAwaiter().GetResult();
                    cts.Cancel();
                    Task.WhenAll(progressLoop, collaborationLoop).GetAwaiter().GetResult();
                }
                finally
                {
                    runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                return false;
            }

            case "monitor-goal":
                GoalMonitoringSubscriptionCommand.RunAsync(
                    parts,
                    Console.Out,
                    context.Kernel,
                    context.Workspace,
                    context.Agents,
                    context.WorkerProfiles,
                    context.ReloadKernel).GetAwaiter().GetResult();
                return false;

            case "dashboard":
            {
                var dashboardMode = GetFlagValue(parts, "--mode");
                if (dashboardMode is not null)
                {
                    var baseArgs = RemoveFlagWithValue(parts, "--mode");
                    switch (dashboardMode.ToLowerInvariant())
                    {
                        case "local":
                        {
                            var modeArgList = new List<string>(baseArgs) { [0] = "serve-dashboard" };
                            var localArgs = DashboardHost.ParseDashboardHostArgs(modeArgList, "serve-dashboard", defaultOpenBrowser: false);
                            DashboardHost.RunDashboardHostAsync(context.Workspace, context.Providers, localArgs, new AgentCatalog(context.Agents)).GetAwaiter().GetResult();
                            return false;
                        }
                        case "hosted":
                        {
                            var modeArgList = new List<string>(baseArgs) { [0] = "hosted-dashboard" };
                            var hostedModeArgs = DashboardHost.ParseDashboardHostArgs(modeArgList, "hosted-dashboard", defaultOpenBrowser: false);
                            DashboardHost.RunDashboardHostAsync(context.Workspace, context.Providers, hostedModeArgs, new AgentCatalog(context.Agents)).GetAwaiter().GetResult();
                            return false;
                        }
                        case "read-only":
                        {
                            var modeArgList = new List<string>(baseArgs) { [0] = "simple-hosted-dashboard" };
                            var readOnlyArgs = DashboardHost.ParseDashboardHostArgs(modeArgList, "simple-hosted-dashboard", defaultOpenBrowser: false);
                            DashboardHost.RunDashboardHostAsync(context.Workspace, context.Providers, readOnlyArgs, new AgentCatalog(context.Agents)).GetAwaiter().GetResult();
                            return false;
                        }
                        default:
                            throw new ArgumentException($"Unknown dashboard mode '{dashboardMode}'. Use: local|hosted|read-only");
                    }
                }

                var dashboardArgs = DashboardHost.ParseDashboardArgs(parts);
                var dashboardPath = dashboardArgs.Path;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dashboardPath))!);
                var dashboardOptions = dashboardArgs.Options with
                {
                    AgentDefinitions = context.Agents,
                    WorkerProfiles = context.WorkerProfiles
                };
                File.WriteAllText(dashboardPath, DashboardRenderer.Render(context.Kernel, dashboardOptions));
                Console.WriteLine($"Dashboard: {Path.GetFullPath(dashboardPath)}");
                if (dashboardArgs.Options.AutoRefreshSeconds is > 0)
                {
                    Console.WriteLine($"Auto-refresh: {dashboardArgs.Options.AutoRefreshSeconds.Value}s");
                }
                return false;
            }

            case "serve-dashboard":
                var serveArgs = DashboardHost.ParseDashboardHostArgs(parts, "serve-dashboard", defaultOpenBrowser: false);
                DashboardHost.RunDashboardHostAsync(context.Workspace, context.Providers, serveArgs, new AgentCatalog(context.Agents)).GetAwaiter().GetResult();
                return false;

            case "hosted-dashboard":
                var hostedArgs = DashboardHost.ParseDashboardHostArgs(parts, "hosted-dashboard", defaultOpenBrowser: false);
                DashboardHost.RunDashboardHostAsync(context.Workspace, context.Providers, hostedArgs, new AgentCatalog(context.Agents)).GetAwaiter().GetResult();
                return false;

            case "simple-hosted-dashboard":
                var simpleHostedArgs = DashboardHost.ParseDashboardHostArgs(parts, "simple-hosted-dashboard", defaultOpenBrowser: false);
                DashboardHost.RunDashboardHostAsync(context.Workspace, context.Providers, simpleHostedArgs, new AgentCatalog(context.Agents)).GetAwaiter().GetResult();
                return false;

            case "open-dashboard":
                var openArgs = DashboardHost.ParseDashboardHostArgs(parts, "open-dashboard", defaultOpenBrowser: true);
                DashboardHost.RunDashboardHostAsync(context.Workspace, context.Providers, openArgs, new AgentCatalog(context.Agents)).GetAwaiter().GetResult();
                return false;

            case "transcript":
                context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, null);
                var transcriptPath = parts.Count > 1
                    ? parts[1]
                    : context.Workspace.TranscriptPath;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(transcriptPath))!);
                File.WriteAllText(
                    transcriptPath,
                    GoalTranscriptRenderer.Render(
                        context.Kernel,
                        context.CurrentGoal,
                        context.WorkerProfiles,
                        context.Agents,
                        context.Workspace.ExecutionDirectory));
                Console.WriteLine($"Transcript: {Path.GetFullPath(transcriptPath)}");
                return false;

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
        var options = new RunEventMaintenanceOptions(
            TimeSpan.FromDays(retentionDays),
            keepRows,
            vacuum,
            MaxConductorTickPayloadBytes: payloadMaxBytes,
            DeleteBatchSize: batchSize,
            LegacyOversizedConductorTickPurge: legacyPurge);
        var result = store.MaintainAsync(options)
            .GetAwaiter()
            .GetResult();

        var mode = legacyPurge ? "legacy-purge" : "manual";
        var status = result.Deferred ? "deferred" : "completed";
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

        if (!result.Deferred)
        {
            RunEventMaintenanceCadence.TryAppendRunEventReceipt(
                store,
                mode,
                options,
                result,
                DateTimeOffset.UtcNow);
        }
    }

    private static async Task RunOperatorListenLoopAsync(
        CliExecutionContext context,
        ICollaborationItemStore collaborationStore,
        DiscordProgressViewService progressView,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunDiscordRefreshWithRetryAsync(
                "progress view",
                token => ReconcileProgressViewAsync(context, collaborationStore, progressView, token),
                cancellationToken);

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(15), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal static async Task RunCollaborationReconcileLoopAsync(
        DiscordCollaborationViewService collaborationView,
        TimeSpan reconcileInterval,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunDiscordRefreshWithRetryAsync(
                "collaboration view",
                collaborationView.ReconcileAsync,
                cancellationToken,
                delay);

            try
            {
                await delay(reconcileInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static Task DispatchOperatorDecisionCommandAsync(
        string command,
        CliExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parts = CliArgumentParser.SplitCommand(command);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stateRepository = CreateOperatorDecisionStateRepository(parts, context.Workspace);
            var agents = context.Agents;
            var workerProfiles = context.WorkerProfiles;
            Goal? currentGoal = null;
            CliPersistentStateRunner.ExecuteCommand(
                parts,
                stateRepository,
                context.Workspace,
                ref agents,
                context.Providers,
                ref workerProfiles,
                ref currentGoal,
                DiscordDecisionOperatorChannel.Instance,
                operatorIntentSubmissionSource: CliPersistentStateRunner.OperatorIntentSubmissionSource.Discord);
        }, cancellationToken);
    }

    internal static ITransactionalOrchestratorStateRepository CreateOperatorDecisionStateRepository(
        IReadOnlyList<string> parts,
        OrchestratorWorkspace workspace)
    {
        ProgramStartupLifecycle.EnsureStateDbInitialized(parts, workspace);
        return new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
    }

    private sealed class DiscordDecisionOperatorChannel : IOperatorChannel
    {
        public static readonly DiscordDecisionOperatorChannel Instance = new();

        public string ChannelType => "discord";

        public Task SendEscalationAsync(
            OperatorEscalation escalation,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    internal static async Task RunDiscordRefreshWithRetryAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await operation(cancellationToken);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (DiscordOperatorFaultClassifier.IsAuthError(ex))
            {
                DiscordOperatorFaultClassifier.DisableForProcess(operationName, ex);
                return;
            }
            catch (Exception ex) when (DiscordOperatorFaultClassifier.IsTransient(ex))
            {
                attempt++;
                var backoff = ComputeDiscordRetryBackoff(attempt);
                Console.Error.WriteLine(
                    $"operator-listen: transient Discord error while refreshing {operationName}: {ex.Message}. Retrying in {backoff.TotalSeconds:0.#}s.");
                try
                {
                    await delay(backoff, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private static TimeSpan ComputeDiscordRetryBackoff(int attempt)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Min(attempt - 1, 5)));
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task ReconcileProgressViewAsync(
        CliExecutionContext context,
        ICollaborationItemStore collaborationStore,
        DiscordProgressViewService progressView,
        CancellationToken cancellationToken)
    {
        var kernel = context.ReloadKernel();
        var openEscalations = (await collaborationStore.GetAttentionQueueAsync(cancellationToken)).Count;
        var catalog = OperatorChannelStore.Load(context.Workspace.OperatorChannelPath);
        var projection = StatusProjector.Project(StatusProjector.BuildInput(
            kernel.Goals,
            openEscalations,
            BuildOperatorInboxUrl(catalog.DashboardBaseUrl),
            DateTimeOffset.UtcNow,
            factProvider: goal => GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(context.Workspace, goal)));
        await progressView.ReconcileAsync(projection, cancellationToken);
    }

    private static string? BuildOperatorInboxUrl(string? dashboardBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(dashboardBaseUrl))
            return null;

        return dashboardBaseUrl.TrimEnd('/') + "/api/operator-inbox";
    }

    private static bool? HandleOperatorChannelCommand(IReadOnlyList<string> parts, CliExecutionContext context)
    {
        var sub = parts.Count > 1 ? parts[1].ToLowerInvariant() : "show";
        switch (sub)
        {
            case "set":
            {
                var channelType = parts.Count > 2 ? parts[2].ToLowerInvariant() : null;
                if (string.IsNullOrWhiteSpace(channelType))
                    throw new ArgumentException("Usage: operator-channel set discord [--forum-channel-id <id>] [--dashboard-url <url>] [--operator-user-id <id>]... [--operator-user-ids <id1,id2,...>]");
                var forumChannelId = GetFlagValue(parts, "--forum-channel-id");
                var dashboardUrl = GetFlagValue(parts, "--dashboard-url");
                var userIdList = new List<string>(GetFlagValues(parts, "--operator-user-id"));
                var csvIds = GetFlagValue(parts, "--operator-user-ids");
                if (!string.IsNullOrWhiteSpace(csvIds))
                    userIdList.AddRange(csvIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                IReadOnlyList<string>? operatorUserIds = userIdList.Count > 0 ? userIdList : null;
                var existing = OperatorChannelStore.Load(context.Workspace.OperatorChannelPath);
                var catalog = MergeOperatorChannelCatalog(
                    existing,
                    channelType,
                    dashboardUrl,
                    forumChannelId,
                    operatorUserIds);
                OperatorChannelStore.Save(context.Workspace.OperatorChannelPath, catalog);
                Console.WriteLine($"Operator channel set: type={catalog.ChannelType} forumChannelId={catalog.ForumChannelId ?? "(none)"} dashboardUrl={catalog.DashboardBaseUrl ?? "(none)"} operatorUserIds={catalog.OperatorUserIds?.Count ?? 0}");
                Console.WriteLine("Note: bot token (MCGO_DISCORD_BOT_TOKEN) is read from env at startup and is not stored.");
                return false;
            }
            case "show":
            {
                var catalog = OperatorChannelStore.Load(context.Workspace.OperatorChannelPath);
                var botToken = OperatorChannelFactory.ResolveBotToken();
                Console.WriteLine($"Operator channel: type={catalog.ChannelType}");
                Console.WriteLine($"  forumChannelId: {catalog.ForumChannelId ?? "(none)"}");
                Console.WriteLine($"  progressThreadId: {catalog.ProgressThreadId ?? "(none)"}");
                Console.WriteLine($"  progressStatusMessageId: {catalog.ProgressStatusMessageId ?? "(none)"}");
                Console.WriteLine($"  controlPlaneMutedUntil: {catalog.ControlPlaneMutedUntil?.ToString("O") ?? "(none)"}");
                Console.WriteLine($"  deadManHeartbeat: {(catalog.DeadManHeartbeatEnabled ? "enabled" : "disabled")}");
                Console.WriteLine($"  dashboardUrl: {catalog.DashboardBaseUrl ?? "(none)"}");
                Console.WriteLine($"  bot token: {(string.IsNullOrWhiteSpace(botToken) ? "not set" : "set (MCGO_DISCORD_BOT_TOKEN)")}");
                Console.WriteLine($"  active channel: {context.Channel.ChannelType}");
                var userIds = catalog.OperatorUserIds;
                if (userIds is { Count: > 0 })
                    Console.WriteLine($"  operatorUserIds ({userIds.Count}): {string.Join(", ", userIds)}");
                else
                    Console.WriteLine("  operatorUserIds: (none)");
                return false;
            }
            case "test":
            {
                if (parts.Any(part => part.Equals("--spine", StringComparison.OrdinalIgnoreCase)))
                {
                    RaiseOperatorChannelSpineTestItem(context.Workspace.OrchestratorDirectory);
                    return false;
                }

                var catalog = OperatorChannelStore.Load(context.Workspace.OperatorChannelPath);
                var botToken = OperatorChannelFactory.ResolveBotToken();
                var channel = OperatorChannelComposition.Create(catalog, botToken, context.Workspace.OrchestratorDirectory);
                OperatorChannelFactory.SendTestEscalationAsync(channel, Console.Out).GetAwaiter().GetResult();
                return false;
            }
            default:
                throw new ArgumentException($"Unknown operator-channel sub-command '{sub}'. Usage: operator-channel set|show|test");
        }
    }

    internal static OperatorChannelCatalog MergeOperatorChannelCatalog(
        OperatorChannelCatalog existing,
        string channelType,
        string? dashboardUrl,
        string? forumChannelId,
        IReadOnlyList<string>? operatorUserIds) =>
        existing with
        {
            ChannelType = channelType,
            DashboardBaseUrl = dashboardUrl ?? existing.DashboardBaseUrl,
            ForumChannelId = forumChannelId ?? existing.ForumChannelId,
            OperatorUserIds = operatorUserIds ?? existing.OperatorUserIds
        };

    private static bool? HandleOperatorControlPlaneCommand(IReadOnlyList<string> parts, CliExecutionContext context)
    {
        var sub = parts.Count > 1 ? parts[1].ToLowerInvariant() : "show";
        switch (sub)
        {
            case "replay":
            {
                var hours = GetFlagValue(parts, "--hours") is { } rawHours
                    ? ParsePositiveInteger(rawHours, "--hours")
                    : 48;
                var now = DateTimeOffset.UtcNow;
                var from = now.AddHours(-hours);
                var catalog = OperatorChannelStore.Load(context.Workspace.OperatorChannelPath);
                var policy = new ControlPlaneDeliveryPolicy(MutedUntil: catalog.ControlPlaneMutedUntil);
                var store = CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory);
                var collaborationCards = store.ListAsync().GetAwaiter().GetResult()
                    .Select(ControlPlaneDecisionCard.FromCollaborationItem);
                var backlogItems = new BacklogStore(context.Workspace.BacklogStorePath)
                    .ListAsync(includeAll: false)
                    .GetAwaiter()
                    .GetResult()
                    .Select(item => new ControlPlaneBacklogDigestItem(
                        item.Id,
                        item.Title,
                        item.Status,
                        item.UpdatedAt,
                        item.SourceGoalId));
                var inboxCards = OperatorInbox.Build(
                        context.Kernel,
                        context.Agents,
                        context.WorkerProfiles,
                        context.Workspace,
                        includeAcknowledged: true)
                    .Items
                    .Select(OperatorInboxControlPlaneProjection.Project);
                var report = new ControlPlaneReplayHarness(policy)
                    .ReplayAsync(collaborationCards.Concat(inboxCards), backlogItems, from, now)
                    .GetAwaiter()
                    .GetResult();
                Console.WriteLine($"control-plane-replay windowHours={hours} {report.FormatCounts()}");
                return false;
            }
            case "mute":
            {
                if (parts.Count < 3 || !TryParseDuration(parts[2], out var duration))
                    throw new ArgumentException("Usage: operator-control-plane mute 24h");
                var catalog = OperatorChannelStore.Load(context.Workspace.OperatorChannelPath);
                var mutedUntil = DateTimeOffset.UtcNow.Add(duration);
                OperatorChannelStore.Save(context.Workspace.OperatorChannelPath, catalog with { ControlPlaneMutedUntil = mutedUntil });
                Console.WriteLine($"operator-control-plane mutedUntil={mutedUntil:O}");
                return false;
            }
            case "show":
            {
                var catalog = OperatorChannelStore.Load(context.Workspace.OperatorChannelPath);
                Console.WriteLine($"operator-control-plane mutedUntil={catalog.ControlPlaneMutedUntil?.ToString("O") ?? "(none)"}");
                Console.WriteLine($"operator-control-plane deadManHeartbeat={(catalog.DeadManHeartbeatEnabled ? "enabled" : "disabled")}");
                Console.WriteLine("operator-control-plane livePosting=disabled-by-default");
                return false;
            }
            default:
                throw new ArgumentException("Unknown operator-control-plane sub-command. Usage: operator-control-plane replay [--hours 48]|mute 24h|show");
        }
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        if (value.EndsWith("h", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value[..^1], out var hours) &&
            hours > 0)
        {
            duration = TimeSpan.FromHours(hours);
            return true;
        }

        if (value.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value[..^1], out var minutes) &&
            minutes > 0)
        {
            duration = TimeSpan.FromMinutes(minutes);
            return true;
        }

        return false;
    }

    internal static CollaborationItem RaiseOperatorChannelSpineTestItem(string orchestratorDirectory)
    {
        var store = CollaborationItemStore.ForDirectory(orchestratorDirectory);
        var correlationKey = $"operator-channel-test-spine-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        var item = store.RaiseAsync(
                CollaborationItemType.Verify,
                "operator-channel-test",
                "Operator channel spine verification",
                "Seeded by `operator-channel test --spine` to verify the Discord listener delivers a spine-backed reach-up item and resolves it through the collaboration store.",
                correlationKey)
            .GetAwaiter()
            .GetResult();
        Console.WriteLine($"operator-channel test --spine: raised {item.Type} item {item.Id} correlationKey={item.CorrelationKey}");
        return item;
    }

    private static bool GitCommitShaExists(string executionDirectory, string sha) =>
        GitCli.Run(executionDirectory, 5_000, "cat-file", "-e", $"{sha}^{{commit}}").Succeeded;

    private static DateTimeOffset? ParseDurationsSince(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.EndsWith("d", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var days) &&
            days > 0)
        {
            return DateTimeOffset.UtcNow.AddDays(-days);
        }

        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException("Invalid --since value. Use an ISO date/time or a positive Nd value such as 14d.");
    }

    private static void RunStableSlotDotnet(IReadOnlyList<string> parts, CliExecutionContext context)
    {
        if (parts.Count < 2)
        {
            throw new ArgumentException("Usage: stable-slot-dotnet <dotnet-arguments>");
        }

        using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock();
        if (parts[1].Equals("mtp-test", StringComparison.OrdinalIgnoreCase))
        {
            var resultsSurviveCleanup = RunStableSlotMtpTest(parts, context, lease);
            if (resultsSurviveCleanup)
            {
                DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(lease.Environment);
            }

            return;
        }

        var exitCode = RunStableSlotProcess(
            "dotnet",
            [.. parts.Skip(1), .. lease.Environment.Arguments],
            context.Workspace.RootDirectory,
            configureDotnetEnvironment: true);
        if (exitCode != 0)
        {
            throw new CliExitException(exitCode);
        }

        lease.ReleaseExecutionLock();
        DotnetBuildEnvironmentManager.TryCleanupSuccessfulRun(lease.Environment);
    }

    private static bool RunStableSlotMtpTest(
        IReadOnlyList<string> parts,
        CliExecutionContext context,
        DotnetBuildEnvironmentLease buildLease)
    {
        var environment = buildLease.Environment;
        if (parts.Count < 3)
        {
            throw new ArgumentException("Usage: stable-slot-dotnet mtp-test <project> [--filter <filter>] [--results-directory <path>] [--no-build]");
        }

        var project = Path.GetFullPath(parts[2], context.Workspace.RootDirectory);
        var noBuild = parts.Any(part => part.Equals("--no-build", StringComparison.OrdinalIgnoreCase));
        var projectName = Path.GetFileNameWithoutExtension(project);
        var executable = Path.Combine(
            environment.ArtifactsPath,
            "bin",
            projectName,
            "debug",
            $"{projectName}{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}");
        var managedAssembly = Path.Combine(
            environment.ArtifactsPath,
            "bin",
            projectName,
            "debug",
            $"{projectName}.dll");
        if (ShouldBuildStableSlotMtpProject(
                noBuild,
                File.Exists(executable) && File.Exists(managedAssembly)))
        {
            var buildExit = RunStableSlotProcess(
                "dotnet",
                ["build", project, "--nologo", "-v", "quiet", "-clp:ErrorsOnly", .. environment.Arguments],
                context.Workspace.RootDirectory,
                configureDotnetEnvironment: true);
            if (buildExit != 0)
            {
                throw new CliExitException(buildExit);
            }
        }

        if (!File.Exists(executable))
        {
            throw new InvalidOperationException($"MTP test executable was not produced: {executable}");
        }

        if (!File.Exists(managedAssembly))
        {
            throw new InvalidOperationException($"MTP test managed assembly was not produced: {managedAssembly}");
        }

        buildLease.ReleaseExecutionLock();
        var configuredResultsDirectory = ReadStableSlotOption(parts, "--results-directory");
        var resultsDirectory = ResolveStableSlotResultsDirectory(
            configuredResultsDirectory,
            context.Workspace.RootDirectory,
            Path.Combine(environment.ArtifactsPath, "TestResults"));
        Directory.CreateDirectory(resultsDirectory);
        var mtpArguments = new List<string>
        {
            "--no-ansi",
            "--progress",
            "off",
            "--results-directory",
            resultsDirectory,
            "--report-trx",
            "--report-trx-filename",
            $"{projectName}.trx"
        };
        var filter = ReadStableSlotOption(parts, "--filter");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            mtpArguments.AddRange(TranslateStableSlotMtpFilter(filter));
        }

        var testExit = RunStableSlotProcess(
            "dotnet",
            [managedAssembly, .. mtpArguments],
            context.Workspace.RootDirectory,
            configureDotnetEnvironment: false);
        if (testExit != 0)
        {
            throw new CliExitException(testExit);
        }

        return configuredResultsDirectory is not null &&
            !Path.GetFullPath(resultsDirectory)
                .StartsWith(
                    Path.GetFullPath(environment.RootPath)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldBuildStableSlotMtpProject(bool noBuild, bool artifactsExist) =>
        !noBuild || !artifactsExist;

    internal static string ResolveStableSlotResultsDirectory(
        string? configuredResultsDirectory,
        string workspaceRoot,
        string defaultResultsDirectory) =>
        configuredResultsDirectory is null
            ? Path.GetFullPath(defaultResultsDirectory)
            : Path.GetFullPath(configuredResultsDirectory, Path.GetFullPath(workspaceRoot));

    private static string? ReadStableSlotOption(IReadOnlyList<string> parts, string option)
    {
        for (var index = 3; index < parts.Count - 1; index++)
        {
            if (parts[index].Equals(option, StringComparison.OrdinalIgnoreCase))
            {
                return parts[index + 1];
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> TranslateStableSlotMtpFilter(string filter)
    {
        var arguments = new List<string>();
        foreach (var rawToken in System.Text.RegularExpressions.Regex.Split(filter, @"[&|]"))
        {
            var token = rawToken.Trim();
            var match = System.Text.RegularExpressions.Regex.Match(
                token,
                @"^FullyQualifiedName\s*(?<op>!~|~)\s*(?<value>[A-Za-z_][A-Za-z0-9_.]*)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                match = System.Text.RegularExpressions.Regex.Match(
                    token,
                    @"^(?<value>[A-Za-z_][A-Za-z0-9_.]*)$");
                if (!match.Success)
                {
                    throw new ArgumentException($"Unsupported stable-slot MTP filter token '{token}'.");
                }
            }

            arguments.Add(match.Groups["op"].Value == "!~"
                ? "--filter-not-class"
                : "--filter-class");
            arguments.Add($"*{match.Groups["value"].Value}*");
        }

        return arguments;
    }

    internal static int RunStableSlotProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        bool configureDotnetEnvironment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(startInfo.Environment, workingDirectory);
        if (configureDotnetEnvironment)
        {
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["UseSharedCompilation"] = "false";
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        using (ProcessTreeGuiSuppression.AcquireErrorModeForChildSpawn())
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start dotnet process.");
        }

        using (process)
        {
            var standardOutput = PipeDrain.Start(process.StandardOutput, "cli-process-stdout-drain");
            var standardError = PipeDrain.Start(process.StandardError, "cli-process-stderr-drain");
            process.WaitForExit();
            var drainDeadline = Environment.TickCount64 + PipeDrain.DefaultTimeoutMilliseconds;
            var outputDrained = standardOutput.Join(drainDeadline);
            var errorDrained = standardError.Join(drainDeadline);
            Console.Out.Write(standardOutput.Text);
            Console.Error.Write(standardError.Text);
            if (!outputDrained || !errorDrained)
            {
                Console.Error.WriteLine(PipeDrain.DescribeTimeout(
                    "CLI child process",
                    PipeDrain.DefaultTimeoutMilliseconds,
                    standardOutput,
                    standardError));
                return process.ExitCode == 0 ? 1 : process.ExitCode;
            }

            return process.ExitCode;
        }
    }

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

    private static DistributedArchitectureDto BuildCliArchitectureReport(CliExecutionContext context)
    {
        return DistributedArchitectureDto.Create(
            context.Workspace,
            context.Agents,
            context.WorkerProfiles,
            operatorControlsEnabled: false);
    }
}
