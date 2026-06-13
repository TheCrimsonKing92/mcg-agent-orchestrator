using Mcg.AgentOrchestrator.App.Orchestration;
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
            ConsoleViews.PrintWorkerProfileChecks(new AgentCatalog(context.Agents), context.WorkerProfiles, parts.Count > 1 ? parts[1] : null);
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
            var workerExecutionDirectory = context.Workspace.ResolveExecutionDirectory(context.CurrentGoal.Id);
            var brief = context.Kernel.BuildTaskBrief(context.CurrentGoal.Id, workerTask.Id, workingDirectory: workerExecutionDirectory);
            var preparation = WorkerCommandTemplate.Prepare(
                brief,
                parts[2],
                parts[3],
                context.Workspace.PromptDirectory,
                WorkerProfileDispatcher.BuildDispatchVariables(workerTask.RequiredRole, workerExecutionDirectory, null));
            context.Kernel.RecordTaskDispatch(
                context.CurrentGoal.Id,
                workerTask.Id,
                new TaskDispatchRecord(
                    parts[2],
                    preparation.Command,
                    workerExecutionDirectory,
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
            var profileDispatch = GoalManagementCommandService.ProfileDispatchTask(context.Kernel, context.Workspace, context.CurrentGoal, profileTask, profile, context.Agents);
            Console.WriteLine($"Prompt: {profileDispatch.PromptPath}");
            ConsoleViews.PrintTask(context.CurrentGoal, profileTask);
            return true;

        case "profile-dispatch-ready":
            CliArgumentParser.RequirePartCount(parts, 2, "profile-dispatch-ready <profile-name>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var readyProfile = context.WorkerProfiles.GetRequired(parts[1]);
            var dispatched = GoalManagementCommandService.ProfileDispatchReadyTasks(context.Kernel, context.Workspace, context.CurrentGoal, readyProfile, context.Agents);
            foreach (var dispatchResult in dispatched)
            {
                Console.WriteLine($"Task {ConsoleViews.GetTaskDisplayNumber(context.CurrentGoal, dispatchResult.Task.Id)} prompt: {dispatchResult.PromptPath}");
            }

            Console.WriteLine($"Profile dispatches created: {dispatched.Count}");
            return dispatched.Count > 0;

        case "subscription-dispatch":
            var subscriptionTask = ResolveDispatchCommandTask(parts, context, "subscription-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [--confirm-limit-review <note>]");
            AcknowledgeSubscriptionLimitReviewFromCli(context, subscriptionTask, parts);
            var subscriptionDispatch = GoalManagementCommandService.SubscriptionDispatchTask(context.Kernel, context.Workspace, context.CurrentGoal!, subscriptionTask, context.Agents, context.WorkerProfiles);
            Console.WriteLine($"Profile: {subscriptionDispatch.Task.LastDispatch?.WorkerName}");
            Console.WriteLine($"Prompt: {subscriptionDispatch.PromptPath}");
            ConsoleViews.PrintTask(context.CurrentGoal!, subscriptionTask);
            return true;

        case "subscription-dispatch-ready":
            context.CurrentGoal = ResolveDispatchCommandGoal(parts, context, "subscription-dispatch-ready [goal-prefix|--goal <goal-prefix>]");
            var subscriptionDispatches = GoalManagementCommandService.SubscriptionDispatchReadyTasks(context.Kernel, context.Workspace, context.CurrentGoal, context.Agents, context.WorkerProfiles);
            foreach (var dispatchResult in subscriptionDispatches)
            {
                Console.WriteLine($"Task {ConsoleViews.GetTaskDisplayNumber(context.CurrentGoal, dispatchResult.Task.Id)} profile {dispatchResult.Task.LastDispatch?.WorkerName}: {dispatchResult.PromptPath}");
            }

            Console.WriteLine($"Subscription dispatches created: {subscriptionDispatches.Count}");
            return subscriptionDispatches.Count > 0;

        case "cross-goal-start-plan":
            ConsoleViews.PrintCrossGoalSubscriptionStartPlan(CrossGoalSubscriptionStartPlanner.Build(
                context.Kernel,
                context.Agents,
                context.WorkerProfiles,
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag)));
            return false;

        case "start-subscription-ready-goals":
            var startGoalsPolicy = ResolveCliAutonomyPolicy(parts);
            startGoalsPolicy.ThrowIfDisallowed(AutonomyAction.DispatchStart, "start-subscription-ready-goals");
            EnsureCliConfirmation(
                parts,
                "--confirm-batch-start",
                "start-subscription-ready-goals requires --confirm-batch-start because it can start multiple worker processes across goals.");
            var crossGoalPlan = CrossGoalSubscriptionStartPlanner.Build(
                context.Kernel,
                context.Agents,
                context.WorkerProfiles,
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            ConsoleViews.PrintCrossGoalSubscriptionStartPlan(crossGoalPlan);
            var startedAny = false;
            foreach (var candidate in crossGoalPlan.FirstBatchCandidates)
            {
                var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, null, candidate.GoalId);
                EnsureGoalReadinessAllowsStart(context, goal, HasCliConfirmation(parts, "--confirm-readiness-risk"));
                RecordPolicyAllowed(context, goal, startGoalsPolicy, AutonomyAction.DispatchStart, "start-subscription-ready-goals");
                SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
                    SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
                        goal,
                        context.Agents,
                        context.WorkerProfiles,
                        task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(context.Kernel, goal, task, context.Agents)),
                    HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
                var result = GoalManagementCommandService.StartSubscriptionReadyTasks(
                    context.Kernel,
                    context.Workspace,
                    goal,
                    context.Agents,
                    context.WorkerProfiles);
                ConsoleViews.PrintSubscriptionStartResult(goal, result);
                startedAny |= result.Dispatches.Count > 0 || result.Processes.Tasks.Count > 0;
            }

            return startedAny;

        case "start-subscription-ready":
            var startReadyPolicy = ResolveCliAutonomyPolicy(parts);
            startReadyPolicy.ThrowIfDisallowed(AutonomyAction.DispatchStart, "start-subscription-ready");
            EnsureCliConfirmation(
                parts,
                "--confirm-batch-start",
                "start-subscription-ready requires --confirm-batch-start because it can start multiple worker processes.");
            context.CurrentGoal = ResolveDispatchCommandGoal(parts, context, "start-subscription-ready [goal-prefix|--goal <goal-prefix>] --confirm-batch-start [--confirm-large-paid-subscription-start]");
            EnsureGoalReadinessAllowsStart(context, context.CurrentGoal, HasCliConfirmation(parts, "--confirm-readiness-risk"));
            RecordPolicyAllowed(context, context.CurrentGoal, startReadyPolicy, AutonomyAction.DispatchStart, "start-subscription-ready");
            SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
                SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
                    context.CurrentGoal,
                    context.Agents,
                    context.WorkerProfiles,
                    task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(context.Kernel, context.CurrentGoal, task, context.Agents)),
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            var subscriptionStart = GoalManagementCommandService.StartSubscriptionReadyTasks(context.Kernel, context.Workspace, context.CurrentGoal, context.Agents, context.WorkerProfiles);
            ConsoleViews.PrintSubscriptionStartResult(context.CurrentGoal, subscriptionStart);
            return subscriptionStart.Dispatches.Count > 0 || subscriptionStart.Processes.Tasks.Count > 0;

        case "execute-dispatch":
            var executePolicy = ResolveCliAutonomyPolicy(parts);
            executePolicy.ThrowIfDisallowed(AutonomyAction.DispatchStart, "execute-dispatch");
            EnsureCliConfirmation(
                parts,
                "--confirm-dispatch-start",
                "execute-dispatch requires --confirm-dispatch-start because it can start a worker process.");
            var executeTask = ResolveDispatchCommandTask(parts, context, "execute-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> --confirm-dispatch-start [--confirm-large-paid-subscription-start]");
            RecordPolicyAllowed(context, context.CurrentGoal!, executePolicy, AutonomyAction.DispatchStart, "execute-dispatch");
            SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
                SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(context.Kernel, context.CurrentGoal!, executeTask),
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            new LocalDispatchRunner()
                .ExecuteLatestDispatchAsync(context.Kernel, context.CurrentGoal!.Id, executeTask.Id)
                .GetAwaiter()
                .GetResult();
            ConsoleViews.PrintTask(context.CurrentGoal!, executeTask);
            return true;

        case "start-dispatch":
            var startDispatchPolicy = ResolveCliAutonomyPolicy(parts);
            startDispatchPolicy.ThrowIfDisallowed(AutonomyAction.DispatchStart, "start-dispatch");
            EnsureCliConfirmation(
                parts,
                "--confirm-dispatch-start",
                "start-dispatch requires --confirm-dispatch-start because it can start a worker process.");
            var startTask = ResolveDispatchCommandTask(parts, context, "start-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> --confirm-dispatch-start [--confirm-large-paid-subscription-start]");
            RecordPolicyAllowed(context, context.CurrentGoal!, startDispatchPolicy, AutonomyAction.DispatchStart, "start-dispatch");
            SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
                SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(context.Kernel, context.CurrentGoal!, startTask),
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            new BackgroundDispatchRunner().StartLatestDispatch(context.Kernel, context.CurrentGoal!.Id, startTask.Id, context.Workspace.LogDirectory);
            ConsoleViews.PrintTask(context.CurrentGoal!, startTask);
            return true;

        case "start-dispatches":
            var startDispatchesPolicy = ResolveCliAutonomyPolicy(parts);
            startDispatchesPolicy.ThrowIfDisallowed(AutonomyAction.DispatchStart, "start-dispatches");
            EnsureCliConfirmation(
                parts,
                "--confirm-batch-start",
                "start-dispatches requires --confirm-batch-start because it can start multiple worker processes.");
            context.CurrentGoal = ResolveDispatchCommandGoal(parts, context, "start-dispatches [goal-prefix|--goal <goal-prefix>] --confirm-batch-start [--confirm-large-paid-subscription-start]");
            EnsureGoalReadinessAllowsStart(context, context.CurrentGoal, HasCliConfirmation(parts, "--confirm-readiness-risk"));
            RecordPolicyAllowed(context, context.CurrentGoal, startDispatchesPolicy, AutonomyAction.DispatchStart, "start-dispatches");
            SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
                SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(context.Kernel, context.CurrentGoal),
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            var started = GoalManagementCommandService.StartDispatches(context.Kernel, context.Workspace, context.CurrentGoal);
            ConsoleViews.PrintProcessBatchResult(context.CurrentGoal, started);
            return started.Tasks.Count > 0;

        case "refresh-dispatch":
            var refreshPolicy = ResolveCliAutonomyPolicy(parts);
            var refreshTask = ResolveDispatchCommandTask(parts, context, "refresh-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number>");
            EnsurePolicyAllows(context, context.CurrentGoal!, refreshPolicy, AutonomyAction.Refresh, "refresh-dispatch");
            new BackgroundDispatchRunner().RefreshLatestProcess(context.Kernel, context.CurrentGoal!.Id, refreshTask.Id);
            ConsoleViews.PrintTask(context.CurrentGoal!, refreshTask);
            return true;

        case "refresh-dispatches":
            var refreshBatchPolicy = ResolveCliAutonomyPolicy(parts);
            context.CurrentGoal = ResolveDispatchCommandGoal(parts, context, "refresh-dispatches [goal-prefix|--goal <goal-prefix>]");
            EnsurePolicyAllows(context, context.CurrentGoal, refreshBatchPolicy, AutonomyAction.Refresh, "refresh-dispatches");
            var refreshed = GoalManagementCommandService.RefreshDispatches(context.Kernel, context.CurrentGoal);
            ConsoleViews.PrintProcessBatchResult(context.CurrentGoal, refreshed);
            return refreshed.Tasks.Count > 0;

        case "logs":
            // Peel the optional stream keyword from the end before goal-prefix resolution so that
            // "logs 1 stdout" does not misinterpret "1" as a goal prefix.
            var logStreamArg = parts.Count >= 3 && IsLogStreamKeyword(parts[^1]) ? parts[^1] : null;
            var logCoreParts = logStreamArg != null ? (IReadOnlyList<string>)parts.Take(parts.Count - 1).ToList() : parts;
            var logTask = ResolveDispatchCommandTask(logCoreParts, context, "logs <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [stdout|stderr|exit|all]");
            ConsoleViews.PrintProcessLogs(logTask, logStreamArg != null ? CliArgumentParser.ParseProcessLogStream(logStreamArg) : ProcessLogStream.All);
            return false;

        case "cancel-dispatch":
            var cancelTask = ResolveDispatchCommandTask(parts, context, "cancel-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number>");
            new BackgroundDispatchRunner().CancelLatestProcess(context.Kernel, context.CurrentGoal!.Id, cancelTask.Id);
            ConsoleViews.PrintTask(context.CurrentGoal!, cancelTask);
            return true;

        default:
            return null;
    }
}

private static bool IsLogStreamKeyword(string value) =>
    value is "all" or "stdout" or "out" or "stderr" or "err" or "exit" or "exitcode" or "code";

private static void AcknowledgeSubscriptionLimitReviewFromCli(CliExecutionContext context, TaskSpec task, IReadOnlyList<string> parts)
{
    const string flag = "--confirm-limit-review";
    var note = GetFlagValue(parts, flag);
    if (note is not null)
    {
        context.Kernel.AcknowledgeSubscriptionLimitReview(context.CurrentGoal!.Id, task.Id, note);
        return;
    }

    if (HasCliConfirmation(parts, flag))
    {
        throw new ArgumentException("Usage: subscription-dispatch <task-number> [--confirm-limit-review <note>]");
    }

    if (DispatchFailureClassifier.RequiresSubscriptionLimitReview(task) &&
        !DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, DateTimeOffset.UtcNow, out _))
    {
        var failures = DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task);
        throw new InvalidOperationException(
            $"Task '{task.Id}' hit a recoverable subscription usage limit {failures} time(s); inspect model, profile, or timing, then rerun subscription-dispatch with --confirm-limit-review <note>.");
    }
}

private static Goal ResolveDispatchCommandGoal(IReadOnlyList<string> parts, CliExecutionContext context, string usage)
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

    return OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, goalPrefix);
}

private static TaskSpec ResolveDispatchCommandTask(IReadOnlyList<string> parts, CliExecutionContext context, string usage)
{
    return ResolveDispatchCommandTaskWithNextIndex(parts, context, usage).Task;
}

private static (TaskSpec Task, int NextIndex) ResolveDispatchCommandTaskWithNextIndex(IReadOnlyList<string> parts, CliExecutionContext context, string usage)
{
    if (parts.Count < 2)
    {
        throw new ArgumentException($"Usage: {usage}");
    }

    string? goalPrefix = null;
    string taskNumber;
    int nextIndex;
    if (parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Count < 4)
        {
            throw new ArgumentException($"Usage: {usage}");
        }

        goalPrefix = parts[2];
        taskNumber = parts[3];
        nextIndex = 4;
    }
    else if (parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal))
    {
        goalPrefix = parts[1];
        taskNumber = parts[2];
        nextIndex = 3;
    }
    else
    {
        taskNumber = parts[1];
        nextIndex = 2;
    }

    context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, goalPrefix);
    return (OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, taskNumber), nextIndex);
}
}
