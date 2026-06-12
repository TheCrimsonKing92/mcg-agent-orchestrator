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
            case "doctor":
                ConsoleViews.PrintHealth(OrchestratorHealthInspector.InspectCurrentEnvironment(new AgentCatalog(context.Agents), context.WorkerProfiles));
                return false;

            case "tenant":
                ConsoleViews.PrintTenant(context.Workspace);
                return false;

            case "architecture":
                ConsoleViews.PrintArchitecture(BuildCliArchitectureReport(context));
                return false;

            case "state-rollback":
                if (!parts.Any(part => part.Equals("--confirm-state-rollback", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("State rollback restores state.json from state.json.bak. Re-run with --confirm-state-rollback after confirming no dashboard or worker is writing state.");
                }

                var rollback = OrchestratorStateStore.RestoreBackup(context.StatePath);
                var restored = OrchestratorStateStore.Load(context.StatePath);
                context.Kernel.ReplaceWithSnapshot(restored.ExportSnapshot());
                context.CurrentGoal = OrchestratorEntityResolver.GetLatestGoal(context.Kernel);
                ConsoleViews.PrintStateRollback(rollback);
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

            case "dashboard":
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

    private static DistributedArchitectureDto BuildCliArchitectureReport(CliExecutionContext context)
    {
        return DistributedArchitectureDto.Create(
            context.Workspace,
            context.Agents,
            context.WorkerProfiles,
            operatorControlsEnabled: false);
    }
}
