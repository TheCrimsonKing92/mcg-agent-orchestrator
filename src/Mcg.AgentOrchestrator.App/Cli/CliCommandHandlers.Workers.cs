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
            GoalRefinementGate.EnsureRefined(context.Kernel, context.Workspace, context.Providers, context.CurrentGoal, eventWriter: context.EventWriter);
            GoalRefinementGate.ThrowIfAwaitingClarification(context.Workspace, context.CurrentGoal);
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
            {
                string? profileGoalPrefix = null;
                string profileTaskNumber;
                int profileIndex;
                if (parts.Count > 1 && parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Count < 5)
                        throw new ArgumentException("Usage: profile-dispatch [--goal <goal-prefix>] <task-number> <profile-name>");
                    profileGoalPrefix = parts[2];
                    profileTaskNumber = parts[3];
                    profileIndex = 4;
                }
                else
                {
                    if (parts.Count < 3)
                        throw new ArgumentException("Usage: profile-dispatch [--goal <goal-prefix>] <task-number> <profile-name>");
                    profileTaskNumber = parts[1];
                    profileIndex = 2;
                }

                context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, profileGoalPrefix);
                var profileTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, profileTaskNumber);
                var profile = context.WorkerProfiles.GetRequired(parts[profileIndex]);
                EnsureGoalWorkspaceForDispatch(context, context.CurrentGoal);
                try
                {
                    var profileDispatch = GoalManagementCommandService.ProfileDispatchTask(context.Kernel, context.Workspace, context.CurrentGoal, profileTask, profile, context.Agents, context.Providers);
                    Console.WriteLine($"Prompt: {profileDispatch.PromptPath}");
                    ConsoleViews.PrintTask(context.CurrentGoal, profileTask);
                    if (HasCliConfirmation(parts, "--confirm-dispatch-start"))
                        LaunchLatestDispatch(context, context.CurrentGoal, profileTask, parts, "profile-dispatch", refreshBeforeStart: false);
                    return true;
                }
                catch (InvalidOperationException profileEx)
                {
                    throw new InvalidOperationException(
                        $"Goal '{context.CurrentGoal.Id.Value[..8]}' task {ConsoleViews.GetTaskDisplayNumber(context.CurrentGoal, profileTask.Id)}: {profileEx.Message}", profileEx);
                }
            }

        case "profile-dispatch-ready":
            {
                string? readyGoalPrefix = null;
                int readyProfileIndex = 1;
                if (parts.Count > 1 && parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Count < 4)
                        throw new ArgumentException("Usage: profile-dispatch-ready [--goal <goal-prefix>] <profile-name>");
                    readyGoalPrefix = parts[2];
                    readyProfileIndex = 3;
                }
                if (parts.Count <= readyProfileIndex)
                    throw new ArgumentException("Usage: profile-dispatch-ready [--goal <goal-prefix>] <profile-name>");
                context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, readyGoalPrefix);
                var readyProfile = context.WorkerProfiles.GetRequired(parts[readyProfileIndex]);
                EnsureGoalWorkspaceForDispatch(context, context.CurrentGoal);
                var dispatched = GoalManagementCommandService.ProfileDispatchReadyTasks(context.Kernel, context.Workspace, context.CurrentGoal, readyProfile, context.Agents, context.Providers);
                foreach (var dispatchResult in dispatched)
                {
                    Console.WriteLine($"Task {ConsoleViews.GetTaskDisplayNumber(context.CurrentGoal, dispatchResult.Task.Id)} prompt: {dispatchResult.PromptPath}");
                }

                Console.WriteLine($"Profile dispatches created: {dispatched.Count}");
                return dispatched.Count > 0;
            }

        case "subscription-dispatch":
            var subscriptionTask = ResolveDispatchCommandTask(parts, context, "subscription-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [--confirm-limit-review <note>|--confirm-limit-review --text-file <path>] [--subscription-model <model>] [--subscription <profile>] [--subscription-reasoning <effort>] [--allow-git-reference]");
            AcknowledgeSubscriptionLimitReviewFromCli(context, subscriptionTask, parts);
            EnsureGoalWorkspaceForDispatch(context, context.CurrentGoal!);
            try
            {
                var overrideProfileName = GetFlagValue(parts, "--subscription");
                var overrideModelName = GetFlagValue(parts, "--subscription-model");
                var overrideReasoning = GetFlagValue(parts, "--subscription-reasoning");
                DispatchModelOverride? modelOverride = overrideProfileName is not null || overrideModelName is not null || overrideReasoning is not null
                    ? new DispatchModelOverride(overrideProfileName, overrideModelName, overrideReasoning)
                    : null;
                var allowGitReference = HasCliConfirmation(parts, "--allow-git-reference");
                var subscriptionDispatch = GoalManagementCommandService.SubscriptionDispatchTask(
                    context.Kernel,
                    context.Workspace,
                    context.CurrentGoal!,
                    subscriptionTask,
                    context.Agents,
                    context.WorkerProfiles,
                    modelOverride,
                    allowGitReference,
                    context.Providers);
                Console.WriteLine($"Profile: {subscriptionDispatch.Task.LastDispatch?.WorkerName}");
                Console.WriteLine($"Prompt: {subscriptionDispatch.PromptPath}");
                ConsoleViews.PrintTask(context.CurrentGoal!, subscriptionTask);
                if (HasCliConfirmation(parts, "--confirm-dispatch-start"))
                    LaunchLatestDispatch(context, context.CurrentGoal!, subscriptionTask, parts, "subscription-dispatch", refreshBeforeStart: false);
                return true;
            }
            catch (InvalidOperationException subscriptionEx)
            {
                throw new InvalidOperationException(
                    $"Goal '{context.CurrentGoal!.Id.Value[..8]}' task {ConsoleViews.GetTaskDisplayNumber(context.CurrentGoal!, subscriptionTask.Id)}: {subscriptionEx.Message}", subscriptionEx);
            }

        case "subscription-dispatch-ready":
            context.CurrentGoal = ResolveDispatchCommandGoal(parts, context, "subscription-dispatch-ready [goal-prefix|--goal <goal-prefix>]");
            EnsureGoalWorkspaceForDispatch(context, context.CurrentGoal);
            var subscriptionDispatches = GoalManagementCommandService.SubscriptionDispatchReadyBatch(context.Kernel, context.Workspace, context.CurrentGoal, context.Agents, context.WorkerProfiles, context.Providers);
            EmitReadyBlockedDiagnostics(subscriptionDispatches.Blocked);
            foreach (var dispatchResult in subscriptionDispatches.Dispatches)
            {
                Console.WriteLine($"Task {ConsoleViews.GetTaskDisplayNumber(context.CurrentGoal, dispatchResult.Task.Id)} profile {dispatchResult.Task.LastDispatch?.WorkerName}: {dispatchResult.PromptPath}");
            }

            Console.WriteLine($"Subscription dispatches created: {subscriptionDispatches.Dispatches.Count}");
            return subscriptionDispatches.Dispatches.Count > 0;

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
                    context.WorkerProfiles,
                    context.Providers);
                ConsoleViews.PrintSubscriptionStartResult(goal, result);
                startedAny |= result.Dispatches.Count > 0 || result.Processes.Tasks.Count > 0;
            }

            return startedAny;

        case "start-subscription-ready":
            var startReadyPolicy = ResolveCliAutonomyPolicy(parts);
            context.CurrentGoal = ResolveDispatchCommandGoal(parts, context, "start-subscription-ready [goal-prefix|--goal <goal-prefix>] --confirm-batch-start [--confirm-large-paid-subscription-start]");
            if (!startReadyPolicy.Allows(AutonomyAction.DispatchStart))
            {
                EmitReadyBlockedDiagnosticsForAssigned(context.CurrentGoal, context.Agents, context.WorkerProfiles, "autonomy-policy");
            }

            startReadyPolicy.ThrowIfDisallowed(AutonomyAction.DispatchStart, "start-subscription-ready");
            if (!HasCliConfirmation(parts, "--confirm-batch-start"))
            {
                EmitReadyBlockedDiagnosticsForAssigned(context.CurrentGoal, context.Agents, context.WorkerProfiles, "start-gate");
            }

            EnsureCliConfirmation(
                parts,
                "--confirm-batch-start",
                "start-subscription-ready requires --confirm-batch-start because it can start multiple worker processes.");
            var startReadySweep = TerminalGoalSweep.Run(context.Kernel, context.Workspace.ExecutionDirectory, context.CurrentGoal.Id);
            ConsoleViews.PrintTerminalGoalSweep(startReadySweep);
            context.CurrentGoal = context.Kernel.GetGoal(context.CurrentGoal.Id);
            var readiness = GoalReadinessPreflight.Build(
                context.CurrentGoal,
                context.Agents,
                context.Workspace.ExecutionDirectory,
                context.WorkerProfiles,
                context.Worktrees.TryResolve);
            if (!readiness.AllowsStart(HasCliConfirmation(parts, "--confirm-readiness-risk")))
            {
                EmitReadyBlockedDiagnosticsForAssigned(context.CurrentGoal, context.Agents, context.WorkerProfiles, "start-gate");
            }

            EnsureGoalReadinessAllowsStart(context, context.CurrentGoal, HasCliConfirmation(parts, "--confirm-readiness-risk"));
            RecordPolicyAllowed(context, context.CurrentGoal, startReadyPolicy, AutonomyAction.DispatchStart, "start-subscription-ready");
            var startReadyRisk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
                    context.CurrentGoal,
                    context.Agents,
                    context.WorkerProfiles,
                    task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(context.Kernel, context.CurrentGoal, task, context.Agents));
            if (startReadyRisk is { IsAnomalous: true } &&
                !HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag))
            {
                EmitReadyBlockedDiagnosticsForAssigned(context.CurrentGoal, context.Agents, context.WorkerProfiles, "prompt-size-cost");
            }

            SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
                startReadyRisk,
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            var subscriptionStart = GoalManagementCommandService.StartSubscriptionReadyTasks(context.Kernel, context.Workspace, context.CurrentGoal, context.Agents, context.WorkerProfiles, context.Providers);
            EmitReadyBlockedDiagnostics(subscriptionStart.BlockedDiagnostics);
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
            GoalManagementCommandService.RefreshPreparedDispatchBeforeStart(
                context.Kernel,
                context.Workspace,
                context.CurrentGoal!,
                executeTask,
                context.Agents,
                context.WorkerProfiles,
                context.Providers);
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
            EnsureCliConfirmation(
                parts,
                "--confirm-dispatch-start",
                "start-dispatch requires --confirm-dispatch-start because it can start a worker process.");
            var startTask = ResolveDispatchCommandTask(parts, context, "start-dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> --confirm-dispatch-start [--confirm-large-paid-subscription-start]");
            LaunchLatestDispatch(context, context.CurrentGoal!, startTask, parts, "start-dispatch");
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
            GoalManagementCommandService.RefreshPreparedDispatchesBeforeStart(
                context.Kernel,
                context.Workspace,
                context.CurrentGoal,
                context.Agents,
                context.WorkerProfiles,
                context.Providers);
            SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
                SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(context.Kernel, context.CurrentGoal),
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            var started = GoalManagementCommandService.StartDispatches(
                context.Kernel,
                context.Workspace,
                context.CurrentGoal,
                context.Agents,
                context.WorkerProfiles,
                context.Providers,
                refreshBeforeStart: false);
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

private static void LaunchLatestDispatch(CliExecutionContext context, Goal goal, TaskSpec task, IReadOnlyList<string> parts, string commandName, bool refreshBeforeStart = true)
{
    var policy = ResolveCliAutonomyPolicy(parts);
    policy.ThrowIfDisallowed(AutonomyAction.DispatchStart, commandName);
    RecordPolicyAllowed(context, goal, policy, AutonomyAction.DispatchStart, commandName);
    SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
        SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(context.Kernel, goal, task),
        HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
    if (refreshBeforeStart)
    {
        GoalManagementCommandService.RefreshPreparedDispatchBeforeStart(
            context.Kernel,
            context.Workspace,
            goal,
            task,
            context.Agents,
            context.WorkerProfiles,
            context.Providers);
    }

    SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
        SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(context.Kernel, goal, task),
        HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
    new BackgroundDispatchRunner().StartLatestDispatch(context.Kernel, goal.Id, task.Id, context.Workspace.LogDirectory);
}

private static void EmitReadyBlockedDiagnostics(IReadOnlyList<ReadyBlockedDiagnostic> diagnostics)
{
    foreach (var diagnostic in diagnostics.OrderBy(diagnostic => diagnostic.TaskNumber))
    {
        Console.Error.WriteLine(diagnostic.ToLine());
    }
}

private static void EmitReadyBlockedDiagnosticsForAssigned(
    Goal goal,
    IReadOnlyList<AgentDefinition> agents,
    WorkerProfileCatalog profiles,
    string reason)
{
    var planItems = SubscriptionPlanBuilder.Build(goal, agents, profiles)
        .Items
        .Where(item => item.TaskStatus == WorkTaskStatus.Assigned)
        .OrderBy(item => item.TaskNumber);
    foreach (var item in planItems)
    {
        var task = goal.Tasks.FirstOrDefault(task => task.Id.Value == item.TaskId);
        var agent = task?.AssignedAgentId is null
            ? null
            : agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId);
        var provider = task is null || agent is null
            ? item.ProfileName
            : WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent, goal, task, profiles);
        provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
        Console.Error.WriteLine(
            $"READY_BLOCKED goal={goal.Id.Value[..8]} task={item.TaskNumber} provider={provider} reason={reason}");
    }
}

private static bool IsLogStreamKeyword(string value) =>
    value is "all" or "stdout" or "out" or "stderr" or "err" or "exit" or "exitcode" or "code";

private static void AcknowledgeSubscriptionLimitReviewFromCli(CliExecutionContext context, TaskSpec task, IReadOnlyList<string> parts)
{
    const string flag = "--confirm-limit-review";
    var note = ResolveFlagTextArgumentOrDefault(parts, flag, "--text-file");
    if (note is not null)
    {
        context.Kernel.AcknowledgeSubscriptionLimitReview(context.CurrentGoal!.Id, task.Id, note);
        return;
    }

    if (HasCliConfirmation(parts, flag))
    {
        throw new ArgumentException("Usage: subscription-dispatch <task-number> [--confirm-limit-review <note>|--confirm-limit-review --text-file <path>]");
    }

    if (DispatchFailureClassifier.RequiresSubscriptionLimitReview(task) &&
        !DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, DateTimeOffset.UtcNow, out _))
    {
        var failures = DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task);
        throw new InvalidOperationException(
            $"Task '{task.Id}' hit a recoverable subscription usage limit {failures} time(s); inspect model, profile, or timing, then rerun subscription-dispatch with --confirm-limit-review <note> or --confirm-limit-review --text-file <path>.");
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
    else if (parts.Count > 2 && parts[2].Equals("--goal", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Count < 4)
        {
            throw new ArgumentException($"Usage: {usage}");
        }

        goalPrefix = parts[3];
        taskNumber = parts[1];
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
