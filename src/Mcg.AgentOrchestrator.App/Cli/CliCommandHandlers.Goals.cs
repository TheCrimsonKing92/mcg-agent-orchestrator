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

private static readonly Regex BacklogObjectiveReferenceRegex = new(
    @"\bbacklog\s+([0-9a-f]{8,64})\b",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

private static readonly Regex OpeningBacklogObjectiveReferenceRegex = new(
    @"\A\s*\(?\s*backlog\s+([0-9a-f]{8,64})\s*\)?",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

private sealed record SourceBacklogItemLink(BacklogItem Item, bool FromExplicitFlag);

private static int PersistResolvedParkedHumanWaitsForNextTick(
    CliExecutionContext context,
    AgentOrchestratorKernel parkedKernel)
{
    var promoted = parkedKernel.RefreshParkedGoalsWithResolvedHumanWaits();
    if (promoted == 0)
    {
        return 0;
    }

    var changedGoalIds = parkedKernel.Goals
        .Where(goal => goal.Status != GoalStatus.Parked)
        .Select(goal => goal.Id)
        .ToArray();
    // Parked goals are persisted here but not ingested into the live loop kernel.
    // The next conduct tick reloads them as normal non-Parked goals before prewalk.
    try
    {
        context.PersistGoalCheckpoint(parkedKernel, changedGoalIds);
    }
    catch (Exception ex)
    {
        var goalPrefixes = string.Join(',', changedGoalIds.Select(id => id.Value[..Math.Min(8, id.Value.Length)]));
        var detail = $"PARKED_UNPARK_PERSISTENCE_FAILED goals={goalPrefixes} count={changedGoalIds.Length} exception={ex.GetType().Name} message={FormatConductToken(ex.Message)}";
        Console.WriteLine(detail);
        Console.Out.Flush();
        try
        {
            new ConductEventLogWriter(context.Workspace.ConductEventsLogPath)
                .Append("parked-unpark-persistence-failure", changedGoalIds.Length == 1 ? goalPrefixes : null, detail);
        }
        catch
        {
            // Shared event streaming is advisory; stdout remains the primary conduct receipt.
        }

        throw;
    }

    return changedGoalIds.Length;
}

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
                var simpleAliasObjective = ResolveBriefObjective(parts, "goal <objective> --simple | goal --brief-file <path> --simple | goal --text-file <path> --simple");
                var simpleAliasParts = new List<string> { "simple-goal", simpleAliasObjective };
                AppendGoalAliasFlags(parts, simpleAliasParts, includeRoleAgentFlags: true, "--simple", "--brief-file", "--text-file");
                return TryExecuteGoalCommand("simple-goal", simpleAliasParts, context);
            }
            // --from-backlog: delegate to backlog-intake (objective used as heading filter)
            if (HasCliConfirmation(parts, "--from-backlog"))
            {
                var backlogFilter = ResolveBacklogIntakeFilter(context, parts, "--from-backlog");
                var backlogAliasParts = new List<string> { "backlog-intake" };
                if (backlogFilter is not null)
                    backlogAliasParts.Add(backlogFilter);
                AppendGoalAliasFlags(parts, backlogAliasParts, includeRoleAgentFlags: false, "--from-backlog", "--brief-file", "--text-file");
                return TryExecuteGoalCommand("backlog-intake", backlogAliasParts, context);
            }
            // --run: create 5-role goal then delegate to run-goal
            if (HasCliConfirmation(parts, "--run"))
            {
                var runObjective = ResolveBriefObjective(parts, "goal <objective> --run | goal --brief-file <path> --run | goal --text-file <path> --run");
                var runObjectivePlan = BuildGoalObjectivePlan(context, runObjective, simple: false);
                GoalObjectivePlanner.ThrowIfBlocked(runObjectivePlan);
                ConsoleViews.PrintGoalObjectivePlan(runObjectivePlan);
                var runSourceBacklogLink = ResolveSourceBacklogItemLink(context, parts, runObjective);
                PrintClosedSourceBacklogWarning(runSourceBacklogLink);
                var runAgents = ApplyRoleAgentOverrides(parts, context.Agents);
                context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, runAgents, runObjective, context.Workspace, context.Providers, context.EventWriter);
                ApplySourceBacklogItemLink(context, context.CurrentGoal, runSourceBacklogLink);
                ConsoleViews.PrintGoal(context.CurrentGoal);
                var runParts = new List<string> { "run-goal", context.CurrentGoal.Id.Value[..8] };
                AppendGoalAliasFlags(parts, runParts, includeRoleAgentFlags: false, "--run", "--brief-file", "--text-file");
                return TryExecuteGoalCommand("run-goal", runParts, context);
            }
            var goalObjective = ResolveBriefObjective(parts, "goal <objective> [--simple] [--from-backlog] [--run] | goal --brief-file <path> | goal --text-file <path>");
            var goalObjectivePlan = BuildGoalObjectivePlan(context, goalObjective, simple: false);
            GoalObjectivePlanner.ThrowIfBlocked(goalObjectivePlan);
            ConsoleViews.PrintGoalObjectivePlan(goalObjectivePlan);
            var goalSourceBacklogLink = ResolveSourceBacklogItemLink(context, parts, goalObjective);
            PrintClosedSourceBacklogWarning(goalSourceBacklogLink);
            var goalAgents = ApplyRoleAgentOverrides(parts, context.Agents);
            context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateGoal(context.Kernel, goalAgents, goalObjective, context.Workspace, context.Providers, context.EventWriter);
            ApplySourceBacklogItemLink(context, context.CurrentGoal, goalSourceBacklogLink);
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "simple-goal":
            var simpleObjective = ResolveBriefObjective(parts, "simple-goal <objective> | simple-goal --brief-file <path> | simple-goal --text-file <path>");
            var simpleObjectivePlan = BuildGoalObjectivePlan(context, simpleObjective, simple: true);
            GoalObjectivePlanner.ThrowIfBlocked(simpleObjectivePlan);
            ConsoleViews.PrintGoalObjectivePlan(simpleObjectivePlan);
            var simpleSourceBacklogLink = ResolveSourceBacklogItemLink(context, parts, simpleObjective);
            PrintClosedSourceBacklogWarning(simpleSourceBacklogLink);
            var simpleAgents = ApplyRoleAgentOverrides(parts, context.Agents);
            context.CurrentGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(context.Kernel, simpleAgents, simpleObjective, context.Workspace, context.Providers, context.EventWriter);
            ApplySourceBacklogItemLink(context, context.CurrentGoal, simpleSourceBacklogLink);
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
            CliArgumentParser.RequirePartCount(parts, 3, $"{command} <goal-id-prefix> <reason> [--confirm-goal-stop] | {command} <goal-id-prefix> --text-file <path> [--confirm-goal-stop]");
            context.CurrentGoal = HandleGoalStopCommand(context, parts, command.Equals("supersede-goal", StringComparison.OrdinalIgnoreCase));
            ConsoleViews.PrintGoal(context.CurrentGoal);
            return true;

        case "abandon-goal":
            CliArgumentParser.RequirePartCount(parts, 3, "abandon-goal <goal-id-prefix> <reason> [--confirm-goal-abandon] | abandon-goal <goal-id-prefix> --text-file <path> [--confirm-goal-abandon]");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
            var abandonParts = RemoveStandaloneFlag(parts, "--confirm-goal-abandon");
            var abandonReason = ResolveTextArgument(abandonParts, inlineIndex: 2, "abandon-goal <goal-id-prefix> <reason> [--confirm-goal-abandon] | abandon-goal <goal-id-prefix> --text-file <path> [--confirm-goal-abandon]", "--text-file");
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
            var forceCleanup = HasCliConfirmation(parts, "--force");
            var landedBranchRef = landedBranch;
            if (!forceCleanup)
            {
                var localBranch = RunGoalMarkLandedStep(
                    "branch-local-check",
                    () => GitCli.Run(landedDir, "rev-parse", "--verify", "--quiet", $"refs/heads/{landedBranch}"));
                var localExists = localBranch.ExitCode == 0;
                var remoteExists = !localExists &&
                    RunGoalMarkLandedStep(
                        "branch-remote-check",
                        () => GitCli.Run(landedDir, "rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{landedBranch}")).ExitCode == 0;
                if (!localExists && !remoteExists)
                    throw new InvalidOperationException(
                        $"goal-mark-landed: branch '{landedBranch}' was not found locally or remotely; ancestry check cannot run. " +
                        "Use --force if you have manually confirmed the work is in main.");
                var branchRef = localExists ? landedBranch : $"origin/{landedBranch}";
                landedBranchRef = branchRef;
                var ancestry = RunGoalMarkLandedStep(
                    "branch-ancestry-check",
                    () => GitCli.Run(landedDir, "merge-base", "--is-ancestor", branchRef, "HEAD"));
                if (ancestry.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"goal-mark-landed: branch '{landedBranch}' is not an ancestor of the current HEAD; " +
                        "verify the work was merged into main before using this command. Use --force to bypass this check.");
            }
            var landedMergeCommit = RunGoalMarkLandedStep(
                "landing-intent-commit-resolve",
                () => GitCli.Run(landedDir, "rev-parse", forceCleanup ? "HEAD" : landedBranchRef));
            if (landedMergeCommit.ExitCode == 0 && !string.IsNullOrWhiteSpace(landedMergeCommit.Output))
            {
                GoalOperationJournal.RecordLandingIntent(
                    landedDir,
                    landedGoal,
                    landedBranch,
                    LandingExecutor.IntegrationBranchName,
                    landedMergeCommit.Output.Trim(),
                    "goal-mark-landed");
            }
            GoalOperationJournal.Begin(landedDir, landedGoal, "conductor:land", "Out-of-band landing recorded via goal-mark-landed.");
            GoalOperationJournal.Completed(landedDir, landedGoal, "conductor:land", $"Goal {landedGp} was already merged to main.");
            GoalOperationJournal.Begin(landedDir, landedGoal, "conductor:record", "Recording out-of-band landing to SQLite dogfood log.");
            RecordDogfoodEntry(context.Workspace, landedGoal);
            GoalOperationJournal.Completed(landedDir, landedGoal, "conductor:record", context.Workspace.DogfoodLogStorePath);
            GoalOperationJournal.RecordTerminalDisposition(
                landedDir,
                landedGoal,
                new GoalTerminalDisposition(
                    GoalTerminalDispositionKind.Retired,
                    $"Goal {landedGp} was marked landed out-of-band via goal-mark-landed; retire from future terminal sweeps."));
            JournalAutoCloseSourceBacklogItem(context, landedGoal);

            var hadWorktree = context.Worktrees.TryResolve(landedDir, landedId) is not null;
            context.Kernel.CompleteGoal(landedId, "Goal marked landed after durable out-of-band landing; cleanup deferred to conductor sweep.");
            context.PersistCheckpoint(context.Kernel);
            RecordDeferredGoalCleanup(context, landedGoal, "remove:goal-mark-landed-deferred", "goal-mark-landed");
            PrintGoalMarkLandedSummary(hadWorktree, cleanupComplete: false);
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
            CliArgumentParser.RequirePartCount(parts, 3, "park-goal <goal-id-prefix> <reason> [--confirm-goal-park] | park-goal <goal-id-prefix> --text-file <path> [--confirm-goal-park]");
            context.CurrentGoal = HandleGoalParkCommand(context, parts);
            return HasCliConfirmation(parts, "--confirm-goal-park") ||
                parts[2].Contains("--confirm-goal-park", StringComparison.OrdinalIgnoreCase);

        case "unpark-goal":
            CliArgumentParser.RequirePartCount(parts, 3, "unpark-goal <goal-id-prefix> <reason> [--confirm-goal-unpark] | unpark-goal <goal-id-prefix> --text-file <path> [--confirm-goal-unpark]");
            context.CurrentGoal = HandleGoalUnparkCommand(context, parts);
            return HasCliConfirmation(parts, "--confirm-goal-unpark") ||
                parts[2].Contains("--confirm-goal-unpark", StringComparison.OrdinalIgnoreCase);

        case "rollback-goal":
            CliArgumentParser.RequirePartCount(parts, 3, "rollback-goal <goal-id-prefix> <reason> [--confirm-goal-rollback] | rollback-goal <goal-id-prefix> --text-file <path> [--confirm-goal-rollback]");
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
            var rollbackParts = RemoveStandaloneFlag(parts, "--confirm-goal-rollback");
            var rollbackReason = ResolveTextArgument(rollbackParts, inlineIndex: 2, "rollback-goal <goal-id-prefix> <reason> [--confirm-goal-rollback] | rollback-goal <goal-id-prefix> --text-file <path> [--confirm-goal-rollback]", "--text-file");
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
            CliArgumentParser.RequirePartCount(parts, 4, "agent <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>] [--subscription-reasoning <effort>]");
            if ((HasCliConfirmation(parts, "--complex-model") && GetFlagValue(parts, "--complex-model") is null) ||
                (HasCliConfirmation(parts, "--subscription-model") && GetFlagValue(parts, "--subscription-model") is null) ||
                (HasCliConfirmation(parts, "--subscription-reasoning") && GetFlagValue(parts, "--subscription-reasoning") is null))
            {
                throw new ArgumentException("Usage: agent <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>] [--subscription-reasoning <effort>]");
            }
            var agent = CreateCliAgentDefinition(parts);
            var previousRoleAgent = context.Agents.FirstOrDefault(existing => existing.Role == agent.Role);
            context.Agents = new AgentCatalog(context.Agents).UpsertRole(agent).Agents;
            AgentCatalogStore.Save(context.AgentCatalogPath, new AgentCatalog(context.Agents));
            WarnAboutTasksPinnedToRemovedAgent(context.Kernel, previousRoleAgent, agent);
            ConsoleViews.PrintAgents(context.Agents);
            return false;

        case "agent-add":
            CliArgumentParser.RequirePartCount(parts, 4, "agent-add <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>] [--subscription-reasoning <effort>]");
            if ((HasCliConfirmation(parts, "--complex-model") && GetFlagValue(parts, "--complex-model") is null) ||
                (HasCliConfirmation(parts, "--subscription-model") && GetFlagValue(parts, "--subscription-model") is null) ||
                (HasCliConfirmation(parts, "--subscription-reasoning") && GetFlagValue(parts, "--subscription-reasoning") is null))
            {
                throw new ArgumentException("Usage: agent-add <role> <provider> <model> [name] [--complex-model <model>] [--subscription-model <model>] [--subscription-reasoning <effort>]");
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
                ResolveGoalStatusText(context.Workspace, context.CurrentGoal),
                new PortfolioStore(context.Workspace.PortfolioStorePath).GetGoalMembershipAsync(context.CurrentGoal.Id.Value).GetAwaiter().GetResult());
            PrintGoalCleanupBackoffStatus(context.Workspace.ExecutionDirectory, context.CurrentGoal.Id);
            return false;

        case "monitor":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            ConsoleViews.PrintMonitor(
                context.Kernel.BuildMonitor(context.CurrentGoal.Id),
                ResolveGoalStatusText(context.Workspace, context.CurrentGoal));
            return false;

        case "goal-timing":
            CliArgumentParser.RequirePartCount(parts, 2, "goal-timing <goal-prefix> | goal-timing --all");
            if (parts[1].Equals("--all", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleViews.PrintGoalTimingRollup(context.Kernel.BuildGoalTimingRollup(BuildGoalTimingContexts(context)));
                return false;
            }

            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
            ConsoleViews.PrintGoalTimingReport(context.Kernel.BuildGoalTimingReport(
                context.CurrentGoal.Id,
                BuildGoalTimingContext(context, context.CurrentGoal)));
            return false;

        case "readiness":
            context.CurrentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts.Count > 1 ? parts[1] : null);
            var readinessSweep = TerminalGoalSweep.Run(context.Kernel, context.Workspace.ExecutionDirectory, context.CurrentGoal.Id);
            ConsoleViews.PrintTerminalGoalSweep(readinessSweep);
            TerminalGoalSweepAttention.Surface(context.Kernel, readinessSweep, context.Workspace.OrchestratorDirectory, context.CurrentGoal.Id);
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
            TerminalGoalSweepAttention.Surface(context.Kernel, recoverySweep, context.Workspace.OrchestratorDirectory, context.CurrentGoal.Id);
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
                if (acceptancePolicy.Kind == AutonomyPolicyKind.SupervisedAuto)
                {
                    var receipt = OperatorInbox.ClearOwnershipHoldsAfterLanding(
                        context.Workspace,
                        context.CurrentGoal,
                        $"acceptance {context.CurrentGoal.Id.Value[..8]} --autonomy supervised-auto");
                    if (receipt.ClearedHoldIds.Count > 0)
                    {
                        Console.WriteLine(
                            $"Ownership holds cleared: goal={context.CurrentGoal.Id.Value[..8]} holds={string.Join(",", receipt.ClearedHoldIds)} command=\"{receipt.TriggeringCommand}\"");
                    }
                }

                if (!noRecord)
                {
                    AutoRecordDogfoodEntry(context);
                }

                JournalAutoCloseSourceBacklogItem(context, context.CurrentGoal);
                CleanupGoalWorkspaceAfterMerge(context, context.CurrentGoal, acceptancePolicy, keepWorkspace);
            }

            ConsoleViews.PrintAcceptanceSummary(
                context.CurrentGoal,
                GoalAcceptanceStatusProjector.Build(context.Kernel, context.CurrentGoal, context.Workspace.ExecutionDirectory));
            return false;

        case "workspace":
            return HandleWorkspaceCommand(context, parts);

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
            TerminalGoalSweepAttention.Surface(context.Kernel, nextSweep, context.Workspace.OrchestratorDirectory, context.CurrentGoal.Id);
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
            ConsoleViews.PrintNextActions(context.CurrentGoal, context.Kernel.BuildNextActions(context.CurrentGoal.Id), context.WorkerProfiles, context.Agents, nextHealth, conductorDisposition);
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
            var advance = GoalManagementCommandService.AdvanceGoalAsync(context.Kernel, context.Agents, context.WorkerProfiles, context.Providers, context.Workspace, context.CurrentGoal)
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
            var landResult = LandingExecutor.Execute(
                context.Kernel,
                context.CurrentGoal,
                context.Workspace,
                context.Channel,
                eventWriter: context.EventWriter);
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
                var unscopedStallTickThreshold = ResolveUnscopedStallTickThreshold(parts);

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
                    context.Providers,
                    context.PersistGoalCheckpoint);
                var stopFilePath = Path.Combine(context.Workspace.ExecutionDirectory, ConductorBatchLoop.StopFileName);
                var handoffOptions = new ConductLoopHandoffOptions(
                    Args: parts.ToArray(),
                    ExecutionDirectory: context.Workspace.ExecutionDirectory,
                    OrchestratorDirectory: context.Workspace.OrchestratorDirectory,
                    LogDirectory: context.Workspace.LogDirectory,
                    RunEventStorePath: context.Workspace.RunEventStorePath,
                    StopFilePath: stopFilePath,
                    RenewalCount: ConductorLoopHandoff.ParseRenewalCount(parts),
                    MaxRenewals: ConductorLoopHandoff.DefaultMaxRenewalsWithoutLanding,
                    ReleaseCurrentLease: context.ReleaseConductLoopLease,
                    ReacquireCurrentLease: context.ReacquireConductLoopLease,
                    StopFailedSuccessor: ConductorLoopHandoff.StopFailedSuccessor);
                var handoff = ConductorLoopHandoff.Create(handoffOptions);
                var repositoryRoot = context.Workspace.ExecutionDirectory;
                var appOutputDirectory = Path.Combine(
                    repositoryRoot,
                    "src",
                    "Mcg.AgentOrchestrator.App",
                    "bin",
                    "Debug",
                    "net10.0");
                var selfRelaunch = ConductorSelfRelaunch.Create(new ConductorSelfRelaunchOptions(
                    RepositoryRoot: repositoryRoot,
                    AppProjectPath: Path.Combine(repositoryRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
                    AppDllPath: Path.Combine(appOutputDirectory, "Mcg.AgentOrchestrator.App.dll"),
                    AppHeadMarkerPath: Path.Combine(appOutputDirectory, "Mcg.AgentOrchestrator.App.dll.git-head"),
                    UpdateHeadMarkerScriptPath: Path.Combine(repositoryRoot, "scripts", "Update-AppDllGitHeadMarker.ps1"),
                    ResolveRunDirectoryScriptPath: Path.Combine(repositoryRoot, "scripts", "resolve-run-dir.ps1"),
                    StateStorePath: context.Workspace.SqliteStatePath,
                    AgentCatalogPath: context.Workspace.AgentCatalogPath,
                    WorkerProfilePath: context.Workspace.WorkerProfilePath,
                    ModelFunctionCatalogPath: context.Workspace.ModelFunctionCatalogPath,
                    DotnetPath: Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH") ?? "dotnet",
                    PowerShellPath: "powershell",
                    HandoffOptions: handoffOptions));

                // Reconcile finished dispatches (read exit files, record results, advance tasks) at the
                // start of every tick. Without this the loop holds a goal at Running forever — the worker
                // finishes but its result is never recorded — and a stop/restart re-dispatches the same
                // stage. Fault-isolated so one goal's refresh failure can't kill the loop.
                var terminalSweepCache = new TerminalGoalSweepCache();
                var parkedGoalSafetyNetTick = 0;
                TerminalGoalSweepResult reconcileSweep(AgentOrchestratorKernel loopKernel)
                {
                    // Refresh tracked goals from persisted state before every tick, then ingest newly
                    // submitted goals. This keeps role handoff decisions tied to durable task status
                    // instead of stale loop-local objects.
                    try
                    {
                        var reloadedKernel = context.ReloadKernel();
                        loopKernel.MarkKnownDependencyGoalStatuses(reloadedKernel.KnownDependencyGoalStatuses);
                        loopKernel.MarkKnownCompletedDependencyGoals(reloadedKernel.KnownCompletedDependencyGoals);
                        var snapshot = reloadedKernel.ExportSnapshot();
                        loopKernel.RefreshTrackedGoals(snapshot);
                        loopKernel.IngestNewGoals(snapshot);
                    }
                    catch { /* dynamic pickup is best-effort */ }

                    AgentOrchestratorKernel? resolvedParkedHumanWaitKernel = null;
                    try
                    {
                        // Fast path: every tick, query completed human-input rows for Parked goal ids,
                        // hydrate only those candidates, and persist promotions for the next tick's prewalk.
                        resolvedParkedHumanWaitKernel = context.ReloadResolvedParkedHumanWaitKernel();
                    }
                    catch { /* dynamic pickup is best-effort */ }

                    if (resolvedParkedHumanWaitKernel is not null)
                    {
                        PersistResolvedParkedHumanWaitsForNextTick(context, resolvedParkedHumanWaitKernel);
                    }

                    AgentOrchestratorKernel? parkedGoalSafetyNetKernel = null;
                    parkedGoalSafetyNetTick++;
                    if (CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(parkedGoalSafetyNetTick))
                    {
                        try
                        {
                            parkedGoalSafetyNetKernel = context.ReloadParkedGoalSafetyNetKernel();
                        }
                        catch { /* dynamic pickup is best-effort */ }
                    }

                    if (parkedGoalSafetyNetKernel is not null)
                    {
                        PersistResolvedParkedHumanWaitsForNextTick(context, parkedGoalSafetyNetKernel);
                    }

                    foreach (var resolved in loopKernel.SweepStaleHumanWaits(TimeSpan.FromHours(24)))
                    {
                        Console.WriteLine($"[conduct --loop] Resolved stale human wait {resolved.RequestId.Value[..8]} ({resolved.Kind}) via {resolved.Resolution}.");
                    }

                    foreach (var loopGoal in loopKernel.Goals.ToArray())
                    {
                        try { GoalManagementCommandService.RefreshDispatches(loopKernel, loopGoal); }
                        catch { /* per-goal isolation */ }
                    }

                    var terminalSweep = TerminalGoalSweep.Run(loopKernel, context.Workspace.ExecutionDirectory, cache: terminalSweepCache);
                    ConsoleViews.PrintTerminalGoalSweep(terminalSweep);
                    TerminalGoalSweepAttention.Surface(loopKernel, terminalSweep, context.Workspace.OrchestratorDirectory);
                    GoalWorktreeOrphanSweepScheduler.SweepIfDue(context.Workspace.ExecutionDirectory, loopKernel);
                    RunEventMaintenanceCadence.TryRunIfDue(
                        context.Workspace.RunEventStorePath,
                        context.Workspace.ConductEventsLogPath);
                    RemoteGitMirror.TryStartBackgroundProcessing(loopKernel, context.Workspace.ExecutionDirectory);
                    return terminalSweep;
                }
                var loopReaper = new BackgroundDispatchRunner();
                using var loopWakeSignal = watchInterval is not null
                    ? new FileSystemWatcherConductorWakeSignal(context.Workspace.LogDirectory)
                    : null;
                var loopSummary = new ConductorBatchLoop(
                    measuredSweep: reconcileSweep,
                    reapGoalRunningDispatches: (loopKernel, loopGoal) => loopReaper.CancelRunningProcessesForGoal(loopKernel, loopGoal.Id),
                    detachGoalRunningDispatches: (loopKernel, loopGoal) => loopReaper.DetachRunningProcessesForGoal(loopKernel, loopGoal.Id),
                    recoverInterruptedDispatches: loopKernel => loopReaper.RequeueInterruptedDispatches(loopKernel),
                    refreshGoalDispatchesBeforeAdvance: (loopKernel, loopGoal) => { GoalManagementCommandService.RefreshDispatches(loopKernel, loopGoal); },
                    handoffOnMaxDuration: handoff,
                    conductEventLogWriter: new ConductEventLogWriter(context.Workspace.ConductEventsLogPath),
                    progressiveReviewGlances: ProgressiveReviewGlanceCoordinator.CreateDefault(context.Workspace, context.WorkerProfiles),
                    progressiveReviewSteering: ProgressiveReviewSteeringCoordinator.CreateDefault(context.Workspace, context.Agents, context.WorkerProfiles, context.Providers),
                    selfRelaunch: selfRelaunch).Run(
                    context.Kernel, loopDriver, loopPolicy, stopFilePath, loopMaxIter,
                    watchInterval: watchInterval, onTick: onTick, wakeSignal: loopWakeSignal, maxDuration: maxDuration,
                    persistTick: context.PersistCheckpoint, keepAliveWhenIdle: loopDaemon,
                    persistGoalTick: context.PersistGoalCheckpoint,
                    buildOperatorDispositions: loopKernel => ConductorOperatorDispositionSnapshots.Build(loopKernel, context.Workspace.ExecutionDirectory),
                    quiet: quietWatchProgress,
                    stallWarningThreshold: stallWarningThreshold,
                    unscopedStallTickThreshold: unscopedStallTickThreshold);
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
                context.Providers,
                context.PersistGoalCheckpoint);

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
                    RemoteGitMirror.TryStartBackgroundProcessing(wk, context.Workspace.ExecutionDirectory, context.CurrentGoal.Id);
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
                    (wk, goal) => { GoalManagementCommandService.RefreshDispatches(wk, goal); },
                    conductEventLogWriter: new ConductEventLogWriter(context.Workspace.ConductEventsLogPath),
                    progressiveReviewGlances: ProgressiveReviewGlanceCoordinator.CreateDefault(context.Workspace, context.WorkerProfiles),
                    progressiveReviewSteering: ProgressiveReviewSteeringCoordinator.CreateDefault(context.Workspace, context.Agents, context.WorkerProfiles, context.Providers)).Run(
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
}
