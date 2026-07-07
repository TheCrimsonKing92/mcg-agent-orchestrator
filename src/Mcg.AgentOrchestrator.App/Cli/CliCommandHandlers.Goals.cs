using System.Text.Json;
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
internal const int GoalMarkLandedPromptTimeoutMilliseconds = 10_000;

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

private static GoalObjectivePlan BuildGoalObjectivePlan(CliExecutionContext context, string objective, bool simple) =>
    GoalObjectivePlanner.Build(objective, simple, context.Kernel.BuildTaskDurationStats());

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
                var runObjectivePlan = BuildGoalObjectivePlan(context, runObjective, simple: false);
                GoalObjectivePlanner.ThrowIfBlocked(runObjectivePlan);
                ConsoleViews.PrintGoalObjectivePlan(runObjectivePlan);
                var runAgents = ApplyRoleAgentOverrides(parts, context.Agents);
                context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, runAgents, runObjective, context.Workspace, context.Providers, context.EventWriter);
                ConsoleViews.PrintGoal(context.CurrentGoal);
                var runParts = new List<string> { "run-goal", context.CurrentGoal.Id.Value[..8] };
                AppendGoalAliasFlags(parts, runParts, includeRoleAgentFlags: false, "--run", "--brief-file");
                return TryExecuteGoalCommand("run-goal", runParts, context);
            }
            var goalObjective = ResolveBriefObjective(parts, "goal <objective> [--simple] [--from-backlog] [--run] | goal --brief-file <path>");
            var goalObjectivePlan = BuildGoalObjectivePlan(context, goalObjective, simple: false);
            GoalObjectivePlanner.ThrowIfBlocked(goalObjectivePlan);
            ConsoleViews.PrintGoalObjectivePlan(goalObjectivePlan);
            var goalAgents = ApplyRoleAgentOverrides(parts, context.Agents);
            context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, goalAgents, goalObjective, context.Workspace, context.Providers, context.EventWriter);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "simple-goal":
            var simpleObjective = ResolveBriefObjective(parts, "simple-goal <objective> | simple-goal --brief-file <path>");
            var simpleObjectivePlan = BuildGoalObjectivePlan(context, simpleObjective, simple: true);
            GoalObjectivePlanner.ThrowIfBlocked(simpleObjectivePlan);
            ConsoleViews.PrintGoalObjectivePlan(simpleObjectivePlan);
            var simpleAgents = ApplyRoleAgentOverrides(parts, context.Agents);
            context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, simpleAgents, simpleObjective, context.Workspace, context.Providers, context.EventWriter);
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

        case "goal-mark-landed":
        {
            var landedPrefix = GetOptionalArgument(parts, "--confirm-goal-mark-landed", "--force");
            if (landedPrefix is null)
                throw new ArgumentException(
                    "Usage: goal-mark-landed <goal-prefix> --confirm-goal-mark-landed [--force]");
            if (!HasCliConfirmation(parts, "--confirm-goal-mark-landed"))
                throw new InvalidOperationException(
                    $"goal-mark-landed requires --confirm-goal-mark-landed to prevent accidental finalization. " +
                    $"Re-run with --confirm-goal-mark-landed after verifying that goal {landedPrefix} was merged to main out-of-band.");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, landedPrefix);
            var landedGoal = context.CurrentGoal;
            var landedId = landedGoal.Id;
            var landedGp = landedId.Value[..8];
            if (landedGoal.Status is not (GoalStatus.Verified or GoalStatus.Completed))
                throw new InvalidOperationException(
                    $"goal-mark-landed is only valid for Verified or Completed goals; goal {landedGp} is {landedGoal.Status}. " +
                    "Active or InProgress goals self-heal via the conductor; only verified force-landed goals need this command.");
            var landedDir = context.Workspace.ExecutionDirectory;
            var landedBranch = context.Worktrees.BranchName(landedId);
            var cleanupDeadline = GoalMarkLandedCleanupDeadline.Start(
                GoalMarkLandedPromptTimeoutMilliseconds,
                context.GoalMarkLandedElapsedMilliseconds);
            var forceCleanup = HasCliConfirmation(parts, "--force");
            if (!forceCleanup)
            {
                var localBranch = RunGoalMarkLandedStep(
                    cleanupDeadline,
                    "branch-local-check",
                    timeout => GitCli.Run(landedDir, timeout, "rev-parse", "--verify", "--quiet", $"refs/heads/{landedBranch}"));
                var localExists = localBranch.ExitCode == 0;
                var remoteExists = !localExists &&
                    RunGoalMarkLandedStep(
                        cleanupDeadline,
                        "branch-remote-check",
                        timeout => GitCli.Run(landedDir, timeout, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{landedBranch}")).ExitCode == 0;
                if (!localExists && !remoteExists)
                    throw new InvalidOperationException(
                        $"goal-mark-landed: branch '{landedBranch}' was not found locally or remotely; ancestry check cannot run. " +
                        "Use --force if you have manually confirmed the work is in main.");
                var branchRef = localExists ? landedBranch : $"origin/{landedBranch}";
                var ancestry = RunGoalMarkLandedStep(
                    cleanupDeadline,
                    "branch-ancestry-check",
                    timeout => GitCli.Run(landedDir, timeout, "merge-base", "--is-ancestor", branchRef, "HEAD"));
                if (ancestry.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"goal-mark-landed: branch '{landedBranch}' is not an ancestor of the current HEAD; " +
                        "verify the work was merged into main before using this command. Use --force to bypass this check.");
            }
            GoalOperationJournal.Begin(landedDir, landedGoal, "conductor:land", "Out-of-band landing recorded via goal-mark-landed.");
            GoalOperationJournal.Completed(landedDir, landedGoal, "conductor:land", $"Goal {landedGp} was already merged to main.");
            GoalOperationJournal.Begin(landedDir, landedGoal, "conductor:record", "Recording out-of-band landing to SQLite dogfood log.");
            RecordDogfoodEntry(context.Workspace, landedGoal);
            GoalOperationJournal.Completed(landedDir, landedGoal, "conductor:record", context.Workspace.DogfoodLogStorePath);

            var hadWorktree = context.Worktrees.TryResolve(landedDir, landedId) is not null;
            var cleanupComplete = true;
            var branchDeleted = true;
            var appHostLockReleased = true;

            if (!TryRunGoalMarkLandedBestEffortStep(
                    cleanupDeadline,
                    "worktree-and-branch-cleanup",
                    timeout => context.Worktrees.Remove(
                        landedDir,
                        landedId,
                        context.Kernel,
                        timeout),
                    out var removeResult,
                    out var removeFailure))
            {
                cleanupComplete = false;
                RecordDeferredGoalCleanup(landedDir, landedId, "remove:cleanup-budget-exhausted");
                Console.WriteLine($"cleanup deferred: {removeFailure}");
            }
            if (removeResult is { IsComplete: false })
            {
                cleanupComplete = false;
                PrintWorkspaceRemoveResult(removeResult);
            }

            if (!TryRunGoalMarkLandedBestEffortStep(
                    cleanupDeadline,
                    "branch-cleanup-check",
                    timeout => GitCli.Run(landedDir, timeout, "rev-parse", "--verify", "--quiet", $"refs/heads/{landedBranch}"),
                    out var branchAfterCleanup,
                    out var branchCheckFailure))
            {
                cleanupComplete = false;
                branchDeleted = false;
                RecordDeferredGoalCleanup(landedDir, landedId, "remove:branch-delete-check-failed");
                Console.WriteLine($"cleanup deferred: {branchCheckFailure}");
            }
            else if (branchAfterCleanup.ExitCode == 0)
            {
                branchDeleted = false;
                GitCli.GitResult? forcedBranchRemoval = null;
                string? branchDeleteFailure = null;
                if (forceCleanup &&
                    TryRunGoalMarkLandedBestEffortStep(
                        cleanupDeadline,
                        "branch-force-delete",
                        timeout => GitCli.Run(landedDir, timeout, "branch", "-D", landedBranch),
                        out forcedBranchRemoval,
                        out branchDeleteFailure) &&
                    forcedBranchRemoval is { ExitCode: 0 } &&
                    TryRunGoalMarkLandedBestEffortStep(
                        cleanupDeadline,
                        "branch-force-delete-check",
                        timeout => GitCli.Run(landedDir, timeout, "rev-parse", "--verify", "--quiet", $"refs/heads/{landedBranch}"),
                        out branchAfterCleanup,
                        out branchCheckFailure) &&
                    branchAfterCleanup.ExitCode != 0)
                {
                    branchDeleted = true;
                    if (removeResult is { IsComplete: false })
                    {
                        var reconciled = TryRunGoalMarkLandedBestEffortStep(
                            cleanupDeadline,
                            "post-branch-cleanup-reconcile",
                            timeout => context.Worktrees.Remove(
                                landedDir,
                                landedId,
                                context.Kernel,
                                timeout),
                            out var reconciledCleanup,
                            out var reconcileFailure);
                        cleanupComplete = reconciled && reconciledCleanup is { IsComplete: true };
                        if (!cleanupComplete)
                        {
                            var detail = reconcileFailure ?? reconciledCleanup?.Message ?? "post-branch cleanup reconciliation failed";
                            Console.WriteLine($"cleanup deferred: {detail}");
                        }
                    }
                }
                else
                {
                    cleanupComplete = false;
                    RecordDeferredGoalCleanup(landedDir, landedId, "remove:branch-delete-failed");
                    var detail = forceCleanup
                        ? forcedBranchRemoval is null
                            ? branchDeleteFailure ?? branchCheckFailure ?? "branch still exists"
                            : forcedBranchRemoval.Value.Error
                        : "retry with --force only after manually confirming ancestry";
                    Console.WriteLine($"cleanup deferred: branch '{landedBranch}' still exists; {detail}");
                }
            }

            if (!TryRunGoalMarkLandedBestEffortStep(
                    cleanupDeadline,
                    "app-host-lock-inspect",
                    _ => DotnetBuildEnvironmentManager.InspectGoalLease(landedId),
                    out var leaseStatus,
                    out var leaseInspectFailure))
            {
                cleanupComplete = false;
                appHostLockReleased = false;
                RecordDeferredGoalCleanup(landedDir, landedId, "remove:app-host-lock-inspect-failed");
                Console.WriteLine($"cleanup deferred: {leaseInspectFailure}");
            }
            else if (leaseStatus is null)
            {
                cleanupComplete = false;
                appHostLockReleased = false;
                RecordDeferredGoalCleanup(landedDir, landedId, "remove:app-host-lock-inspect-failed");
                Console.WriteLine("cleanup deferred: app-host lock status was unavailable");
            }
            else if (leaseStatus.OwnerProcessAlive)
            {
                cleanupComplete = false;
                appHostLockReleased = false;
                RecordDeferredGoalCleanup(landedDir, landedId, "remove:app-host-lock-active");
                Console.WriteLine($"cleanup deferred: app-host lock {leaseStatus.LeaseId} is active; owner pid={leaseStatus.OwnerProcessId}");
            }
            else if (leaseStatus.CanCleanup)
            {
                if (!TryRunGoalMarkLandedBestEffortStep(
                        cleanupDeadline,
                        "app-host-lock-release",
                        _ => DotnetBuildEnvironmentManager.TryCleanupOrphanedGoalLease(
                            landedId,
                            out DotnetBuildLeaseStatus _,
                            out string _),
                        out var leaseCleaned,
                        out var leaseReleaseFailure))
                {
                    cleanupComplete = false;
                    appHostLockReleased = false;
                    RecordDeferredGoalCleanup(landedDir, landedId, "remove:app-host-lock-release-failed");
                    Console.WriteLine($"cleanup deferred: {leaseReleaseFailure}");
                }
                else if (!leaseCleaned)
                {
                    cleanupComplete = false;
                    appHostLockReleased = false;
                    RecordDeferredGoalCleanup(landedDir, landedId, "remove:app-host-lock-release-failed");
                    Console.WriteLine($"cleanup deferred: app-host lock {leaseStatus.LeaseId} was not released");
                }
            }

            GoalOperationJournal.Begin(landedDir, landedGoal, "conductor:cleanup", "Removing goal workspace after out-of-band landing.");
            if (cleanupComplete)
            {
                GoalOperationJournal.Completed(landedDir, landedGoal, "conductor:cleanup", $"Goal {landedGp} marked as landed out-of-band; workspace removed.");
                if (landedGoal.Status == GoalStatus.Verified)
                    context.Kernel.CompleteGoal(landedId, "Goal marked landed after durable out-of-band landing, recording, and cleanup evidence.");
                context.EventWriter.AppendCleanedUp(landedId);
            }
            else
            {
                var cleanupBackoff = GoalWorktrees.TryGetCleanupBackoff(landedDir, landedId);
                var detail = cleanupBackoff is null
                    ? "cleanup-needed"
                    : GoalWorktrees.FormatCleanupBackoff(cleanupBackoff);
                GoalOperationJournal.Failed(landedDir, landedGoal, "conductor:cleanup", $"Deferred cleanup after landing: {detail}");
            }

            PrintGoalMarkLandedSummary(hadWorktree, branchDeleted, appHostLockReleased, cleanupComplete);
            return true;
        }

        case "acceptance-repair":
        {
            var repairPrefix = GetOptionalArgument(parts, "--confirm-acceptance-repair");
            if (repairPrefix is null)
                throw new ArgumentException("Usage: acceptance-repair <goal-prefix> --confirm-acceptance-repair");
            if (!HasCliConfirmation(parts, "--confirm-acceptance-repair"))
                throw new InvalidOperationException(
                    "acceptance-repair only clears stale acceptance failure after merge and cleanup evidence is present. " +
                    "Re-run with --confirm-acceptance-repair after inspecting the goal journal.");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, repairPrefix);
            var repairGoal = context.CurrentGoal;
            if (!TryReconcileLandedCleanedAcceptance(context, repairGoal, "acceptance-repair", out var repairDetail))
            {
                throw new InvalidOperationException(repairDetail);
            }

            Console.WriteLine(repairDetail);
            return true;
        }

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
            if (parts.Count > 1 && parts[1].Equals("subscribe", StringComparison.OrdinalIgnoreCase))
            {
                GoalMonitoringSubscriptionCommand.RunAsync(
                    parts,
                    Console.Out,
                    context.Kernel,
                    context.Workspace,
                    context.Agents,
                    context.WorkerProfiles,
                    context.ReloadKernel).GetAwaiter().GetResult();
                return false;
            }

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
            var previousRoleAgent = context.Agents.FirstOrDefault(existing => existing.Role == agent.Role);
            context.Agents = new AgentCatalog(context.Agents).UpsertRole(agent).Agents;
            AgentCatalogStore.Save(context.AgentCatalogPath, new AgentCatalog(context.Agents));
            WarnAboutTasksPinnedToRemovedAgent(context.Kernel, previousRoleAgent, agent);
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
            ConsoleViews.PrintGoal(
                context.CurrentGoal,
                ResolveGoalFriendlyLabel(context.CurrentGoal, context.Workspace.BacklogStorePath),
                ResolveGoalStatusText(context.Workspace, context.CurrentGoal));
            PrintGoalCleanupBackoffStatus(context.Workspace.ExecutionDirectory, context.CurrentGoal.Id);
            return false;

        case "monitor":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintMonitor(
                context.Kernel.BuildMonitor(context.CurrentGoal.Id),
                ResolveGoalStatusText(context.Workspace, context.CurrentGoal));
            return false;

        case "readiness":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            var readinessSweep = TerminalGoalSweep.Run(context.Kernel, context.Workspace.ExecutionDirectory, context.CurrentGoal.Id);
            ConsoleViews.PrintTerminalGoalSweep(readinessSweep);
            context.CurrentGoal = context.Kernel.GetGoal(context.CurrentGoal.Id);
            ConsoleViews.PrintGoalReadinessPreflight(GoalReadinessPreflight.Build(
                context.CurrentGoal,
                context.Agents,
                context.Workspace.ExecutionDirectory,
                context.WorkerProfiles,
                context.Worktrees.TryResolve));
            return readinessSweep.Changed;

        case "goal-recovery":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            var recoverySweep = TerminalGoalSweep.Run(context.Kernel, context.Workspace.ExecutionDirectory, context.CurrentGoal.Id);
            ConsoleViews.PrintTerminalGoalSweep(recoverySweep);
            context.CurrentGoal = context.Kernel.GetGoal(context.CurrentGoal.Id);
            ConsoleViews.PrintGoalRecoveryReport(GoalRecoveryPlanner.Build(context.Kernel, context.CurrentGoal, context.Workspace.ExecutionDirectory));
            return recoverySweep.Changed;

        case "dogfood-eval":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintHistoricalDogfoodEvaluation(HistoricalDogfoodEvaluationHarness.Evaluate(
                context.Kernel,
                context.CurrentGoal,
                context.Agents,
                context.WorkerProfiles,
                context.Workspace));
            return false;

        case "dogfood-log":
            HandleDogfoodLog(context, parts);
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
            if (RunAcceptanceWorkspaceMerge(context, skipVerify))
            {
                if (!noRecord)
                {
                    AutoRecordDogfoodEntry(context);
                }

                GoalLandingPostActions.AutoCloseSourceBacklogItem(context.CurrentGoal, context.Workspace.BacklogStorePath, Console.WriteLine);
                CleanupGoalWorkspaceAfterMerge(context, context.CurrentGoal, acceptancePolicy, keepWorkspace);
            }

            ConsoleViews.PrintAcceptanceSummary(context.CurrentGoal, context.Kernel.BuildGoalAcceptanceSummary(context.CurrentGoal.Id));
            return false;

        case "workspace":
            HandleWorkspaceCommand(context, parts);
            return false;

        case "evidence":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintEvidenceSummary(context.CurrentGoal, context.Kernel.BuildGoalEvidenceSummary(context.CurrentGoal.Id));
            return false;

        case "goal-changes":
        {
            var changesGoalPrefix = GetOptionalArgument(parts, "--role", "--task", "--committed", "--working", "--all", "--flat", "--json");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, changesGoalPrefix);
            var changesRoleFilter = GetFlagValue(parts, "--role");
            var changesTaskFilter = GetFlagValue(parts, "--task");
            var changesCommitted = HasCliConfirmation(parts, "--committed");
            var changesWorking = HasCliConfirmation(parts, "--working");
            var changesFlat = HasCliConfirmation(parts, "--flat");
            var changesJson = HasCliConfirmation(parts, "--json");
            if (!changesCommitted && !changesWorking) { changesCommitted = true; changesWorking = true; }
            var changesWorktree = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, context.CurrentGoal.Id);
            var changesReport = GoalChangesReader.Build(context.CurrentGoal, changesWorktree, changesCommitted, changesWorking, changesRoleFilter, changesTaskFilter);
            if (changesJson)
                ConsoleViews.PrintGoalChangesJson(changesReport);
            else if (changesFlat)
                ConsoleViews.PrintGoalChangesFlat(changesReport);
            else
                ConsoleViews.PrintGoalChanges(changesReport);
            return false;
        }

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

        case "goal-diagnostics":
        {
            var diagnosticsGoalPrefix = GetOptionalArgument(parts);
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, diagnosticsGoalPrefix);
            PrintBoundedGoalDiagnostics(context);
            return false;
        }

        case "next":
        {
            var isFull = HasCliConfirmation(parts, "--full");
            var nextGoalPrefix = GetOptionalArgument(parts, "--full");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, nextGoalPrefix);
            if (isFull)
            {
                PrintBoundedGoalDiagnostics(context);
                return false;
            }

            var nextSweep = TerminalGoalSweep.Run(context.Kernel, context.Workspace.ExecutionDirectory, context.CurrentGoal.Id);
            ConsoleViews.PrintTerminalGoalSweep(nextSweep);
            context.CurrentGoal = context.Kernel.GetGoal(context.CurrentGoal.Id);
            var nextPolicy = ResolveCliAutonomyPolicy(parts);
            var nextHealth = GoalHealthEvaluator.Build(
                context.Kernel,
                context.CurrentGoal,
                context.Agents,
                context.WorkerProfiles,
                context.Workspace.ExecutionDirectory,
                nextPolicy);
            var conductorDisposition = ConductorOperatorDispositionSnapshots.TryReadLatestForGoal(context.Workspace.RunEventStorePath, context.CurrentGoal);
            ConsoleViews.PrintNextActions(context.CurrentGoal, context.Kernel.BuildNextActions(context.CurrentGoal.Id), context.Agents, nextHealth, conductorDisposition);
            return nextSweep.Changed;
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
            var runGoalResult = RunGoal(context, context.CurrentGoal, HasCliConfirmation(parts, SubscriptionPromptCostGuard.CliConfirmationFlag));
            ConsoleViews.PrintRunGoalResult(context.CurrentGoal, runGoalResult);
            return runGoalResult.Executed;

        case "delegate":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            GoalRefinementGate.EnsureRefined(context.Kernel, context.Workspace, context.Providers, context.CurrentGoal, eventWriter: context.EventWriter);
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
            if (IsHelpRequested(parts))
            {
                CliCommandHelp.TryPrintStartupHelp(parts);
                return false;
            }

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
                    var seconds = ResolveConductPollSeconds(parts);
                    watchInterval = TimeSpan.FromSeconds(seconds);
                    Console.WriteLine($"[conduct --loop --watch] Watch mode active; will sleep {seconds}s between ticks when all goals are held.");
                }

                // --max-duration N: stop after N seconds of wall-clock time (independent of --max-iterations).
                TimeSpan? maxDuration = null;
                if (GetFlagValue(parts, "--max-duration") is { } mdStr)
                    maxDuration = TimeSpan.FromSeconds(int.Parse(mdStr, System.Globalization.CultureInfo.InvariantCulture));
                var quietWatchProgress = HasCliConfirmation(parts, "--quiet");
                var stallWarningThreshold = ResolveWatchStallWarningThreshold(parts);

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

                Action<BatchTickSummary>? onTick = ConductorTickPusher.CreateStoreCallback(context.Workspace.RunEventStorePath);
                var dashboardUrl = GetFlagValue(parts, "--dashboard-url")
                    ?? ConductorTickPusher.TryReadDashboardUrl(context.Workspace.DashboardUrlFilePath);
                if (dashboardUrl is not null)
                {
                    Console.WriteLine($"[conduct --loop] Dashboard compatibility push enabled: {dashboardUrl}");
                    var storeTick = onTick;
                    var httpTick = ConductorTickPusher.CreateCallback(dashboardUrl);
                    onTick = tick =>
                    {
                        storeTick(tick);
                        httpTick(tick);
                    };
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
                    // Refresh tracked goals from persisted state before every tick, then ingest newly
                    // submitted goals. This keeps role handoff decisions tied to durable task status
                    // instead of stale loop-local objects.
                    try
                    {
                        var snapshot = context.ReloadKernel().ExportSnapshot();
                        loopKernel.RefreshTrackedGoals(snapshot);
                        loopKernel.IngestNewGoals(snapshot);
                    }
                    catch { /* dynamic pickup is best-effort */ }

                    foreach (var resolved in loopKernel.SweepStaleHumanWaits(TimeSpan.FromHours(24)))
                    {
                        Console.WriteLine($"[conduct --loop] Resolved stale human wait {resolved.RequestId.Value[..8]} ({resolved.Kind}) via {resolved.Resolution}.");
                    }

                    foreach (var loopGoal in loopKernel.Goals.ToArray())
                    {
                        try { GoalManagementCommandService.RefreshDispatches(loopKernel, loopGoal); }
                        catch { /* per-goal isolation */ }
                    }

                    var terminalSweep = TerminalGoalSweep.Run(loopKernel, context.Workspace.ExecutionDirectory);
                    ConsoleViews.PrintTerminalGoalSweep(terminalSweep);
                    GoalWorktreeOrphanSweepScheduler.SweepIfDue(context.Workspace.ExecutionDirectory, loopKernel);
                };
                var loopReaper = new BackgroundDispatchRunner();
                using var loopWakeSignal = watchInterval is not null
                    ? new FileSystemWatcherConductorWakeSignal(context.Workspace.LogDirectory)
                    : null;
                var loopSummary = new ConductorBatchLoop(
                    reconcileSweep,
                    (loopKernel, loopGoal) => loopReaper.CancelRunningProcessesForGoal(loopKernel, loopGoal.Id),
                    (loopKernel, loopGoal) => loopReaper.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id),
                    loopKernel => loopReaper.RequeueInterruptedDispatches(loopKernel),
                    (loopKernel, loopGoal) => { GoalManagementCommandService.RefreshDispatches(loopKernel, loopGoal); }).Run(
                    context.Kernel, loopDriver, loopPolicy, stopFilePath, loopMaxIter,
                    watchInterval: watchInterval, onTick: onTick, wakeSignal: loopWakeSignal, maxDuration: maxDuration,
                    persistTick: context.PersistCheckpoint, keepAliveWhenIdle: loopDaemon,
                    persistGoalTick: context.PersistGoalCheckpoint,
                    buildOperatorDispositions: loopKernel => ConductorOperatorDispositionSnapshots.Build(loopKernel, context.Workspace.ExecutionDirectory),
                    quiet: quietWatchProgress,
                    stallWarningThreshold: stallWarningThreshold);
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
                var watchPollSeconds = ResolveConductPollSeconds(parts);
                TimeSpan? watchMax = int.TryParse(GetFlagValue(parts, "--max-duration"), out var wmd)
                    ? TimeSpan.FromSeconds(wmd) : null;
                var watchReaper = new BackgroundDispatchRunner();
                Action<AgentOrchestratorKernel> watchSweep = wk =>
                {
                    foreach (var resolved in wk.SweepStaleHumanWaits(TimeSpan.FromHours(24)))
                    {
                        Console.WriteLine($"[conduct --watch] Resolved stale human wait {resolved.RequestId.Value[..8]} ({resolved.Kind}) via {resolved.Resolution}.");
                    }

                    watchReaper.SweepExitedProcesses(wk, context.CurrentGoal.Id);
                    var g = wk.Goals.FirstOrDefault(x => x.Id.Value == watchGoalId);
                    if (g is not null) { try { GoalManagementCommandService.RefreshDispatches(wk, g); } catch { } }
                };
                var watchStopPath = Path.Combine(context.Workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName);
                Console.WriteLine($"[conduct --watch] Driving goal {watchGoalId[..8]} [{conductPolicy.Name}] continuously; poll {watchPollSeconds}s; stop via {ConductorBatchLoop.StopFileName}.");
                using var watchWakeSignal = new FileSystemWatcherConductorWakeSignal(context.Workspace.LogDirectory);
                var watchSummary = new ConductorBatchLoop(
                    watchSweep,
                    (wk, goal) => watchReaper.CancelRunningProcessesForGoal(wk, goal.Id),
                    (wk, goal) => watchReaper.DetachRunningProcessesForGoal(wk, goal.Id),
                    wk => watchReaper.RequeueInterruptedDispatches(wk),
                    (wk, goal) => { GoalManagementCommandService.RefreshDispatches(wk, goal); }).Run(
                    context.Kernel, conductDriver, conductPolicy, watchStopPath,
                    watchInterval: TimeSpan.FromSeconds(watchPollSeconds),
                    onTick: ConductorTickPusher.CreateStoreCallback(context.Workspace.RunEventStorePath),
                    wakeSignal: watchWakeSignal,
                    maxDuration: watchMax,
                    onlyGoalId: watchGoalId, persistTick: context.PersistCheckpoint,
                    persistGoalTick: context.PersistGoalCheckpoint,
                    buildOperatorDispositions: wk => ConductorOperatorDispositionSnapshots.Build(wk, context.Workspace.ExecutionDirectory));
                Console.WriteLine($"Conduct --watch complete: ticks={watchSummary.Ticks} advanced={watchSummary.Advanced} held={watchSummary.Held} escalated={watchSummary.Escalated}{(watchSummary.StopRequested ? " (stopped)" : "")}");
                return watchSummary.Escalated == 0;
            }
            if (HasCliConfirmation(parts, "--poll-seconds") || HasCliConfirmation(parts, "--watch-interval"))
            {
                throw new ArgumentException("--poll-seconds requires --watch.");
            }

            var scopedReaper = new BackgroundDispatchRunner();
            var scopedReconciled = scopedReaper.SweepExitedProcesses(context.Kernel, context.CurrentGoal.Id);
            var refreshedGoal = context.Kernel.GetGoal(context.CurrentGoal.Id);
            try
            {
                GoalManagementCommandService.RefreshDispatches(context.Kernel, refreshedGoal);
                refreshedGoal = context.Kernel.GetGoal(refreshedGoal.Id);
            }
            catch
            {
            }

            context.CurrentGoal = refreshedGoal;
            if (scopedReconciled > 0)
            {
                Console.WriteLine($"[conduct] Reconciled {scopedReconciled} exited dispatch(es) for goal {context.CurrentGoal.Id.Value[..8]}.");
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

// Deterministic recording: acceptance stores a dogfood entry rendered from the goal's
// receipts, so a landed goal is journaled without the operator hand-writing prose. --no-record opts out.
private static void AutoRecordDogfoodEntry(CliExecutionContext context)
{
    var goal = context.CurrentGoal!;
    RecordDogfoodEntry(context.Workspace, goal);
    Console.WriteLine($"Recorded dogfood-log entry for goal {goal.Id.Value[..8]} to {context.Workspace.DogfoodLogStorePath}.");
}

private static DogfoodLogRecord RecordDogfoodEntry(OrchestratorWorkspace workspace, Goal goal)
{
    var entry = DogfoodLogRenderer.Render(goal);
    return new DogfoodLogStore(workspace.DogfoodLogStorePath)
        .UpsertAsync(new DogfoodLogAppend(
            goal.Id.Value,
            entry.Header,
            entry.Summary,
            entry.OperatorGate,
            entry.ModelFit,
            entry.Render()))
        .GetAwaiter()
        .GetResult();
}

private static void HandleRecordGoal(CliExecutionContext context)
{
    var goal = context.CurrentGoal!;
    var record = RecordDogfoodEntry(context.Workspace, goal);
    Console.Write(record.RenderedMarkdown);
    Console.WriteLine();
    Console.WriteLine($"Recorded to {context.Workspace.DogfoodLogStorePath}");
}

private static void HandleDogfoodLog(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var subcommand = parts.Count > 1 && !parts[1].StartsWith("--", StringComparison.Ordinal)
        ? parts[1]
        : "list";

    switch (subcommand.ToLowerInvariant())
    {
        case "list":
        {
            var limit = GetFlagValue(parts, "--limit") is { } value
                ? ParsePositiveInteger(value, "--limit")
                : 20;
            var records = new DogfoodLogStore(context.Workspace.DogfoodLogStorePath)
                .ListRecentAsync(limit)
                .GetAwaiter()
                .GetResult();
            foreach (var record in records)
            {
                Console.WriteLine(record.RenderedMarkdown);
                Console.WriteLine();
            }
            if (records.Count == 0)
            {
                Console.WriteLine($"No dogfood log entries in {context.Workspace.DogfoodLogStorePath}.");
            }
            return;
        }

        case "add":
        case "record":
        {
            var goalPrefix = parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal)
                ? parts[2]
                : null;
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, goalPrefix);
            HandleRecordGoal(context);
            return;
        }

        default:
            throw new ArgumentException(CliCommandHelp.DogfoodLogUsage);
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

    var objectivePlan = BuildGoalObjectivePlan(context, objective, simple);
    GoalObjectivePlanner.ThrowIfBlocked(objectivePlan);
    ConsoleViews.PrintGoalObjectivePlan(objectivePlan);

    var existingGoalId = GoalOperationJournal.TryFindLifecycleGoal(context.Workspace.ExecutionDirectory, commandName, objective);
    context.CurrentGoal = existingGoalId is not null &&
        context.Kernel.Goals.FirstOrDefault(goal => goal.Id == existingGoalId) is { } existingGoal
        ? existingGoal
        : simple
            ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, objective, context.Workspace, context.Providers, context.EventWriter)
            : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, objective, context.Workspace, context.Providers, context.EventWriter);
    var goal = context.CurrentGoal;
    var goalPrefix = goal.Id.Value[..8];
    Console.WriteLine($"Lifecycle goal: {goal.Id.Value}");
    if (existingGoalId is not null && existingGoalId == goal.Id)
    {
        Console.WriteLine(simple ? "Stage simple-goal: reused existing idempotent goal." : "Stage goal: reused existing idempotent goal.");
        var journal = GoalOperationJournal.Read(context.Workspace.ExecutionDirectory, goal.Id);
        if (goal.Status == GoalStatus.Completed &&
            context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is null &&
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

    var branch = context.Worktrees.BranchName(goal.Id);
    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "workspace:create", $"branch {branch}");
    var workspacePath = context.Worktrees.Ensure(context.Workspace.ExecutionDirectory, goal.Id);
    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "workspace:create", workspacePath);
    Console.WriteLine($"Stage workspace create: {workspacePath} (branch {branch})");
    EnsureGoalReadinessAllowsStart(context, goal, HasCliConfirmation(parts, "--confirm-readiness-risk"));
    EnsureLifecycleParallelGateAllowsStart(context, goal);

    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "run-goal", "Starting subscription-driven goal loop.");
    RecordPolicyAllowed(context, goal, policy, AutonomyAction.DispatchStart, commandName);
    var runGoalResult = RunGoal(context, goal, allowLargePaidSubscriptionStart: true);
    Console.WriteLine("Stage run-goal:");
    ConsoleViews.PrintRunGoalResult(goal, runGoalResult);
    if (goal.Status != GoalStatus.Verified)
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "run-goal", runGoalResult.StopReason);
        var next = BuildLifecycleRunGoalNextCommand(goalPrefix, runGoalResult);
        Console.WriteLine($"Stage run-goal: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped after run-goal. Next: {next}");
    }

    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "run-goal", "Goal reached Verified status.");
    if (!TryEnsurePolicyAllows(context, goal, policy, AutonomyAction.Acceptance, $"{commandName} acceptance", out var acceptancePolicyError))
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "acceptance", acceptancePolicyError);
        var next = $"acceptance {goalPrefix} --autonomy {AutonomyPolicy.SupervisedAuto.Name}";
        Console.WriteLine($"Stage acceptance: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped before acceptance. Next: {next}");
    }

    Console.WriteLine("Stage acceptance:");
    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "acceptance", "Running acceptance evidence and merge.");
    if (!RunAcceptanceWorkspaceMerge(context))
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance did not pass or merge was blocked.");
        var next = $"acceptance {goalPrefix}";
        Console.WriteLine($"Stage acceptance: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped after acceptance. Next: {next}");
    }

    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "acceptance", "Acceptance passed and merge completed.");
    GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, context.Workspace.BacklogStorePath, Console.WriteLine);
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
        removeResult = context.Worktrees.Remove(context.Workspace.ExecutionDirectory, goal.Id, context.Kernel);
    }
    catch (InvalidOperationException ex)
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", ex.Message);
        context.Kernel.RecordAcceptanceFailure(goal.Id, ["remove-worktree"]);
        context.EventWriter.AppendAcceptanceResult(goal.Id, false, ["remove-worktree"]);
        var next = $"workspace remove {goalPrefix}";
        Console.WriteLine($"Stage workspace remove: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped during workspace cleanup. Next: {next}", ex);
    }

    PrintWorkspaceRemoveResult(removeResult);
    if (!removeResult.IsComplete)
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
        context.Kernel.RecordAcceptanceFailure(goal.Id, ["remove-worktree"]);
        context.EventWriter.AppendAcceptanceResult(goal.Id, false, ["remove-worktree"]);
        var next = removeResult.ResumeCommand ?? $"workspace remove {goalPrefix}";
        Console.WriteLine($"Stage workspace remove: stopped. Next: {next}");
        throw new InvalidOperationException($"{commandName} stopped during workspace cleanup. Next: {next}");
    }

    GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
    context.Kernel.CompleteGoal(goal.Id, "Lifecycle command completed goal after acceptance merge and workspace cleanup evidence.");
    ReconcileLandedCleanedAcceptance(context, goal, "workspace cleanup");
    context.EventWriter.AppendCleanedUp(goal.Id);
}

private static RunGoalService.RunGoalResult RunGoal(
    CliExecutionContext context,
    Goal goal,
    bool allowLargePaidSubscriptionStart)
{
    if (context.RunGoalOverride is not null)
    {
        return context.RunGoalOverride(goal).GetAwaiter().GetResult();
    }

    return RunGoalService.RunAsync(
        context.Kernel,
        context.Agents,
        context.WorkerProfiles,
        context.Workspace,
        goal,
        allowLargePaidSubscriptionStart,
        pollInterval: context.RunGoalPollInterval,
        sleep: context.RunGoalSleep,
        providers: context.Providers)
        .GetAwaiter().GetResult();
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
            removeResult = context.Worktrees.Remove(context.Workspace.ExecutionDirectory, goal.Id, context.Kernel);
        }
        catch (InvalidOperationException ex)
        {
            GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", ex.Message);
            context.Kernel.RecordAcceptanceFailure(goal.Id, ["remove-worktree"]);
            context.EventWriter.AppendAcceptanceResult(goal.Id, false, ["remove-worktree"]);
            throw new InvalidOperationException($"acceptance-queue stopped at {goalPrefix}: workspace cleanup failed.", ex);
        }

        PrintWorkspaceRemoveResult(removeResult);
        if (!removeResult.IsComplete)
        {
            GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
            context.Kernel.RecordAcceptanceFailure(goal.Id, ["remove-worktree"]);
            context.EventWriter.AppendAcceptanceResult(goal.Id, false, ["remove-worktree"]);
            throw new InvalidOperationException($"acceptance-queue stopped at {goalPrefix}: workspace cleanup incomplete. Next: {removeResult.ResumeCommand ?? $"workspace remove {goalPrefix}"}");
        }

        GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
        context.Kernel.CompleteGoal(goal.Id, "Acceptance queue completed goal after merge and workspace cleanup evidence.");
        ReconcileLandedCleanedAcceptance(context, goal, "workspace cleanup");
        context.EventWriter.AppendCleanedUp(goal.Id);
        context.PersistCheckpoint(context.Kernel);
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
        applied.Add($"{goal.Id.Value[..8]} start-subscription-ready dispatches={result.Dispatches.Count} processes={result.Processes.Tasks.Count} readyBlocked={result.BlockedDiagnostics.Count}");
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
    var sweep = TerminalGoalSweep.Run(context.Kernel, context.Workspace.ExecutionDirectory, goal.Id);
    ConsoleViews.PrintTerminalGoalSweep(sweep);
    goal = context.Kernel.GetGoal(goal.Id);
    if (context.CurrentGoal?.Id == goal.Id)
    {
        context.CurrentGoal = goal;
    }

    var readiness = GoalReadinessPreflight.Build(
        goal,
        context.Agents,
        context.Workspace.ExecutionDirectory,
        context.WorkerProfiles,
        context.Worktrees.TryResolve);
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

private static void PrintBoundedGoalDiagnostics(CliExecutionContext context)
{
    var goal = context.CurrentGoal!;
    var prefix = goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)];
    Console.WriteLine();
    Console.WriteLine($"Goal diagnostics {prefix} {goal.Status}: {OutputTextPreview.CreateSummary(goal.Objective).Text}");
    Console.WriteLine("Mode: bounded; skipped deep readiness, subscription prompt estimation, recovery planning, supervisor planning, inbox scan, and dispatch worktree git inspection.");

    var diagnosticsSweep = TerminalGoalSweep.Diagnose(context.Kernel, context.Workspace.ExecutionDirectory, goal.Id);
    ConsoleViews.PrintTerminalGoalSweep(diagnosticsSweep, includeRepairs: false);

    var verificationSatisfied = goal.Tasks.Count > 0 && goal.Tasks.All(task => task.LastVerification?.Succeeded == true);
    var dispatchSurface = new DispatchStateSurface(inspectWorktree: false);
    var dispositionSurface = new GoalOperatorDispositionSurface(dispatchSurface: dispatchSurface);
    var disposition = dispositionSurface.Evaluate(
        goal,
        pendingHumanInputCount: context.Kernel.BuildHumanInputWorklist(goal.Id).OpenCount,
        verificationSatisfied);
    ConsoleViews.PrintOperatorDisposition(disposition);

    Console.WriteLine("Dispatches:");
    var dispatches = disposition.Dispatches
        .Where(dispatch => dispatch.DispatchState is not null || dispatch.State is not OperatorDispositionState.Idle)
        .ToList();
    if (dispatches.Count == 0)
    {
        Console.WriteLine("  none");
    }
    else
    {
        foreach (var dispatch in dispatches)
        {
            var task = goal.Tasks.First(candidate => candidate.Id == dispatch.TaskId);
            Console.WriteLine($"  Task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} {dispatch.Role} status={dispatch.TaskStatus} state={dispatch.State} command='{dispatch.NextSafeCommand}' reason='{OutputTextPreview.CreateTimeline(dispatch.Reason).Text}'");
            if (dispatch.DispatchState is { } dispatchState)
            {
                ConsoleViews.PrintDispatchState(dispatchState, "    ");
            }
        }
    }

    var actions = context.Kernel.BuildNextActions(goal.Id);
    Console.WriteLine("Next actions:");
    if (actions.Items.Count == 0)
    {
        Console.WriteLine("  none");
    }
    else
    {
        foreach (var item in actions.Items.Take(3))
        {
            Console.WriteLine($"  {item.Kind}: {OutputTextPreview.CreateTimeline(item.Message).Text}");
        }
    }

    Console.WriteLine("Deeper commands:");
    Console.WriteLine($"  readiness {prefix}");
    Console.WriteLine($"  evidence {prefix}");
    Console.WriteLine($"  stages {prefix}");
    Console.WriteLine($"  gates {prefix}");
    Console.WriteLine($"  subscription-plan {prefix}");
    Console.WriteLine($"  model-outcomes");
    Console.WriteLine($"  loop-health");
    Console.WriteLine($"  failure-triage {prefix}");
    Console.WriteLine($"  goal-recovery {prefix}");
    Console.WriteLine($"  operator-inbox {prefix}");
    Console.WriteLine();
}

private static void PrintNextFullDetail(CliExecutionContext context, AutonomyPolicy policy)
{
    var goal = context.CurrentGoal!;
    ConsoleViews.PrintGoal(goal);
    ConsoleViews.PrintMonitor(context.Kernel.BuildMonitor(goal.Id));
    ConsoleViews.PrintGoalReadinessPreflight(GoalReadinessPreflight.Build(
        goal,
        context.Agents,
        context.Workspace.ExecutionDirectory,
        context.WorkerProfiles,
        context.Worktrees.TryResolve));
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

private static void WarnAboutTasksPinnedToRemovedAgent(AgentOrchestratorKernel kernel, AgentDefinition? previousAgent, AgentDefinition replacementAgent)
{
    if (previousAgent is null || previousAgent.Id == replacementAgent.Id)
    {
        return;
    }

    var affected = kernel.Goals
        .Where(goal => goal.Status is GoalStatus.Active or GoalStatus.WaitingForHuman)
        .SelectMany(goal => goal.Tasks
            .Where(task => task.AssignedAgentId == previousAgent.Id &&
                task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))
            .Select(task => $"{goal.Id.Value[..8]}:{task.Id.Value}"))
        .ToList();
    if (affected.Count == 0)
    {
        return;
    }

    Console.Error.WriteLine(
        $"Warning: replaced {previousAgent.Role} agent '{previousAgent.Id.Value}' with '{replacementAgent.Id.Value}', but in-flight task(s) remain pinned to the removed agent: {string.Join(", ", affected)}. Use reassign-agent to update them deliberately.");
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
    var forceReclaim = HasCliConfirmation(parts, "--force-reclaim");
    if (createGoal && createSimpleGoal)
    {
        throw new ArgumentException("Use either --create-goal or --create-simple-goal, not both.");
    }

    // Batch submission (a3f6b536): multiple positional filters each create one goal in a single command.
    // Single-filter behaviour below is unchanged; batch only engages with 2+ filters and a create flag.
    var batchFilters = parts
        .Skip(1)
        .Where(part => !part.StartsWith("--", StringComparison.Ordinal))
        .ToList();
    if (batchFilters.Count > 1 && (createGoal || createSimpleGoal))
    {
        var batchPlans = new List<(string Filter, BacklogIntakePlan Plan)>();
        foreach (var filter in batchFilters)
        {
            var itemPlan = BacklogIntakePlanner.Build(context.Workspace.BacklogStorePath, filter, 2);
            ThrowIfAmbiguousBacklogIntakeMatch(filter, itemPlan);
            batchPlans.Add((filter, itemPlan));
        }

        var created = 0;
        var matched = 0;
        foreach (var (filter, itemPlan) in batchPlans)
        {
            if (itemPlan.Items.Count == 0)
            {
                Console.WriteLine($"No backlog item matched '{filter}'; skipping.");
                continue;
            }

            var batchItem = itemPlan.Items.Single();
            matched++;
            if (TryReuseBacklogIntakeGoal(context, batchItem, out var reusedBatchGoal) ||
                TryReuseBacklogIntakeRecord(context, batchItem, out reusedBatchGoal))
            {
                if (reusedBatchGoal is not null)
                {
                    context.CurrentGoal = reusedBatchGoal;
                    Console.WriteLine($"Backlog slice '{batchItem.Heading}' already has goal {reusedBatchGoal.Id.Value[..8]}; no new goal created.");
                }
                continue;
            }

            var batchReservation = ReserveBacklogIntake(context, batchItem, forceReclaim);
            if (batchReservation.Kind != BacklogIntakeReservationKind.Acquired)
            {
                PrintBacklogIntakeRecord(batchReservation.Record, context.Kernel);
                continue;
            }

            var batchGoal = createSimpleGoal
                ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, batchItem.SuggestedObjective, context.Workspace, context.Providers, context.EventWriter)
                : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, batchItem.SuggestedObjective, context.Workspace, context.Providers, context.EventWriter);

            if (!string.IsNullOrEmpty(batchItem.Id))
            {
                context.Kernel.SetGoalSourceBacklogItemId(batchGoal.Id, batchItem.Id);
            }

            context.CurrentGoal = batchGoal;
            PersistBacklogIntakeGoal(context, batchItem, batchGoal);
            created++;
            Console.WriteLine(createSimpleGoal
                ? $"Created simple goal from backlog slice '{batchItem.Heading}'."
                : $"Created five-role goal from backlog slice '{batchItem.Heading}'.");
        }

        if (matched == 0)
        {
            throw new InvalidOperationException("No backlog items matched the requested filters.");
        }

        Console.WriteLine($"Created {created} goal(s) from {batchFilters.Count} requested backlog slice(s).");
        return created > 0;
    }

    var headingFilter = parts
        .Skip(1)
        .FirstOrDefault(part => !part.StartsWith("--", StringComparison.Ordinal));
    var plan = BacklogIntakePlanner.Build(
        context.Workspace.BacklogStorePath,
        string.IsNullOrWhiteSpace(headingFilter) ? null : headingFilter,
        createGoal || createSimpleGoal ? 2 : 5);
    if (createGoal || createSimpleGoal)
    {
        ThrowIfAmbiguousBacklogIntakeMatch(headingFilter, plan);
    }

    if (plan.Items.Count == 0)
    {
        if (!string.IsNullOrWhiteSpace(headingFilter))
        {
            var doneItem = new BacklogStore(context.Workspace.BacklogStorePath)
                .GetByExactIdAsync(BacklogStore.SlugId(headingFilter)).GetAwaiter().GetResult();
            if (doneItem is { Status: BacklogItemStatus.Done })
            {
                Console.WriteLine($"Backlog item '{doneItem.Title}' is already Done; no goal created.");
                return false;
            }
        }

        throw new InvalidOperationException("No backlog items matched the requested filter.");
    }

    ConsoleViews.PrintBacklogIntakePlan(plan);
    if (!createGoal && !createSimpleGoal)
    {
        return false;
    }

    var item = plan.Items.Single();
    var backlogItemId = item.Id;
    if (TryReuseBacklogIntakeGoal(context, item, out var existingGoal) ||
        TryReuseBacklogIntakeRecord(context, item, out existingGoal))
    {
        if (existingGoal is not null)
        {
            context.CurrentGoal = existingGoal;
            Console.WriteLine($"Backlog slice already has goal {existingGoal.Id.Value[..8]}; no new goal created.");
            ConsoleViews.PrintGoal(existingGoal);
        }
        return false;
    }

    var reservation = ReserveBacklogIntake(context, item, forceReclaim);
    if (reservation.Kind != BacklogIntakeReservationKind.Acquired)
    {
        PrintBacklogIntakeRecord(reservation.Record, context.Kernel);
        return false;
    }

    context.CurrentGoal = createSimpleGoal
        ? GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, context.Agents, item.SuggestedObjective, context.Workspace, context.Providers, context.EventWriter)
        : GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, context.Agents, item.SuggestedObjective, context.Workspace, context.Providers, context.EventWriter);

    if (!string.IsNullOrEmpty(backlogItemId))
    {
        context.Kernel.SetGoalSourceBacklogItemId(context.CurrentGoal.Id, backlogItemId);
    }

    PersistBacklogIntakeGoal(context, item, context.CurrentGoal);
    Console.WriteLine(createSimpleGoal ? "Created simple goal from backlog slice." : "Created five-role goal from backlog slice.");
    ConsoleViews.PrintGoal(context.CurrentGoal);
    return true;
}

private static void ThrowIfAmbiguousBacklogIntakeMatch(string? filter, BacklogIntakePlan plan)
{
    if (plan.Items.Count <= 1)
    {
        return;
    }

    var label = string.IsNullOrWhiteSpace(filter) ? "<empty>" : filter;
    var matches = string.Join(Environment.NewLine, plan.Items.Select(item => $"  {item.Id} - {item.Heading}"));
    throw new InvalidOperationException($"Backlog filter '{label}' matched multiple items; narrow the filter or use an exact id:{Environment.NewLine}{matches}");
}

private static bool TryReuseBacklogIntakeGoal(
    CliExecutionContext context,
    BacklogIntakeItem item,
    [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Goal? existingGoal)
{
    existingGoal = string.IsNullOrWhiteSpace(item.Id)
        ? null
        : context.Kernel.FindGoalBySourceBacklogItemId(item.Id);
    return existingGoal is not null;
}

private static bool TryReuseBacklogIntakeRecord(
    CliExecutionContext context,
    BacklogIntakeItem item,
    [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Goal? existingGoal)
{
    existingGoal = null;
    if (string.IsNullOrWhiteSpace(item.Id))
        return false;

    var record = new BacklogIntakeRecordStore(context.Workspace.SqliteStatePath).Get(item.Id);
    if (record is null || string.IsNullOrWhiteSpace(record.GoalId))
        return false;

    existingGoal = context.Kernel.Goals.FirstOrDefault(goal =>
        goal.Id.Value.Equals(record.GoalId, StringComparison.Ordinal));
    return existingGoal is not null;
}

private static BacklogIntakeReservation ReserveBacklogIntake(
    CliExecutionContext context,
    BacklogIntakeItem item,
    bool forceReclaim)
{
    return new BacklogIntakeRecordStore(context.Workspace.SqliteStatePath)
        .Reserve(item.Id, item.Heading, forceReclaim);
}

private static void PersistBacklogIntakeGoal(CliExecutionContext context, BacklogIntakeItem item, Goal goal)
{
    if (string.IsNullOrWhiteSpace(item.Id))
        return;

    context.PersistCheckpoint(context.Kernel);
    new BacklogIntakeRecordStore(context.Workspace.SqliteStatePath).MarkGoalCreated(item.Id, goal.Id.Value);
}

internal static string? ResolveGoalFriendlyLabel(Goal goal, string backlogStorePath)
{
    if (string.IsNullOrWhiteSpace(goal.SourceBacklogItemId) ||
        string.IsNullOrWhiteSpace(backlogStorePath) ||
        !File.Exists(backlogStorePath))
    {
        return null;
    }

    try
    {
        var item = new BacklogStore(backlogStorePath)
            .GetByExactIdAsync(goal.SourceBacklogItemId)
            .GetAwaiter()
            .GetResult();
        return string.IsNullOrWhiteSpace(item?.Title) ? null : item.Title;
    }
    catch
    {
        return null;
    }
}

private static string ResolveGoalStatusText(OrchestratorWorkspace workspace, Goal goal)
{
    var lifecycle = GoalLifecycle.ResolveState(goal, GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(workspace, goal));
    return DashboardResponseMapper.GoalStatusText(goal.Status, lifecycle);
}

private static void PrintBacklogIntakeRecord(BacklogIntakeRecord record, AgentOrchestratorKernel kernel)
{
    Console.WriteLine($"Backlog intake for source {record.SourceBacklogItemId} is {record.Status}; no new goal created.");
    if (!string.IsNullOrWhiteSpace(record.GoalId))
    {
        Console.WriteLine($"Goal: {record.GoalId[..Math.Min(8, record.GoalId.Length)]}");
        var goal = kernel.Goals.FirstOrDefault(candidate => candidate.Id.Value.Equals(record.GoalId, StringComparison.Ordinal));
        if (goal is not null)
            ConsoleViews.PrintGoal(goal);
        Console.WriteLine($"Recommended next command: next {record.GoalId[..Math.Min(8, record.GoalId.Length)]} --full");
        return;
    }

    Console.WriteLine($"Started: {record.StartedAt:O}");
    Console.WriteLine($"Last heartbeat: {record.LastHeartbeatAt:O}");
    if (record.OwnerProcessId is int ownerPid)
        Console.WriteLine($"Owner PID: {ownerPid}");
    if (!string.IsNullOrWhiteSpace(record.StdoutPath))
        Console.WriteLine($"Log: {record.StdoutPath}");
    if (!string.IsNullOrWhiteSpace(record.StderrPath))
        Console.WriteLine($"Error log: {record.StderrPath}");
    Console.WriteLine($"Recommended next command: retry after the owner exits, or rerun backlog-intake \"{record.Heading}\" --force-reclaim after confirming it is stale.");
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

private static TimeSpan? ResolveWatchStallWarningThreshold(IReadOnlyList<string> parts)
{
    if (GetFlagValue(parts, "--stall-warning-seconds") is { } secondsValue)
    {
        return TimeSpan.FromSeconds(ParsePositiveInteger(secondsValue, "--stall-warning-seconds"));
    }

    if (GetFlagValue(parts, "--stall-warning-minutes") is { } minutesValue)
    {
        return TimeSpan.FromMinutes(ParsePositiveInteger(minutesValue, "--stall-warning-minutes"));
    }

    return null;
}

private static int ResolveConductPollSeconds(IReadOnlyList<string> parts)
{
    if (GetFlagValue(parts, "--poll-seconds") is { } pollSeconds)
    {
        return ParsePositiveInteger(pollSeconds, "--poll-seconds");
    }

    if (HasCliConfirmation(parts, "--poll-seconds"))
    {
        throw new ArgumentException("--poll-seconds requires a positive integer value.");
    }

    if (GetFlagValue(parts, "--watch-interval") is { } watchInterval)
    {
        return ParsePositiveInteger(watchInterval, "--poll-seconds");
    }

    if (HasCliConfirmation(parts, "--watch-interval"))
    {
        throw new ArgumentException("--poll-seconds requires a positive integer value.");
    }

    return ConductorBatchLoop.DefaultWatchIntervalSeconds;
}

private static int ParsePositiveInteger(string value, string flag)
{
    if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
    {
        throw new ArgumentException($"{flag} requires a positive integer value.");
    }

    return parsed;
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
        Console.WriteLine("  attention waits: resolve with park reason");
        Console.WriteLine($"  command: park-goal {goal.Id.Value[..8]} <reason> --confirm-goal-park");
        return goal;
    }

    var runner = new BackgroundDispatchRunner();
    foreach (var task in runningTasks)
    {
        _ = runner.CancelLatestProcess(context.Kernel, goal.Id, task.Id);
    }

    var resolvedHumanWaits = context.Kernel.HumanInputRequests.Count(request => request.GoalId == goal.Id && !request.IsCompleted);
    _ = context.Kernel.ParkGoal(goal.Id, reason);
    var store = CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory);
    var resolvedAttentionItems = store.ResolveOpenForGoalAsync(goal.Id.Value, $"Goal parked: {reason}").GetAwaiter().GetResult();
    Console.WriteLine($"Goal parked {goal.Id.Value[..8]}.");
    Console.WriteLine($"Cancelled running dispatches: {runningTasks.Length}");
    Console.WriteLine($"Resolved human waits: {resolvedHumanWaits}");
    Console.WriteLine($"Resolved attention items: {resolvedAttentionItems}");
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
    if (!context.Worktrees.IsGitWorkTree(context.Workspace.ExecutionDirectory))
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
    if (IsHelpRequested(parts))
    {
        CliCommandHelp.TryPrintStartupHelp(parts);
        return;
    }

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
    var branch = context.Worktrees.BranchName(goal.Id);
    switch (normalizedAction)
    {
        case "status":
            var existing = context.Worktrees.TryResolve(executionDirectory, goal.Id);
            Console.WriteLine(existing is null
                ? $"Goal has no workspace. Create one with: workspace create (branch {branch})"
                : $"Workspace: {existing} (branch {branch})");
            return;

        case "create":
            Console.WriteLine($"Workspace: {context.Worktrees.Ensure(executionDirectory, goal.Id)} (branch {branch})");
            return;

        case "merge":
            var merge = context.Worktrees.TryFastForwardMerge(executionDirectory, goal.Id);
            Console.WriteLine(merge is null
                ? "Goal has no workspace branch to merge."
                : FormatWorkspaceMerge(merge));
            return;

        case "rebase":
            Console.WriteLine(FormatWorkspaceRebase(context.Worktrees.TryRebaseOntoMain(executionDirectory, goal.Id)));
            return;

        case "remove":
            var policy = ResolveCliAutonomyPolicy(parts);
            EnsurePolicyAllows(context, goal, policy, AutonomyAction.WorkspaceCleanup, "workspace remove");
            GoalOperationJournal.Begin(executionDirectory, goal, "workspace:remove", "Removing goal workspace.");
            GoalWorktreeRemoveResult removeResult;
            try
            {
                removeResult = context.Worktrees.Remove(executionDirectory, goal.Id, context.Kernel);
            }
            catch (InvalidOperationException ex)
            {
                GoalOperationJournal.Failed(executionDirectory, goal, "workspace:remove", ex.Message);
                throw;
            }

            PrintWorkspaceRemoveResult(removeResult);
            if (removeResult.IsComplete)
            {
                GoalOperationJournal.Completed(executionDirectory, goal, "workspace:remove", removeResult.Message);
                if (TryReconcileLandedCleanedAcceptance(context, goal, "workspace remove", out _, cleanupEvidenceRecorded: true))
                {
                    context.EventWriter.AppendCleanedUp(goal.Id);
                }
            }
            else if (TryCompleteLandedBranchOnlyWorkspaceRemove(context, goal, removeResult, out var completedRemoveDetail))
            {
                GoalOperationJournal.Completed(executionDirectory, goal, "workspace:remove", completedRemoveDetail);
                if (TryReconcileLandedCleanedAcceptance(context, goal, "workspace remove", out _, cleanupEvidenceRecorded: true))
                {
                    context.EventWriter.AppendCleanedUp(goal.Id);
                }
            }
            else
            {
                GoalOperationJournal.Failed(executionDirectory, goal, "workspace:remove", removeResult.Message);
            }
            return;

        default:
            throw new ArgumentException("Usage: workspace [create|merge|rebase|remove] [goal-id-prefix]");
    }
}

private static void PrintConductUsage()
{
    Console.WriteLine(CliCommandHelp.ConductUsage);
    Console.WriteLine("  -h, --help  Show this help.");
}

private static void PrintWorkspaceUsage()
{
    Console.WriteLine(CliCommandHelp.WorkspaceUsage);
    Console.WriteLine("  -h, --help  Show this help.");
}

private static bool RunAcceptanceWorkspaceMerge(CliExecutionContext context, bool skipVerify = false)
{
    var goal = context.CurrentGoal!;
    if (TryReconcileLandedCleanedAcceptance(context, goal, "acceptance retry", out var reconciledDetail))
    {
        Console.WriteLine(reconciledDetail);
        context.EventWriter.AppendAcceptanceResult(goal.Id, true, []);
        return true;
    }

    if (TryNormalizePrematureCompletedGoalForAcceptance(context, goal, out var normalizedGoal, out var normalizedDetail))
    {
        Console.WriteLine(normalizedDetail);
        goal = normalizedGoal;
        context.CurrentGoal = normalizedGoal;
    }

    if (goal.Status != GoalStatus.Verified)
    {
        return false;
    }

    var worktreePath = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id);
    AcceptanceVerificationResult? verification = null;
    string? testedWorktreeHead = null;
    if (worktreePath is not null)
    {
        var rebaseCheckStarted = System.Diagnostics.Stopwatch.StartNew();
        var needsRebase = context.Worktrees.NeedsRebaseOntoMain(context.Workspace.ExecutionDirectory, goal.Id);
        rebaseCheckStarted.Stop();
        context.PhaseTimings.Record(
            "workspace-rebase-check",
            rebaseCheckStarted.Elapsed,
            ("goal", goal.Id.Value[..8]),
            ("needed", needsRebase));

        if (needsRebase)
        {
            var rebaseStarted = System.Diagnostics.Stopwatch.StartNew();
            var rebase = context.Worktrees.TryRebaseOntoMain(context.Workspace.ExecutionDirectory, goal.Id);
            rebaseStarted.Stop();
            context.PhaseTimings.Record(
                "workspace-rebase",
                rebaseStarted.Elapsed,
                ("goal", goal.Id.Value[..8]),
                ("status", rebase.Status),
                ("updated", rebase.UpdatedBranch));
            Console.WriteLine($"Workspace rebase: {FormatWorkspaceRebase(rebase)}");

            if (!rebase.UpdatedBranch)
            {
                var failedChecks = new[] { $"workspace rebase: {rebase.Status.ToString().ToLowerInvariant()}" };
                context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks);
                context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
                Console.WriteLine("Acceptance evidence: blocked; merge blocked");
                return false;
            }
        }
        else
        {
            context.PhaseTimings.Record(
                "workspace-rebase",
                TimeSpan.Zero,
                ("goal", goal.Id.Value[..8]),
                ("status", "skipped"),
                ("reason", "already-up-to-date"));
        }

        testedWorktreeHead = context.Worktrees.ResolveHead(worktreePath);
        var changedFiles = context.Worktrees.GetChangedFiles(worktreePath);
        if (skipVerify)
        {
            context.PhaseTimings.Record(
                "verification-suite",
                TimeSpan.Zero,
                ("goal", goal.Id.Value[..8]),
                ("status", "skipped"),
                ("reason", "--skip-verify"));
            Console.WriteLine("Verification: skipped (--skip-verify)");
        }
        else
        {
            var verificationStarted = System.Diagnostics.Stopwatch.StartNew();
            int? stableSlotIndex = null;
            DotnetBuildEnvironmentLease? stableSlotLease = null;
            try
            {
                var stableSlotSelector = context.StableSlotSelector ?? SelectFirstAvailableStableSlot;
                stableSlotLease = stableSlotSelector(
                    context.StableSlotAcquisitionTimeout,
                    wait => Console.WriteLine($"waiting for slot-{wait.SlotIndex} lease held by pid {wait.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}"));
                stableSlotIndex = ParseStableSlotIndex(stableSlotLease.Environment.SlotOwnerToken)
                    ?? throw new IOException($"Stable slot lease did not identify a slot: {stableSlotLease.Environment.SlotOwnerToken}");
            }
            catch (IOException ex)
            {
                stableSlotLease?.Dispose();
                Console.WriteLine($"BLOCKER step=verification reason=build-slot-timeout detail=\"{EscapeBlockerDetail(ex.Message)}\" action=\"Wait for a stable dotnet build slot to clear, then rerun acceptance.\"");
                throw new InvalidOperationException("acceptance blocked waiting for a stable dotnet build slot.", ex);
            }

            using (stableSlotLease)
            {
                verification = context.AcceptanceVerifier.RunAsync(worktreePath, goal.Id, changedFiles, stableSlotIndex, stableSlotLease).GetAwaiter().GetResult();
            }
            verificationStarted.Stop();
            context.PhaseTimings.Record(
                "verification-suite",
                verificationStarted.Elapsed,
                ("goal", goal.Id.Value[..8]),
                ("slot", stableSlotIndex.HasValue ? $"slot-{stableSlotIndex.Value}" : null),
                ("passed", verification.Passed),
                ("exit", verification.ExitCode),
                ("checks", verification.Checks?.Count ?? 0));
            foreach (var check in verification.Checks ?? [])
            {
                context.PhaseTimings.Record(
                    "verification-check",
                    TimeSpan.FromMilliseconds(check.DurationMilliseconds ?? 0),
                    ("goal", goal.Id.Value[..8]),
                    ("name", check.Name),
                    ("passed", check.Passed),
                    ("exit", check.ExitCode),
                    ("advisory", check.Advisory));
                Console.WriteLine($"Verification check: {(check.Passed ? "passed" : "failed")} - {check.Name}" +
                    (check.ExitCode is null ? "" : $" (exit {check.ExitCode})") +
                    (string.IsNullOrWhiteSpace(check.ArtifactsPath) ? "" : $" artifacts={check.ArtifactsPath}"));
            }
        }
    }
    else
    {
        context.PhaseTimings.Record(
            "workspace-rebase",
            TimeSpan.Zero,
            ("goal", goal.Id.Value[..8]),
            ("status", "skipped"),
            ("reason", "no-worktree"));
        context.PhaseTimings.Record(
            "verification-suite",
            TimeSpan.Zero,
            ("goal", goal.Id.Value[..8]),
            ("status", "skipped"),
            ("reason", "no-worktree"));
    }

    if (skipVerify || verification is { Passed: true })
    {
        context.Kernel.ClearAcceptanceFailure(goal.Id);
    }

    var expectedGoalFingerprint = BuildGoalFingerprint(context.Kernel, goal.Id);
    var evidence = context.Worktrees.BuildAcceptanceEvidence(context.Kernel, goal, worktreePath, verification, skipVerify);
    ConsoleViews.PrintAcceptanceEvidenceBundle(evidence);

    if (!evidence.Passed)
    {
        if (verification is { Passed: false })
        {
            Console.WriteLine($"Verification: failed (exit {verification.ExitCode}); merge blocked");
            Console.WriteLine($"Verification artifacts: {verification.ArtifactsPath}");
            if (TryBuildVerificationTimeoutBlocker(verification) is { } timeoutBlocker)
            {
                Console.WriteLine(timeoutBlocker);
            }

            if (!string.IsNullOrWhiteSpace(verification.OutputTail))
            {
                Console.WriteLine(verification.OutputTail);
            }
        }
        else
        {
            Console.WriteLine("Acceptance evidence: blocked; merge blocked");
        }

        var failedChecks = verification?.Checks?
            .Where(c => !c.Passed && !c.Advisory)
            .Select(c => c.Name)
            .ToList() ?? ["acceptance evidence blocked"];
        context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks);
        context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
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
    GoalLandingPostActions.RunAdvisorySemanticAcceptance(
        goal,
        context.Workspace,
        context.Providers,
        context.WorkerProfiles,
        worktreePath,
        verification,
        Console.WriteLine);

    var hostStop = context.StopAcceptanceHosts(new AcceptanceHostStopRequest(
        goal.Id,
        worktreePath,
        TimeSpan.FromSeconds(30)));
    Console.WriteLine(hostStop.Message);
    if (!hostStop.Succeeded)
    {
        context.Kernel.RecordAcceptanceFailure(goal.Id, ["stop-host"]);
        context.EventWriter.AppendAcceptanceResult(goal.Id, false, ["stop-host"]);
        return false;
    }

    var mergeStarted = System.Diagnostics.Stopwatch.StartNew();
    var mergeCommit = context.FinalizeAcceptanceMerge(new AcceptanceMergeCommitRequest(
            goal.Id,
            expectedGoalFingerprint,
            testedWorktreeHead,
            Merge: () =>
            {
                var pendingRollback = GoalRollbackPlanner.CapturePendingAcceptance(context.Workspace.ExecutionDirectory, goal.Id);
                var merge = context.Worktrees.TryFastForwardMerge(context.Workspace.ExecutionDirectory, goal.Id);

                if (merge is null)
                {
                    return new AcceptanceMergeCommitResult(true, null);
                }

                if (merge.FastForwarded && pendingRollback is not null)
                {
                    GoalRollbackPlanner.RecordAcceptance(context.Workspace.ExecutionDirectory, pendingRollback);
                }

                return new AcceptanceMergeCommitResult(merge.FastForwarded, FormatWorkspaceMerge(merge));
            }));
    mergeStarted.Stop();
    context.PhaseTimings.Record(
        "workspace-merge",
        mergeStarted.Elapsed,
        ("goal", goal.Id.Value[..8]),
        ("fastForwarded", mergeCommit.FastForwarded),
        ("guardFailure", mergeCommit.GuardFailure),
        ("message", mergeCommit.Message));

    if (mergeCommit.GuardFailure)
    {
        var failedChecks = new[] { mergeCommit.Message ?? "acceptance state changed during acceptance verification" };
        context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks);
        context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
        Console.WriteLine($"BLOCKER step=acceptance-state-guard reason=state-changed detail=\"{EscapeBlockerDetail(failedChecks[0])}\" action=\"Resolve concurrent goal or worktree changes, then rerun acceptance.\"");
        ConsoleViews.PrintAcceptanceSummary(goal, context.Kernel.BuildGoalAcceptanceSummary(goal.Id));
        throw new InvalidOperationException(failedChecks[0]);
    }

    if (mergeCommit.Message is not null)
    {
        Console.WriteLine($"Workspace merge: {mergeCommit.Message}");
        if (mergeCommit.FastForwarded)
        {
            RecordAcceptanceCompleted(context, goal);
        }
        else
        {
            var failedChecks = new[] { "merge" };
            context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks);
            context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
            Console.WriteLine($"BLOCKER step=merge reason={mergeCommit.Message} action=\"Resolve conflicts on {context.Worktrees.BranchName(goal.Id)}, rerun verification, then rerun acceptance.\"");
        }
        return mergeCommit.FastForwarded;
    }

    RecordAcceptanceCompleted(context, goal);
    return true;
}

private static DotnetBuildEnvironmentLease SelectFirstAvailableStableSlot(TimeSpan? timeout, Action<DotnetBuildStableSlotWait>? onWait)
{
    return DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(timeout, onWait);
}

private static int? ParseStableSlotIndex(string slotOwnerToken)
{
    return slotOwnerToken.StartsWith("slot-", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(slotOwnerToken[5..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var slotIndex)
            ? slotIndex
            : null;
}

private static bool TryNormalizePrematureCompletedGoalForAcceptance(
    CliExecutionContext context,
    Goal goal,
    out Goal normalizedGoal,
    out string detail)
{
    normalizedGoal = goal;
    var goalPrefix = goal.Id.Value[..8];
    if (goal.Status != GoalStatus.Completed)
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} is {goal.Status}.";
        return false;
    }

    if (!GoalWorktrees.IsGitWorkTree(context.Workspace.ExecutionDirectory))
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} is not in a git worktree.";
        return false;
    }

    var hasBranchArtifact = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is not null ||
        GoalWorktrees.HasBranch(context.Workspace.ExecutionDirectory, goal.Id);
    if (!hasBranchArtifact ||
        GoalWorktrees.IsBranchMergedIntoCurrent(context.Workspace.ExecutionDirectory, goal.Id))
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} has no unmerged goal branch.";
        return false;
    }

    if (!context.Kernel.NormalizePrematureCompletedGoalToVerified(
            goal.Id,
            "acceptance: normalized raw Completed goal with unmerged branch back to Verified before merge."))
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} does not have passed task verification gates.";
        return false;
    }

    normalizedGoal = context.Kernel.GetGoal(goal.Id);
    detail = $"Acceptance repair: normalized raw Completed goal {goalPrefix} to Verified so acceptance can merge {context.Worktrees.BranchName(goal.Id)}.";
    return true;
}

private static void RecordAcceptanceCompleted(CliExecutionContext context, Goal goal)
{
    GoalOperationJournal.Completed(
        context.Workspace.ExecutionDirectory,
        goal,
        "acceptance",
        "Acceptance passed and merge completed.");
    context.Kernel.ClearAcceptanceFailure(goal.Id);
    context.EventWriter.AppendAcceptanceResult(goal.Id, true, []);
}

private static string? TryBuildVerificationTimeoutBlocker(AcceptanceVerificationResult verification)
{
    var timedOutCheck = verification.Checks?
        .FirstOrDefault(check =>
            !check.Advisory &&
            !check.Passed &&
            (check.Name.StartsWith("acceptance-check-timeout:", StringComparison.OrdinalIgnoreCase) ||
             check.ResultSummary?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true ||
             check.OutputTail?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true));

    if (timedOutCheck is null)
        return null;

    var artifactPath = !string.IsNullOrWhiteSpace(timedOutCheck.ArtifactsPath)
        ? timedOutCheck.ArtifactsPath
        : verification.ArtifactsPath;
    var artifactDetail = string.IsNullOrWhiteSpace(artifactPath) ? "none" : artifactPath;
    return $"BLOCKER step=verification reason=timeout check=\"{timedOutCheck.Name}\" artifacts={artifactDetail} action=\"Inspect verification command, artifact path, and last output above; rerun acceptance after clearing the blocker.\"";
}

private static string EscapeBlockerDetail(string value) =>
    value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

private static void ReconcileLandedCleanedAcceptance(
    CliExecutionContext context,
    Goal goal,
    string source,
    bool cleanupEvidenceRecorded = false)
{
    TryReconcileLandedCleanedAcceptance(context, goal, source, out _, cleanupEvidenceRecorded);
}

private static bool TryReconcileLandedCleanedAcceptance(
    CliExecutionContext context,
    Goal goal,
    string source,
    out string detail,
    bool cleanupEvidenceRecorded = false)
{
    if (!HasLandedCleanedTerminalEvidence(context, goal, out detail, cleanupEvidenceRecorded))
    {
        return false;
    }

    if (goal.Status == GoalStatus.Verified)
    {
        context.Kernel.CompleteGoal(goal.Id, $"Acceptance repaired after durable landing and cleanup evidence from {source}.");
    }

    context.Kernel.ClearAcceptanceFailure(goal.Id);
    detail = $"Acceptance repaired: goal {goal.Id.Value[..8]} is already landed and workspace cleanup is recorded ({source}).";
    return true;
}

private static bool HasLandedCleanedTerminalEvidence(
    CliExecutionContext context,
    Goal goal,
    out string detail,
    bool cleanupEvidenceRecorded = false)
{
    var goalPrefix = goal.Id.Value[..8];
    if (goal.Status is not (GoalStatus.Verified or GoalStatus.Completed))
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} is {goal.Status}, not Verified or Completed.";
        return false;
    }

    if (context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is not null)
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} still has a worktree.";
        return false;
    }

    var journal = GoalOperationJournal.Read(context.Workspace.ExecutionDirectory, goal.Id);
    var hasLandingEvidence = journal.LatestByOperation.Any(entry =>
        entry.Status == GoalOperationStatus.Completed &&
        (entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
         entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase)));
    if (!hasLandingEvidence)
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} has no completed acceptance or conductor landing evidence.";
        return false;
    }

    var hasCleanupEvidence = cleanupEvidenceRecorded ||
        journal.LatestByOperation.Any(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            (entry.Operation.Equals("workspace:remove", StringComparison.OrdinalIgnoreCase) ||
             entry.Operation.Equals("conductor:cleanup", StringComparison.OrdinalIgnoreCase)));
    if (!hasCleanupEvidence)
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} has no completed workspace cleanup evidence.";
        return false;
    }

    detail = $"goal {goalPrefix} has completed landing and cleanup evidence.";
    return true;
}

private static bool TryCompleteLandedBranchOnlyWorkspaceRemove(
    CliExecutionContext context,
    Goal goal,
    GoalWorktreeRemoveResult removeResult,
    out string detail)
{
    detail = string.Empty;
    var executionDirectory = context.Workspace.ExecutionDirectory;
    var worktreePath = GoalWorktrees.WorktreePath(executionDirectory, goal.Id);
    if (context.Worktrees.TryResolve(executionDirectory, goal.Id) is not null ||
        Directory.Exists(worktreePath))
    {
        return false;
    }

    var journal = GoalOperationJournal.Read(executionDirectory, goal.Id);
    var hasLandingEvidence = journal.LatestByOperation.Any(entry =>
        entry.Status == GoalOperationStatus.Completed &&
        (entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
         entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase)));
    if (!hasLandingEvidence)
    {
        return false;
    }

    var branch = context.Worktrees.BranchName(goal.Id);
    var branchExists = GitCli.Run(executionDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
    if (branchExists.ExitCode != 0)
    {
        detail = $"Removed workspace. {removeResult.Message}";
        return true;
    }

    var deleteBranch = GitCli.Run(executionDirectory, "branch", "-D", branch);
    if (deleteBranch.ExitCode != 0)
    {
        return false;
    }

    detail = $"Removed workspace and cleaned landed branch {branch}.";
    return true;
}

private static string ResolveWorktreeHead(string worktreePath)
{
    var result = GitCli.Run(worktreePath, "rev-parse", "HEAD");
    if (!result.Succeeded)
    {
        throw new InvalidOperationException($"Failed to resolve worktree HEAD: {result.Error}");
    }

    return result.Output.Trim();
}

private static string BuildGoalFingerprint(AgentOrchestratorKernel kernel, GoalId goalId)
{
    var snapshot = kernel.ExportSnapshot().Goals.FirstOrDefault(goal => goal.Id == goalId.Value)
        ?? throw new InvalidOperationException($"Goal '{goalId.Value}' no longer exists.");
    var landingRelevantState = new
    {
        Tasks = snapshot.Tasks
            .OrderBy(task => task.Id, StringComparer.Ordinal)
            .Select(task => new
            {
                task.Id,
                Role = task.RequiredRole,
                task.Status
            })
    };

    return JsonSerializer.Serialize(landingRelevantState);
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

    var sweep = TerminalGoalSweep.Run(context.Kernel, context.Workspace.ExecutionDirectory, goal.Id);
    ConsoleViews.PrintTerminalGoalSweep(sweep);
    goal = context.Kernel.GetGoal(goal.Id);
    context.CurrentGoal = goal;
    var actions = 0;
    if (sweep.Changed)
    {
        actions++;
    }

    if (context.Kernel.NormalizeGoalLifecycleState(goal.Id, $"recover: normalized terminal goal with non-terminal task(s); {note}"))
    {
        Console.WriteLine("recover: normalized terminal goal with non-terminal task(s) to Active.");
        actions++;
    }

    foreach (var request in context.Kernel.GetPendingHumanInput(goal.Id).ToList())
    {
        context.Kernel.SubmitHumanInput(request.Id, note);
        Console.WriteLine($"recover: answered human-input request {request.Id.Value[..8]}.");
        actions++;
    }

    var alreadyReset = new HashSet<TaskId>();
    foreach (var task in goal.Tasks)
    {
        if (task.Status is WorkTaskStatus.Completed)
        {
            continue;
        }

        if (task.LastProcess is { CompletedAt: null } && task.LastVerification is null &&
            DispatchRecoveryView.Evaluate(goal, task) is { } recoveryDecision)
        {
            PrintRecoverDispatchRecovery(task, goal, recoveryDecision);
            if (recoveryDecision.Action is DispatchRecoveryAction.Hold or DispatchRecoveryAction.ClassifyBlocker)
            {
                continue;
            }

            var runner = new BackgroundDispatchRunner();
            var outcome = runner.ReconcileLatestProcess(context.Kernel, goal.Id, task.Id);
            runner.ApplyRefreshOutcomeAndWriteDiagnostics(context.Kernel, goal.Id, task.Id, outcome);
            goal = context.Kernel.GetGoal(goal.Id);
            context.CurrentGoal = goal;
            var refreshedTask = goal.Tasks.First(candidate => candidate.Id == task.Id);
            actions++;

            if (recoveryDecision.Action == DispatchRecoveryAction.MarkStale &&
                DispatchRecoveryPolicy.IsStaleDispatchRetryVerification(refreshedTask.LastVerification!))
            {
                context.Kernel.RetryTask(goal.Id, refreshedTask.Id, note, invalidateDownstream: !HasRunningDownstreamTask(goal, refreshedTask));
                Console.WriteLine($"recover: reset task {ConsoleViews.GetTaskDisplayNumber(goal, refreshedTask.Id)} to dispatchable.");
                alreadyReset.Add(refreshedTask.Id);
                actions++;
            }

            continue;
        }

        var stuck = task.Status is WorkTaskStatus.Failed or WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman or WorkTaskStatus.Cancelled ||
            task.LastVerification is { Succeeded: false } ||
            task.SubscriptionRetryAfter is not null;
        if (!stuck)
        {
            continue;
        }

        if (task.Status == WorkTaskStatus.Cancelled)
        {
            context.Kernel.RequeueInterruptedDispatch(goal.Id, task.Id, note);
            Console.WriteLine($"recover: requeued interrupted task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} to dispatchable.");
            alreadyReset.Add(task.Id);
            actions++;
            continue;
        }

        // RetryTask refuses Running/WaitingForHuman; normalize to Failed first (the dance's middle step).
        if (task.Status is WorkTaskStatus.Running or WorkTaskStatus.WaitingForHuman)
        {
            context.Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, note);
        }

        context.Kernel.RetryTask(goal.Id, task.Id, note, invalidateDownstream: !HasRunningDownstreamTask(goal, task));
        Console.WriteLine($"recover: reset task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} to dispatchable.");
        alreadyReset.Add(task.Id);
        actions++;
    }

    // Detect lifecycle/task desync: a task is Assigned with no dispatch evidence while all
    // earlier-stage tasks are Completed. This happens after flake-recovery when the conductor
    // retried the task (leaving it Assigned) but previously set the goal aside with
    // LifecycleEscalation. The 'recover' command never saw a stuck task so it printed
    // "nothing to recover", even though the goal was permanently blocked. Detect and report so
    // the operator knows to re-admit (restart the loop or run 'conduct <goal>').
    foreach (var task in goal.Tasks)
    {
        if (alreadyReset.Contains(task.Id) ||
            task.Status != WorkTaskStatus.Assigned ||
            task.LastProcess is { IsRunning: true } ||
            task.LastDispatch is not null ||
            task.LastProcess is not null ||
            task.LastVerification is not null)
        {
            continue;
        }

        var hasIncompleteEarlierStage = goal.Tasks.Any(candidate =>
            GoalManagementCommandService.IsEarlierSdlcStageOf(candidate.RequiredRole, task.RequiredRole) &&
            candidate.Status != WorkTaskStatus.Completed);
        if (hasIncompleteEarlierStage)
        {
            continue;
        }

        context.Kernel.RetryTask(goal.Id, task.Id, $"recover: re-derived lifecycle state for {task.RequiredRole} task {task.Id.Value[..8]} (Assigned, dispatchable, earlier stages Completed); {note}", invalidateDownstream: !HasRunningDownstreamTask(goal, task));
        Console.WriteLine($"recover: task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} {task.RequiredRole} is assigned and dispatchable but has no dispatch record; lifecycle/task desync detected, lifecycle state re-derived. Re-run 'conduct {goal.Id.Value[..8]}' or restart the conductor loop to unblock.");
        actions++;
    }

    if (actions == 0)
    {
        Console.WriteLine("recover: nothing to recover (no pending input, stuck tasks, or lifecycle/task desync).");
    }

    ConsoleViews.PrintGoal(goal);
    return actions > 0;
}

private static void PrintRecoverDispatchRecovery(TaskSpec task, Goal goal, DispatchRecoveryDecision decision)
{
    var blocker = string.IsNullOrWhiteSpace(decision.Blocker)
        ? string.Empty
        : $" blocker='{decision.Blocker}'";
    Console.WriteLine(
        $"recover: task {ConsoleViews.GetTaskDisplayNumber(goal, task.Id)} recovery action='{decision.ActionName}' evidence='{decision.EvidencePath}' reason='{decision.Reason}'{blocker}.");
}

private static bool HasRunningDownstreamTask(Goal goal, TaskSpec task) =>
    goal.Tasks.Any(candidate =>
        GoalManagementCommandService.IsEarlierSdlcStageOf(task.RequiredRole, candidate.RequiredRole) &&
        candidate.LastProcess is { IsRunning: true });

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
    var worktree = context.Worktrees.TryResolve(executionDirectory, goal.Id);
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

    if (sandboxBlockedIds.Count > 0 && !context.Worktrees.IsWorktreeClean(executionDirectory, goal.Id))
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

    if (!context.Worktrees.IsWorktreeClean(executionDirectory, goal.Id) ||
        !context.Worktrees.HasChangesAgainstMain(executionDirectory, goal.Id))
    {
        return;
    }

    var note =
        $"Auto-verified from git ground truth: committed changes on {context.Worktrees.BranchName(goal.Id)} " +
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
    if (context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is not null)
    {
        return;
    }

    // Deterministic creation needs a git work tree to host the worktree; outside a git repo,
    // fall back to the existing execution-directory resolution rather than failing the dispatch.
    if (!context.Worktrees.IsGitWorkTree(context.Workspace.ExecutionDirectory))
    {
        return;
    }

    var branch = context.Worktrees.BranchName(goal.Id);
    GoalOperationJournal.Begin(context.Workspace.ExecutionDirectory, goal, "workspace:create", $"branch {branch}");
    var path = context.Worktrees.Ensure(context.Workspace.ExecutionDirectory, goal.Id);
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
        removeResult = context.Worktrees.Remove(context.Workspace.ExecutionDirectory, goal.Id, context.Kernel);
    }
    catch (InvalidOperationException ex)
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", ex.Message);
        context.Kernel.RecordAcceptanceFailure(goal.Id, ["remove-worktree"]);
        context.EventWriter.AppendAcceptanceResult(goal.Id, false, ["remove-worktree"]);
        Console.WriteLine($"Workspace cleanup failed: {ex.Message}. Resume with: conduct {goalPrefix} --loop");
        Console.WriteLine($"BLOCKER step=remove-worktree reason=\"{ex.Message}\" path={context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) ?? "(unknown)"} action=\"Retry conductor cleanup with conduct {goalPrefix} --loop.\"");
        return;
    }

    PrintWorkspaceRemoveResult(removeResult);
    if (removeResult.IsComplete)
    {
        GoalOperationJournal.Completed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
        context.Kernel.CompleteGoal(goal.Id, "Acceptance completed goal after merge and workspace cleanup evidence.");
        ReconcileLandedCleanedAcceptance(context, goal, "workspace cleanup");
        context.EventWriter.AppendCleanedUp(goal.Id);
    }
    else
    {
        GoalOperationJournal.Failed(context.Workspace.ExecutionDirectory, goal, "workspace:remove", removeResult.Message);
        context.Kernel.RecordAcceptanceFailure(goal.Id, ["remove-worktree"]);
        context.EventWriter.AppendAcceptanceResult(goal.Id, false, ["remove-worktree"]);
        Console.WriteLine($"BLOCKER step=remove-worktree reason=\"{removeResult.Message}\" path={removeResult.LeftoverPath ?? context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) ?? "(unknown)"} action=\"Retry conductor cleanup with conduct {goalPrefix} --loop.\"");
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

    if (result.CleanupBackoff is not null)
    {
        Console.WriteLine($"Cleanup backoff: {GoalWorktrees.FormatCleanupBackoff(result.CleanupBackoff)}");
    }

    if (result.ResumeCommand is not null)
    {
        Console.WriteLine($"Resume: {result.ResumeCommand}");
    }
}

private static T RunGoalMarkLandedStep<T>(GoalMarkLandedCleanupDeadline deadline, string stepName, Func<int, T> step)
{
    var remainingBeforeStep = deadline.RemainingMilliseconds;
    if (deadline.IsExpired)
    {
        Console.Error.WriteLine($"goal-mark-landed slow substep: {stepName} elapsedMs={deadline.ElapsedMilliseconds}");
        throw new TimeoutException($"goal-mark-landed cleanup exceeded {GoalMarkLandedPromptTimeoutMilliseconds}ms before substep '{stepName}'.");
    }

    var startedAt = System.Diagnostics.Stopwatch.StartNew();
    var reportedSlowStep = false;
    try
    {
        var result = step(remainingBeforeStep);
        startedAt.Stop();
        if (deadline.IsExpired)
        {
            Console.Error.WriteLine($"goal-mark-landed slow substep: {stepName} elapsedMs={deadline.ElapsedMilliseconds}");
            reportedSlowStep = true;
            throw new TimeoutException($"goal-mark-landed cleanup exceeded {deadline.TotalMilliseconds}ms during substep '{stepName}'.");
        }

        return result;
    }
    catch
    {
        startedAt.Stop();
        if (!reportedSlowStep && deadline.IsExpired)
            Console.Error.WriteLine($"goal-mark-landed slow substep: {stepName} elapsedMs={deadline.ElapsedMilliseconds}");
        throw;
    }
}

private static bool TryRunGoalMarkLandedBestEffortStep<T>(
    GoalMarkLandedCleanupDeadline deadline,
    string stepName,
    Func<int, T> step,
    out T? result,
    out string? failure)
{
    result = default;
    failure = null;
    if (deadline.IsExpired)
    {
        Console.Error.WriteLine($"goal-mark-landed slow substep: {stepName} elapsedMs={deadline.ElapsedMilliseconds}");
        failure = $"goal-mark-landed cleanup exceeded {GoalMarkLandedPromptTimeoutMilliseconds}ms before substep '{stepName}'";
        return false;
    }

    try
    {
        result = step(deadline.RemainingMilliseconds);
        if (!deadline.IsExpired)
        {
            return true;
        }

        Console.Error.WriteLine($"goal-mark-landed slow substep: {stepName} elapsedMs={deadline.ElapsedMilliseconds}");
        failure = $"goal-mark-landed cleanup exceeded {deadline.TotalMilliseconds}ms during substep '{stepName}'";
        return false;
    }
    catch (Exception ex)
    {
        if (deadline.IsExpired)
            Console.Error.WriteLine($"goal-mark-landed slow substep: {stepName} elapsedMs={deadline.ElapsedMilliseconds}");
        failure = $"{stepName} failed: {ex.Message}";
        return false;
    }
}

private static void RecordDeferredGoalCleanup(string executionDirectory, GoalId goalId, string reason)
{
    var backoff = GoalWorktrees.RecordGoalCleanupNeeded(executionDirectory, goalId, reason);
    if (backoff is not null)
    {
        Console.WriteLine($"Cleanup backoff: {GoalWorktrees.FormatCleanupBackoff(backoff)}");
    }
}

private static void PrintGoalCleanupBackoffStatus(string executionDirectory, GoalId goalId)
{
    var backoff = GoalWorktrees.TryGetCleanupBackoff(executionDirectory, goalId);
    if (backoff is null)
        return;

    Console.WriteLine($"Cleanup backoff: {GoalWorktrees.FormatCleanupBackoff(backoff)}");
    Console.WriteLine($"Cleanup retry: conduct {goalId.Value[..8].ToLowerInvariant()} --loop");
}

private sealed class GoalMarkLandedCleanupDeadline
{
    private readonly System.Diagnostics.Stopwatch stopwatch;
    private readonly Func<long>? elapsedMilliseconds;

    private GoalMarkLandedCleanupDeadline(int totalMilliseconds, Func<long>? elapsedMilliseconds)
    {
        TotalMilliseconds = totalMilliseconds;
        this.elapsedMilliseconds = elapsedMilliseconds;
        stopwatch = System.Diagnostics.Stopwatch.StartNew();
    }

    public int TotalMilliseconds { get; }

    public long ElapsedMilliseconds => elapsedMilliseconds?.Invoke() ?? stopwatch.ElapsedMilliseconds;

    public int RemainingMilliseconds => Math.Max(1, TotalMilliseconds - (int)Math.Min(int.MaxValue, ElapsedMilliseconds));
    public bool IsExpired => ElapsedMilliseconds >= TotalMilliseconds;

    public static GoalMarkLandedCleanupDeadline Start(int totalMilliseconds, Func<long>? elapsedMilliseconds) =>
        new(totalMilliseconds, elapsedMilliseconds);
}

private static void PrintGoalMarkLandedSummary(
    bool hadWorktree,
    bool branchDeleted,
    bool appHostLockReleased,
    bool cleanupComplete)
{
    Console.WriteLine("Goal landed cleanup:");
    Console.WriteLine(hadWorktree
        ? cleanupComplete ? "cleanup: worktree removed" : "cleanup: worktree cleanup deferred"
        : "cleanup: worktree already absent");
    Console.WriteLine(branchDeleted ? "cleanup: branch deleted" : "cleanup: branch cleanup deferred");
    Console.WriteLine(appHostLockReleased ? "cleanup: app-host lock released" : "cleanup: app-host lock cleanup deferred");
    Console.WriteLine(cleanupComplete ? "cleanup: goal marked CleanedUp" : "cleanup: goal marked landed; cleanup-needed recorded");
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
