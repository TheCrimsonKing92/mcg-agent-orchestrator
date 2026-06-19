using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    private static bool? TryExecuteSystemCommand(string command, IReadOnlyList<string> parts, CliExecutionContext context)
    {
        switch (command)
        {
            case "attention":
            {
                var store = CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory);
                var queue = store.GetAttentionQueueAsync().GetAwaiter().GetResult();
                ConsoleViews.PrintAttentionQueue(queue);
                return false;
            }

            case "doctor":
                ConsoleViews.PrintHealth(OrchestratorHealthInspector.InspectCurrentEnvironment(new AgentCatalog(context.Agents), context.WorkerProfiles));
                return false;

            case "tenant":
                ConsoleViews.PrintTenant(context.Workspace);
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

            case "operator-listen":
            {
                var catalog = OperatorChannelStore.Load(context.Workspace.OperatorChannelPath);
                var botToken = Environment.GetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN");
                var store = CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory);
                var runtime = OperatorChannelFactory.CreateDiscordRuntime(
                    catalog,
                    context.Workspace.OperatorChannelPath,
                    botToken,
                    store,
                    context.Workspace.OrchestratorDirectory);
                if (runtime is null)
                {
                    Console.WriteLine("operator-listen: Discord not configured or MCGO_DISCORD_BOT_TOKEN missing.");
                    return false;
                }

                Console.WriteLine("operator-listen: Discord listener running. Progress view refreshes every 15 minutes. Press Ctrl+C to stop.");
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cts.Cancel();
                };
                RunOperatorListenLoopAsync(context, store, runtime.ProgressView, cts.Token).GetAwaiter().GetResult();
                runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return false;
            }

            case "monitor-goal":
                GoalMonitoringSubscriptionCommand.RunAsync(parts, Console.Out).GetAwaiter().GetResult();
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
                File.WriteAllText(dashboardPath, DashboardRenderer.Render(context.Kernel, dashboardArgs.Options));
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
                File.WriteAllText(transcriptPath, GoalTranscriptRenderer.Render(context.Kernel, context.CurrentGoal, context.Agents));
                Console.WriteLine($"Transcript: {Path.GetFullPath(transcriptPath)}");
                return false;

            default:
                return null;
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
            await ReconcileProgressViewAsync(context, collaborationStore, progressView, cancellationToken);

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
            DateTimeOffset.UtcNow));
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
                var botToken = Environment.GetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN");
                Console.WriteLine($"Operator channel: type={catalog.ChannelType}");
                Console.WriteLine($"  forumChannelId: {catalog.ForumChannelId ?? "(none)"}");
                Console.WriteLine($"  progressThreadId: {catalog.ProgressThreadId ?? "(none)"}");
                Console.WriteLine($"  progressStatusMessageId: {catalog.ProgressStatusMessageId ?? "(none)"}");
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
                var botToken = Environment.GetEnvironmentVariable("MCGO_DISCORD_BOT_TOKEN");
                var channel = OperatorChannelFactory.Create(catalog, botToken, context.Workspace.OrchestratorDirectory);
                OperatorChannelFactory.SendTestEscalationAsync(channel, Console.Out).GetAwaiter().GetResult();
                return false;
            }
            default:
                throw new ArgumentException($"Unknown operator-channel sub-command '{sub}'. Usage: operator-channel set|show|test");
        }
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

    private static DistributedArchitectureDto BuildCliArchitectureReport(CliExecutionContext context)
    {
        return DistributedArchitectureDto.Create(
            context.Workspace,
            context.Agents,
            context.WorkerProfiles,
            operatorControlsEnabled: false);
    }
}
