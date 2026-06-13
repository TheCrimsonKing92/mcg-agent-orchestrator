using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
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
            var goalObjectivePlan = GoalObjectivePlanner.Build(parts[1], simple: false);
            GoalObjectivePlanner.ThrowIfBlocked(goalObjectivePlan);
            ConsoleViews.PrintGoalObjectivePlan(goalObjectivePlan);
            context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, parts[1]);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "simple-goal":
            CliArgumentParser.RequirePartCount(parts, 2, "simple-goal <objective>");
            var simpleObjectivePlan = GoalObjectivePlanner.Build(parts[1], simple: true);
            GoalObjectivePlanner.ThrowIfBlocked(simpleObjectivePlan);
            ConsoleViews.PrintGoalObjectivePlan(simpleObjectivePlan);
            context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, parts[1]);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "lifecycle-simple-goal":
            HandleLifecycleGoal(context, parts, simple: true);
            return true;

        case "lifecycle-goal":
            HandleLifecycleGoal(context, parts, simple: false);
            return true;

        case "goal-plan":
            return HandleGoalPlan(context, parts);

        case "intent-template":
            return HandleOperatorIntentTemplate(context, parts);

        case "backlog-intake":
            return HandleBacklogIntake(context, parts);

        case "autonomy-policies":
            ConsoleViews.PrintAutonomyPolicies();
            return false;

        case "cancel-goal":
        case "supersede-goal":
            CliArgumentParser.RequirePartCount(parts, 3, $"{command} <goal-id-prefix> <reason> [--confirm-goal-stop]");
            context.CurrentGoal = HandleGoalStopCommand(context, parts, command.Equals("supersede-goal", StringComparison.OrdinalIgnoreCase));
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "abandon-goal":
            CliArgumentParser.RequirePartCount(parts, 3, "abandon-goal <goal-id-prefix> <reason> [--confirm-goal-abandon]");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
            var abandonReason = RemoveFlag(parts[2], "--confirm-goal-abandon");
            if (!HasCliConfirmation(parts, "--confirm-goal-abandon") &&
                !parts[2].Contains("--confirm-goal-abandon", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleViews.PrintGoalAbandonPlan(GoalAbandonPlanner.Build(
                    context.Kernel,
                    context.CurrentGoal,
                    context.Workspace,
                    abandonReason));
                return false;
            }

            var abandonPlan = GoalAbandonPlanner.Apply(
                context.Kernel,
                context.CurrentGoal,
                context.Workspace,
                abandonReason);
            ConsoleViews.PrintGoalAbandonPlan(abandonPlan);
            if (!abandonPlan.CanApply)
            {
                throw new InvalidOperationException("abandon-goal could not apply because one or more steps are blocked.");
            }

            return true;

        case "park-goal":
            CliArgumentParser.RequirePartCount(parts, 3, "park-goal <goal-id-prefix> <reason> [--confirm-goal-park]");
            context.CurrentGoal = HandleGoalParkCommand(context, parts);
            return HasCliConfirmation(parts, "--confirm-goal-park") ||
                parts[2].Contains("--confirm-goal-park", StringComparison.OrdinalIgnoreCase);

        case "rollback-goal":
            CliArgumentParser.RequirePartCount(parts, 3, "rollback-goal <goal-id-prefix> <reason> [--confirm-goal-rollback]");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
            var rollbackReason = RemoveFlag(parts[2], "--confirm-goal-rollback");
            var rollbackPlan = HasCliConfirmation(parts, "--confirm-goal-rollback") ||
                parts[2].Contains("--confirm-goal-rollback", StringComparison.OrdinalIgnoreCase)
                ? GoalRollbackPlanner.Apply(context.Workspace.ExecutionDirectory, context.CurrentGoal, rollbackReason)
                : GoalRollbackPlanner.Build(context.Workspace.ExecutionDirectory, context.CurrentGoal, rollbackReason);
            ConsoleViews.PrintGoalRollbackPlan(rollbackPlan);
            if (rollbackPlan.DryRun)
            {
                return false;
            }

            if (!rollbackPlan.CanApply)
            {
                throw new InvalidOperationException("rollback-goal could not apply because rollback metadata or branch state is blocked.");
            }

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

        case "readiness":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintGoalReadinessPreflight(GoalReadinessPreflight.Build(
                context.CurrentGoal,
                context.Agents,
                context.Workspace.ExecutionDirectory));
            return false;

        case "goal-recovery":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintGoalRecoveryReport(GoalRecoveryPlanner.Build(context.Kernel, context.CurrentGoal, context.Workspace.ExecutionDirectory));
            return false;

        case "dogfood-eval":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintHistoricalDogfoodEvaluation(HistoricalDogfoodEvaluationHarness.Evaluate(
                context.Kernel,
                context.CurrentGoal,
                context.Agents,
                context.WorkerProfiles,
                context.Workspace));
            return false;

        case "failure-triage":
            var triagePolicy = ResolveCliAutonomyPolicy(parts);
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, GetOptionalArgument(parts));
            ConsoleViews.PrintFailureTriageReport(FailureTriagePlanner.Build(
                context.Kernel,
                context.CurrentGoal,
                context.Agents,
                context.Workspace.ExecutionDirectory,
                triagePolicy));
            return false;

        case "retention-plan":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, GetOptionalArgument(parts));
            ConsoleViews.PrintGoalArtifactRetentionPlan(GoalArtifactRetentionPlanner.Build(
                context.Kernel,
                context.CurrentGoal,
                context.Workspace));
            return false;

        case "acceptance-queue":
            HandleAcceptanceQueue(context, parts);
            return HasCliConfirmation(parts, "--apply");

        case "drain-goals":
            return HandleGoalDrain(context, parts);

        case "supervisor":
            var supervisorPolicy = ResolveCliAutonomyPolicy(parts);
            var supervisorGoalPrefix = GetOptionalArgument(parts, "--apply-safe");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, supervisorGoalPrefix);
            if (HasCliConfirmation(parts, "--apply-safe"))
            {
                var result = GoalSupervisor.ApplySafe(
                    context.Kernel,
                    context.CurrentGoal,
                    context.Agents,
                    context.Workspace,
                    supervisorPolicy);
                ConsoleViews.PrintGoalSupervisorPlan(result.Plan, result.AppliedActions);
                return result.AppliedActions.Count > 0;
            }

            ConsoleViews.PrintGoalSupervisorPlan(GoalSupervisor.Build(
                context.Kernel,
                context.CurrentGoal,
                context.Agents,
                context.Workspace.ExecutionDirectory,
                supervisorPolicy));
            return false;

        case "build-lease-cleanup":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(
                context.Kernel,
                context.CurrentGoal,
                GetOptionalArgument(parts, "--confirm-build-lease-cleanup"));
            if (!HasCliConfirmation(parts, "--confirm-build-lease-cleanup"))
            {
                throw new InvalidOperationException("build-lease-cleanup deletes an orphaned goal build lease. Re-run with --confirm-build-lease-cleanup after goal-recovery reports canCleanup=True.");
            }

            if (!DotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(context.CurrentGoal.Id, out _, out var cleanupDetail))
            {
                throw new InvalidOperationException(cleanupDetail);
            }

            Console.WriteLine(cleanupDetail);
            return false;

        case "acceptance":
            var acceptancePolicy = ResolveCliAutonomyPolicy(parts);
            var skipVerify = HasCliConfirmation(parts, "--skip-verify");
            var acceptanceGoalPart = GetOptionalArgument(parts, "--skip-verify");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, acceptanceGoalPart);
            EnsurePolicyAllows(context, context.CurrentGoal, acceptancePolicy, AutonomyAction.Acceptance, "acceptance merge");
            ConsoleViews.PrintAcceptanceSummary(context.CurrentGoal, context.Kernel.BuildGoalAcceptanceSummary(context.CurrentGoal.Id));
            RunAcceptanceWorkspaceMerge(context, skipVerify);
            return false;

        case "workspace":
            HandleWorkspaceCommand(context, parts);
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

        case "operator-inbox":
            var inboxGoal = GetOptionalArgument(parts, "--show-acknowledged");
            var inbox = OperatorInbox.Build(
                context.Kernel,
                context.Agents,
                context.WorkerProfiles,
                context.Workspace,
                inboxGoal,
                HasCliConfirmation(parts, "--show-acknowledged"));
            ConsoleViews.PrintOperatorInbox(inbox);
            return false;

        case "operator-inbox-ack":
            CliArgumentParser.RequirePartCount(parts, 2, "operator-inbox-ack <item-id> [note]");
            var ackGoal = GetFlagValue(parts, "--goal");
            var ackReport = OperatorInbox.Acknowledge(
                context.Kernel,
                context.Agents,
                context.WorkerProfiles,
                context.Workspace,
                parts[1],
                parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal) ? parts[2] : null,
                ackGoal);
            ConsoleViews.PrintOperatorInbox(ackReport);
            return false;

        case "next":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            var nextHealth = GoalHealthEvaluator.Build(
                context.Kernel,
                context.CurrentGoal,
                context.Agents,
                context.WorkerProfiles,
                context.Workspace.ExecutionDirectory,
                ResolveCliAutonomyPolicy(parts));
            ConsoleViews.PrintNextActions(context.CurrentGoal, context.Kernel.BuildNextActions(context.CurrentGoal.Id), context.Agents, nextHealth);
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
            var subscriptionAdvancePolicy = ResolveCliAutonomyPolicy(parts);
            subscriptionAdvancePolicy.ThrowIfDisallowed(AutonomyAction.DispatchStart, "advance-subscription");
            EnsureCliConfirmation(
                parts,
                "--confirm-subscription-advance",
                "advance-subscription requires --confirm-subscription-advance because it can prepare or start subscription worker processes.");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(
                context.Kernel,
                context.CurrentGoal,
                GetOptionalArgument(parts, "--confirm-subscription-advance", SubscriptionPromptCostGuard.CliConfirmationFlag));
            RecordPolicyAllowed(context, context.CurrentGoal, subscriptionAdvancePolicy, AutonomyAction.DispatchStart, "advance-subscription");
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
            var runGoalPolicy = ResolveCliAutonomyPolicy(parts);
            runGoalPolicy.ThrowIfDisallowed(AutonomyAction.DispatchStart, "run-goal");
            EnsureCliConfirmation(
                parts,
                "--confirm-batch-start",
                "run-goal requires --confirm-batch-start because it starts worker processes.");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(
                context.Kernel,
                context.CurrentGoal,
                GetOptionalArgument(parts, "--confirm-batch-start", SubscriptionPromptCostGuard.CliConfirmationFlag, "--confirm-readiness-risk"));
            EnsureGoalReadinessAllowsStart(context, context.CurrentGoal, HasCliConfirmation(parts, "--confirm-readiness-risk"));
            RecordPolicyAllowed(context, context.CurrentGoal, runGoalPolicy, AutonomyAction.DispatchStart, "run-goal");
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

private static void HandleLifecycleGoal(CliExecutionContext context, IReadOnlyList<string> parts, bool simple)
{
    var commandName = simple ? "lifecycle-simple-goal" : "lifecycle-goal";
    var policy = ResolveCliAutonomyPolicy(parts);
    Console.WriteLine($"Autonomy policy: {policy.Name}");
    policy.ThrowIfDisallowed(AutonomyAction.DispatchStart, commandName);
    CliArgumentParser.RequirePartCount(
        parts,
        2,
        $"{commandName} <objective> --confirm-batch-start [{SubscriptionPromptCostGuard.CliConfirmationFlag}]");
    EnsureCliConfirmation(
        parts,
        "--confirm-batch-start",
        $"{commandName} requires --confirm-batch-start because it starts worker processes.");
    EnsureCliConfirmation(
        parts,
        SubscriptionPromptCostGuard.CliConfirmationFlag,
        $"{commandName} requires {SubscriptionPromptCostGuard.CliConfirmationFlag} because it can start large paid subscription prompts.");

    var objective = parts[1].Trim();
    if (string.IsNullOrWhiteSpace(objective))
    {
        throw new ArgumentException(
            $"Usage: {commandName} <objective> --confirm-batch-start [{SubscriptionPromptCostGuard.CliConfirmationFlag}]");
    }

    var objectivePlan = GoalObjectivePlanner.Build(objective, simple);
    GoalObjectivePlanner.ThrowIfBlocked(objectivePlan);
    ConsoleViews.PrintGoalObjectivePlan(objectivePlan);

    var existingGoalId = GoalOperationJournal.TryFindLifecycleGoal(context.Workspace.ExecutionDirectory, commandName, objective);
    context.CurrentGoal = existingGoalId is not null &&
        context.Kernel.Goals.FirstOrDefault(goal => goal.Id == existingGoalId) is { } existingGoal
        ? existingGoal
        : simple
            ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, objective)
            : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, objective);
    var goal = context.CurrentGoal;
    var goalPrefix = goal.Id.Value[..8];
    Console.WriteLine($"Lifecycle goal: {goal.Id.Value}");
    if (existingGoalId is not null && existingGoalId == goal.Id)
    {
        Console.WriteLine(simple ? "Stage simple-goal: reused existing idempotent goal." : "Stage goal: reused existing idempotent goal.");
        var journal = GoalOperationJournal.Read(context.Workspace.ExecutionDirectory, goal.Id);
        if (goal.Status == GoalStatus.Completed &&
            GoalWorktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is null &&
            journal.LatestByOperation.Any(entry => entry.Operation == "workspace:remove" && entry.Status == GoalOperationStatus.Completed))
        {
            Console.WriteLine("Lifecycle goal already completed and workspace cleanup is recorded.");
            return;
        }
    }
    else
    {
        GoalOperationJournal.RecordLifecycleGoal(context.Workspace.ExecutionDirectory, goal, commandName, objective);
        GoalOperationJournal.Completed(
            context.Workspace.ExecutionDirectory,
            goal,
            simple ? "lifecycle-simple-goal:create" : "lifecycle-goal:create",
            "Goal created and activated.");
        Console.WriteLine(simple ? "Stage simple-goal: created and activated." : "Stage goal: created and activated.");
    }

    var branch = GoalWorktrees.BranchName(goal.Id);
    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "workspace:create", $"branch {branch}");
    var workspacePath = GoalWorktrees.Ensure(context.Workspace.ExecutionDirectory, goal.Id);
    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "workspace:create", workspacePath);
    Console.WriteLine($"Stage workspace create: {workspacePath} (branch {branch})");
    EnsureGoalReadinessAllowsStart(context, goal, HasCliConfirmation(parts, "--confirm-readiness-risk"));
    EnsureLifecycleParallelGateAllowsStart(context, goal);

    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "run-goal", "Starting subscription-driven goal loop.");
    RecordPolicyAllowed(context, goal, policy, AutonomyAction.DispatchStart, commandName);
    var runGoalResult = RunGoalService.RunAsync(
        context.Kernel,
        context.Agents,
        context.WorkerProfiles,
        context.Workspace,
        goal,
        allowLargePaidSubscriptionStart: true)
        .GetAwaiter().GetResult();
    Console.WriteLine("Stage run-goal:");
    ConsoleViews.PrintRunGoalResult(goal, runGoalResult);
    if (goal.Status != GoalStatus.Completed)
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "run-goal", runGoalResult.StopReason);
        var next = BuildLifecycleRunGoalNextCommand(goalPrefix, runGoalResult);
        Console.WriteLine($"Stage run-goal: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped after run-goal. Next: {next}");
    }

    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "run-goal", "Goal reached Completed status.");
    if (!TryEnsurePolicyAllows(context, goal, policy, AutonomyAction.Acceptance, $"{commandName} acceptance", out var acceptancePolicyError))
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "acceptance", acceptancePolicyError);
        var next = $"acceptance {goalPrefix} --autonomy {AutonomyPolicy.SupervisedAuto.Name}";
        Console.WriteLine($"Stage acceptance: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped before acceptance. Next: {next}");
    }

    Console.WriteLine("Stage acceptance:");
    ConsoleViews.PrintAcceptanceSummary(goal, context.Kernel.BuildGoalAcceptanceSummary(goal.Id));
    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "acceptance", "Running acceptance evidence and merge.");
    if (!RunAcceptanceWorkspaceMerge(context))
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance did not pass or merge was blocked.");
        var next = $"acceptance {goalPrefix}";
        Console.WriteLine($"Stage acceptance: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped after acceptance. Next: {next}");
    }

    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance passed and merge completed.");
    if (!TryEnsurePolicyAllows(context, goal, policy, AutonomyAction.WorkspaceCleanup, $"{commandName} workspace cleanup", out var cleanupPolicyError))
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", cleanupPolicyError);
        var next = $"workspace remove {goalPrefix} --autonomy {AutonomyPolicy.SupervisedAuto.Name}";
        Console.WriteLine($"Stage workspace remove: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped before workspace cleanup. Next: {next}");
    }

    Console.WriteLine("Stage workspace remove:");
    GoalWorktreeRemoveResult removeResult;
    try
    {
        GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "workspace:remove", "Removing goal workspace.");
        removeResult = GoalWorktrees.Remove(context.Workspace.ExecutionDirectory, goal.Id);
    }
    catch (InvalidOperationException ex)
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", ex.Message);
        var next = $"workspace remove {goalPrefix}";
        Console.WriteLine($"Stage workspace remove: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped during workspace cleanup. Next: {next}", ex);
    }

    PrintWorkspaceRemoveResult(removeResult);
    if (!removeResult.IsComplete)
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
        var next = removeResult.ResumeCommand ?? $"workspace remove {goalPrefix}";
        Console.WriteLine($"Stage workspace remove: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped during workspace cleanup. Next: {next}");
    }

    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
}

private static void HandleAcceptanceQueue(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var policy = ResolveCliAutonomyPolicy(parts);
    var apply = HasCliConfirmation(parts, "--apply");
    var plan = AcceptanceQueuePlanner.Build(context.Kernel, context.Workspace.ExecutionDirectory, policy);
    ConsoleViews.PrintAcceptanceQueuePlan(plan);
    if (!apply)
    {
        return;
    }

    EnsureCliConfirmation(
        parts,
        "--confirm-acceptance-queue",
        "acceptance-queue --apply requires --confirm-acceptance-queue because it can merge branches and remove workspaces.");

    if (plan.ReadyCount == 0)
    {
        Console.WriteLine("Acceptance queue apply: no ready goals.");
        return;
    }

    foreach (var item in plan.Items.Where(item => item.Disposition == AcceptanceQueueDisposition.Ready))
    {
        var goal = context.Kernel.Goals.First(goal => goal.Id == item.GoalId);
        context.CurrentGoal = goal;
        var goalPrefix = item.GoalPrefix;
        Console.WriteLine($"Acceptance queue goal {goalPrefix}:");

        EnsurePolicyAllows(context, goal, policy, AutonomyAction.Acceptance, "acceptance queue merge");
        ConsoleViews.PrintAcceptanceSummary(goal, context.Kernel.BuildGoalAcceptanceSummary(goal.Id));
        GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance queue running evidence and merge.");
        if (!RunAcceptanceWorkspaceMerge(context))
        {
            GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance queue merge blocked.");
            throw new InvalidOperationException($"acceptance-queue stopped at {goalPrefix}: acceptance did not pass or merge was blocked.");
        }

        GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance queue merge completed.");
        EnsurePolicyAllows(context, goal, policy, AutonomyAction.WorkspaceCleanup, "acceptance queue workspace cleanup");
        GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "workspace:remove", "Acceptance queue removing goal workspace.");
        GoalWorktreeRemoveResult removeResult;
        try
        {
            removeResult = GoalWorktrees.Remove(context.Workspace.ExecutionDirectory, goal.Id);
        }
        catch (InvalidOperationException ex)
        {
            GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", ex.Message);
            throw new InvalidOperationException($"acceptance-queue stopped at {goalPrefix}: workspace cleanup failed.", ex);
        }

        PrintWorkspaceRemoveResult(removeResult);
        if (!removeResult.IsComplete)
        {
            GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
            throw new InvalidOperationException($"acceptance-queue stopped at {goalPrefix}: workspace cleanup incomplete. Next: {removeResult.ResumeCommand ?? $"workspace remove {goalPrefix}"}");
        }

        GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
    }
}

private static bool HandleGoalDrain(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var policy = ResolveCliAutonomyPolicy(parts);
    var drainPolicy = GoalDrainPolicyStore.LoadOrDefault(context.Workspace);
    var apply = HasCliConfirmation(parts, "--apply");
    var costConfirmed = HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag);
    var plan = GoalDrainPlanner.Build(
        context.Kernel,
        context.Agents,
        context.WorkerProfiles,
        context.Workspace,
        policy,
        apply,
        costConfirmed,
        drainPolicy);
    if (!apply)
    {
        ConsoleViews.PrintGoalDrainPlan(plan);
        return false;
    }

    EnsureCliConfirmation(
        parts,
        "--confirm-goal-drain",
        "drain-goals --apply requires --confirm-goal-drain because it can start workers and mutate task state.");
    EnsureCliConfirmation(
        parts,
        "--confirm-batch-start",
        "drain-goals --apply requires --confirm-batch-start because it can start worker processes.");
    policy.ThrowIfDisallowed(AutonomyAction.DispatchStart, "drain-goals");

    var applied = new List<string>();
    foreach (var goal in context.Kernel.Goals.ToArray())
    {
        var supervisor = GoalSupervisor.ApplySafe(context.Kernel, goal, context.Agents, context.Workspace, policy);
        applied.AddRange(supervisor.AppliedActions.Select(action => $"{goal.Id.Value[..8]} {action}"));
    }

    var crossGoalPlan = CrossGoalSubscriptionStartPlanner.Build(
        context.Kernel,
        context.Agents,
        context.WorkerProfiles,
        costConfirmed);
    var startableGoalIds = plan.Items
        .Where(item => item.Stage == "subscription-start" && item.CanApply)
        .Select(item => item.GoalPrefix)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var candidate in crossGoalPlan.Candidates.Where(candidate => startableGoalIds.Contains(candidate.GoalPrefix)))
    {
        var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, null, candidate.GoalId);
        EnsureGoalReadinessAllowsStart(context, goal, HasCliConfirmation(parts, "--confirm-readiness-risk"));
        RecordPolicyAllowed(context, goal, policy, AutonomyAction.DispatchStart, "drain-goals");
        SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(
            SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
                goal,
                context.Agents,
                context.WorkerProfiles,
                task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(context.Kernel, goal, task, context.Agents)),
            costConfirmed);
        var result = GoalManagementCommandService.StartSubscriptionReadyTasks(
            context.Kernel,
            context.Workspace,
            goal,
            context.Agents,
            context.WorkerProfiles);
        applied.Add($"{goal.Id.Value[..8]} start-subscription-ready dispatches={result.Dispatches.Count} processes={result.Processes.Tasks.Count}");
    }

    var updated = GoalDrainPlanner.Build(
        context.Kernel,
        context.Agents,
        context.WorkerProfiles,
        context.Workspace,
        policy,
        apply,
        costConfirmed,
        drainPolicy);
    ConsoleViews.PrintGoalDrainPlan(updated, applied);
    return applied.Count > 0;
}

private static string BuildLifecycleRunGoalNextCommand(string goalPrefix, RunGoalService.RunGoalResult result)
{
    if (result.BlockingAction?.SuggestedCommand is { Length: > 0 } command)
    {
        return command;
    }

    var next = $"run-goal {goalPrefix} --confirm-batch-start";
    return result.StopReason.Contains(SubscriptionPromptCostGuard.CliConfirmationFlag, StringComparison.OrdinalIgnoreCase)
        ? $"{next} {SubscriptionPromptCostGuard.CliConfirmationFlag}"
        : next;
}

internal static void EnsureGoalReadinessAllowsStart(CliExecutionContext context, Goal goal, bool confirmed)
{
    var readiness = GoalReadinessPreflight.Build(goal, context.Agents, context.Workspace.ExecutionDirectory);
    if (!readiness.AllowsStart(confirmed))
    {
        ConsoleViews.PrintGoalReadinessPreflight(readiness);
    }

    GoalReadinessPreflight.ThrowIfStartBlocked(readiness, confirmed);
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
    for (var i = 1; i < parts.Count; i++)
    {
        var part = parts[i];
        if (flags.Any(flag => part.Equals(flag, StringComparison.OrdinalIgnoreCase)))
        {
            continue;
        }

        if (IsCliValueFlag(part))
        {
            i++;
            continue;
        }

        return part;
    }

    return null;
}

private static bool HandleBacklogIntake(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var createGoal = HasCliConfirmation(parts, "--create-goal");
    var createSimpleGoal = HasCliConfirmation(parts, "--create-simple-goal");
    if (createGoal && createSimpleGoal)
    {
        throw new ArgumentException("Use either --create-goal or --create-simple-goal, not both.");
    }

    var headingFilter = parts
        .Skip(1)
        .FirstOrDefault(part => !part.StartsWith("--", StringComparison.Ordinal));
    var plan = BacklogIntakePlanner.Build(
        context.Workspace.ExecutionDirectory,
        string.IsNullOrWhiteSpace(headingFilter) ? null : headingFilter,
        createGoal || createSimpleGoal ? 1 : 5);
    if (plan.Items.Count == 0)
    {
        throw new InvalidOperationException("No backlog items matched the requested filter.");
    }

    ConsoleViews.PrintBacklogIntakePlan(plan);
    if (!createGoal && !createSimpleGoal)
    {
        return false;
    }

    var item = plan.Items.Single();
    context.CurrentGoal = createSimpleGoal
        ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, item.SuggestedObjective)
        : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, item.SuggestedObjective);
    Console.WriteLine(createSimpleGoal ? "Created simple goal from backlog slice." : "Created five-role goal from backlog slice.");
    ConsoleViews.PrintGoal(context.CurrentGoal);
    return true;
}

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
        ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, plan.ReadyObjective)
        : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, plan.ReadyObjective);
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
        context.Workspace.ExecutionDirectory,
        string.IsNullOrWhiteSpace(headingFilter) ? null : headingFilter,
        maxItems: 10);
    if (intake.Items.Count == 0)
    {
        throw new InvalidOperationException("No backlog items matched the requested filter.");
    }

    var plan = GoalDependencyPlanner.Build(intake);
    ConsoleViews.PrintGoalDependencyPlan(plan);
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
            ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, node.ReadyObjective)
            : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, node.ReadyObjective);
        Console.WriteLine(createSimpleGoals
            ? $"Created simple goal {context.CurrentGoal.Id.Value[..8]} from plan node {node.Id}."
            : $"Created five-role goal {context.CurrentGoal.Id.Value[..8]} from plan node {node.Id}.");
    }

    return true;
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

private static AutonomyPolicy ResolveCliAutonomyPolicy(IReadOnlyList<string> parts)
{
    return AutonomyPolicy.Parse(GetFlagValue(parts, "--autonomy") ?? GetFlagValue(parts, "--autonomy-policy"));
}

private static void EnsurePolicyAllows(
    CliExecutionContext context,
    Goal goal,
    AutonomyPolicy policy,
    AutonomyAction action,
    string operation)
{
    if (!TryEnsurePolicyAllows(context, goal, policy, action, operation, out var error))
    {
        throw new InvalidOperationException(error);
    }
}

private static void EnsureLifecycleParallelGateAllowsStart(CliExecutionContext context, Goal goal)
{
    var plan = CrossGoalSubscriptionStartPlanner.Build(
        context.Kernel,
        context.Agents,
        context.WorkerProfiles,
        costRiskConfirmed: true);
    var decision = plan.ParallelPlan.Decisions.FirstOrDefault(decision =>
        decision.IntentId.Equals(goal.Id.Value, StringComparison.OrdinalIgnoreCase));
    if (decision is null)
    {
        return;
    }

    ConsoleViews.PrintCrossGoalSubscriptionStartPlan(plan);
    if (decision.Disposition == ParallelExecutionDisposition.Concurrent && decision.BatchNumber == 1)
    {
        return;
    }

    var reasons = string.Join("; ", decision.Reasons);
    throw new InvalidOperationException(
        $"Lifecycle start blocked by cross-goal parallel safety gate for goal {goal.Id.Value[..8]}: {decision.Disposition} batch={decision.BatchNumber?.ToString() ?? "none"} ({reasons}). Run cross-goal-start-plan and wait for batch 1 compatibility before lifecycle start.");
}

private static bool TryEnsurePolicyAllows(
    CliExecutionContext context,
    Goal goal,
    AutonomyPolicy policy,
    AutonomyAction action,
    string operation,
    out string error)
{
    var allowed = policy.Allows(action);
    AutonomyPolicyEvidence.Record(context.Kernel, goal, policy, action, operation, allowed);
    if (allowed)
    {
        error = string.Empty;
        return true;
    }

    try
    {
        policy.ThrowIfDisallowed(action, operation);
        error = string.Empty;
        return true;
    }
    catch (InvalidOperationException ex)
    {
        error = ex.Message;
        return false;
    }
}

private static void RecordPolicyAllowed(
    CliExecutionContext context,
    Goal goal,
    AutonomyPolicy policy,
    AutonomyAction action,
    string operation)
{
    AutonomyPolicyEvidence.Record(context.Kernel, goal, policy, action, operation, allowed: true);
}

private static string? GetFirstNonFlagArgument(IReadOnlyList<string> parts, int startIndex)
{
    for (var i = startIndex; i < parts.Count; i++)
    {
        var part = parts[i];
        if (IsCliValueFlag(part))
        {
            i++;
            continue;
        }

        if (!part.StartsWith("--", StringComparison.Ordinal))
        {
            return part;
        }
    }

    return null;
}

private static bool IsCliValueFlag(string part)
{
    return part.Equals("--autonomy", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--autonomy-policy", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--complex-model", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--confirm-limit-review", StringComparison.OrdinalIgnoreCase);
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

private static Goal HandleGoalParkCommand(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
    var reason = RemoveFlag(parts[2], "--confirm-goal-park");
    if (string.IsNullOrWhiteSpace(reason))
    {
        throw new ArgumentException("Goal park reason cannot be empty.", nameof(parts));
    }

    var runningTasks = goal.Tasks.Where(task => task.LastProcess is { IsRunning: true }).ToArray();
    if (!HasCliConfirmation(parts, "--confirm-goal-park") &&
        !parts[2].Contains("--confirm-goal-park", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Goal park dry run {goal.Id.Value[..8]}:");
        Console.WriteLine($"  reason: {reason}");
        Console.WriteLine($"  running dispatches to cancel: {runningTasks.Length}");
        Console.WriteLine("  resume gate: create goal-level human input request");
        Console.WriteLine($"  command: park-goal {goal.Id.Value[..8]} <reason> --confirm-goal-park");
        return goal;
    }

    var runner = new BackgroundDispatchRunner();
    foreach (var task in runningTasks)
    {
        _ = runner.CancelLatestProcess(context.Kernel, goal.Id, task.Id);
    }

    var request = context.Kernel.RequestHumanInput(goal.Id, null, $"Goal parked: {reason}");
    Console.WriteLine($"Goal parked {goal.Id.Value[..8]}.");
    Console.WriteLine($"Cancelled running dispatches: {runningTasks.Length}");
    Console.WriteLine($"Resume gate: answer {request.Id.Value[..8]} <resume note>");
    return goal;
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

private static void HandleWorkspaceCommand(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var action = parts.Count > 1 ? parts[1] : null;
    var goalPrefix = GetFirstNonFlagArgument(parts, startIndex: 2);

    var normalizedAction = (action ?? "status").ToLowerInvariant();
    if (normalizedAction is not ("status" or "create" or "merge" or "rebase" or "remove"))
    {
        throw new ArgumentException("Usage: workspace [create|merge|rebase|remove] [goal-id-prefix]");
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

        case "rebase":
            Console.WriteLine(FormatWorkspaceRebase(GoalWorktrees.TryRebaseOntoMain(executionDirectory, goal.Id)));
            return;

        case "remove":
            var policy = ResolveCliAutonomyPolicy(parts);
            EnsurePolicyAllows(context, goal, policy, AutonomyAction.WorkspaceCleanup, "workspace remove");
            PrintWorkspaceRemoveResult(GoalWorktrees.Remove(executionDirectory, goal.Id));
            return;

        default:
            throw new ArgumentException("Usage: workspace [create|merge|rebase|remove] [goal-id-prefix]");
    }
}

private static bool RunAcceptanceWorkspaceMerge(CliExecutionContext context, bool skipVerify = false)
{
    var goal = context.CurrentGoal!;
    if (goal.Status != GoalStatus.Completed)
    {
        return false;
    }

    var worktreePath = GoalWorktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id);
    AcceptanceVerificationResult? verification = null;
    if (worktreePath is not null)
    {
        var changedFiles = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);
        if (skipVerify)
        {
            Console.WriteLine("Verification: skipped (--skip-verify)");
        }
        else
        {
            verification = context.AcceptanceVerifier.RunAsync(worktreePath, goal.Id, changedFiles).GetAwaiter().GetResult();
            foreach (var check in verification.Checks ?? [])
            {
                Console.WriteLine($"Verification check: {(check.Passed ? "passed" : "failed")} - {check.Name}" +
                    (check.ExitCode is null ? "" : $" (exit {check.ExitCode})") +
                    (string.IsNullOrWhiteSpace(check.ArtifactsPath) ? "" : $" artifacts={check.ArtifactsPath}"));
            }
        }
    }

    var evidence = GoalAcceptanceEvidenceBundleBuilder.Build(context.Kernel, goal, worktreePath, verification, skipVerify);
    ConsoleViews.PrintAcceptanceEvidenceBundle(evidence);

    if (!evidence.Passed)
    {
        if (verification is { Passed: false })
        {
            Console.WriteLine($"Verification: failed (exit {verification.ExitCode}); merge blocked");
            Console.WriteLine($"Verification artifacts: {verification.ArtifactsPath}");
            if (!string.IsNullOrWhiteSpace(verification.OutputTail))
            {
                Console.WriteLine(verification.OutputTail);
            }
        }
        else
        {
            Console.WriteLine("Acceptance evidence: blocked; merge blocked");
        }

        return false;
    }

    if (verification is not null)
    {
        Console.WriteLine($"Verification: passed (exit {verification.ExitCode})");
        Console.WriteLine($"Verification artifacts: {verification.ArtifactsPath}");
    }

    var pendingRollback = GoalRollbackPlanner.CapturePendingAcceptance(context.Workspace.ExecutionDirectory, goal.Id);
    var merge = GoalWorktrees.TryFastForwardMerge(context.Workspace.ExecutionDirectory, goal.Id);
    if (merge is not null)
    {
        Console.WriteLine($"Workspace merge: {FormatWorkspaceMerge(merge)}");
        if (merge.FastForwarded && pendingRollback is not null)
        {
            GoalRollbackPlanner.RecordAcceptance(context.Workspace.ExecutionDirectory, pendingRollback);
        }

        return merge.FastForwarded;
    }

    return true;
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

private static string FormatWorkspaceRebase(GoalWorktreeRebaseResult rebase)
{
    var text = rebase.Message;
    if (rebase.ConflictFiles.Count > 0)
    {
        text += $"{Environment.NewLine}Conflict files:";
        foreach (var file in rebase.ConflictFiles)
        {
            text += $"{Environment.NewLine}  {file}";
        }
    }

    if (!string.IsNullOrWhiteSpace(rebase.SuggestedCommand))
    {
        text += $"{Environment.NewLine}Next: {rebase.SuggestedCommand}";
    }

    return text;
}
}
