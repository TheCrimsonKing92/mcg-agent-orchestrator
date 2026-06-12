using Mcg.AgentOrchestrator.App.Providers;
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
            CliArgumentParser.RequirePartCount(parts, 2, "task <task-number|task-id-prefix>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            ConsoleViews.PrintTask(context.CurrentGoal, OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]));
            return false;

        case "tasks":
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            ConsoleViews.PrintTaskQueryResult(context.CurrentGoal, context.Kernel.QueryTasks(context.CurrentGoal.Id, CliArgumentParser.ParseTaskQuery(parts.Skip(1))));
            return false;

        case "add-task":
            CliArgumentParser.RequirePartCount(parts, 3, "add-task <role> <description>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var addedTask = context.Kernel.AddTask(context.CurrentGoal.Id, CliArgumentParser.ParseAgentRole(parts[1]), parts[2], context.Agents);
            ConsoleViews.PrintTask(context.CurrentGoal, addedTask);
            return true;

        case "verification-plan":
            CliArgumentParser.RequirePartCount(parts, 2, "verification-plan <task-number> [plan]");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var planTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            if (parts.Count == 2)
            {
                Console.WriteLine(planTask.VerificationPlan ?? "none");
                return false;
            }

            context.Kernel.SetTaskVerificationPlan(context.CurrentGoal.Id, planTask.Id, parts[2]);
            ConsoleViews.PrintTask(context.CurrentGoal, planTask);
            return true;

        case "brief":
            CliArgumentParser.RequirePartCount(parts, 2, "brief <task-number>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var briefTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            Console.WriteLine(context.Kernel.BuildTaskBrief(context.CurrentGoal.Id, briefTask.Id).Content);
            return false;

        case "timeline":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintTimeline(context.CurrentGoal);
            return false;

        case "task-timeline":
            CliArgumentParser.RequirePartCount(parts, 2, "task-timeline <task-number>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            ConsoleViews.PrintTaskTimeline(context.CurrentGoal, OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]));
            return false;

        case "pending":
            ConsoleViews.PrintPendingHumanInput(context.Kernel);
            return false;

        case "run":
            CliArgumentParser.RequirePartCount(parts, 2, "run <task-number> [--confirm-paid-api-run] [--confirm-large-paid-api-prompt]");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var runTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            var runAgent = ResolveAssignedAgent(runTask, context.Agents);
            if (AgentExecutionPolicies.AllowsSubscription(runAgent.ExecutionPolicy))
            {
                throw new InvalidOperationException(
                    $"Task '{runTask.Id}' is assigned to '{runAgent.Name}' with execution policy {runAgent.ExecutionPolicy}; use subscription-dispatch {parts[1]} first, or api-run {parts[1]} for explicit API execution.");
            }

            EnsurePaidApiRunConfirmed(context.CurrentGoal, runTask, runAgent, parts);
            RunApiTask(context, runTask);
            return true;

        case "api-run":
            CliArgumentParser.RequirePartCount(parts, 2, "api-run <task-number> [--confirm-paid-api-run] [--confirm-large-paid-api-prompt]");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var apiRunTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            var apiRunAgent = ResolveAssignedAgent(apiRunTask, context.Agents);
            EnsureExplicitApiRunAllowed(apiRunTask, apiRunAgent);
            EnsurePaidApiRunConfirmed(context.CurrentGoal, apiRunTask, apiRunAgent, parts);
            RunApiTask(context, apiRunTask);
            return true;

        case "retry":
            CliArgumentParser.RequirePartCount(parts, 3, "retry <task-number> <message>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var retryTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            context.Kernel.RetryTask(context.CurrentGoal.Id, retryTask.Id, parts[2]);
            ConsoleViews.PrintTask(context.CurrentGoal, retryTask);
            return true;

        case "re-delegate":
        case "redelegate":
            CliArgumentParser.RequirePartCount(parts, 2, "re-delegate <task-number>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var redelegateTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            context.Kernel.RedelegateTask(context.CurrentGoal.Id, redelegateTask.Id, context.Agents);
            ConsoleViews.PrintTask(context.CurrentGoal, redelegateTask);
            return true;

        case "note":
            CliArgumentParser.RequirePartCount(parts, 3, "note <task-number> <message>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var noteTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            context.Kernel.RecordTaskNote(context.CurrentGoal.Id, noteTask.Id, parts[2]);
            ConsoleViews.PrintTask(context.CurrentGoal, noteTask);
            return true;

        case "dispatch":
            CliArgumentParser.RequirePartCount(parts, 4, "dispatch <task-number> <worker-name> <command>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var dispatchTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            var dispatch = new TaskDispatchRecord(parts[2], parts[3], context.Workspace.ResolveExecutionDirectory(context.CurrentGoal.Id), DateTimeOffset.UtcNow);
            context.Kernel.RecordTaskDispatch(context.CurrentGoal.Id, dispatchTask.Id, dispatch);
            ConsoleViews.PrintTask(context.CurrentGoal, dispatchTask);
            return true;

        case "verify":
            CliArgumentParser.RequirePartCount(parts, 3, "verify <task-number> <command>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var verifyTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            var verification = new LocalProcessVerifier()
                .RunAsync(parts[2], context.Workspace.ResolveExecutionDirectory(context.CurrentGoal.Id))
                .GetAwaiter()
                .GetResult();
            context.Kernel.RecordTaskVerification(context.CurrentGoal.Id, verifyTask.Id, verification);
            ConsoleViews.PrintTask(context.CurrentGoal, verifyTask);
            return true;

        case "verify-manual":
            CliArgumentParser.RequirePartCount(parts, 4, "verify-manual <task-number> <passed|failed> <note>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var manualTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            var manualVerification = ManualVerificationRecorder.Create(
                CliArgumentParser.ParseManualVerificationPassed(parts[2]),
                parts[3],
                context.Workspace.RootDirectory,
                DateTimeOffset.UtcNow);
            context.Kernel.RecordTaskVerification(context.CurrentGoal.Id, manualTask.Id, manualVerification);
            ConsoleViews.PrintTask(context.CurrentGoal, manualTask);
            return true;

        case "verifications":
            CliArgumentParser.RequirePartCount(parts, 2, "verifications <task-number>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            ConsoleViews.PrintVerificationHistory(OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]));
            return false;

        case "progress":
            CliArgumentParser.RequirePartCount(parts, 4, "progress <task-number> <running|completed|failed|cancelled> <message>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var progressTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            context.Kernel.ReportTaskProgress(context.CurrentGoal.Id, progressTask.Id, CliArgumentParser.ParseReportableStatus(parts[2]), parts[3]);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "ask":
            CliArgumentParser.RequirePartCount(parts, 3, "ask <task-number> <question>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var askTask = OrchestratorEntityResolver.GetTaskByDisplayNumber(context.CurrentGoal, parts[1]);
            var request = context.Kernel.RequestHumanInput(context.CurrentGoal.Id, askTask.Id, parts[2]);
            Console.WriteLine($"Human input requested: {request.Id}");
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "ask-goal":
            CliArgumentParser.RequirePartCount(parts, 2, "ask-goal <question>");
            context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var goalRequest = context.Kernel.RequestHumanInput(context.CurrentGoal.Id, null, parts[1]);
            Console.WriteLine($"Human input requested: {goalRequest.Id}");
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "answer":
            CliArgumentParser.RequirePartCount(parts, 3, "answer <request-id> <answer>");
            var resolvedRequest = OrchestratorEntityResolver.ResolveHumanInputRequest(context.Kernel, parts[1]);
            context.Kernel.SubmitHumanInput(resolvedRequest.Id, parts[2]);
            context.CurrentGoal = context.Kernel.GetGoal(resolvedRequest.GoalId);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        default:
            return null;
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
