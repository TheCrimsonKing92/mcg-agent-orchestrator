using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool HandleOperatorIntentTemplate(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var createGoal = HasCliConfirmation(parts, "--create-goal");
    var createSimpleGoal = HasCliConfirmation(parts, "--create-simple-goal");
    if (createGoal && createSimpleGoal)
    {
        throw new ArgumentException("Use either --create-goal or --create-simple-goal, not both.");
    }

    if (!TryParseIntentTemplateRequest(parts, out var templateName, out var request))
    {
        ConsoleViews.PrintOperatorIntentTemplates(OperatorIntentTemplates.All);
        return false;
    }

    var plan = OperatorIntentTemplates.Build(templateName, request);
    ConsoleViews.PrintOperatorIntentPlan(plan);
    if (!createGoal && !createSimpleGoal)
    {
        return false;
    }

    context.CurrentGoal = createSimpleGoal
        ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, plan.ReadyObjective, context.Workspace, context.Providers, context.EventWriter)
        : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, plan.ReadyObjective, context.Workspace, context.Providers, context.EventWriter);
    Console.WriteLine(createSimpleGoal ? "Created simple goal from intent template." : "Created five-role goal from intent template.");
    ConsoleViews.PrintGoal(context.CurrentGoal);
    return true;
}

private static bool TryParseIntentTemplateRequest(
    IReadOnlyList<string> parts,
    out string templateName,
    out string request)
{
    templateName = string.Empty;
    request = string.Empty;
    var values = parts.Skip(1)
        .Where(part => !part.StartsWith("--", StringComparison.Ordinal))
        .ToArray();
    if (values.Length == 0 ||
        values[0].Equals("list", StringComparison.OrdinalIgnoreCase) ||
        values[0].Equals("templates", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    templateName = values[0];
    request = string.Join(' ', values.Skip(1)).Trim();
    if (!string.IsNullOrWhiteSpace(request))
    {
        return true;
    }

    var split = values[0].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (split.Length < 2)
    {
        throw new ArgumentException("Usage: intent-template <template> <request> [--create-goal|--create-simple-goal]");
    }

    templateName = split[0];
    request = split[1];
    return true;
}

private static bool HandleGoalPlan(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var createGoals = HasCliConfirmation(parts, "--create-goals");
    var createSimpleGoals = HasCliConfirmation(parts, "--create-simple-goals");
    if (createGoals && createSimpleGoals)
    {
        throw new ArgumentException("Use either --create-goals or --create-simple-goals, not both.");
    }

    var headingFilter = parts
        .Skip(1)
        .FirstOrDefault(part => !part.StartsWith("--", StringComparison.Ordinal));
    var intake = BacklogIntakePlanner.Build(
        context.Workspace.BacklogStorePath,
        string.IsNullOrWhiteSpace(headingFilter) ? null : headingFilter,
        maxItems: 10);
    if (intake.Items.Count == 0)
    {
        throw new InvalidOperationException("No backlog items matched the requested filter.");
    }

    var plan = GoalDependencyPlanner.Build(intake);
    ConsoleViews.PrintGoalDependencyPlan(plan);
    foreach (var node in plan.Nodes)
    {
        PrintScopeCollisionAdvisory(context, node.ReadyObjective);
    }

    if (!createGoals && !createSimpleGoals)
    {
        return false;
    }

    if (!plan.CompiledGraph.IsRunnable)
    {
        throw new InvalidOperationException("goal-plan compiled graph has validation errors; inspect the printed findings before creating goals.");
    }

    foreach (var node in plan.Nodes)
    {
        context.CurrentGoal = createSimpleGoals
            ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, node.ReadyObjective, context.Workspace, context.Providers, context.EventWriter)
            : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, node.ReadyObjective, context.Workspace, context.Providers, context.EventWriter);
        Console.WriteLine(createSimpleGoals
            ? $"Created simple goal {context.CurrentGoal.Id.Value[..8]} from plan node {node.Id}."
            : $"Created five-role goal {context.CurrentGoal.Id.Value[..8]} from plan node {node.Id}.");
    }

    return true;
}

private const int IdeationSampleCount = 3;

private static bool HandleIdeate(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var appendBacklog = HasCliConfirmation(parts, "--append-backlog");

    Console.WriteLine("Gathering evidence context...");
    var evidenceContext = IdeationProposalPlanner.BuildEvidenceContext(context.Kernel, context.Workspace);

    // Use Ideation role when a matching agent is configured; fall back to Planner for LLM-reasoning lane.
    var ideationRole = context.Agents.Any(a => a.Status == AgentStatus.Available && a.Role == AgentRole.Ideation)
        ? AgentRole.Ideation
        : AgentRole.Planner;

    var prompt = IdeationProposalPlanner.BuildPrompt(evidenceContext);
    Console.WriteLine($"Running ideation worker ({IdeationSampleCount} samples)...");
    var sampleTasks = Enumerable.Range(0, IdeationSampleCount).Select(_ =>
    {
        var sampleKernel = new AgentOrchestratorKernel();
        var sampleTaskSpec = new TaskSpec(
            TaskId.New(),
            prompt,
            ideationRole,
            "Output only a fenced JSON array of ranked idea objects with title, rationale, scope, value, effort, and risk fields.");
        var sampleGoal = sampleKernel.CreateGoal("Propose improvement ideas", [sampleTaskSpec]);
        sampleKernel.ActivateGoal(sampleGoal.Id, context.Agents);
        var sampleRunner = new AgentTaskRunner(sampleKernel, context.Agents, context.Providers);
        return sampleRunner.RunAsync(sampleGoal.Id, sampleTaskSpec.Id)
            .ContinueWith(__ => IdeationProposalPlanner.Parse(
                sampleTaskSpec.LastExecution?.Output ?? string.Empty),
                TaskScheduler.Default);
    }).ToArray();

    var candidates = Task.WhenAll(sampleTasks).GetAwaiter().GetResult();
    var plan = IdeationProposalPlanner.SelectBestOfN(candidates);
    ConsoleViews.PrintIdeationPlan(plan);

    if (appendBacklog && plan.IsValid && plan.Ideas.Count > 0)
    {
        var store = new BacklogStore(context.Workspace.BacklogStorePath);
        foreach (var idea in plan.Ideas)
        {
            var body = $"{idea.Rationale} Scope: {idea.Scope}. Value: {idea.Value}. Effort: {idea.Effort}. Risk: {idea.Risk}.";
            store.AddAsync(idea.Title, body).GetAwaiter().GetResult();
        }
        Console.WriteLine($"Appended {plan.Ideas.Count} idea(s) to the backlog store.");
    }
    else if (appendBacklog && !plan.IsValid)
    {
        throw new InvalidOperationException(
            $"ideate --append-backlog blocked: plan has {plan.ValidationErrors.Count} validation error(s). Fix hand-wavy ideas before appending.");
    }

    return plan.IsValid;
}

private const int PlanSampleCount = 3;

private static bool HandlePlan(CliExecutionContext context, IReadOnlyList<string> parts)
{
    CliArgumentParser.RequirePartCount(parts, 2, "plan <direction> [--confirm-plan]");
    var direction = parts[1];
    var confirmPlan = HasCliConfirmation(parts, "--confirm-plan");

    var objPlan = BuildGoalObjectivePlan(context, direction, simple: true);
    GoalObjectivePlanner.ThrowIfBlocked(objPlan);
    ConsoleViews.PrintGoalObjectivePlan(objPlan);

    Console.WriteLine($"Running planner decomposition ({PlanSampleCount} samples)...");
    var sampleTasks = Enumerable.Range(0, PlanSampleCount).Select(_ =>
    {
        var sampleKernel = new AgentOrchestratorKernel();
        var sampleTaskSpec = new TaskSpec(
            TaskId.New(),
            GoalDagDecompositionPlanner.BuildPrompt(direction),
            AgentRole.Planner,
            "Output only a fenced JSON array of nodes with id, objective, and dependsOn fields.");
        var sampleGoal = sampleKernel.CreateGoal(direction, [sampleTaskSpec]);
        sampleKernel.ActivateGoal(sampleGoal.Id, context.Agents);
        var sampleRunner = new AgentTaskRunner(sampleKernel, context.Agents, context.Providers);
        return sampleRunner.RunAsync(sampleGoal.Id, sampleTaskSpec.Id)
            .ContinueWith(__ => GoalDagDecompositionPlanner.Parse(
                direction, sampleTaskSpec.LastExecution?.Output ?? string.Empty),
                TaskScheduler.Default);
    }).ToArray();

    var candidates = Task.WhenAll(sampleTasks).GetAwaiter().GetResult();
    var dagPlan = GoalDagDecompositionPlanner.SelectBestOfN(candidates);
    ConsoleViews.PrintGoalDagPlan(dagPlan);

    if (!confirmPlan)
        return false;

    if (!dagPlan.IsValid)
        throw new InvalidOperationException(
            $"Plan has {dagPlan.ValidationErrors.Count} validation error(s); inspect the preview and fix the direction before confirming.");

    var goalIds = new Dictionary<string, GoalId>(StringComparer.OrdinalIgnoreCase);
    foreach (var node in dagPlan.Nodes)
    {
        context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            context.Kernel, context.Agents, node.Objective);
        goalIds[node.Id] = context.CurrentGoal.Id;
        Console.WriteLine($"Created goal {context.CurrentGoal.Id.Value[..8]} for plan node {node.Id}.");
    }

    foreach (var node in dagPlan.Nodes)
        foreach (var depId in node.DependsOn)
            context.Kernel.SetGoalDependency(goalIds[node.Id], goalIds[depId]);

    return true;
}
}
