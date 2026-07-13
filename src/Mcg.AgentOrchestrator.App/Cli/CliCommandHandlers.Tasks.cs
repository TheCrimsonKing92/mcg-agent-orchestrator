using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool? TryExecuteTaskCommand(string command, IReadOnlyList<string> parts, CliExecutionContext context)
{
    switch (command)
    {
        case "task":
            var taskTarget = ResolveCommandTaskTarget(parts, context, "task <task-number|task-id-prefix>|<goal-prefix> <task-number|task-id-prefix>|--goal <goal-prefix> <task-number|task-id-prefix>");
            ConsoleViews.PrintTask(context.CurrentGoal!, taskTarget.Task);
            return false;

        case "tasks":
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            ConsoleViews.PrintTaskQueryResult(context.CurrentGoal, context.Kernel.QueryTasks(context.CurrentGoal.Id, CliArgumentParser.ParseTaskQuery(parts.Skip(1))));
            return false;

        case "add-task":
            CliArgumentParser.RequirePartCount(parts, 3, "add-task <role> <description> | add-task <role> --text-file <path>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var taskDescription = ResolveTextArgument(parts, inlineIndex: 2, "add-task <role> <description> | add-task <role> --text-file <path>", "--text-file");
            var addedTask = context.Kernel.AddTask(context.CurrentGoal.Id, CliArgumentParser.ParseAgentRole(parts[1]), taskDescription, context.Agents);
            ConsoleViews.PrintTask(context.CurrentGoal, addedTask);
            return true;

        case "verification-plan":
            var planTarget = ResolveCommandTaskTarget(parts, context, "verification-plan <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [plan]");
            var planTask = planTarget.Task;
            if (parts.Count == planTarget.NextIndex)
            {
                Console.WriteLine(planTask.VerificationPlan ?? "none");
                return false;
            }

            context.Kernel.SetTaskVerificationPlan(context.CurrentGoal!.Id, planTask.Id, parts[planTarget.NextIndex]);
            ConsoleViews.PrintTask(context.CurrentGoal!, planTask);
            return true;

        case "brief":
            var briefTarget = ResolveCommandTaskTarget(parts, context, "brief <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number>");
            var briefTask = briefTarget.Task;
            Console.WriteLine(context.Kernel.BuildTaskBrief(context.CurrentGoal!.Id, briefTask.Id).Content);
            return false;

        case "timeline":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintTimeline(context.CurrentGoal);
            return false;

        case "task-timeline":
            var timelineTarget = ResolveCommandTaskTarget(parts, context, "task-timeline <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number>");
            ConsoleViews.PrintTaskTimeline(context.CurrentGoal!, timelineTarget.Task);
            return false;

        case "goal-events":
            CliArgumentParser.RequirePartCount(parts, 2, "goal-events <goal-prefix> [--follow]");
            GoalEventsCommand.RunAsync(
                context.Workspace.GoalLifecycleEventsDirectory,
                parts[1],
                HasCliConfirmation(parts, "--follow"),
                Console.Out,
                context.Kernel.Goals.Select(goal => goal.Id.Value)).GetAwaiter().GetResult();
            return false;

        case "pending":
            ConsoleViews.PrintPendingHumanInput(context.Kernel);
            return false;

        case "run":
            var runPolicy = ResolveCliAutonomyPolicy(parts);
            runPolicy.ThrowIfDisallowed(AutonomyAction.ModelRun, "run");
            var runTarget = ResolveCommandTaskTarget(parts, context, "run <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [--confirm-paid-api-run] [--confirm-large-paid-api-prompt]");
            var runTask = runTarget.Task;
            var runTaskLabel = parts[runTarget.NextIndex - 1];
            var runAgent = ResolveAssignedAgent(runTask, context.Agents);
            if (AgentExecutionPolicies.AllowsSubscription(runAgent.ExecutionPolicy))
            {
                throw new InvalidOperationException(
                    $"Task '{runTask.Id}' is assigned to '{runAgent.Name}' with execution policy {runAgent.ExecutionPolicy}; use subscription-dispatch {runTaskLabel} first, or api-run {runTaskLabel} for explicit API execution.");
            }

            EnsurePaidApiRunConfirmed(context.CurrentGoal!, runTask, runAgent, parts);
            RecordPolicyAllowed(context, context.CurrentGoal!, runPolicy, AutonomyAction.ModelRun, "run");
            RunApiTask(context, runTask);
            return true;

        case "api-run":
            var apiRunPolicy = ResolveCliAutonomyPolicy(parts);
            apiRunPolicy.ThrowIfDisallowed(AutonomyAction.ModelRun, "api-run");
            var apiRunTarget = ResolveCommandTaskTarget(parts, context, "api-run <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [--confirm-paid-api-run] [--confirm-large-paid-api-prompt]");
            var apiRunTask = apiRunTarget.Task;
            var apiRunAgent = ResolveAssignedAgent(apiRunTask, context.Agents);
            EnsureExplicitApiRunAllowed(apiRunTask, apiRunAgent);
            EnsurePaidApiRunConfirmed(context.CurrentGoal!, apiRunTask, apiRunAgent, parts);
            RecordPolicyAllowed(context, context.CurrentGoal!, apiRunPolicy, AutonomyAction.ModelRun, "api-run");
            RunApiTask(context, apiRunTask);
            return true;

        case "retry":
            var retryPolicy = ResolveCliAutonomyPolicy(parts);
            var retryUsage = "retry <task-number> <message>|retry <goal-prefix> <task-number> <message>|retry --goal <goal-prefix> <task-number> <message>|retry <task-number> --text-file <path>";
            var retryTarget = ResolveCommandTaskTarget(parts, context, retryUsage);
            RequireRemainingArgument(parts, retryTarget.NextIndex, retryUsage);
            var retryTask = retryTarget.Task;
            EnsurePolicyAllows(context, context.CurrentGoal!, retryPolicy, AutonomyAction.Retry, "retry");
            var retryMessage = ResolveTextArgument(parts, retryTarget.NextIndex, retryUsage, "--text-file");
            context.Kernel.RetryTask(context.CurrentGoal!.Id, retryTask.Id, retryMessage);
            GoalLifecycleCommands.RecordCapabilityWarnings(
                context.Kernel,
                context.CurrentGoal.Id,
                GoalObjectivePlanner.BuildCapabilityWarnings(retryMessage));
            ConsoleViews.PrintTask(context.CurrentGoal!, retryTask);
            return true;

        case "reassign-agent":
            var reassignUsage = "reassign-agent <task-number> <agent-id>|<goal-prefix> <task-number> <agent-id>|--goal <goal-prefix> <task-number> <agent-id>";
            var reassignTarget = ResolveCommandTaskTarget(parts, context, reassignUsage);
            RequireRemainingArgument(parts, reassignTarget.NextIndex, reassignUsage);
            var targetAgentId = parts[reassignTarget.NextIndex];
            var targetAgent = new AgentCatalog(context.Agents).FindById(targetAgentId);
            if (targetAgent is null)
            {
                Console.Error.WriteLine($"ERROR: agent id '{targetAgentId}' was not found.");
                return false;
            }

            context.Kernel.ReassignTaskAgent(context.CurrentGoal!.Id, reassignTarget.Task.Id, targetAgent);
            ConsoleViews.PrintTask(context.CurrentGoal!, reassignTarget.Task);
            return true;

        case "re-delegate":
        case "redelegate":
            var redelegatePolicy = ResolveCliAutonomyPolicy(parts);
            var redelegateTarget = ResolveCommandTaskTarget(parts, context, "re-delegate <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number>");
            var redelegateTask = redelegateTarget.Task;
            EnsurePolicyAllows(context, context.CurrentGoal!, redelegatePolicy, AutonomyAction.ProviderFailover, command);
            context.Kernel.RedelegateTask(context.CurrentGoal!.Id, redelegateTask.Id, context.Agents);
            ConsoleViews.PrintTask(context.CurrentGoal!, redelegateTask);
            return true;

        case "note":
            var noteTarget = ResolveCommandTaskTarget(parts, context, "note <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <message>");
            RequireRemainingArgument(parts, noteTarget.NextIndex, "note <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <message>");
            var noteTask = noteTarget.Task;
            context.Kernel.RecordTaskNote(context.CurrentGoal!.Id, noteTask.Id, parts[noteTarget.NextIndex]);
            Console.WriteLine($"Note added to task {noteTask.Id}");
            return true;

        case "dispatch":
            var dispatchTarget = ResolveCommandTaskTarget(parts, context, "dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <worker-name> <command>");
            RequireRemainingArgument(parts, dispatchTarget.NextIndex + 1, "dispatch <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <worker-name> <command>");
            var dispatchTask = dispatchTarget.Task;
            var dispatch = new TaskDispatchRecord(parts[dispatchTarget.NextIndex], parts[dispatchTarget.NextIndex + 1], context.Workspace.ResolveExecutionDirectory(context.CurrentGoal!.Id), DateTimeOffset.UtcNow);
            context.Kernel.RecordTaskDispatch(context.CurrentGoal!.Id, dispatchTask.Id, dispatch);
            ConsoleViews.PrintTask(context.CurrentGoal!, dispatchTask);
            return true;

        case "verify":
            var verifyPolicy = ResolveCliAutonomyPolicy(parts);
            var verifyTarget = ResolveCommandTaskTarget(parts, context, "verify <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <command>");
            RequireRemainingArgument(parts, verifyTarget.NextIndex, "verify <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <command>");
            var verifyTask = verifyTarget.Task;
            EnsurePolicyAllows(context, context.CurrentGoal!, verifyPolicy, AutonomyAction.BuildTest, "verify");
            var verification = new LocalProcessVerifier()
                .RunAsync(
                    parts[verifyTarget.NextIndex],
                    context.Workspace.ResolveExecutionDirectory(context.CurrentGoal!.Id),
                    context.CurrentGoal!.Id,
                    verifyTask.Id)
                .GetAwaiter()
                .GetResult();
            context.Kernel.RecordTaskVerification(context.CurrentGoal!.Id, verifyTask.Id, verification);
            ConsoleViews.PrintTask(context.CurrentGoal!, verifyTask);
            return true;

        case "verify-manual":
            var manualUsage = "verify-manual <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <passed|failed> <note>|verify-manual <task-number> <passed|failed> --text-file <path>";
            var manualTarget = ResolveCommandTaskTarget(parts, context, manualUsage);
            RequireRemainingArgument(parts, manualTarget.NextIndex + 1, manualUsage);
            var manualTask = manualTarget.Task;
            var manualVerification = ManualVerificationRecorder.Create(
                CliArgumentParser.ParseManualVerificationPassed(parts[manualTarget.NextIndex]),
                ResolveTextArgument(parts, manualTarget.NextIndex + 1, manualUsage, "--text-file"),
                context.Workspace.RootDirectory,
                DateTimeOffset.UtcNow);
            context.Kernel.RecordTaskVerification(context.CurrentGoal!.Id, manualTask.Id, manualVerification);
            ConsoleViews.PrintTask(context.CurrentGoal!, manualTask);
            return true;

        case "verifications":
            var verificationsTarget = ResolveCommandTaskTarget(parts, context, "verifications <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number>");
            ConsoleViews.PrintVerificationHistory(verificationsTarget.Task);
            return false;

        case "progress":
            var progressUsage = "progress <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <running|completed|failed|cancelled> <message>|progress <task-number> <status> --text-file <path>";
            var progressTarget = ResolveCommandTaskTarget(parts, context, progressUsage);
            RequireRemainingArgument(parts, progressTarget.NextIndex + 1, progressUsage);
            var progressTask = progressTarget.Task;
            context.Kernel.ReportTaskProgress(context.CurrentGoal!.Id, progressTask.Id, CliArgumentParser.ParseReportableStatus(parts[progressTarget.NextIndex]), ResolveTextArgument(parts, progressTarget.NextIndex + 1, progressUsage, "--text-file"));
            ConsoleViews.PrintGoal(context.CurrentGoal!);
            return true;

        case "ask":
            var askTarget = ResolveCommandTaskTarget(parts, context, "ask <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <question>");
            RequireRemainingArgument(parts, askTarget.NextIndex, "ask <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <question>");
            var askTask = askTarget.Task;
            var request = context.Kernel.RequestHumanInput(context.CurrentGoal!.Id, askTask.Id, parts[askTarget.NextIndex]);
            Console.WriteLine($"Human input requested: {request.Id}");
            ConsoleViews.PrintGoal(context.CurrentGoal!);
            return true;

        case "ask-goal":
            CliArgumentParser.RequirePartCount(parts, 2, "ask-goal <question>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var goalRequest = context.Kernel.RequestHumanInput(context.CurrentGoal.Id, null, parts[1]);
            Console.WriteLine($"Human input requested: {goalRequest.Id}");
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "answer":
            CliArgumentParser.RequirePartCount(parts, 3, "answer <request-id> <answer> | answer <request-id> --text-file <path>");
            var resolvedRequest = OrchestratorEntityResolver.ResolveHumanInputRequest(context.Kernel, parts[1]);
            context.Kernel.SubmitHumanInput(resolvedRequest.Id, ResolveTextArgument(parts, inlineIndex: 2, "answer <request-id> <answer> | answer <request-id> --text-file <path>", "--text-file"));
            context.CurrentGoal = context.Kernel.GetGoal(resolvedRequest.GoalId);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        default:
            return null;
    }
}

private static (TaskSpec Task, int NextIndex) ResolveCommandTaskTarget(IReadOnlyList<string> parts, CliExecutionContext context, string usage)
{
    if (parts.Count < 2)
    {
        throw new ArgumentException($"Usage: {usage}");
    }

    string? goalPrefix = null;
    string taskValue;
    int nextIndex;

    if (parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Count < 4)
        {
            throw new ArgumentException($"Usage: {usage}");
        }

        goalPrefix = parts[2];
        taskValue = parts[3];
        nextIndex = 4;
    }
    else if (parts.Count > 2 &&
        !parts[2].StartsWith("--", StringComparison.Ordinal) &&
        (!int.TryParse(parts[1], out _) || parts[1].Length >= 8) &&
        TryResolveGoalPrefix(context.Kernel, parts[1], out _))
    {
        goalPrefix = parts[1];
        taskValue = parts[2];
        nextIndex = 3;
    }
    else
    {
        taskValue = parts[1];
        nextIndex = 2;
    }

    context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, goalPrefix);
    return (OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, taskValue), nextIndex);
}

private static bool TryResolveGoalPrefix(AgentOrchestratorKernel kernel, string idOrPrefix, out Goal? goal)
{
    var matches = kernel.Goals.Where(candidate => candidate.Id.Value.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 1)
    {
        goal = matches[0];
        return true;
    }

    goal = null;
    return false;
}

private static void RequireRemainingArgument(IReadOnlyList<string> parts, int index, string usage)
{
    if (parts.Count <= index)
    {
        throw new ArgumentException($"Usage: {usage}");
    }
}

private static void RunApiTask(CliExecutionContext context, TaskSpec task)
{
    var diffProvider = GoalWorktreeDiffProvider.Create(context.Workspace.ExecutionDirectory);
    var runner = new AgentTaskRunner(context.Kernel, context.Agents, context.Providers, goalDiffProvider: diffProvider);
    var result = runner.RunAsync(context.CurrentGoal!.Id, task.Id).GetAwaiter().GetResult();
    ConsoleViews.PrintTask(result.Goal, result.Task);
}

private static void EnsureExplicitApiRunAllowed(TaskSpec task, AgentDefinition agent)
{
    if (!AgentExecutionPolicies.AllowsApi(agent.ExecutionPolicy))
    {
        throw new InvalidOperationException($"Agent '{agent.Name}' is configured for subscription execution only.");
    }

    if (agent.ExecutionPolicy != AgentExecutionPolicy.ApiOnly && !IsCleanExplicitApiRun(task))
    {
        throw new InvalidOperationException(
            $"Explicit API execution for task {task.Id.Value[..8]} is only available before subscription work, model output, or verification evidence exists.");
    }
}

private static void EnsurePaidApiRunConfirmed(Goal goal, TaskSpec task, AgentDefinition agent, IReadOnlyList<string> parts)
{
    var preview = AgentTaskRunner.PreviewRun(goal, task, [agent]);
    if (!ProviderSmokeRunner.IsPaidProviderName(preview.ProviderName))
    {
        return;
    }

    if (!HasCliConfirmation(parts, "--confirm-paid-api-run"))
    {
        throw new InvalidOperationException(
            $"Paid API execution for provider '{preview.ProviderName}' requires --confirm-paid-api-run because it can make a live billable request.");
    }

    ApiPromptCostGuard.ThrowIfConfirmationRequired(
        ApiPromptCostGuard.Evaluate(preview, goal),
        HasCliConfirmation(parts, ApiPromptCostGuard.CliConfirmationFlag));
}

private static bool IsCleanExplicitApiRun(TaskSpec task)
{
    return task.Status == WorkTaskStatus.Assigned &&
        task.LastDispatch is null &&
        task.LastProcess is null &&
        task.LastExecution is null &&
        task.LastVerification is null &&
        task.SubscriptionRetryAfter is null;
}

private static AgentDefinition ResolveAssignedAgent(TaskSpec task, IReadOnlyList<AgentDefinition> agents)
{
    if (task.AssignedAgentId is null)
    {
        throw new InvalidOperationException($"Task '{task.Id}' is not assigned to an agent.");
    }

    return agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId)
        ?? throw new KeyNotFoundException($"Assigned agent '{task.AssignedAgentId}' was not found.");
}
}
