using Mcg.AgentOrchestrator.Core;
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
                throw new InvalidOperationException("provider-smoke all requires --confirm-all because broad paid smoke tests are deliberate.");
            }

            if (ProviderSmokeRunner.RequiresPaidConfirmation(smokeTarget) && !confirmAllSmoke && !confirmPaidSmoke)
            {
                throw new InvalidOperationException("Paid provider smoke requires --confirm-paid-smoke because it can make a live billable request.");
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
            File.WriteAllText(transcriptPath, GoalTranscriptRenderer.Render(context.Kernel, context.CurrentGoal));
            Console.WriteLine($"Transcript: {Path.GetFullPath(transcriptPath)}");
            return false;

        default:
            return null;
    }
}
}
