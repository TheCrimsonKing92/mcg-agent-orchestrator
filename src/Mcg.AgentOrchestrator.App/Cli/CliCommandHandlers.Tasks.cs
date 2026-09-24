using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
internal sealed record GoalScopedTaskMutationCommand(
    string Command,
    IReadOnlyList<string> Parts,
    string? Text,
    WorkTaskStatus? ProgressStatus,
    TaskVerificationRecord? ManualVerification,
    RetryRoundKind? RetryRoundKind,
    AutonomyPolicy RetryPolicy,
    IReadOnlyList<string>? GatedDeliverableIds = null,
    RetryCause? RetryCause = null,
    PreparedOperatorAdjudication? Adjudication = null)
{
    internal string? SuppliedGoalSelector { get; init; }
}

internal enum GoalScopedTaskMutationRenderKind
{
    None,
    Goal,
    Task,
    NoteAcknowledgement,
    Text
}

internal sealed record GoalScopedTaskMutationOutcome(
    bool ShouldSave,
    Goal Goal,
    TaskSpec? Task,
    GoalScopedTaskMutationRenderKind RenderKind,
    string? Text = null);

internal static GoalScopedTaskMutationCommand PrepareGoalScopedTaskMutationCommand(
    CliArgumentParser.GoalScopedTaskTargetArgs target,
    bool hasInlineGoalPrefix,
    OrchestratorWorkspace workspace)
{
    var parts = target.Parts;
    if (parts.Count == 0)
    {
        throw new ArgumentException("Missing command.");
    }

    var command = parts[0].ToLowerInvariant();
    var prepared = command switch
    {
        "progress" => PrepareProgressMutation(parts, hasInlineGoalPrefix),
        "verify-manual" => PrepareManualVerificationMutation(parts, hasInlineGoalPrefix, workspace),
        "retry" => PrepareRetryMutation(parts, hasInlineGoalPrefix),
        "adjudicate" => PrepareAdjudicationMutation(parts, hasInlineGoalPrefix, workspace),
        "verification-plan" => PrepareVerificationPlanMutation(parts, hasInlineGoalPrefix),
        "note" => PrepareNoteMutation(parts, hasInlineGoalPrefix),
        _ => throw new ArgumentException($"Unsupported goal-scoped task mutation command: {parts[0]}")
    };
    return prepared with
    {
        SuppliedGoalSelector = target.ExplicitGoalSelector ?? (hasInlineGoalPrefix ? parts[1] : null)
    };
}

internal static GoalScopedTaskMutationOutcome ExecuteGoalScopedTaskMutationWithoutRendering(
    GoalScopedTaskMutationCommand command,
    CliExecutionContext context)
{
    switch (command.Command)
    {
        case "progress":
            var progressUsage = "progress <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <running|completed|failed|cancelled> <message>|progress <task-number> <status> --text-file <path>";
            var progressTarget = ResolveCommandTaskTarget(command.Parts, context, progressUsage);
            context.Kernel.ReportTaskProgress(
                context.CurrentGoal!.Id,
                progressTarget.Task.Id,
                command.ProgressStatus ?? throw new InvalidOperationException("Prepared progress command is missing a status."),
                command.Text ?? throw new InvalidOperationException("Prepared progress command is missing text."));
            return new GoalScopedTaskMutationOutcome(true, context.CurrentGoal!, progressTarget.Task, GoalScopedTaskMutationRenderKind.Goal);

        case "verify-manual":
            var manualUsage = "verify-manual <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <passed|failed> <note>|verify-manual <task-number> <passed|failed> --text-file <path>";
            var manualTarget = ResolveCommandTaskTarget(command.Parts, context, manualUsage);
            context.Kernel.RecordTaskVerification(
                context.CurrentGoal!.Id,
                manualTarget.Task.Id,
                command.ManualVerification ?? throw new InvalidOperationException("Prepared verify-manual command is missing verification evidence."));
            return new GoalScopedTaskMutationOutcome(true, context.CurrentGoal!, manualTarget.Task, GoalScopedTaskMutationRenderKind.Task);

        case "retry":
            var retryUsage = CliCommandHelp.RetryUsage;
            var retryTarget = ResolveCommandTaskTarget(command.Parts, context, retryUsage);
            var retryTask = retryTarget.Task;
            EnsurePolicyAllows(context, context.CurrentGoal!, command.RetryPolicy, AutonomyAction.Retry, "retry");
            var retryMessage = command.Text ?? throw new InvalidOperationException("Prepared retry command is missing text.");
            context.Kernel.RetryTask(
                context.CurrentGoal!.Id,
                retryTask.Id,
                retryMessage,
                retryCause: command.RetryCause ?? throw new InvalidOperationException("Prepared retry command is missing an explicit retry cause."),
                retryRoundKind: command.RetryRoundKind,
                invalidateDownstream: true);
            GoalLifecycleCommands.RecordCapabilityWarnings(
                context.Kernel,
                context.CurrentGoal.Id,
                GoalObjectivePlanner.BuildCapabilityWarnings(retryMessage));
            return new GoalScopedTaskMutationOutcome(true, context.CurrentGoal!, retryTask, GoalScopedTaskMutationRenderKind.Task);

        case "verification-plan":
            var planUsage = "verification-plan <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [plan]";
            var planTarget = ResolveCommandTaskTarget(command.Parts, context, planUsage);
            var planTask = planTarget.Task;
            if (command.Text is null)
            {
                return new GoalScopedTaskMutationOutcome(false, context.CurrentGoal!, planTask, GoalScopedTaskMutationRenderKind.Text, planTask.VerificationPlan ?? "none");
            }

            context.Kernel.SetTaskVerificationPlan(context.CurrentGoal!.Id, planTask.Id, command.Text);
            return new GoalScopedTaskMutationOutcome(true, context.CurrentGoal!, planTask, GoalScopedTaskMutationRenderKind.Task);

        case "note":
            var noteUsage = "note <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <message>|note <task-number> --text-file <path>";
            var noteTarget = ResolveCommandTaskTarget(command.Parts, context, noteUsage);
            context.Kernel.RecordOperatorTaskNote(
                context.CurrentGoal!.Id,
                noteTarget.Task.Id,
                command.Text ?? throw new InvalidOperationException("Prepared note command is missing text."),
                command.GatedDeliverableIds);
            return new GoalScopedTaskMutationOutcome(true, context.CurrentGoal!, noteTarget.Task, GoalScopedTaskMutationRenderKind.NoteAcknowledgement);

        default:
            throw new ArgumentException($"Unsupported goal-scoped task mutation command: {command.Command}");
    }
}

internal static TaskSpec ResolveGoalScopedTaskMutationTarget(
    GoalScopedTaskMutationCommand command,
    CliExecutionContext context)
{
    var usage = command.Command switch
    {
        "progress" => "progress <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <running|completed|failed|cancelled> <message>",
        "verify-manual" => "verify-manual <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <passed|failed> <note>",
        "retry" => "retry <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <message>",
        "adjudicate" => CliCommandHelp.AdjudicateUsage,
        "verification-plan" => "verification-plan <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [plan]",
        "note" => "note <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <message>",
        _ => throw new ArgumentException($"Unsupported goal-scoped task mutation command: {command.Command}")
    };
    return ResolveCommandTaskTarget(command.Parts, context, usage).Task;
}

internal static void RenderGoalScopedTaskMutation(GoalScopedTaskMutationOutcome outcome)
{
    switch (outcome.RenderKind)
    {
        case GoalScopedTaskMutationRenderKind.None:
            break;
        case GoalScopedTaskMutationRenderKind.Goal:
            ConsoleViews.PrintGoal(outcome.Goal);
            break;
        case GoalScopedTaskMutationRenderKind.Task:
            ConsoleViews.PrintTask(outcome.Goal, outcome.Task ?? throw new InvalidOperationException("Task render requires a task."));
            break;
        case GoalScopedTaskMutationRenderKind.NoteAcknowledgement:
            Console.WriteLine($"Note added to task {(outcome.Task ?? throw new InvalidOperationException("Note render requires a task.")).Id}");
            break;
        case GoalScopedTaskMutationRenderKind.Text:
            Console.WriteLine(outcome.Text ?? string.Empty);
            break;
        default:
            throw new ArgumentOutOfRangeException(nameof(outcome.RenderKind), outcome.RenderKind, "Unknown goal-scoped task mutation render kind.");
    }
}

private static GoalScopedTaskMutationCommand PrepareProgressMutation(IReadOnlyList<string> parts, bool hasInlineGoalPrefix)
{
    var usage = "progress <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <running|completed|failed|cancelled> <message>|progress <task-number> <status> --text-file <path>";
    var taskIndex = ResolveGoalScopedTaskArgumentIndex(parts, hasInlineGoalPrefix, usage);
    var statusIndex = taskIndex + 1;
    RequireRemainingArgument(parts, statusIndex + 1, usage);
    return new GoalScopedTaskMutationCommand(
        "progress",
        parts,
        ResolveTextArgument(parts, statusIndex + 1, usage, "--text-file"),
        CliArgumentParser.ParseReportableStatus(parts[statusIndex]),
        ManualVerification: null,
        RetryRoundKind: null,
        RetryPolicy: AutonomyPolicy.Default);
}

private static GoalScopedTaskMutationCommand PrepareManualVerificationMutation(
    IReadOnlyList<string> parts,
    bool hasInlineGoalPrefix,
    OrchestratorWorkspace workspace)
{
    var usage = "verify-manual <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <passed|failed> <note>|verify-manual <task-number> <passed|failed> --text-file <path>";
    var taskIndex = ResolveGoalScopedTaskArgumentIndex(parts, hasInlineGoalPrefix, usage);
    var resultIndex = taskIndex + 1;
    RequireRemainingArgument(parts, resultIndex + 1, usage);
    var manualVerification = ManualVerificationRecorder.Create(
        CliArgumentParser.ParseManualVerificationPassed(parts[resultIndex]),
        ResolveTextArgument(parts, resultIndex + 1, usage, "--text-file"),
        workspace.RootDirectory,
        DateTimeOffset.UtcNow);
    return new GoalScopedTaskMutationCommand(
        "verify-manual",
        parts,
        Text: null,
        ProgressStatus: null,
        manualVerification,
        RetryRoundKind: null,
        RetryPolicy: AutonomyPolicy.Default);
}

private static GoalScopedTaskMutationCommand PrepareRetryMutation(IReadOnlyList<string> parts, bool hasInlineGoalPrefix)
{
    var usage = "retry <task-number> <message> [--cause <cause>] [--mechanical]|retry <goal-prefix> <task-number> <message> [--cause <cause>] [--mechanical]|retry --goal <goal-prefix> <task-number> <message> [--cause <cause>] [--mechanical]|retry <task-number> --text-file <path> [--cause <cause>] [--mechanical]";
    var retryPolicy = ResolveCliAutonomyPolicy(parts);
    var retryCause = ParseRequiredRetryCause(parts, usage);
    var retryRoundKind = HasCliConfirmation(parts, "--mechanical")
        ? RetryRoundKind.Mechanical
        : (RetryRoundKind?)null;
    var retryParts = RemoveFlagWithValue(RemoveStandaloneFlag(parts, "--mechanical"), "--cause");
    var taskIndex = ResolveGoalScopedTaskArgumentIndex(retryParts, hasInlineGoalPrefix, usage);
    var messageIndex = taskIndex + 1;
    RequireRemainingArgument(retryParts, messageIndex, usage);
    return new GoalScopedTaskMutationCommand(
        "retry",
        retryParts,
        ResolveTextArgument(retryParts, messageIndex, usage, "--text-file"),
        ProgressStatus: null,
        ManualVerification: null,
        retryRoundKind,
        retryPolicy,
        RetryCause: retryCause);
}

private static RetryCause ParseRequiredRetryCause(IReadOnlyList<string> parts, string usage)
{
    var value = GetFlagValue(parts, "--cause");
    if (value is null)
        return RetryCause.Unknown;

    if (!Enum.TryParse<RetryCause>(value, ignoreCase: true, out var cause) ||
        !Enum.IsDefined(cause))
    {
        throw new ArgumentException(
            $"Retry --cause must be one of <{string.Join('|', Enum.GetNames<RetryCause>())}>; Usage: {usage}");
    }

    return cause;
}

private static GoalScopedTaskMutationCommand PrepareVerificationPlanMutation(IReadOnlyList<string> parts, bool hasInlineGoalPrefix)
{
    var usage = "verification-plan <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> [plan]";
    var taskIndex = ResolveGoalScopedTaskArgumentIndex(parts, hasInlineGoalPrefix, usage);
    var planIndex = taskIndex + 1;
    var plan = parts.Count == planIndex ? null : parts[planIndex];
    return new GoalScopedTaskMutationCommand(
        "verification-plan",
        parts,
        plan,
        ProgressStatus: null,
        ManualVerification: null,
        RetryRoundKind: null,
        RetryPolicy: AutonomyPolicy.Default);
}

private static GoalScopedTaskMutationCommand PrepareNoteMutation(IReadOnlyList<string> parts, bool hasInlineGoalPrefix)
{
    var usage = "note <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <message> [--gate-deliverable <id>...]|note <task-number> --text-file <path> [--gate-deliverable <id>...]";
    var gatedDeliverableIds = GetOperatorGateDeliverableIds(parts);
    var noteParts = RemoveFlagWithValue(parts, "--gate-deliverable");
    var taskIndex = ResolveGoalScopedTaskArgumentIndex(noteParts, hasInlineGoalPrefix, usage);
    var messageIndex = taskIndex + 1;
    RequireRemainingArgument(noteParts, messageIndex, usage);
    return new GoalScopedTaskMutationCommand(
        "note",
        noteParts,
        ResolveTextArgument(noteParts, messageIndex, usage, "--text-file"),
        ProgressStatus: null,
        ManualVerification: null,
        RetryRoundKind: null,
        RetryPolicy: AutonomyPolicy.Default,
        gatedDeliverableIds);
}

private static int ResolveGoalScopedTaskArgumentIndex(IReadOnlyList<string> parts, bool hasInlineGoalPrefix, string usage)
{
    if (parts.Count < 2)
    {
        throw new ArgumentException($"Usage: {usage}");
    }

    if (parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Count < 4)
        {
            throw new ArgumentException($"Usage: {usage}");
        }

        return 3;
    }

    if (hasInlineGoalPrefix)
    {
        if (parts.Count < 3)
        {
            throw new ArgumentException($"Usage: {usage}");
        }

        return 2;
    }

    return 1;
}

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
            var addTaskUsage = "add-task [--goal <goal-prefix>] <role> <description> [--before-role <role>] | add-task [--goal <goal-prefix>] <role> --text-file <path> [--before-role <role>]";
            CliArgumentParser.RequirePartCount(parts, 3, addTaskUsage);
            var hasGoalTarget = parts[1].Equals("--goal", StringComparison.OrdinalIgnoreCase);
            if (hasGoalTarget && parts.Count < 5)
            {
                throw new ArgumentException($"Usage: {addTaskUsage}");
            }

            context.CurrentGoal = hasGoalTarget
                ? OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[2])
                : OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
            var roleIndex = hasGoalTarget ? 3 : 1;
            var taskDescription = ResolveTextArgument(
                parts,
                inlineIndex: roleIndex + 1,
                addTaskUsage,
                "--text-file");
            var beforeRoleValue = GetFlagValue(parts, "--before-role");
            var addedTask = context.Kernel.AddTask(
                context.CurrentGoal.Id,
                CliArgumentParser.ParseAgentRole(parts[roleIndex]),
                taskDescription,
                context.Agents,
                beforeRole: beforeRoleValue is null
                    ? null
                    : CliArgumentParser.ParseAgentRole(beforeRoleValue));
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
            var retryUsage = "retry <task-number> <message> [--cause <cause>] [--mechanical]|retry <goal-prefix> <task-number> <message> [--cause <cause>] [--mechanical]|retry --goal <goal-prefix> <task-number> <message> [--cause <cause>] [--mechanical]|retry <task-number> --text-file <path> [--cause <cause>] [--mechanical]";
            var retryCause = ParseRequiredRetryCause(parts, retryUsage);
            var retryRoundKind = HasCliConfirmation(parts, "--mechanical")
                ? RetryRoundKind.Mechanical
                : (RetryRoundKind?)null;
            var retryParts = RemoveFlagWithValue(RemoveStandaloneFlag(parts, "--mechanical"), "--cause");
            var retryTarget = ResolveCommandTaskTarget(retryParts, context, retryUsage);
            RequireRemainingArgument(retryParts, retryTarget.NextIndex, retryUsage);
            var retryTask = retryTarget.Task;
            EnsurePolicyAllows(context, context.CurrentGoal!, retryPolicy, AutonomyAction.Retry, "retry");
            var retryMessage = ResolveTextArgument(retryParts, retryTarget.NextIndex, retryUsage, "--text-file");
            context.Kernel.RetryTask(
                context.CurrentGoal!.Id,
                retryTask.Id,
                retryMessage,
                retryCause: retryCause,
                retryRoundKind: retryRoundKind,
                invalidateDownstream: true);
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
                Console.Error.WriteLine($"ERROR: AGENT_REASSIGNMENT_HOLD code=AgentNotFound agent_id='{targetAgentId}'.");
                return false;
            }

            if (targetAgent.Role != reassignTarget.Task.RequiredRole)
            {
                Console.Error.WriteLine(
                    $"ERROR: AGENT_REASSIGNMENT_HOLD code=RoleMismatch agent_id='{targetAgent.Id.Value}' " +
                    $"agent_role={targetAgent.Role} required_role={reassignTarget.Task.RequiredRole}.");
                return false;
            }

            if (targetAgent.Status != AgentStatus.Available)
            {
                Console.Error.WriteLine(
                    $"ERROR: AGENT_REASSIGNMENT_HOLD code=AgentUnavailable agent_id='{targetAgent.Id.Value}' " +
                    $"agent_status={targetAgent.Status}.");
                return false;
            }

            var routingEffect = reassignTarget.Task.Status == WorkTaskStatus.Running &&
                reassignTarget.Task.LastProcess is not null
                ? "next-attempt"
                : reassignTarget.Task.Status == WorkTaskStatus.Running &&
                  reassignTarget.Task.LastDispatch is not null
                    ? "rebuild-before-start"
                    : "next-dispatch";
            context.Kernel.ReassignTaskAgent(context.CurrentGoal!.Id, reassignTarget.Task.Id, targetAgent);
            Console.WriteLine(
                $"AGENT_REASSIGNMENT_ACKNOWLEDGED task={reassignTarget.Task.Id.Value} " +
                $"agent={targetAgent.Id.Value} harness={targetAgent.Subscription?.WorkerProfileName ?? "api"} effect={routingEffect}");
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
            var noteUsage = "note <task-number>|<goal-prefix> <task-number>|--goal <goal-prefix> <task-number> <message> [--gate-deliverable <id>...]|note <task-number> --text-file <path> [--gate-deliverable <id>...]";
            var noteGateIds = GetOperatorGateDeliverableIds(parts);
            var noteParts = RemoveFlagWithValue(parts, "--gate-deliverable");
            var noteTarget = ResolveCommandTaskTarget(noteParts, context, noteUsage);
            RequireRemainingArgument(noteParts, noteTarget.NextIndex, noteUsage);
            var noteTask = noteTarget.Task;
            context.Kernel.RecordOperatorTaskNote(
                context.CurrentGoal!.Id,
                noteTask.Id,
                ResolveTextArgument(noteParts, noteTarget.NextIndex, noteUsage, "--text-file"),
                noteGateIds);
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
            const string answerUsage = "answer <request-id> <answer> [--gate-deliverable <id>...] | answer <request-id> --text-file <path> [--gate-deliverable <id>...]";
            var answerGateIds = GetOperatorGateDeliverableIds(parts);
            var answerParts = RemoveFlagWithValue(parts, "--gate-deliverable");
            CliArgumentParser.RequirePartCount(answerParts, 3, answerUsage);
            var resolvedRequest = OrchestratorEntityResolver.ResolveHumanInputRequest(context.Kernel, answerParts[1]);
            context.Kernel.SubmitHumanInput(
                resolvedRequest.Id,
                ResolveTextArgument(answerParts, inlineIndex: 2, answerUsage, "--text-file"),
                answerGateIds);
            context.CurrentGoal = context.Kernel.GetGoal(resolvedRequest.GoalId);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "supersede":
            const string supersedeUsage = "supersede <goal-id> <clarification-id> <answer> | supersede <goal-id> <clarification-id> --text-file <path>";
            CliArgumentParser.RequirePartCount(parts, 4, supersedeUsage);
            var supersedeGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
            var supersedeText = ResolveTextArgument(parts, inlineIndex: 3, supersedeUsage, "--text-file");
            var matchesHumanInput = context.Kernel.HumanInputRequests
                .Any(request => request.Id.Value.StartsWith(parts[2], StringComparison.OrdinalIgnoreCase));
            string authoritativeText;
            string supersededId;
            if (matchesHumanInput)
            {
                var supersedeRequest = OrchestratorEntityResolver.ResolveHumanInputRequest(
                    context.Kernel,
                    supersedeGoal.Id,
                    parts[2]);
                var authoritativeAnswer = context.Kernel.SupersedeHumanInput(
                    supersedeGoal.Id,
                    supersedeRequest.Id,
                    supersedeText,
                    HumanInputAnswerOrigin.Operator);
                authoritativeText = authoritativeAnswer.Text;
                supersededId = supersedeRequest.Id.Value[..8];
            }
            else
            {
                var store = CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory);
                var identityUniverse = AllClarifications(store);
                var clarification = ResolveClarificationByShortId(
                    AllClarificationsForGoal(store, supersedeGoal),
                    identityUniverse
                        .Where(item => string.Equals(item.GoalId, supersedeGoal.Id.Value, StringComparison.OrdinalIgnoreCase))
                        .ToArray(),
                    parts[2],
                    $"Clarification id '{parts[2]}' was not found on goal '{supersedeGoal.Id.Value}'.",
                    $"Id '{parts[2]}' is ambiguous ({{0}} matches); copy a full id from `attention show {supersedeGoal.Id.Value[..8]} --all` or use a full correlation key.");
                var updated = store.SupersedeClarificationAsync(
                    supersedeGoal.Id.Value,
                    clarification.Id,
                    supersedeText,
                    HumanInputAnswerOrigin.Operator,
                    supersedeGoal.AuthoritativeBrief.Version).GetAwaiter().GetResult();
                authoritativeText = updated.AuthoritativeAnswer?.Text ?? updated.Resolution!;
                supersededId = ClarificationId(updated, identityUniverse);
                RefreshSpecRefinerPrecedentSnapshot(context.Workspace, updated);
            }
            context.CurrentGoal = supersedeGoal;
            Console.WriteLine(
                $"Superseded clarification {supersededId}. " +
                $"Authoritative answer: {authoritativeText}");
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "gate-satisfied":
            const string gateSatisfiedUsage = "gate-satisfied <request-id|task-note-record-id> <deliverable-id> <evidence> | gate-satisfied <request-id|task-note-record-id> <deliverable-id> --text-file <path>";
            CliArgumentParser.RequirePartCount(parts, 4, gateSatisfiedUsage);
            var gateEvidence = ResolveTextArgument(parts, inlineIndex: 3, gateSatisfiedUsage, "--text-file");
            if (parts[1].StartsWith("task-note:", StringComparison.OrdinalIgnoreCase))
            {
                var gateSource = OrchestratorEntityResolver.ResolveOperatorTaskNoteGateSource(context.Kernel, parts[1]);
                context.Kernel.MarkOperatorGateSatisfied(
                    gateSource.Goal.Id,
                    gateSource.SourceRecordId,
                    parts[2],
                    gateEvidence);
                context.CurrentGoal = gateSource.Goal;
            }
            else
            {
                var gateRequest = OrchestratorEntityResolver.ResolveHumanInputRequest(context.Kernel, parts[1]);
                context.Kernel.MarkOperatorGateSatisfied(
                    gateRequest.Id,
                    parts[2],
                    gateEvidence);
                context.CurrentGoal = context.Kernel.GetGoal(gateRequest.GoalId);
            }
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        default:
            return null;
    }
}

private static void RefreshSpecRefinerPrecedentSnapshot(
    OrchestratorWorkspace workspace,
    CollaborationItem clarification)
{
    var topicKey = clarification.CorrelationKey is null
        ? null
        : GoalRefinementService.ExtractTopicKey(clarification.CorrelationKey);
    var authoritativeAnswer = clarification.AuthoritativeAnswer;
    if (string.IsNullOrWhiteSpace(topicKey) ||
        authoritativeAnswer is null ||
        string.IsNullOrWhiteSpace(clarification.GoalId))
    {
        return;
    }

    try
    {
        new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath)
            .RecordPrecedentAsync(
                topicKey,
                authoritativeAnswer.Text,
                "Operator clarification answer.",
                originItemId: clarification.Id,
                originGoalId: clarification.GoalId,
                originAnswerId: authoritativeAnswer.Id,
                originBriefVersion: authoritativeAnswer.BriefVersion)
            .GetAwaiter()
            .GetResult();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"Warning: authoritative answer was superseded, but its reusable precedent snapshot could not be refreshed: {ex.Message}");
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
