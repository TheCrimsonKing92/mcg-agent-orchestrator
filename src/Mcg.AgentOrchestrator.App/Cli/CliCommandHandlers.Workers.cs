using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool? TryExecuteWorkerCommand(string command, IReadOnlyList<string> parts, CliExecutionContext context)
{
    switch (command)
    {
        case "worker-profiles":
            ConsoleViews.PrintWorkerProfiles(context.WorkerProfiles);
            return false;

        case "worker-profile":
            CliArgumentParser.RequirePartCount(parts, 3, "worker-profile <name> <command-template>");
            context.WorkerProfiles = context.WorkerProfiles.Upsert(new WorkerProfile(parts[1], parts[2]));
            WorkerProfileStore.Save(context.WorkerProfilePath, context.WorkerProfiles);
            ConsoleViews.PrintWorkerProfiles(context.WorkerProfiles);
            return false;

        case "worker-profile-check":
            ConsoleViews.PrintWorkerProfileChecks(context.WorkerProfiles, parts.Count > 1 ? parts[1] : null);
            return false;

        case "worker-profile-export":
            CliArgumentParser.RequirePartCount(parts, 2, "worker-profile-export <path>");
            WorkerProfileStore.Save(parts[1], context.WorkerProfiles);
            Console.WriteLine($"Worker profiles exported: {Path.GetFullPath(parts[1])}");
            return false;

        case "worker-profile-import":
            CliArgumentParser.RequirePartCount(parts, 2, "worker-profile-import <path> [merge|replace]");
            var importedProfiles = WorkerProfileStore.LoadRequired(parts[1]);
            var mode = parts.Count > 2 ? parts[2] : "merge";
            context.WorkerProfiles = CliArgumentParser.ParseWorkerProfileImportMode(mode) switch
            {
                WorkerProfileImportMode.Merge => context.WorkerProfiles.Merge(importedProfiles),
                WorkerProfileImportMode.Replace => importedProfiles,
                _ => throw new InvalidOperationException($"Unsupported worker profile import mode: {mode}.")
            };
            WorkerProfileStore.Save(context.WorkerProfilePath, context.WorkerProfiles);
            Console.WriteLine($"Worker profiles imported: {importedProfiles.Profiles.Count} ({mode.ToLowerInvariant()})");
            ConsoleViews.PrintWorkerProfiles(context.WorkerProfiles);
            return false;

        case "worker-dispatch":
            CliArgumentParser.RequirePartCount(parts, 4, "worker-dispatch <task-number> <worker-name> <command-template>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var workerTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            var brief = context.Kernel.BuildTaskBrief(context.CurrentGoal.Id, workerTask.Id);
            var preparation = WorkerCommandTemplate.Prepare(
                brief,
                parts[2],
                parts[3],
                context.Workspace.PromptDirectory);
            context.Kernel.RecordTaskDispatch(
                context.CurrentGoal.Id,
                workerTask.Id,
                new TaskDispatchRecord(
                    parts[2],
                    preparation.Command,
                    context.Workspace.RootDirectory,
                    DateTimeOffset.UtcNow,
                    PromptCharacterCount: preparation.PromptCharacterCount));
            Console.WriteLine($"Prompt: {preparation.PromptPath}");
            ConsoleViews.PrintTask(context.CurrentGoal, workerTask);
            return true;

        case "profile-dispatch":
            CliArgumentParser.RequirePartCount(parts, 3, "profile-dispatch <task-number> <profile-name>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var profileTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            var profile = context.WorkerProfiles.GetRequired(parts[2]);
            var profileDispatch = GoalManagementCommandService.ProfileDispatchTask(context.Kernel, context.Workspace, context.CurrentGoal, profileTask, profile);
            Console.WriteLine($"Prompt: {profileDispatch.PromptPath}");
            ConsoleViews.PrintTask(context.CurrentGoal, profileTask);
            return true;

        case "profile-dispatch-ready":
            CliArgumentParser.RequirePartCount(parts, 2, "profile-dispatch-ready <profile-name>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var readyProfile = context.WorkerProfiles.GetRequired(parts[1]);
            var dispatched = GoalManagementCommandService.ProfileDispatchReadyTasks(context.Kernel, context.Workspace, context.CurrentGoal, readyProfile);
            foreach (var dispatchResult in dispatched)
            {
                Console.WriteLine($"Task {ConsoleViews.GetTaskDisplayNumber(context.CurrentGoal, dispatchResult.Task.Id)} prompt: {dispatchResult.PromptPath}");
            }

            Console.WriteLine($"Profile dispatches created: {dispatched.Count}");
            return dispatched.Count > 0;

        case "subscription-dispatch":
            CliArgumentParser.RequirePartCount(parts, 2, "subscription-dispatch <task-number>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var subscriptionTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            var subscriptionDispatch = GoalManagementCommandService.SubscriptionDispatchTask(context.Kernel, context.Workspace, context.CurrentGoal, subscriptionTask, context.Agents, context.WorkerProfiles);
            Console.WriteLine($"Profile: {subscriptionDispatch.Task.LastDispatch?.WorkerName}");
            Console.WriteLine($"Prompt: {subscriptionDispatch.PromptPath}");
            ConsoleViews.PrintTask(context.CurrentGoal, subscriptionTask);
            return true;

        case "subscription-dispatch-ready":
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var subscriptionDispatches = GoalManagementCommandService.SubscriptionDispatchReadyTasks(context.Kernel, context.Workspace, context.CurrentGoal, context.Agents, context.WorkerProfiles);
            foreach (var dispatchResult in subscriptionDispatches)
            {
                Console.WriteLine($"Task {ConsoleViews.GetTaskDisplayNumber(context.CurrentGoal, dispatchResult.Task.Id)} profile {dispatchResult.Task.LastDispatch?.WorkerName}: {dispatchResult.PromptPath}");
            }

            Console.WriteLine($"Subscription dispatches created: {subscriptionDispatches.Count}");
            return subscriptionDispatches.Count > 0;

        case "start-subscription-ready":
            EnsureCliConfirmation(
                parts,
                "--confirm-batch-start",
                "start-subscription-ready requires --confirm-batch-start because it can start multiple worker processes.");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
                SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
                    context.CurrentGoal,
                    context.Agents,
                    context.WorkerProfiles,
                    task => context.Kernel.BuildTaskBrief(context.CurrentGoal.Id, task.Id).Content.Length),
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            var subscriptionStart = GoalManagementCommandService.StartSubscriptionReadyTasks(context.Kernel, context.Workspace, context.CurrentGoal, context.Agents, context.WorkerProfiles);
            ConsoleViews.PrintSubscriptionStartResult(context.CurrentGoal, subscriptionStart);
            return subscriptionStart.Dispatches.Count > 0 || subscriptionStart.Processes.Tasks.Count > 0;

        case "execute-dispatch":
            CliArgumentParser.RequirePartCount(parts, 2, "execute-dispatch <task-number> --confirm-dispatch-start");
            EnsureCliConfirmation(
                parts,
                "--confirm-dispatch-start",
                "execute-dispatch requires --confirm-dispatch-start because it can start a worker process.");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var executeTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            new LocalDispatchRunner()
                .ExecuteLatestDispatchAsync(context.Kernel, context.CurrentGoal.Id, executeTask.Id)
                .GetAwaiter()
                .GetResult();
            ConsoleViews.PrintTask(context.CurrentGoal, executeTask);
            return true;

        case "start-dispatch":
            CliArgumentParser.RequirePartCount(parts, 2, "start-dispatch <task-number> --confirm-dispatch-start");
            EnsureCliConfirmation(
                parts,
                "--confirm-dispatch-start",
                "start-dispatch requires --confirm-dispatch-start because it can start a worker process.");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var startTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
                SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(context.Kernel, context.CurrentGoal, startTask),
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            new BackgroundDispatchRunner().StartLatestDispatch(context.Kernel, context.CurrentGoal.Id, startTask.Id, context.Workspace.LogDirectory);
            ConsoleViews.PrintTask(context.CurrentGoal, startTask);
            return true;

        case "start-dispatches":
            EnsureCliConfirmation(
                parts,
                "--confirm-batch-start",
                "start-dispatches requires --confirm-batch-start because it can start multiple worker processes.");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
                SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(context.Kernel, context.CurrentGoal),
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            var started = GoalManagementCommandService.StartDispatches(context.Kernel, context.Workspace, context.CurrentGoal);
            ConsoleViews.PrintProcessBatchResult(context.CurrentGoal, started);
            return started.Tasks.Count > 0;

        case "refresh-dispatch":
            CliArgumentParser.RequirePartCount(parts, 2, "refresh-dispatch <task-number>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var refreshTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            new BackgroundDispatchRunner().RefreshLatestProcess(context.Kernel, context.CurrentGoal.Id, refreshTask.Id);
            ConsoleViews.PrintTask(context.CurrentGoal, refreshTask);
            return true;

        case "refresh-dispatches":
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var refreshed = GoalManagementCommandService.RefreshDispatches(context.Kernel, context.CurrentGoal);
            ConsoleViews.PrintProcessBatchResult(context.CurrentGoal, refreshed);
            return refreshed.Tasks.Count > 0;

        case "logs":
            CliArgumentParser.RequirePartCount(parts, 2, "logs <task-number> [stdout|stderr|exit|all]");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var logTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            ConsoleViews.PrintProcessLogs(logTask, parts.Count > 2 ? CliArgumentParser.ParseProcessLogStream(parts[2]) : ProcessLogStream.All);
            return false;

        case "cancel-dispatch":
            CliArgumentParser.RequirePartCount(parts, 2, "cancel-dispatch <task-number>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var cancelTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            new BackgroundDispatchRunner().CancelLatestProcess(context.Kernel, context.CurrentGoal.Id, cancelTask.Id);
            ConsoleViews.PrintTask(context.CurrentGoal, cancelTask);
            return true;

        default:
            return null;
    }
}
}
