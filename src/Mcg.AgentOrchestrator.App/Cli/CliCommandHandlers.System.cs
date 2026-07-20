using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    private static List<CollaborationItem> OpenClarificationsForGoal(CollaborationItemStore store, Goal goal)
    {
        return store.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.CorrelationKey) &&
                item.CorrelationKey!.StartsWith("spec-clarification:", StringComparison.Ordinal) &&
                string.Equals(item.GoalId, goal.Id.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<CollaborationItem> OpenClarifications(CollaborationItemStore store)
    {
        return store.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.CorrelationKey) &&
                item.CorrelationKey!.StartsWith("spec-clarification:", StringComparison.Ordinal))
            .ToList();
    }

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
        string id,
        string notFoundMessage,
        string ambiguousMessage)
    {
        var matches = clarifications
            .Where(c => ShortClarificationId(c.CorrelationKey!).StartsWith(id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            throw new ArgumentException(notFoundMessage);
        if (matches.Count > 1)
            throw new ArgumentException(string.Format(ambiguousMessage, matches.Count));

        return matches[0];
    }

    private static CollaborationItem? ResolveClarificationByExactShortId(
        IReadOnlyList<CollaborationItem> clarifications,
        string id,
        string ambiguousMessage)
    {
        var matches = clarifications
            .Where(c => string.Equals(ShortClarificationId(c.CorrelationKey!), id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count > 1)
            throw new ArgumentException(string.Format(ambiguousMessage, matches.Count));

        return matches.Count == 1 ? matches[0] : null;
    }

    // Stable short id for a clarification, derived from the trailing hash segment of its correlation key
    // (spec-clarification:<goal>:<fork-kind>:<hash>). Intrinsic to the item, so answering one clarification
    // never renumbers the others — unlike a positional index.
    private static string ShortClarificationId(string correlationKey)
    {
        var lastSegment = correlationKey[(correlationKey.LastIndexOf(':') + 1)..];
        return lastSegment.Length <= 8 ? lastSegment : lastSegment[..8];
    }

    private static bool? TryExecuteSystemCommand(string command, IReadOnlyList<string> parts, CliExecutionContext context)
    {
        switch (command)
        {
            case "gate-status":
                ConsoleViews.PrintGateStatus(GateHeartbeatArtifacts.ReadStableSlots());
                return false;

            case "run-events-maintenance":
                HandleRunEventsMaintenance(parts, context);
                return false;

            case "cleanup-status":
                PrintCleanupStatus(context.Workspace.ExecutionDirectory);
                return false;

            case "attention":
            {
                var store = CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory);

                // `attention dismiss <goal-id-prefix>`: resolve all of a goal's open attention items
                // out-of-band (e.g. a goal abandoned or handled outside Discord). The collaboration view
                // retires the goal's message on the next reconcile, so stale items stop being rendered.
                if (parts.Count > 1 && parts[1].Equals("dismiss", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Count < 3)
                        throw new ArgumentException("Usage: attention dismiss <goal-id-prefix>");

                    var goalPrefix = parts[2];
                    var open = store.GetAttentionQueueAsync().GetAwaiter().GetResult()
                        .Where(item =>
                            !string.IsNullOrWhiteSpace(item.CorrelationKey) &&
                            (item.GoalId?.StartsWith(goalPrefix, StringComparison.OrdinalIgnoreCase) ?? false))
                        .ToList();

                    var dismissed = open.Count(item =>
                        store.TryResolveAsync(item.CorrelationKey!, "dismissed by operator").GetAwaiter().GetResult());
                    Console.WriteLine($"Dismissed {dismissed} open attention item(s) for goal '{goalPrefix}'.");
                    return false;
                }

                // `attention show [--goal] <goal-id-prefix>`: list open typed human waits, falling back to
                // collaboration clarifications for compatibility with existing spec-refinement reach-ups.
                // and the question, so the operator can read them before deciding to answer or dismiss.
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
                        ConsoleViews.PrintCollaborationItems(globalQueue, includeHistory);
                        return changed;
                    }

                    var goal = ResolveAttentionGoal(context.Kernel, goalPrefix);
                    var waitsForGoal = HumanWaitsForAttention(context.Kernel, goal.Id, includeHistory);
                    if (waitsForGoal.Count > 0)
                    {
                        ConsoleViews.PrintHumanWaits(waitsForGoal, DateTimeOffset.UtcNow, includeHistory);
                        if (includeHistory)
                        {
                            var allItems = store.ListAsync().GetAwaiter().GetResult();
                            ConsoleViews.PrintCollaborationItems(
                                CollaborationItemsForAttention(allItems, context.Kernel, goal.Id, includeHistory),
                                includeHistory);
                        }

                        return changed;
                    }

                    var scopedItems = includeHistory
                        ? CollaborationItemsForAttention(store.ListAsync().GetAwaiter().GetResult(), context.Kernel, goal.Id, includeHistory)
                        : OpenClarificationsForGoal(store, goal).Cast<CollaborationItem>().ToList();
                    if (includeHistory)
                    {
                        if (scopedItems.Count == 0)
                        {
                            Console.WriteLine($"No open attention items for goal {goal.Id.Value}.");
                            return changed;
                        }

                        ConsoleViews.PrintCollaborationItems(scopedItems, includeHistory);
                        return changed;
                    }

                    var clarifications = scopedItems
                        .Where(item =>
                            !string.IsNullOrWhiteSpace(item.CorrelationKey) &&
                            item.CorrelationKey!.StartsWith("spec-clarification:", StringComparison.Ordinal))
                        .ToList();
                    if (clarifications.Count == 0)
                    {
                        Console.WriteLine($"No open attention items for goal {goal.Id.Value}.");
                        return changed;
                    }

                    foreach (var clarification in clarifications)
                    {
                        Console.WriteLine($"[{ShortClarificationId(clarification.CorrelationKey!)}] {clarification.Subject}");
                        if (!string.IsNullOrWhiteSpace(clarification.Body))
                            Console.WriteLine($"    {clarification.Body}");
                    }

                    Console.WriteLine($"Answer with: attention answer {goal.Id.Value[..8]} <id> <answer> or --text-file <path> (ids are stable; answering one does not renumber the rest)");
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
                    // Legacy global syntax wins when the first token is an open clarification id, even if
                    // that token also happens to be a goal prefix.
                    var globalClarification = ResolveClarificationByExactShortId(
                        globalClarifications,
                        parts[2],
                        $"Id '{parts[2]}' is ambiguous ({{0}} matches); use more characters from `attention show`.");
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
                            OpenClarificationsForGoal(store, goal!),
                            id,
                            $"Clarification id '{id}' does not belong to goal '{goal!.Id.Value}'.",
                            $"Id '{id}' is ambiguous ({{0}} matches); use more characters from `attention show {goal!.Id.Value[..8]}`.")
                        : globalClarification!;

                    var resolved = store.TryResolveAsync(clarification.CorrelationKey!, answer).GetAwaiter().GetResult();
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

            case "firewall-setup":
            {
                var exitCode = new FirewallSetupCommand(new WindowsFirewallRuleWriter()).Execute(Console.Out);
                if (exitCode != FirewallSetupCommand.SuccessExitCode)
                {
                    throw new CliExitException(exitCode);
                }

                return false;
            }

            case "repo-process-info":
                RepoProcessCliCommand.PrintInfo(parts, Console.Out);
                return false;

            case "repo-process-stop":
                RepoProcessCliCommand.Stop(parts, Console.Out);
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
                            DateTimeOffset.UtcNow));
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
                        (correlationKey, answer, cancellationToken) =>
                        {
                            // operator-listen runs with an EMPTY kernel and no write lock (SkipsKernelState)
                            // so it stays concurrent with a running conductor. Resolve purely against the
                            // collaboration store (no kernel needed): this clears the clarification in Discord
                            // and records the precedent. The conductor's AwaitingClarification gate reads the
                            // store, so the goal resumes on its next tick, and EnsureRefined then writes the
                            // answer into the goal's RefinedSpec.
                            var service = new GoalRefinementService(
                                context.Providers,
                                ModelFunctionCatalogStore.Load(context.Workspace.ModelFunctionCatalogPath),
                                store,
                                new SpecRefinerPrecedentStore(context.Workspace.SpecRefinerPrecedentsPath));
                            return service.TryResolveOpenClarificationAsync(
                                correlationKey,
                                answer,
                                cancellationToken);
                        },
                        (command, cancellationToken) =>
                            DispatchOperatorDecisionCommandAsync(command, context, cancellationToken));
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
            var stateRepository = new SqliteOrchestratorStateRepository(context.Workspace.SqliteStatePath);
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
                NullOperatorChannel.Instance);
        }, cancellationToken);
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
                var catalog = new OperatorChannelCatalog(channelType, dashboardUrl, forumChannelId, operatorUserIds);
                OperatorChannelStore.Save(context.Workspace.OperatorChannelPath, catalog);
                Console.WriteLine($"Operator channel set: type={catalog.ChannelType} forumChannelId={catalog.ForumChannelId ?? "(none)"} dashboardUrl={catalog.DashboardBaseUrl ?? "(none)"} operatorUserIds={operatorUserIds?.Count ?? 0}");
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
                var channel = OperatorChannelFactory.Create(catalog, botToken, context.Workspace.OrchestratorDirectory);
                OperatorChannelFactory.SendTestEscalationAsync(channel, Console.Out).GetAwaiter().GetResult();
                return false;
            }
            default:
                throw new ArgumentException($"Unknown operator-channel sub-command '{sub}'. Usage: operator-channel set|show|test");
        }
    }

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
                var inboxCards = OperatorInbox.Build(
                        context.Kernel,
                        context.Agents,
                        context.WorkerProfiles,
                        context.Workspace,
                        includeAcknowledged: true)
                    .Items
                    .Select(OperatorInboxControlPlaneProjection.Project);
                var report = new ControlPlaneReplayHarness(policy)
                    .ReplayAsync(collaborationCards.Concat(inboxCards), from, now)
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
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = context.Workspace.RootDirectory
        };
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["UseSharedCompilation"] = "false";
        startInfo.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = context.Workspace.RootDirectory;

        foreach (var part in parts.Skip(1))
        {
            startInfo.ArgumentList.Add(part);
        }

        foreach (var argument in lease.Environment.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet process.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new CliExitException(process.ExitCode);
        }
    }

    private static void PrintCleanupStatus(string executionDirectory)
    {
        var debts = GoalWorktrees.ListCleanupDebt(executionDirectory);
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
                $"remaining_wait={FormatCleanupStatusDuration(debt.RemainingWait)} skip_count={debt.SkipCount} escalated={escalated}");
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

    private static DistributedArchitectureDto BuildCliArchitectureReport(CliExecutionContext context)
    {
        return DistributedArchitectureDto.Create(
            context.Workspace,
            context.Agents,
            context.WorkerProfiles,
            operatorControlsEnabled: false);
    }
}
