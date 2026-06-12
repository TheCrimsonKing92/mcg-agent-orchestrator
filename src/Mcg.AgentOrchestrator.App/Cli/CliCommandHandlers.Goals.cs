using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool? TryExecuteGoalCommand(string command, IReadOnlyList<string> parts, CliExecutionContext context)
{
    switch (command)
    {
        case "prototype":
            var objective = parts.Count > 1 ? parts[1] : "Create a Windows-based agent orchestrator";
            var prototypeKernel = new AgentOrchestratorKernel();
            var prototypeGoal = GoalLifecycleCommands.CreateAndActivateGoal(prototypeKernel, context.Agents, objective);
            ConsoleViews.PrintGoal(prototypeGoal);
            ConsoleViews.PrintTimeline(prototypeGoal);
            return false;

        case "goal":
            CliArgumentParser.RequirePartCount(parts, 2, "goal <objective>");
            context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, parts[1]);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "simple-goal":
            CliArgumentParser.RequirePartCount(parts, 2, "simple-goal <objective>");
            context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, parts[1]);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "cancel-goal":
        case "supersede-goal":
            CliArgumentParser.RequirePartCount(parts, 3, $"{command} <goal-id-prefix> <reason> [--confirm-goal-stop]");
            context.CurrentGoal = HandleGoalStopCommand(context, parts, command.Equals("supersede-goal", StringComparison.OrdinalIgnoreCase));
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "goals":
            ConsoleViews.PrintGoals(context.Kernel);
            return false;

        case "agents":
            ConsoleViews.PrintAgents(context.Agents);
            return false;

        case "agent":
            CliArgumentParser.RequirePartCount(parts, 4, "agent <role> <provider> <model> [name] [--complex-model <model>]");
            if (HasCliConfirmation(parts, "--complex-model") && GetFlagValue(parts, "--complex-model") is null)
            {
                throw new ArgumentException("Usage: agent <role> <provider> <model> [name] [--complex-model <model>]");
            }
            var agent = CreateCliAgentDefinition(parts);
            context.Agents = new AgentCatalog(context.Agents).UpsertRole(agent).Agents;
            AgentCatalogStore.Save(context.AgentCatalogPath, new AgentCatalog(context.Agents));
            ConsoleViews.PrintAgents(context.Agents);
            return false;

        case "agent-add":
            CliArgumentParser.RequirePartCount(parts, 4, "agent-add <role> <provider> <model> [name] [--complex-model <model>]");
            if (HasCliConfirmation(parts, "--complex-model") && GetFlagValue(parts, "--complex-model") is null)
            {
                throw new ArgumentException("Usage: agent-add <role> <provider> <model> [name] [--complex-model <model>]");
            }
            var addedAgent = CreateCliAgentDefinition(parts);
            addedAgent = addedAgent with { Id = BuildAlternateAgentId(addedAgent, GetCliAgentIdSuffix(parts, addedAgent)) };
            context.Agents = new AgentCatalog(context.Agents).AddOrReplaceById(addedAgent).Agents;
            AgentCatalogStore.Save(context.AgentCatalogPath, new AgentCatalog(context.Agents));
            ConsoleViews.PrintAgents(context.Agents);
            return false;

        case "status":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return false;

        case "monitor":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintMonitor(context.Kernel.BuildMonitor(context.CurrentGoal.Id));
            return false;

        case "acceptance":
            var skipVerify = HasCliConfirmation(parts, "--skip-verify");
            var acceptanceGoalPart = GetOptionalArgument(parts, "--skip-verify");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, acceptanceGoalPart);
            ConsoleViews.PrintAcceptanceSummary(context.CurrentGoal, context.Kernel.BuildGoalAcceptanceSummary(context.CurrentGoal.Id));
            PrintAcceptanceWorkspaceMerge(context, skipVerify);
            return false;

        case "workspace":
            HandleWorkspaceCommand(context, parts.Count > 1 ? parts[1] : null, parts.Count > 2 ? parts[2] : null);
            return false;

        case "evidence":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintEvidenceSummary(context.CurrentGoal, context.Kernel.BuildGoalEvidenceSummary(context.CurrentGoal.Id));
            return false;

        case "stages":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintStageReadinessReport(context.CurrentGoal, context.Kernel.BuildStageReadinessReport(context.CurrentGoal.Id), context.Agents);
            return false;

        case "gates":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintVerificationGate(context.CurrentGoal, context.Kernel.BuildVerificationGate(context.CurrentGoal.Id));
            return false;

        case "verify-needed":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintVerificationWorklist(context.CurrentGoal, context.Kernel.BuildVerificationWorklist(context.CurrentGoal.Id));
            return false;

        case "input-needed":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintHumanInputWorklist(context.CurrentGoal, context.Kernel.BuildHumanInputWorklist(context.CurrentGoal.Id));
            return false;

        case "next":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintNextActions(context.CurrentGoal, context.Kernel.BuildNextActions(context.CurrentGoal.Id), context.Agents);
            return false;

        case "subscription-plan":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintSubscriptionPlan(SubscriptionPlanBuilder.Build(
                context.CurrentGoal,
                context.Agents,
                context.WorkerProfiles,
                task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(context.Kernel, context.CurrentGoal, task, context.Agents)));
            return false;

        case "advance":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            var advance = GoalManagementCommandService.AdvanceGoalAsync(context.Kernel, context.Agents, context.Providers, context.Workspace, context.CurrentGoal)
                .GetAwaiter()
                .GetResult();
            ConsoleViews.PrintAdvanceResult(advance);
            return advance.Executed;

        case "advance-subscription":
            EnsureCliConfirmation(
                parts,
                "--confirm-subscription-advance",
                "advance-subscription requires --confirm-subscription-advance because it can prepare or start subscription worker processes.");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(
                context.Kernel,
                context.CurrentGoal,
                GetOptionalArgument(parts, "--confirm-subscription-advance", SubscriptionPromptCostGuard.CliConfirmationFlag));
            var subscriptionAdvance = GoalManagementCommandService.AdvanceGoalWithSubscriptions(
                context.Kernel,
                context.Agents,
                context.WorkerProfiles,
                context.Workspace,
                context.CurrentGoal,
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            ConsoleViews.PrintAdvanceResult(subscriptionAdvance);
            return subscriptionAdvance.Executed;

        case "run-goal":
            EnsureCliConfirmation(
                parts,
                "--confirm-batch-start",
                "run-goal requires --confirm-batch-start because it starts worker processes.");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(
                context.Kernel,
                context.CurrentGoal,
                GetOptionalArgument(parts, "--confirm-batch-start", SubscriptionPromptCostGuard.CliConfirmationFlag));
            var runGoalResult = RunGoalService.RunAsync(
                context.Kernel,
                context.Agents,
                context.WorkerProfiles,
                context.Workspace,
                context.CurrentGoal,
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag))
                .GetAwaiter().GetResult();
            ConsoleViews.PrintRunGoalResult(context.CurrentGoal, runGoalResult);
            return runGoalResult.Executed;

        case "delegate":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            var delegation = context.Kernel.ActivateGoal(context.CurrentGoal.Id, context.Agents);
            ConsoleViews.PrintDelegationPlan(context.CurrentGoal, delegation);
            return delegation.Assignments.Count > 0;

        default:
            return null;
    }
}

private static AgentDefinition CreateCliAgentDefinition(IReadOnlyList<string> parts)
{
    var agentName = parts.Count > 4 && !parts[4].StartsWith("--", StringComparison.Ordinal) ? parts[4] : null;
    var complexModelName = GetFlagValue(parts, "--complex-model");
    return DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        parts[1], parts[2], parts[3], agentName,
        ComplexProviderName: complexModelName is null ? null : parts[2],
        ComplexModelName: complexModelName));
}

private static string GetCliAgentIdSuffix(IReadOnlyList<string> parts, AgentDefinition agent)
{
    return parts.Count > 4 && !parts[4].StartsWith("--", StringComparison.Ordinal)
        ? parts[4]
        : agent.Model.ModelName;
}

private static AgentId BuildAlternateAgentId(AgentDefinition agent, string suffix)
{
    var baseId = $"{Slug(agent.Model.ProviderName)}-{Slug(agent.Role.ToString())}";
    var suffixSlug = Slug(suffix);
    return new AgentId(string.IsNullOrWhiteSpace(suffixSlug) ? baseId : $"{baseId}-{suffixSlug}");
}

private static string Slug(string value)
{
    var chars = value
        .Trim()
        .ToLowerInvariant()
        .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
        .ToArray();
    var slug = new string(chars);
    while (slug.Contains("--", StringComparison.Ordinal))
    {
        slug = slug.Replace("--", "-", StringComparison.Ordinal);
    }

    return slug.Trim('-');
}

private static string? GetOptionalArgument(IReadOnlyList<string> parts, params string[] flags)
{
    return parts.Skip(1).FirstOrDefault(part => !flags.Any(flag => part.Equals(flag, StringComparison.OrdinalIgnoreCase)));
}

private static string? GetFlagValue(IReadOnlyList<string> parts, string flag)
{
    for (var i = 1; i < parts.Count - 1; i++)
    {
        if (parts[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
        {
            return parts[i + 1];
        }
    }

    return null;
}

private static void EnsureCliConfirmation(IReadOnlyList<string> parts, string flag, string message)
{
    if (HasCliConfirmation(parts, flag))
    {
        return;
    }

    throw new InvalidOperationException(message);
}

private static bool HasCliConfirmation(IReadOnlyList<string> parts, string flag)
{
    return parts.Any(part => part.Equals(flag, StringComparison.OrdinalIgnoreCase));
}

private static Goal HandleGoalStopCommand(CliExecutionContext context, IReadOnlyList<string> parts, bool supersede)
{
    var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
    if (RequiresGoalStopConfirmation(context.CurrentGoal, goal) && !HasGoalStopConfirmation(parts))
    {
        throw new InvalidOperationException($"{parts[0]} requires --confirm-goal-stop for active or non-current goals.");
    }

    var reason = RemoveFlag(parts[2], "--confirm-goal-stop");
    return supersede
        ? context.Kernel.SupersedeGoal(goal.Id, reason)
        : context.Kernel.CancelGoal(goal.Id, reason);
}

private static bool RequiresGoalStopConfirmation(Goal? currentGoal, Goal goal)
{
    var isCurrent = currentGoal is not null && currentGoal.Id == goal.Id;
    return !isCurrent || goal.Status == GoalStatus.Active;
}

private static bool HasGoalStopConfirmation(IReadOnlyList<string> parts) =>
    parts.Any(part => part.Equals("--confirm-goal-stop", StringComparison.OrdinalIgnoreCase) ||
        part.Contains("--confirm-goal-stop", StringComparison.OrdinalIgnoreCase));

private static string RemoveFlag(string value, string flag) =>
    value.Replace(flag, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

private static void HandleWorkspaceCommand(CliExecutionContext context, string? action, string? goalPrefix)
{
    var normalizedAction = (action ?? "status").ToLowerInvariant();
    if (normalizedAction is not ("status" or "create" or "merge" or "remove"))
    {
        throw new ArgumentException("Usage: workspace [create|merge|remove] [goal-id-prefix]");
    }

    var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, goalPrefix);
    context.CurrentGoal = goal;
    var executionDirectory = context.Workspace.ExecutionDirectory;
    var branch = GoalWorktrees.BranchName(goal.Id);
    switch (normalizedAction)
    {
        case "status":
            var existing = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
            Console.WriteLine(existing is null
                ? $"Goal has no workspace. Create one with: workspace create (branch {branch})"
                : $"Workspace: {existing} (branch {branch})");
            return;

        case "create":
            Console.WriteLine($"Workspace: {GoalWorktrees.Ensure(executionDirectory, goal.Id)} (branch {branch})");
            return;

        case "merge":
            var merge = GoalWorktrees.TryFastForwardMerge(executionDirectory, goal.Id);
            Console.WriteLine(merge is null
                ? "Goal has no workspace branch to merge."
                : FormatWorkspaceMerge(merge));
            return;

        case "remove":
            PrintWorkspaceRemoveResult(GoalWorktrees.Remove(executionDirectory, goal.Id));
            return;

        default:
            throw new ArgumentException("Usage: workspace [create|merge|remove] [goal-id-prefix]");
    }
}

private static void PrintAcceptanceWorkspaceMerge(CliExecutionContext context, bool skipVerify = false)
{
    var goal = context.CurrentGoal!;
    if (goal.Status != GoalStatus.Completed)
    {
        return;
    }

    var worktreePath = GoalWorktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id);
    if (worktreePath is not null)
    {
        if (skipVerify)
        {
            Console.WriteLine("Verification: skipped (--skip-verify)");
        }
        else
        {
            var verification = context.AcceptanceVerifier.RunAsync(worktreePath).GetAwaiter().GetResult();
            if (!verification.Passed)
            {
                Console.WriteLine($"Verification: failed (exit {verification.ExitCode}); merge blocked");
                if (!string.IsNullOrWhiteSpace(verification.OutputTail))
                {
                    Console.WriteLine(verification.OutputTail);
                }
                return;
            }
            Console.WriteLine($"Verification: passed (exit {verification.ExitCode})");
        }
    }

    var merge = GoalWorktrees.TryFastForwardMerge(context.Workspace.ExecutionDirectory, goal.Id);
    if (merge is not null)
    {
        Console.WriteLine($"Workspace merge: {FormatWorkspaceMerge(merge)}");
    }
}

private static void PrintWorkspaceRemoveResult(GoalWorktreeRemoveResult result)
{
    Console.WriteLine(result.Message);
    if (result.LeftoverPath is not null)
    {
        Console.WriteLine($"Leftover path: {result.LeftoverPath}");
    }

    if (result.LockHolders.Count > 0)
    {
        Console.WriteLine("Likely lock holders:");
        foreach (var holder in result.LockHolders)
        {
            var detail = holder.CommandLine is not null
                ? $": {holder.CommandLine}"
                : " (command line unavailable)";
            Console.WriteLine($"  {holder.ProcessName} (PID {holder.ProcessId}){detail}");
        }
    }

    if (result.ResumeCommand is not null)
    {
        Console.WriteLine($"Resume: {result.ResumeCommand}");
    }
}

private static string FormatWorkspaceMerge(GoalWorktreeMergeResult merge)
{
    return merge.FastForwarded
        ? merge.Message
        : $"{merge.Message} command: {merge.SuggestedCommand}";
}
}
