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
            var smokeTarget = parts.Count > 1 ? parts[1] : ProviderSmokeRunner.DefaultTarget;
            TaskSpec? smokeTask = null;
            if (parts.Count > 2)
            {
                context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
                smokeTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[2]);
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
            DashboardHost.RunDashboardHostAsync(context.Workspace, context.Providers, serveArgs).GetAwaiter().GetResult();
            return false;

        case "hosted-dashboard":
            var hostedArgs = DashboardHost.ParseDashboardHostArgs(parts, "hosted-dashboard", defaultOpenBrowser: false);
            DashboardHost.RunDashboardHostAsync(context.Workspace, context.Providers, hostedArgs).GetAwaiter().GetResult();
            return false;

        case "simple-hosted-dashboard":
            var simpleHostedArgs = DashboardHost.ParseDashboardHostArgs(parts, "simple-hosted-dashboard", defaultOpenBrowser: false);
            DashboardHost.RunDashboardHostAsync(context.Workspace, context.Providers, simpleHostedArgs).GetAwaiter().GetResult();
            return false;

        case "open-dashboard":
            var openArgs = DashboardHost.ParseDashboardHostArgs(parts, "open-dashboard", defaultOpenBrowser: true);
            DashboardHost.RunDashboardHostAsync(context.Workspace, context.Providers, openArgs).GetAwaiter().GetResult();
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
