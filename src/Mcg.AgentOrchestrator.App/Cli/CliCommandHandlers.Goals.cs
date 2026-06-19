using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static readonly Dictionary<string, AgentRole> GoalRoleAgentFlags =
    new Dictionary<string, AgentRole>(StringComparer.OrdinalIgnoreCase)
    {
        ["--planner"] = AgentRole.Planner,
        ["--ideation"] = AgentRole.Ideation,
        ["--researcher"] = AgentRole.Researcher,
        ["--developer"] = AgentRole.Developer,
        ["--tester"] = AgentRole.Tester,
        ["--reviewer"] = AgentRole.Reviewer
    };

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
            // --simple: delegate to simple-goal (1 Developer task)
            if (HasCliConfirmation(parts, "--simple"))
            {
                var simpleAliasObjective = ResolveBriefObjective(parts, "goal <objective> --simple | goal --brief-file <path> --simple");
                var simpleAliasParts = new List<string> { "simple-goal", simpleAliasObjective };
                AppendGoalAliasFlags(parts, simpleAliasParts, includeRoleAgentFlags: true, "--simple", "--brief-file");
                return TryExecuteGoalCommand("simple-goal", simpleAliasParts, context);
            }
            // --from-backlog: delegate to backlog-intake (objective used as heading filter)
            if (HasCliConfirmation(parts, "--from-backlog"))
            {
                var backlogFilter = GetOptionalArgument(parts, "--from-backlog");
                var backlogAliasParts = new List<string> { "backlog-intake" };
                if (backlogFilter is not null)
                    backlogAliasParts.Add(backlogFilter);
                foreach (var flag in parts.Skip(1).Where(p => p.StartsWith("--", StringComparison.Ordinal) && !p.Equals("--from-backlog", StringComparison.OrdinalIgnoreCase)))
                    backlogAliasParts.Add(flag);
                return TryExecuteGoalCommand("backlog-intake", backlogAliasParts, context);
            }
            // --run: create 5-role goal then delegate to run-goal
            if (HasCliConfirmation(parts, "--run"))
            {
                var runObjective = ResolveBriefObjective(parts, "goal <objective> --run | goal --brief-file <path> --run");
                var runObjectivePlan = GoalObjectivePlanner.Build(runObjective, simple: false);
                GoalObjectivePlanner.ThrowIfBlocked(runObjectivePlan);
                ConsoleViews.PrintGoalObjectivePlan(runObjectivePlan);
                var runAgents = ApplyRoleAgentOverrides(parts, context.Agents);
                context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, runAgents, runObjective, context.Workspace, context.Providers);
                ConsoleViews.PrintGoal(context.CurrentGoal);
                var runParts = new List<string> { "run-goal", context.CurrentGoal.Id.Value[..8] };
                AppendGoalAliasFlags(parts, runParts, includeRoleAgentFlags: false, "--run", "--brief-file");
                return TryExecuteGoalCommand("run-goal", runParts, context);
            }
            var goalObjective = ResolveBriefObjective(parts, "goal <objective> [--simple] [--from-backlog] [--run] | goal --brief-file <path>");
            var goalObjectivePlan = GoalObjectivePlanner.Build(goalObjective, simple: false);
            GoalObjectivePlanner.ThrowIfBlocked(goalObjectivePlan);
            ConsoleViews.PrintGoalObjectivePlan(goalObjectivePlan);
            var goalAgents = ApplyRoleAgentOverrides(parts, context.Agents);
            context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, goalAgents, goalObjective, context.Workspace, context.Providers);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "simple-goal":
            var simpleObjective = ResolveBriefObjective(parts, "simple-goal <objective> | simple-goal --brief-file <path>");
            var simpleObjectivePlan = GoalObjectivePlanner.Build(simpleObjective, simple: true);
            GoalObjectivePlanner.ThrowIfBlocked(simpleObjectivePlan);
            ConsoleViews.PrintGoalObjectivePlan(simpleObjectivePlan);
            var simpleAgents = ApplyRoleAgentOverrides(parts, context.Agents);
            context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, simpleAgents, simpleObjective, context.Workspace, context.Providers);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            if (HasCliConfirmation(parts, "--dispatch"))
            {
                EnsureCliConfirmation(
                    parts,
                    "--confirm-dispatch-start",
                    "simple-goal --dispatch requires --confirm-dispatch-start as the certainty signal.");
                var dispatchParts = new List<string> { "subscription-dispatch", "1", "--confirm-dispatch-start" };
                TryExecuteWorkerCommand("subscription-dispatch", dispatchParts, context);
            }
            return true;

        case "lifecycle-simple-goal":
            HandleLifecycleGoal(context, parts, simple: true);
            return true;

        case "lifecycle-goal":
            HandleLifecycleGoal(context, parts, simple: false);
            return true;

        case "goal-depends":
        {
            CliArgumentParser.RequirePartCount(parts, 4, "goal-depends <goal-prefix> --on <dependency-prefix>");
            var dependentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
            var onPrefix = GetFlagValue(parts, "--on")
                ?? throw new ArgumentException("goal-depends requires --on <dependency-prefix>");
            var dependencyGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, null, onPrefix);
            context.Kernel.SetGoalDependency(dependentGoal.Id, dependencyGoal.Id);
            Console.WriteLine($"Dependency set: {dependentGoal.Id.Value[..8]} depends on {dependencyGoal.Id.Value[..8]}");
            return true;
        }

        case "goal-plan":
            return HandleGoalPlan(context, parts);

        case "plan":
            return HandlePlan(context, parts);

        case "ideate":
            return HandleIdeate(context, parts);

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
            CliArgumentParser.RequirePartCount(parts, 4, "agent <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>]");
            if ((HasCliConfirmation(parts, "--complex-model") && GetFlagValue(parts, "--complex-model") is null) ||
                (HasCliConfirmation(parts, "--subscription-model") && GetFlagValue(parts, "--subscription-model") is null))
            {
                throw new ArgumentException("Usage: agent <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>]");
            }
            var agent = CreateCliAgentDefinition(parts);
            context.Agents = new AgentCatalog(context.Agents).UpsertRole(agent).Agents;
            AgentCatalogStore.Save(context.AgentCatalogPath, new AgentCatalog(context.Agents));
            ConsoleViews.PrintAgents(context.Agents);
            return false;

        case "agent-add":
            CliArgumentParser.RequirePartCount(parts, 4, "agent-add <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>]");
            if ((HasCliConfirmation(parts, "--complex-model") && GetFlagValue(parts, "--complex-model") is null) ||
                (HasCliConfirmation(parts, "--subscription-model") && GetFlagValue(parts, "--subscription-model") is null))
            {
                throw new ArgumentException("Usage: agent-add <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>]");
            }
            var addedAgent = CreateCliAgentDefinition(parts);
            addedAgent = addedAgent with { Id = BuildAlternateAgentId(addedAgent, GetCliAgentIdSuffix(parts, addedAgent)) };
            context.Agents = new AgentCatalog(context.Agents).AddOrReplaceById(addedAgent).Agents;
            AgentCatalogStore.Save(context.AgentCatalogPath, new AgentCatalog(context.Agents));
            ConsoleViews.PrintAgents(context.Agents);
            return false;

        case "model-functions":
            ConsoleViews.PrintModelFunctions(ModelFunctionCatalogStore.Load(context.Workspace.ModelFunctionCatalogPath));
            return false;

        case "model-function-add":
            CliArgumentParser.RequirePartCount(parts, 5, "model-function-add <purpose> <lane> <provider> <model> [name] [--subscription <worker-profile> [--subscription-model <alias>] [--subscription-reasoning <effort>]]");
            AddModelFunctionBinding(context, parts);
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

        case "record-goal":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            HandleRecordGoal(context);
            return false;

        case "recover":
            return HandleRecover(context, parts);

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
            var keepWorkspace = HasCliConfirmation(parts, "--keep-workspace");
            var noRecord = HasCliConfirmation(parts, "--no-record");
            var acceptanceGoalPart = GetOptionalArgument(parts, "--skip-verify", "--keep-workspace", "--no-record");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, acceptanceGoalPart);
            EnsurePolicyAllows(context, context.CurrentGoal, acceptancePolicy, AutonomyAction.Acceptance, "acceptance merge");
            AutoVerifyFromGitEvidence(context, context.CurrentGoal);
            ConsoleViews.PrintAcceptanceSummary(context.CurrentGoal, context.Kernel.BuildGoalAcceptanceSummary(context.CurrentGoal.Id));
            if (RunAcceptanceWorkspaceMerge(context, skipVerify))
            {
                if (!noRecord)
                {
                    AutoRecordDogfoodEntry(context);
                }

                AutoCloseSourceBacklogItem(context.CurrentGoal, context.Workspace.BacklogStorePath);
                CleanupGoalWorkspaceAfterMerge(context, context.CurrentGoal, acceptancePolicy, keepWorkspace);
            }

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
        {
            var isFull = HasCliConfirmation(parts, "--full");
            var nextGoalPrefix = GetOptionalArgument(parts, "--full");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, nextGoalPrefix);
            var nextPolicy = ResolveCliAutonomyPolicy(parts);
            var nextHealth = GoalHealthEvaluator.Build(
                context.Kernel,
                context.CurrentGoal,
                context.Agents,
                context.WorkerProfiles,
                context.Workspace.ExecutionDirectory,
                nextPolicy);
            ConsoleViews.PrintNextActions(context.CurrentGoal, context.Kernel.BuildNextActions(context.CurrentGoal.Id), context.Agents, nextHealth);
            if (isFull)
            {
                PrintNextFullDetail(context, nextPolicy);
            }
            return false;
        }

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
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag),
                context.Providers);
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
                HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag),
                providers: context.Providers)
                .GetAwaiter().GetResult();
            ConsoleViews.PrintRunGoalResult(context.CurrentGoal, runGoalResult);
            return runGoalResult.Executed;

        case "delegate":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            GoalRefinementGate.EnsureRefined(context.Kernel, context.Workspace, context.Providers, context.CurrentGoal);
            var delegation = context.Kernel.ActivateGoal(context.CurrentGoal.Id, context.Agents);
            ConsoleViews.PrintDelegationPlan(context.CurrentGoal, delegation);
            return delegation.Assignments.Count > 0;

        case "land":
            CliArgumentParser.RequirePartCount(parts, 2, "land <goal-id-prefix>");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
            var landResult = LandingExecutor.Execute(context.Kernel, context.CurrentGoal, context.Workspace, context.Channel);
            Console.WriteLine($"Land {landResult.GoalPrefix}: {landResult.Message}");
            Console.WriteLine($"  decision: {(landResult.Decision is LandingDecision.Promote ? "Promote" : $"Escalate({((LandingDecision.Escalate)landResult.Decision).Reason})")}");
            Console.WriteLine($"  integration-branch: {landResult.IntegrationBranch}");
            Console.WriteLine($"  main-advanced: {landResult.MainAdvanced}");
            return landResult.MainAdvanced;

        case "goals-prune":
            return HandleGoalsPrune(context, parts);

        case "conduct":
            if (HasCliConfirmation(parts, "--loop"))
            {
                var loopPolicyName = GetFlagValue(parts, "--policy");
                var loopPolicy = loopPolicyName is null
                    ? ConductorAutonomyPolicy.Default
                    : ConductorAutonomyPolicy.All.FirstOrDefault(
                        p => p.Name.Equals(loopPolicyName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException(
                        $"Unknown conductor policy '{loopPolicyName}'. Valid: {string.Join(", ", ConductorAutonomyPolicy.All.Select(p => p.Name))}");
                int? loopMaxIter = null;
                if (GetFlagValue(parts, "--max-iterations") is { } miStr)
                    loopMaxIter = int.Parse(miStr, System.Globalization.CultureInfo.InvariantCulture);

                // --watch: sleep instead of exiting when all goals are held, enabling continuous unattended operation.
                // Accepts --poll-seconds N (preferred) or legacy --watch-interval N.
                TimeSpan? watchInterval = null;
                if (HasCliConfirmation(parts, "--watch"))
                {
                    var intervalSec = GetFlagValue(parts, "--poll-seconds") ?? GetFlagValue(parts, "--watch-interval");
                    var seconds = intervalSec is not null
                        ? int.Parse(intervalSec, System.Globalization.CultureInfo.InvariantCulture)
                        : ConductorBatchLoop.DefaultWatchIntervalSeconds;
                    watchInterval = TimeSpan.FromSeconds(seconds);
                    Console.WriteLine($"[conduct --loop --watch] Watch mode active; will sleep {seconds}s between ticks when all goals are held.");
                }

                // --max-duration N: stop after N seconds of wall-clock time (independent of --max-iterations).
                TimeSpan? maxDuration = null;
                if (GetFlagValue(parts, "--max-duration") is { } mdStr)
                    maxDuration = TimeSpan.FromSeconds(int.Parse(mdStr, System.Globalization.CultureInfo.InvariantCulture));

                // --daemon: run as a PERSISTENT conductor — never exit on an empty backlog. The loop stays
                // alive and polls, so goals submitted later (via a separate `goal` command, backlog
                // promotion, or the dashboard) are ingested by the per-tick sweep and driven without a
                // restart. Implies watch behavior; defaults the poll interval when not given. Stop via the
                // .conduct-stop file or --max-duration.
                var loopDaemon = HasCliConfirmation(parts, "--daemon");
                if (loopDaemon && watchInterval is null)
                {
                    watchInterval = TimeSpan.FromSeconds(ConductorBatchLoop.DefaultWatchIntervalSeconds);
                    Console.WriteLine($"[conduct --loop --daemon] Persistent mode; polling every {ConductorBatchLoop.DefaultWatchIntervalSeconds}s and staying alive on an empty backlog. Stop via {ConductorBatchLoop.StopFileName} or --max-duration.");
                }

                // SSE push: discover dashboard URL and build onTick callback.
                Action<BatchTickSummary>? onTick = null;
                var dashboardUrl = GetFlagValue(parts, "--dashboard-url")
                    ?? ConductorTickPusher.TryReadDashboardUrl(context.Workspace.DashboardUrlFilePath);
                if (dashboardUrl is not null)
                {
                    Console.WriteLine($"[conduct --loop] Dashboard SSE push enabled: {dashboardUrl}");
                    onTick = ConductorTickPusher.CreateCallback(dashboardUrl);
                }

                var loopDriver = new ConductorDriver(
                    context.Kernel,
                    context.Workspace,
                    context.AcceptanceVerifier,
                    context.Agents,
                    context.WorkerProfiles,
                    context.Channel,
                    context.Providers);
                var stopFilePath = Path.Combine(context.Workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName);

                // Reconcile finished dispatches (read exit files, record results, advance tasks) at the
                // start of every tick. Without this the loop holds a goal at Running forever — the worker
                // finishes but its result is never recorded — and a stop/restart re-dispatches the same
                // stage. Fault-isolated so one goal's refresh failure can't kill the loop.
                Action<AgentOrchestratorKernel> reconcileSweep = loopKernel =>
                {
                    // Dynamic goal pickup: ingest goals submitted (via a separate `goal` command, backlog
                    // promotion, or a future API) AFTER this loop loaded, so a long-running batch loop
                    // drives them without a restart. Additive merge only — never clobbers the in-flight
                    // goals this loop is already driving. Best-effort: a reload hiccup must not kill a tick.
                    try { loopKernel.IngestNewGoals(context.ReloadKernel().ExportSnapshot()); }
                    catch { /* dynamic pickup is best-effort */ }

                    foreach (var loopGoal in loopKernel.Goals.ToArray())
                    {
                        try { GoalManagementCommandService.RefreshDispatches(loopKernel, loopGoal); }
                        catch { /* per-goal isolation */ }
                    }
                };
                var loopReaper = new BackgroundDispatchRunner();
                var loopSummary = new ConductorBatchLoop(
                    reconcileSweep,
                    (loopKernel, loopGoal) => loopReaper.CancelRunningProcessesForGoal(loopKernel, loopGoal.Id)).Run(
                    context.Kernel, loopDriver, loopPolicy, stopFilePath, loopMaxIter,
                    watchInterval: watchInterval, onTick: onTick, maxDuration: maxDuration,
                    persistTick: context.PersistCheckpoint, keepAliveWhenIdle: loopDaemon);
                Console.WriteLine($"Conduct --loop complete: ticks={loopSummary.Ticks} advanced={loopSummary.Advanced} held={loopSummary.Held} escalated={loopSummary.Escalated} retried={loopSummary.Retried}{(loopSummary.StopRequested ? " (stopped)" : "")}");
                return loopSummary.Escalated == 0;
            }
            CliArgumentParser.RequirePartCount(parts, 2, "conduct <goal-id-prefix> [--policy <Conservative|Permissive|Manual>]");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
            var conductPolicyName = GetFlagValue(parts, "--policy");
            var conductPolicy = conductPolicyName is null
                ? ConductorAutonomyPolicy.Default
                : ConductorAutonomyPolicy.All.FirstOrDefault(
                    p => p.Name.Equals(conductPolicyName, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException(
                    $"Unknown conductor policy '{conductPolicyName}'. Valid: {string.Join(", ", ConductorAutonomyPolicy.All.Select(p => p.Name))}");
            var conductDriver = new ConductorDriver(
                context.Kernel,
                context.Workspace,
                context.AcceptanceVerifier,
                context.Agents,
                context.WorkerProfiles,
                context.Channel,
                context.Providers);

            // Single-goal continuous mode: drive just this goal to its next checkpoint without the
            // whole-kernel loop, so adding a goal never requires stopping a running loop and other
            // goals/ghosts aren't touched. Reuses the batch loop scoped to one goal.
            if (HasCliConfirmation(parts, "--watch"))
            {
                var watchGoalId = context.CurrentGoal.Id.Value;
                var watchPollSeconds = int.TryParse(
                    GetFlagValue(parts, "--poll-seconds") ?? GetFlagValue(parts, "--watch-interval"),
                    out var wps) && wps > 0 ? wps : ConductorBatchLoop.DefaultWatchIntervalSeconds;
                TimeSpan? watchMax = int.TryParse(GetFlagValue(parts, "--max-duration"), out var wmd)
                    ? TimeSpan.FromSeconds(wmd) : null;
                Action<AgentOrchestratorKernel> watchSweep = wk =>
                {
                    var g = wk.Goals.FirstOrDefault(x => x.Id.Value == watchGoalId);
                    if (g is not null) { try { GoalManagementCommandService.RefreshDispatches(wk, g); } catch { } }
                };
                var watchStopPath = Path.Combine(context.Workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName);
                Console.WriteLine($"[conduct --watch] Driving goal {watchGoalId[..8]} [{conductPolicy.Name}] continuously; poll {watchPollSeconds}s; stop via {ConductorBatchLoop.StopFileName}.");
                var watchReaper = new BackgroundDispatchRunner();
                var watchSummary = new ConductorBatchLoop(
                    watchSweep,
                    (wk, goal) => watchReaper.CancelRunningProcessesForGoal(wk, goal.Id)).Run(
                    context.Kernel, conductDriver, conductPolicy, watchStopPath,
                    watchInterval: TimeSpan.FromSeconds(watchPollSeconds), maxDuration: watchMax,
                    onlyGoalId: watchGoalId, persistTick: context.PersistCheckpoint);
                Console.WriteLine($"Conduct --watch complete: ticks={watchSummary.Ticks} advanced={watchSummary.Advanced} held={watchSummary.Held} escalated={watchSummary.Escalated}{(watchSummary.StopRequested ? " (stopped)" : "")}");
                return watchSummary.Escalated == 0;
            }

            var conductResult = conductDriver.AdvanceOnce(context.CurrentGoal, conductPolicy);
            Console.WriteLine($"Conduct {conductResult.GoalPrefix} [{conductResult.PolicyName}]: {conductResult.Outcome switch {
                ConductorAdvanceOutcome.Executed e => $"executed from {e.FromState} — {e.Description}",
                ConductorAdvanceOutcome.Held h => $"held at {h.State} — {h.Reason}",
                ConductorAdvanceOutcome.Escalated esc => $"escalated at {esc.State} — {esc.Reason}",
                ConductorAdvanceOutcome.Done d => $"done ({d.State})",
                _ => conductResult.Outcome.ToString()
            }}");
            return !conductResult.WasEscalated;

        default:
            return null;
    }
}

// Adds/replaces an orchestrator-internal model-function binding (e.g. an acceptance-judge lane).
// Distinct from agents: these are models the orchestrator invokes for its own functions, not workers.
private static void AddModelFunctionBinding(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var purpose = parts[1];
    var lane = CliArgumentParser.ParseModelLane(parts[2]);
    var provider = parts[3];
    var model = parts[4];
    var name = parts.Count > 5 && !parts[5].StartsWith("--", StringComparison.Ordinal) ? parts[5] : null;
    var subscriptionMode = lane == ModelLane.Local ? SubscriptionMode.LocalBridge : SubscriptionMode.ApiKey;
    var subscriptionProfileName = GetFlagValue(parts, "--subscription");
    var subscriptionModelAlias = GetFlagValue(parts, "--subscription-model");
    var subscriptionReasoning = GetFlagValue(parts, "--subscription-reasoning");
    SubscriptionLaunchProfile? subscription = subscriptionProfileName is not null
        ? new SubscriptionLaunchProfile(subscriptionProfileName, subscriptionModelAlias, subscriptionReasoning)
        : null;
    var binding = new ModelFunctionBinding(
        purpose,
        lane,
        new ModelProfile(provider, model, ModelCapability.Text, subscriptionMode),
        name,
        subscription);

    var path = context.Workspace.ModelFunctionCatalogPath;
    var bindings = ModelFunctionCatalogStore.Load(path).Bindings
        .Where(existing => !(string.Equals(existing.Purpose, purpose, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Model.ProviderName, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.Model.ModelName, model, StringComparison.OrdinalIgnoreCase)))
        .Append(binding)
        .ToList();

    var catalog = new ModelFunctionCatalog(bindings);
    ModelFunctionCatalogStore.Save(path, catalog);
    ConsoleViews.PrintModelFunctions(catalog);
}

private static AgentDefinition CreateCliAgentDefinition(IReadOnlyList<string> parts)
{
    var agentName = parts.Count > 4 && !parts[4].StartsWith("--", StringComparison.Ordinal) ? parts[4] : null;
    var complexModelName = GetFlagValue(parts, "--complex-model");
    var subscriptionModel = GetFlagValue(parts, "--subscription-model");
    return DashboardRequestParser.CreateAgentDefinition(new AgentSubmissionDto(
        parts[1], parts[2], parts[3], agentName,
        SubscriptionModelAlias: subscriptionModel,
        ComplexProviderName: complexModelName is null ? null : parts[2],
        ComplexModelName: complexModelName));
}

// Deterministic recording: acceptance auto-appends a DOGFOOD_LOG entry rendered from the goal's
// receipts, so a landed goal is journaled without the operator hand-writing prose. --no-record opts out.
private static void AutoRecordDogfoodEntry(CliExecutionContext context)
{
    var goal = context.CurrentGoal!;
    var logPath = Path.Combine(context.Workspace.ExecutionDirectory, "DOGFOOD_LOG.md");
    if (!File.Exists(logPath))
    {
        return;
    }

    var text = DogfoodLogRenderer.Render(goal).Render();
    File.AppendAllText(logPath, Environment.NewLine + Environment.NewLine + text);
    Console.WriteLine($"Recorded DOGFOOD entry for goal {goal.Id.Value[..8]} to {logPath}.");
    CommitDogfoodEntry(context.Workspace.ExecutionDirectory, goal.Id.Value[..8]);
}

// Closes the linked backlog item (if any) when a goal lands. Idempotent: already-closed or
// absent items are a safe no-op. Swallows all store exceptions so acceptance never fails here.
internal static bool AutoCloseSourceBacklogItem(Goal? goal, string backlogStorePath)
{
    if (goal?.SourceBacklogItemId is null)
        return false;

    var store = new BacklogStore(backlogStorePath);
    var closed = store.TryCloseByIdAsync(goal.SourceBacklogItemId, $"Goal {goal.Id.Value[..8]} landed.").GetAwaiter().GetResult();
    Console.WriteLine(closed
        ? $"Closed backlog item {goal.SourceBacklogItemId} (goal {goal.Id.Value[..8]} landed)."
        : $"Backlog item {goal.SourceBacklogItemId} already closed or not found (no-op).");
    return closed;
}

private static void CommitDogfoodEntry(string executionDirectory, string goalPrefix)
{
    var add = GitCli.Run(executionDirectory, "add", "DOGFOOD_LOG.md");
    if (add.ExitCode != 0)
    {
        Console.WriteLine($"Dogfood commit skipped: git add failed ({add.Error}).");
        return;
    }

    var diff = GitCli.Run(executionDirectory, "diff", "--cached", "--quiet", "DOGFOOD_LOG.md");
    if (diff.ExitCode == 0)
    {
        return;
    }

    var commit = GitCli.Run(executionDirectory, "commit", "-m", $"Record dogfood entry for goal {goalPrefix}");
    if (commit.ExitCode == 0)
    {
        Console.WriteLine($"Committed DOGFOOD_LOG.md for goal {goalPrefix}.");
    }
    else
    {
        Console.WriteLine($"Dogfood commit failed: {commit.Error}");
    }
}


private static void HandleRecordGoal(CliExecutionContext context)
{
    var goal = context.CurrentGoal!;
    var entry = DogfoodLogRenderer.Render(goal);
    var text = entry.Render();
    Console.Write(text);

    var logPath = Path.Combine(context.Workspace.ExecutionDirectory, "DOGFOOD_LOG.md");
    if (File.Exists(logPath))
    {
        File.AppendAllText(logPath, Environment.NewLine + Environment.NewLine + text);
        Console.WriteLine();
        Console.WriteLine($"Appended to {logPath}");
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine($"DOGFOOD_LOG.md not found at {logPath}; entry printed but not appended.");
    }
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
            ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, objective, context.Workspace, context.Providers)
            : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, objective, context.Workspace, context.Providers);
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
        allowLargePaidSubscriptionStart: true,
        providers: context.Providers)
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
    AutoCloseSourceBacklogItem(goal, context.Workspace.BacklogStorePath);
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
            context.WorkerProfiles,
            context.Providers);
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

private static void PrintNextFullDetail(CliExecutionContext context, AutonomyPolicy policy)
{
    var goal = context.CurrentGoal!;
    ConsoleViews.PrintGoal(goal);
    ConsoleViews.PrintMonitor(context.Kernel.BuildMonitor(goal.Id));
    ConsoleViews.PrintGoalReadinessPreflight(GoalReadinessPreflight.Build(goal, context.Agents, context.Workspace.ExecutionDirectory));
    ConsoleViews.PrintEvidenceSummary(goal, context.Kernel.BuildGoalEvidenceSummary(goal.Id));
    ConsoleViews.PrintStageReadinessReport(goal, context.Kernel.BuildStageReadinessReport(goal.Id), context.Agents);
    ConsoleViews.PrintVerificationGate(goal, context.Kernel.BuildVerificationGate(goal.Id));
    ConsoleViews.PrintVerificationWorklist(goal, context.Kernel.BuildVerificationWorklist(goal.Id));
    ConsoleViews.PrintHumanInputWorklist(goal, context.Kernel.BuildHumanInputWorklist(goal.Id));
    ConsoleViews.PrintSubscriptionPlan(SubscriptionPlanBuilder.Build(
        goal,
        context.Agents,
        context.WorkerProfiles,
        task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(context.Kernel, goal, task, context.Agents)));
    ConsoleViews.PrintModelOutcomeScorecard(context.Kernel.BuildModelOutcomeScorecard());
    ConsoleViews.PrintLoopHealthReport(context.Kernel.BuildLoopHealthReport(null));
    ConsoleViews.PrintFailureTriageReport(FailureTriagePlanner.Build(context.Kernel, goal, context.Agents, context.Workspace.ExecutionDirectory, policy));
    ConsoleViews.PrintGoalRecoveryReport(GoalRecoveryPlanner.Build(context.Kernel, goal, context.Workspace.ExecutionDirectory));
    ConsoleViews.PrintGoalSupervisorPlan(GoalSupervisor.Build(context.Kernel, goal, context.Agents, context.Workspace.ExecutionDirectory, policy));
    ConsoleViews.PrintOperatorInbox(OperatorInbox.Build(context.Kernel, context.Agents, context.WorkerProfiles, context.Workspace, goal.Id.Value[..8], includeAcknowledged: false));
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

private static IReadOnlyList<AgentDefinition> ApplyRoleAgentOverrides(
    IReadOnlyList<string> parts,
    IReadOnlyList<AgentDefinition> agents)
{
    var catalog = new AgentCatalog(agents);
    foreach (var (flag, role) in GoalRoleAgentFlags)
    {
        var agentId = GetFlagValue(parts, flag);
        if (agentId is null && !HasCliConfirmation(parts, flag))
        {
            continue;
        }

        if (string.IsNullOrWhiteSpace(agentId) || agentId.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{flag} requires <agentId>.");
        }

        var agent = catalog.FindById(agentId)
            ?? throw new ArgumentException($"Unknown agent id '{agentId}' for {flag}.");
        if (agent.Role != role)
        {
            throw new ArgumentException(
                $"Agent id '{agentId}' has role {agent.Role}; {flag} requires an agent with role {role}.");
        }

        catalog = catalog.UpsertRole(agent);
    }

    return catalog.Agents;
}

private static void AppendGoalAliasFlags(
    IReadOnlyList<string> parts,
    List<string> target,
    bool includeRoleAgentFlags,
    params string[] excludedFlags)
{
    for (var i = 2; i < parts.Count; i++)
    {
        var part = parts[i];
        if (!part.StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        if (excludedFlags.Any(flag => part.Equals(flag, StringComparison.OrdinalIgnoreCase)) ||
            (!includeRoleAgentFlags && GoalRoleAgentFlags.ContainsKey(part)))
        {
            if (IsCliValueFlag(part))
            {
                i++;
            }
            continue;
        }

        target.Add(part);
        if (IsCliValueFlag(part) && i + 1 < parts.Count)
        {
            target.Add(parts[++i]);
        }
    }
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
    var backlogItemId = BacklogStore.SlugId(item.Heading);

    // Skip intake if the item is already Done in the backlog store.
    if (!string.IsNullOrEmpty(backlogItemId))
    {
        var store = new BacklogStore(context.Workspace.BacklogStorePath);
        var existing = store.GetByExactIdAsync(backlogItemId).GetAwaiter().GetResult();
        if (existing is { Status: BacklogItemStatus.Done })
        {
            Console.WriteLine($"Backlog item '{item.Heading}' is already Done; no goal created.");
            return false;
        }
    }

    context.CurrentGoal = createSimpleGoal
        ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, item.SuggestedObjective, context.Workspace, context.Providers)
        : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, item.SuggestedObjective, context.Workspace, context.Providers);

    if (!string.IsNullOrEmpty(backlogItemId))
    {
        context.Kernel.SetGoalSourceBacklogItemId(context.CurrentGoal.Id, backlogItemId);
    }

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
        ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, plan.ReadyObjective, context.Workspace, context.Providers)
        : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, plan.ReadyObjective, context.Workspace, context.Providers);
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
            ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, node.ReadyObjective, context.Workspace, context.Providers)
            : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, node.ReadyObjective, context.Workspace, context.Providers);
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
        var backlogPath = Path.Combine(context.Workspace.ExecutionDirectory, "BACKLOG.md");
        if (!File.Exists(backlogPath))
            throw new InvalidOperationException($"BACKLOG.md not found at {backlogPath}; cannot append ideas.");
        var entries = string.Concat(plan.Ideas.Select(IdeationProposalPlanner.FormatBacklogEntry));
        File.AppendAllText(backlogPath, entries);
        Console.WriteLine($"Appended {plan.Ideas.Count} idea(s) to BACKLOG.md.");
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

    var objPlan = GoalObjectivePlanner.Build(direction, simple: true);
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

private static IReadOnlyList<string> GetFlagValues(IReadOnlyList<string> parts, string flag)
{
    var results = new List<string>();
    for (var i = 1; i < parts.Count - 1; i++)
    {
        if (parts[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            results.Add(parts[i + 1]);
    }
    return results;
}

private static List<string> RemoveFlagWithValue(IReadOnlyList<string> parts, string flag)
{
    var result = new List<string>(parts.Count);
    for (var i = 0; i < parts.Count; i++)
    {
        if (parts[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
        {
            i++; // skip the flag value too
            continue;
        }

        result.Add(parts[i]);
    }

    return result;
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
    return GoalRoleAgentFlags.ContainsKey(part) ||
        part.Equals("--autonomy", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--autonomy-policy", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--brief-file", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--complex-model", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--confirm-limit-review", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--subscription", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--subscription-model", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("--subscription-reasoning", StringComparison.OrdinalIgnoreCase);
}

private static string ResolveBriefObjective(IReadOnlyList<string> parts, string usage)
{
    var briefFilePath = GetFlagValue(parts, "--brief-file");
    if (briefFilePath is not null)
    {
        if (!File.Exists(briefFilePath))
        {
            throw new InvalidOperationException($"--brief-file not found: {briefFilePath}");
        }
        return File.ReadAllText(briefFilePath, System.Text.Encoding.UTF8);
    }
    CliArgumentParser.RequirePartCount(parts, 2, usage);
    return parts[1];
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

private static bool HandleGoalsPrune(CliExecutionContext context, IReadOnlyList<string> parts)
{
    if (!GoalWorktrees.IsGitWorkTree(context.Workspace.ExecutionDirectory))
    {
        throw new InvalidOperationException(
            "goals-prune requires a git work tree; the execution directory is not inside a git repository.");
    }

    var confirm = HasCliConfirmation(parts, "--confirm-prune");
    var plan = confirm
        ? GoalsPrunePlanner.Apply(context.Kernel, context.Workspace.ExecutionDirectory)
        : GoalsPrunePlanner.Build(context.Kernel, context.Workspace.ExecutionDirectory);

    ConsoleViews.PrintGoalsPrunePlan(plan);
    return plan.PrunedCount > 0;
}

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

    // Advisory only (does not gate the merge): ask a local judge whether the diff actually
    // accomplishes the objective, beyond passing tests. Records a receipt for the eventual
    // local-vs-subscription comparison and blocking flip. Any failure is swallowed.
    RunAdvisorySemanticAcceptance(context, goal, worktreePath, verification);

    var pendingRollback = GoalRollbackPlanner.CapturePendingAcceptance(context.Workspace.ExecutionDirectory, goal.Id);
    var merge = GoalWorktrees.TryFastForwardMerge(context.Workspace.ExecutionDirectory, goal.Id);
    if (merge is { FastForwarded: false })
    {
        // Deterministic: the goal branch is behind main, so a plain ff is impossible. Rebase it
        // onto main and retry the ff instead of punting the merge to the operator. A rebase
        // conflict leaves the branch un-updated, so the merge stays blocked and escalates.
        var rebase = GoalWorktrees.TryRebaseOntoMain(context.Workspace.ExecutionDirectory, goal.Id);
        Console.WriteLine($"Workspace rebase: {FormatWorkspaceRebase(rebase)}");
        if (rebase.UpdatedBranch)
        {
            merge = GoalWorktrees.TryFastForwardMerge(context.Workspace.ExecutionDirectory, goal.Id);
        }
    }

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

// Advisory semantic-acceptance pass: the configured judge lanes decide whether the diff actually
// accomplishes the objective (not merely that tests pass), recorded as a receipt. ADVISORY — it
// never changes the merge outcome and swallows every failure. Dormant unless one or more Judge-role
// agents are configured (one per lane: free-local / cheap-API / capable), so acceptance is
// unchanged on machines/tenants without any. Judges run in parallel; their agreement is the signal
// for whether a cheaper lane suffices before the eventual blocking flip.
private static void RunAdvisorySemanticAcceptance(
    CliExecutionContext context,
    Goal goal,
    string? worktreePath,
    AcceptanceVerificationResult? verification)
{
    if (worktreePath is null)
    {
        return;
    }

    var modelFunctions = ModelFunctionCatalogStore.Load(context.Workspace.ModelFunctionCatalogPath);
    var baseJudges = SemanticAcceptanceEvaluator.BuildJudges(modelFunctions, context.Providers, context.WorkerProfiles);
    if (baseJudges.Count == 0)
    {
        return;
    }

    try
    {
        var criteria = goal.Tasks
            .Select(task => task.VerificationPlan)
            .Where(plan => !string.IsNullOrWhiteSpace(plan))
            .Select(plan => plan!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var testSummary = verification?.Checks is { Count: > 0 } checks
            ? string.Join(Environment.NewLine, checks
                .Where(check => !string.IsNullOrWhiteSpace(check.ResultSummary))
                .Select(check => $"{check.Name}: {check.ResultSummary}"))
            : null;

        var perFileDiffs = GoalAcceptanceEvidenceBundleBuilder.GetPerFileDiffs(worktreePath);
        var inputs = new SemanticAcceptanceInputs(
            goal.Objective,
            criteria,
            GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath),
            GoalAcceptanceEvidenceBundleBuilder.GetDiffExcerpt(worktreePath),
            testSummary,
            perFileDiffs);

        var judges = baseJudges
            .Select(j => (ISemanticJudge)new RecursivePerFileSemanticJudge(j))
            .ToList();

        var report = SemanticAcceptanceEvaluator
            .EvaluateAsync(judges, inputs, TimeSpan.FromSeconds(90))
            .GetAwaiter()
            .GetResult();

        PrintSemanticAcceptanceReport(report);
        AppendSemanticAcceptanceReceipt(context, goal, report);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Semantic acceptance (advisory): skipped after error: {ex.Message}");
    }
}

private static void PrintSemanticAcceptanceReport(SemanticAcceptanceReport report)
{
    Console.WriteLine("Semantic acceptance (advisory — does not gate the merge):");
    foreach (var entry in report.Verdicts)
    {
        var verdict = entry.Verdict;
        if (!verdict.IsValid)
        {
            Console.WriteLine($"  {entry.Judge}: no verdict ({string.Join("; ", verdict.ValidationErrors)})");
            continue;
        }

        var summary = verdict.CriteriaMet ? "criteria MET" : "criteria NOT met";
        Console.WriteLine($"  {entry.Judge}: {summary} (confidence {verdict.Confidence})");
        foreach (var reason in verdict.Reasons.Take(3))
        {
            Console.WriteLine($"    - {reason}");
        }

        foreach (var unmet in verdict.UnmetCriteria)
        {
            Console.WriteLine($"    unmet: {unmet}");
        }
    }
}

private static void AppendSemanticAcceptanceReceipt(
    CliExecutionContext context,
    Goal goal,
    SemanticAcceptanceReport report)
{
    var receipt = new
    {
        at = DateTimeOffset.UtcNow,
        goalId = goal.Id.Value,
        objective = goal.Objective,
        consensus = report.Consensus,
        judges = report.Verdicts.Select(entry => new
        {
            judge = entry.Judge,
            valid = entry.Verdict.IsValid,
            criteriaMet = entry.Verdict.CriteriaMet,
            confidence = entry.Verdict.Confidence,
            reasons = entry.Verdict.Reasons,
            unmetCriteria = entry.Verdict.UnmetCriteria,
            errors = entry.Verdict.ValidationErrors
        })
    };

    var path = context.Workspace.SemanticAcceptanceLogPath;
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    File.AppendAllText(path, JsonSerializer.Serialize(receipt) + Environment.NewLine);
}

// Deterministic recovery: one `recover <goal> <note>` owns the multi-step "unblock" dances the
// operator used to memorize. It answers any open human-input requests (which `SubmitHumanInput`
// flips to Running), normalizes stuck/orphaned tasks to Failed so `RetryTask` accepts them, then
// retries them back to a dispatchable state — all with the single operator note. Genuinely running
// tasks (a live process) are left alone.
private static bool HandleRecover(CliExecutionContext context, IReadOnlyList<string> parts)
{
    if (parts.Count < 3)
    {
        throw new ArgumentException("Usage: recover <goal-prefix> <note>");
    }

    var policy = ResolveCliAutonomyPolicy(parts);
    context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
    var goal = context.CurrentGoal;
    var note = parts[2];
    EnsurePolicyAllows(context, goal, policy, AutonomyAction.Retry, "recover");

    var actions = 0;
    foreach (var request in context.Kernel.GetPendingHumanInput(goal.Id).ToList())
    {
        context.Kernel.SubmitHumanInput(request.Id, note);
        Console.WriteLine($"recover: answered human-input request {request.Id.Value[..8]}.");
        actions++;
    }

    foreach (var task in goal.Tasks)
    {
        if (task.Status is WorkTaskStatus.Completed or WorkTaskStatus.Cancelled ||
            task.LastProcess is { IsRunning: true })
        {
            continue;
        }

        var stuck = task.Status is WorkTaskStatus.Failed or WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman ||
            task.LastVerification is { Succeeded: false } ||
            task.SubscriptionRetryAfter is not null;
        if (!stuck)
        {
            continue;
        }

        // RetryTask refuses Running/WaitingForHuman; normalize to Failed first (the dance's middle step).
        if (task.Status is WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman)
        {
            context.Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, note);
        }

        context.Kernel.RetryTask(goal.Id, task.Id, note);
        Console.WriteLine($"recover: reset task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} to dispatchable.");
        actions++;
    }

    if (actions == 0)
    {
        Console.WriteLine("recover: nothing to recover (no pending input or stuck tasks).");
    }

    ConsoleViews.PrintGoal(goal);
    return actions > 0;
}

// Deterministic verification from git ground truth: when a goal still has un-verified work tasks
// but the goal branch carries committed changes against main on a CLEAN worktree, record the
// verification from that evidence instead of requiring a manual `verify-manual`. The acceptance
// suite + evidence bundle remain the authoritative substance gates downstream (a failed suite or
// a generated/forbidden/no-relevant-change diff still blocks the merge), so this only removes the
// bookkeeping step, never the safety gate.
private static void AutoVerifyFromGitEvidence(CliExecutionContext context, Goal goal)
{
    if (goal.Status == GoalStatus.Completed)
    {
        return;
    }

    var executionDirectory = context.Workspace.ExecutionDirectory;
    var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
    if (worktree is null)
    {
        return;
    }

    // Tasks that failed only because a Low-IL worker could not self-commit under OS confinement
    // (a benign signature — the edits are real, not broken) are eligible for the same git-ground-truth
    // verification as un-run tasks. If their edits still sit uncommitted in the worktree, commit them
    // at Medium so the clean-worktree check below confirms real changes against main. The acceptance
    // suite always runs before any merge, so this only clears bookkeeping — it never lands unproven work.
    var sandboxBlockedIds = goal.Tasks
        .Where(t => t.Status == WorkTaskStatus.Failed &&
            t.LastVerification is { Succeeded: false } verification &&
            DispatchFailureClassifier.IsSandboxCommitBlockedFailure(verification))
        .Select(t => t.Id)
        .ToHashSet();

    if (sandboxBlockedIds.Count > 0 && !GoalWorktrees.IsWorktreeClean(executionDirectory, goal.Id))
    {
        TryCommitSandboxBlockedEdits(worktree, goal);
    }

    var pending = goal.Tasks
        .Where(t => t.Status is WorkTaskStatus.Assigned or WorkTaskStatus.Running ||
            sandboxBlockedIds.Contains(t.Id))
        .ToList();
    if (pending.Count == 0)
    {
        return;
    }

    if (!GoalWorktrees.IsWorktreeClean(executionDirectory, goal.Id) ||
        !GoalWorktrees.HasChangesAgainstMain(executionDirectory, goal.Id))
    {
        return;
    }

    var note =
        $"Auto-verified from git ground truth: committed changes on {GoalWorktrees.BranchName(goal.Id)} " +
        "against main on a clean worktree. The acceptance suite and evidence bundle are the authoritative gates.";
    foreach (var task in pending)
    {
        context.Kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, note, worktree, DateTimeOffset.UtcNow));
        Console.WriteLine($"Auto-verified task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} from git ground truth.");
    }
}

// Commit a sandbox-blocked worker's uncommitted edits on the orchestrator's behalf (Medium
// integrity, so .git is writable). Plain `add -A` honours the worktree's .mcg-sandbox exclude.
private static void TryCommitSandboxBlockedEdits(string worktreePath, Goal goal)
{
    if (!GitCli.Run(worktreePath, "add", "-A").Succeeded)
    {
        return;
    }

    var commit = GitCli.Run(
        worktreePath,
        "commit",
        "-m",
        $"Orchestrator-committed sandbox-blocked worker edits for goal {goal.Id.Value}");
    if (commit.Succeeded)
    {
        Console.WriteLine(
            $"Committed sandbox-blocked worker edits for goal {goal.Id.Value[..8]} (worker could not self-commit under the sandbox).");
    }
}

// Deterministic chorekeeping: dispatch owns workspace creation so the operator never hand-runs
// `workspace create` before dispatching. Idempotent — a no-op when the worktree already exists.
// Without this, ResolveExecutionDirectory silently falls back to the repo root and a dispatch
// would prepare context artifacts into the main checkout.
private static void EnsureGoalWorkspaceForDispatch(CliExecutionContext context, Goal goal)
{
    if (GoalWorktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is not null)
    {
        return;
    }

    // Deterministic creation needs a git work tree to host the worktree; outside a git repo,
    // fall back to the existing execution-directory resolution rather than failing the dispatch.
    if (!GoalWorktrees.IsGitWorkTree(context.Workspace.ExecutionDirectory))
    {
        return;
    }

    var branch = GoalWorktrees.BranchName(goal.Id);
    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "workspace:create", $"branch {branch}");
    var path = GoalWorktrees.Ensure(context.Workspace.ExecutionDirectory, goal.Id);
    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "workspace:create", path);
    Console.WriteLine($"Workspace auto-created: {path} (branch {branch})");
}

// Deterministic post-merge chorekeeping: the acceptance path owns workspace cleanup so the
// operator never hand-runs `workspace remove` after a successful merge. Gated by autonomy
// policy and journaled. A policy block or --keep-workspace leaves the workspace in place and
// is NOT treated as a failure (the merge already succeeded); an incomplete removal (e.g. a
// lock holder) prints a resume hint rather than throwing.
private static void CleanupGoalWorkspaceAfterMerge(
    CliExecutionContext context, Goal goal, AutonomyPolicy policy, bool keepWorkspace)
{
    var goalPrefix = goal.Id.Value[..8];
    if (keepWorkspace)
    {
        Console.WriteLine($"Workspace kept (--keep-workspace). Remove later with: workspace remove {goalPrefix}");
        return;
    }

    if (!TryEnsurePolicyAllows(context, goal, policy, AutonomyAction.WorkspaceCleanup, "workspace cleanup", out _))
    {
        Console.WriteLine(
            $"Workspace cleanup skipped by policy. Remove with: workspace remove {goalPrefix} --autonomy {AutonomyPolicy.SupervisedAuto.Name}");
        return;
    }

    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "workspace:remove", "Acceptance removing goal workspace.");
    GoalWorktreeRemoveResult removeResult;
    try
    {
        removeResult = GoalWorktrees.Remove(context.Workspace.ExecutionDirectory, goal.Id);
    }
    catch (InvalidOperationException ex)
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", ex.Message);
        Console.WriteLine($"Workspace cleanup failed: {ex.Message}. Resume with: workspace remove {goalPrefix}");
        return;
    }

    PrintWorkspaceRemoveResult(removeResult);
    if (removeResult.IsComplete)
    {
        GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
    }
    else
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
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
