using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool? TryExecuteFundamentalsAlias(string command, IReadOnlyList<string> parts, CliExecutionContext context)
{
    switch (command)
    {
        case "accept":
            return HandleAcceptAlias(parts, context);
        case "stop":
            return HandleStopAlias(parts, context);
        case "config":
            return HandleConfigAlias(parts, context);
        default:
            return null;
    }
}

// accept [goal-id] [--skip-verify] [--keep-workspace] [--autonomy <policy>]
// Delegates to acceptance logic then deterministic workspace cleanup (shared with `acceptance`).
private static bool HandleAcceptAlias(IReadOnlyList<string> parts, CliExecutionContext context)
{
    var policy = ResolveCliAutonomyPolicy(parts);
    var skipVerify = HasCliConfirmation(parts, "--skip-verify");
    var keepWorkspace = HasCliConfirmation(parts, "--keep-workspace");
    var noRecord = HasCliConfirmation(parts, "--no-record");
    var goalPart = GetOptionalArgument(parts, "--skip-verify", "--keep-workspace", "--no-record");
    context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, goalPart);
    EnsurePolicyAllows(context, context.CurrentGoal, policy, AutonomyAction.Acceptance, "accept");
    AutoVerifyFromGitEvidence(context, context.CurrentGoal);
    ConsoleViews.PrintAcceptanceSummary(
        context.CurrentGoal,
        GoalAcceptanceStatusProjector.Build(context.Kernel, context.CurrentGoal, context.Workspace.ExecutionDirectory));
    if (!RunAcceptanceWorkspaceMerge(context, skipVerify))
    {
        return false;
    }

    if (!noRecord)
    {
        AutoRecordDogfoodEntry(context);
    }

    CleanupGoalWorkspaceAfterMerge(context, context.CurrentGoal, policy, keepWorkspace);
    return true;
}

// stop <goal-id-prefix> <reason> --as cancel|park|abandon|supersede [--confirm-*]
// Delegates to the appropriate disposal command based on --as mode.
private static bool HandleStopAlias(IReadOnlyList<string> parts, CliExecutionContext context)
{
    if (parts.Count < 3)
    {
        throw new ArgumentException("Usage: stop <goal-id-prefix> <reason> --as cancel|park|abandon|supersede");
    }

    var asMode = GetFlagValue(parts, "--as");
    if (string.IsNullOrWhiteSpace(asMode))
    {
        throw new ArgumentException("stop requires --as cancel|park|abandon|supersede");
    }

    var goalPart = parts[1];
    var reason = parts[2];

    switch (asMode.ToLowerInvariant())
    {
        case "cancel":
        {
            var delegateParts = BuildStopDelegateParts("cancel-goal", goalPart, reason, parts, "--confirm-goal-stop");
            return TryExecuteGoalCommand("cancel-goal", delegateParts, context)!.Value;
        }
        case "supersede":
        {
            var delegateParts = BuildStopDelegateParts("supersede-goal", goalPart, reason, parts, "--confirm-goal-stop");
            return TryExecuteGoalCommand("supersede-goal", delegateParts, context)!.Value;
        }
        case "park":
        {
            var delegateParts = BuildStopDelegateParts("park-goal", goalPart, reason, parts, "--confirm-goal-park");
            return TryExecuteGoalCommand("park-goal", delegateParts, context)!.Value;
        }
        case "abandon":
        {
            var delegateParts = BuildStopDelegateParts("abandon-goal", goalPart, reason, parts, "--confirm-goal-abandon");
            return TryExecuteGoalCommand("abandon-goal", delegateParts, context)!.Value;
        }
        default:
            throw new ArgumentException($"Unknown stop mode '{asMode}'. Use: cancel|park|abandon|supersede");
    }
}

private static List<string> BuildStopDelegateParts(
    string delegateCommand, string goalPart, string reason, IReadOnlyList<string> originalParts, string confirmFlag)
{
    var result = new List<string> { delegateCommand, goalPart, reason };
    if (HasCliConfirmation(originalParts, confirmFlag))
    {
        result.Add(confirmFlag);
    }

    return result;
}

// config <agents|profiles|policy|doctor>
// Delegates to the corresponding agent/worker-profile/autonomy/health handler.
private static bool HandleConfigAlias(IReadOnlyList<string> parts, CliExecutionContext context)
{
    var subcommand = parts.Count > 1 ? parts[1].ToLowerInvariant() : null;
    switch (subcommand)
    {
        case "agents":
            ConsoleViews.PrintAgents(context.Agents);
            return false;
        case "profiles":
            ConsoleViews.PrintWorkerProfiles(context.WorkerProfiles);
            return false;
        case "policy":
            ConsoleViews.PrintAutonomyPolicies();
            return false;
        case "doctor":
            ConsoleViews.PrintHealth(OrchestratorHealthInspector.InspectCurrentEnvironment(
                new AgentCatalog(context.Agents), context.WorkerProfiles));
            return false;
        default:
            Console.WriteLine("config subcommands: agents, profiles, policy, doctor");
            return false;
    }
}
}
