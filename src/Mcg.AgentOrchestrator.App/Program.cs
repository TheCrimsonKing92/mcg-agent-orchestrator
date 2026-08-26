using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

CliProtectedProcessEnvironment.EnsureProtectedPid();

// Hidden detached dispatch-host entrypoint (see DispatchProcessHost). Must run before any tenant,
// workspace, or state setup so the detached worker process stays minimal and self-contained.
if (args.Length >= 2 && args[0] == DispatchProcessHost.SubcommandName)
{
    return DispatchProcessHost.Run(args[1]);
}

if (args.Length >= 2 && args[0] == ConductorParallelAcceptanceAttemptCoordinator.OwnedProcessSubcommandName)
{
    return ConductorParallelAcceptanceAttemptCoordinator.RunOwnedProcess(args[1]);
}

if (args.Length >= 1 && args[0] == ConductorSuccessorSelfCheck.SubcommandName)
{
    return ConductorSuccessorSelfCheck.Run(args);
}

if (args.Length >= 1 && args[0] == PostLandingCanaryCommand.SubcommandName)
{
    return PostLandingCanaryCommand.Run(args);
}

var executionDirectory = Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable);
OrchestratorProjectSelection projectSelection;
OrchestratorTenantSelection tenantSelection;
try
{
    projectSelection = OrchestratorProjectSelection.FromArgs(
        args,
        Environment.GetEnvironmentVariable(OrchestratorProjectSelection.ProjectEnvironmentVariable));
    tenantSelection = OrchestratorTenantSelection.FromArgs(
        projectSelection.CommandArgs,
        Environment.GetEnvironmentVariable(OrchestratorTenantSelection.TenantEnvironmentVariable));
}
catch (Exception ex)
{
    Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
    return 1;
}

var startupArgs = tenantSelection.CommandArgs.Count == 0
    ? []
    : CliArgumentParser.NormalizeArgs(tenantSelection.CommandArgs.ToArray());
try
{
    if (CliCommandHandlers.TryPrintStartupHelp(startupArgs))
    {
        return 0;
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

// MCG_ORCHESTRATOR_REPOSITORY_ROOT pins the workspace root explicitly (used by tests and launchers
// that set CWD to a temp or non-repo directory). When absent, walk up the directory tree to find
// a Git repository root so .orchestrator is rooted with the target repo regardless of launch CWD.
var repoRoot = !string.IsNullOrWhiteSpace(executionDirectory)
    ? executionDirectory
    : OrchestratorWorkspace.ResolveRepoRoot(Environment.CurrentDirectory);
var projectRegistry = OrchestratorProjectRegistry.CreateDefault();
if (startupArgs.Count > 0 && startupArgs[0].Equals("project", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        return ProjectCliCommand.Execute(startupArgs, projectRegistry, repoRoot, projectSelection.ProjectName);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
        return 1;
    }
}

OrchestratorProject activeProject;
try
{
    activeProject = projectRegistry.ResolveActiveProject(repoRoot, projectSelection.ProjectName);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
    return 1;
}

var workspace = activeProject.Name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
    ? OrchestratorWorkspace.ForDirectory(
        activeProject.RootDirectory,
        string.IsNullOrWhiteSpace(executionDirectory) ? null : executionDirectory,
        tenantSelection.TenantName)
    : OrchestratorWorkspace.ForProject(
        activeProject.Name,
        activeProject.RootDirectory,
        tenantName: tenantSelection.TenantName);
try
{
    if (CliCommandHelp.TryPrintStartupHelp(startupArgs))
    {
        return 0;
    }

    CliCommandHelp.ThrowIfInvalidFlags(startupArgs);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

if (GoalBoardCommand.IsBoardCommand(startupArgs))
{
    return ExitCompletedStartupCommand(ProgramStartupLifecycle.RunGoalBoard(startupArgs, workspace));
}

if (ConductorContinuitySupervisor.ShouldSupervise(
        startupArgs,
        ProgramStartupLifecycle.IsAuthorityTransferRequested(startupArgs)))
{
    try
    {
        var maxDurationRestage = Environment.GetEnvironmentVariable(
            "MCG_ORCHESTRATOR_MAX_DURATION_RESTAGE");
        var restageEnabled =
            !string.Equals(maxDurationRestage, "0", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(maxDurationRestage, "false", StringComparison.OrdinalIgnoreCase);
        Func<CancellationToken, ConductorPreparedSuccessor>? stageSuccessor = null;
        if (restageEnabled)
        {
            var repositoryRoot = workspace.ExecutionDirectory;
            var repositoryBuildKey = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(repositoryRoot))))[..16];
            var appOutputDirectory = Path.Combine(
                Path.GetTempPath(),
                "mcg-self-relaunch-build",
                repositoryBuildKey);
            var stagingOptions = new ConductorSuccessorStagingOptions(
                RepositoryRoot: repositoryRoot,
                AppProjectPath: Path.Combine(repositoryRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
                AppDllPath: Path.Combine(appOutputDirectory, "Mcg.AgentOrchestrator.App.dll"),
                UpdateHeadMarkerScriptPath: Path.Combine(repositoryRoot, "scripts", "Update-AppDllGitHeadMarker.ps1"),
                ResolveRunDirectoryScriptPath: Path.Combine(repositoryRoot, "scripts", "resolve-run-dir.ps1"),
                StateStorePath: workspace.SqliteStatePath,
                AgentCatalogPath: workspace.AgentCatalogPath,
                WorkerProfilePath: workspace.WorkerProfilePath,
                ModelFunctionCatalogPath: workspace.ModelFunctionCatalogPath,
                DotnetPath: Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_DOTNET_PATH") ?? "dotnet",
                PowerShellPath: "powershell");
            stageSuccessor = cancellationToken =>
                ConductorSelfRelaunch.PrepareSuccessor(stagingOptions, cancellationToken);
        }

        var conductEventLogWriter = new ConductEventLogWriter(workspace.ConductEventsLogPath);
        var supervisor = new ConductorContinuitySupervisor(
            new SystemConductorSupervisorProcessHost(),
            new SqliteRunEventStore(workspace.RunEventStorePath),
            stageSuccessor: stageSuccessor,
            appendConductEvent: (eventKind, goalId, detail) =>
                conductEventLogWriter.Append(eventKind, goalId, detail));
        return await supervisor.RunAsync(
            startupArgs,
            workspace.ExecutionDirectory,
            Path.Combine(workspace.OrchestratorDirectory, "continuity"),
            activeProject.Name,
            tenantSelection.TenantName,
            workspace.LogDirectory);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
        return 1;
    }
}

try
{
    GoalWorktreeOrphanSweepScheduler.Configure(
        WorktreeCleanupConfiguration.Load(AppContext.BaseDirectory),
        workspace.OrchestratorDirectory);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
    return 1;
}

if (IsGoalEventsFollowCommand(startupArgs))
{
    try
    {
        await GoalEventsCommand.RunAsync(
            workspace.GoalLifecycleEventsDirectory,
            startupArgs[1],
            follow: true,
            Console.Out);
        return ExitCompletedStartupCommand(0);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return ExitCompletedStartupCommand(1);
    }
}

var providers = ProviderRegistryFactory.CreateDefaultProviders();
var agentFallback = ProviderRegistryFactory.IsLlamaCppReachable() ? AgentCatalog.LlamaCppDefault() : null;
var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath, agentFallback).Agents;
var workerProfiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
var operatorCatalog = OperatorChannelStore.Load(workspace.OperatorChannelPath);
var operatorBotToken = OperatorChannelFactory.ResolveBotToken();
IOperatorChannel operatorChannel;
if (SkipsStartupOperatorChannel(startupArgs))
{
    operatorChannel = NullOperatorChannel.Instance;
}
else
{
    try
    {
        operatorChannel = OperatorChannelComposition.Create(operatorCatalog, operatorBotToken, workspace.OrchestratorDirectory);
    }
    catch
    {
        operatorChannel = NullOperatorChannel.Instance;
    }
}

if (startupArgs.Count > 0 && startupArgs[0].Equals("prototype-ui", StringComparison.OrdinalIgnoreCase))
{
    return DashboardHost.RunPrototypeUi(startupArgs, providers, agentFallback, tenantSelection.TenantName);
}

if (startupArgs.Count > 0 && startupArgs[0].Equals("prototype", StringComparison.OrdinalIgnoreCase))
{
    var objective = startupArgs.Count > 1 ? string.Join(' ', startupArgs.Skip(1)) : "Create a Windows-based agent orchestrator";
    var prototypeKernel = new AgentOrchestratorKernel();
    var prototypeGoal = GoalLifecycleCommands.CreateAndActivateGoal(prototypeKernel, agents, objective);
    ConsoleViews.PrintGoal(prototypeGoal);
    ConsoleViews.PrintTimeline(prototypeGoal);
    return 0;
}

if (CliPersistentStateRunner.IsGoalIntakeStatusCommand(startupArgs))
{
    try
    {
        ProgramStartupLifecycle.EnsureStateDbInitialized(startupArgs, workspace);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
        return ExitCompletedStartupCommand(1);
    }
}

if (TrialCompareCliCommand.RequiresHistoricalState(startupArgs))
{
    try
    {
        var historicalStateRepository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
        CliReadOnlyCommandRunner.ExecuteHistoricalTrialCompare(
            startupArgs,
            historicalStateRepository,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            operatorChannel);
        return ExitCompletedStartupCommand(0);
    }
    catch (CliExitException ex)
    {
        return ExitCompletedStartupCommand(ex.ExitCode);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return ExitCompletedStartupCommand(1);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
        return ExitCompletedStartupCommand(1);
    }
}

if (CliPersistentStateRunner.SkipsKernelState(startupArgs))
{
    var commandKernel = new AgentOrchestratorKernel();
    Goal? commandCurrentGoal = null;
    try
    {
        CliCommandDispatcher.ExecuteCommand(startupArgs, commandKernel, workspace, ref agents, providers, ref workerProfiles, ref commandCurrentGoal, operatorChannel);
        return ExitCompletedStartupCommand(0);
    }
    catch (CliExitException ex)
    {
        return ExitCompletedStartupCommand(ex.ExitCode);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return ExitCompletedStartupCommand(1);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
        return ExitCompletedStartupCommand(1);
    }
}

ITransactionalOrchestratorStateRepository stateRepository;
AgentOrchestratorKernel kernel;
Goal? currentGoal;
try
{
    var isConductLoop = CliPersistentStateRunner.IsConductLoop(startupArgs);
    var authorityTransferRequested =
        ProgramStartupLifecycle.IsAuthorityTransferRequested(startupArgs);
    if (isConductLoop)
    {
        // A handoff successor must not migrate or load state until the incumbent
        // explicitly transfers its sole-writer authority.
        ConductorLoopHandoff.WaitForAuthorityTransferIfRequested();
    }

    ProgramStartupLifecycle.EnsureStateDbInitialized(startupArgs, workspace);

    ProgramStartupLifecycle.InitializeWorkerProcessTracking(
        RunsStartupCleanup(startupArgs),
        authorityTransferRequested,
        workspace.SqliteStatePath,
        workspace.ExecutionDirectory);
    stateRepository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
    kernel = await stateRepository.LoadAsync();
    currentGoal = OrchestratorEntityResolver.GetLatestGoal(kernel);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
    return 1;
}

if (startupArgs.Count > 0)
{
    try
    {
        CliPersistentStateRunner.ExecuteCommand(startupArgs, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, operatorChannel);
        return ExitCompletedStartupCommand(0);
    }
    catch (CliExitException ex)
    {
        return ExitCompletedStartupCommand(ex.ExitCode);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return ExitCompletedStartupCommand(1);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
        return ExitCompletedStartupCommand(1);
    }
}

Console.WriteLine("MCG Agent Orchestrator");
Console.WriteLine($"Project: {workspace.ProjectName}");
Console.WriteLine($"Tenant: {workspace.TenantName}");
Console.WriteLine($"State: {workspace.SqliteStatePath}");
Console.WriteLine();
Console.WriteLine("Fundamentals:");
Console.WriteLine("  next [goal-id] [--full] [--autonomy <policy>]");
Console.WriteLine("    Show recommended next action and print the exact command to run it.");
Console.WriteLine("    --full: also surfaces status, monitor, readiness, evidence, stages, gates, verify-needed,");
Console.WriteLine("            input-needed, subscription-plan, model-outcomes, durations, dispatch-value, loop-health, failure-triage,");
Console.WriteLine("            goal-recovery, supervisor, and operator-inbox detail in one output.");
Console.WriteLine("  goal <objective> [--pipeline auto|five-role] [--simple] [--from-backlog] [--run --confirm-batch-start]");
Console.WriteLine("    Create a goal. --pipeline: automatic selection or explicitly require five roles. --simple: single Developer task. --from-backlog: read from the backlog store. --run: create and start.");
Console.WriteLine("  accept [goal-id] [--skip-verify] [--autonomy <policy>]");
Console.WriteLine("    Accept a completed goal: run acceptance checks, merge workspace, and clean up worktree.");
Console.WriteLine("  stop <goal-id> <reason>|--text-file <path> --as cancel|park|abandon|supersede [--confirm-goal-stop|--confirm-goal-park|--confirm-goal-abandon]");
Console.WriteLine("    Stop a goal using the specified disposal mode.");
Console.WriteLine("  config <agents|profiles|policy|doctor>");
Console.WriteLine("    View configuration: agents=agent catalog, profiles=worker profiles, policy=autonomy policies, doctor=health check.");
Console.WriteLine("  project list|show [name]|create <name> --root <path>|select <name>");
Console.WriteLine("    Manage global project registry and active project selection.");
Console.WriteLine("  dashboard [path] [--refresh seconds] | --mode local|hosted|read-only [port|url] [--refresh seconds] [--open] [--no-open]");
Console.WriteLine("    Render static dashboard HTML or launch a hosted dashboard server.");
Console.WriteLine();
Console.WriteLine("Advanced/Internal (used by automation, tests, and advanced workflows):");
Console.WriteLine("  Inspection verbs folded into 'next --full': status, monitor, readiness, evidence, stages, gates,");
Console.WriteLine("    verify-needed, input-needed, subscription-plan, model-outcomes, durations, dispatch-value, loop-health, failure-triage,");
Console.WriteLine("    goal-recovery, supervisor, operator-inbox. All still work standalone.");
Console.WriteLine("  doctor, architecture, tenant, project");
Console.WriteLine("  provider-smoke [openai|anthropic|ollama] [--confirm-paid-smoke] [task-number], provider-smoke all --confirm-all");
Console.WriteLine("  prototype [objective], prototype-ui [url] [--refresh seconds] [--open] [--no-open]");
Console.WriteLine("  serve-dashboard [port|url] [--refresh seconds] [--open] [--no-open]");
Console.WriteLine("  operator-listen");
Console.WriteLine("  hosted-dashboard [port|url] [--refresh seconds] [--open] [--no-open]");
Console.WriteLine("  simple-hosted-dashboard [port|url] [--refresh seconds] [--open] [--no-open]");
Console.WriteLine("  open-dashboard [port|url] [--refresh seconds] [--open] [--no-open]");
Console.WriteLine("  transcript [path]");
Console.WriteLine("  simple-goal <objective>, goal-plan [heading-filter] [--create-goals|--create-simple-goals] [--backlog-coverage full|slice]");
Console.WriteLine("  backlog-intake [heading-filter] [--create-goal [--pipeline auto|five-role]|--create-simple-goal] [--backlog-coverage full|slice] [--force-reclaim]");
Console.WriteLine("  intent-template [template request] [--create-goal|--create-simple-goal]");
Console.WriteLine("  goals, agents, autonomy-policies");
Console.WriteLine("  agent <role> <provider> <model> [name] [--complex-model <model>] (replace role)");
Console.WriteLine("  agent-add <role> <provider> <model> [name] [--complex-model <model>] (add/replace id)");
Console.WriteLine("  reassign-agent <task-number> <agent-id> (persist exact task agent assignment)");
Console.WriteLine("  goals subscribe [<goal-id>|--goal-prefix <prefix>] [--since <event-id>|--from-cursor <cursor>] [--once] [--format ndjson|human] [--task <id>] [--event-kind <kind,...>] [--wait-terminal]");
Console.WriteLine("  monitor-goal <goal-id> [--since <event-id>|--from-cursor <cursor>] [--once] [--format sse|ndjson|human] [--goal-prefix <prefix>] [--task <id>] [--event-kind <kind,...>] [--wait-terminal]");
Console.WriteLine("  monitor-goal <dashboard-url> <goal-id> [--since <event-id>] [--once]");
Console.WriteLine("  acceptance [goal-id] [--autonomy <policy>], workspace [create|merge|remove] [goal-id-prefix] [--force-terminal-cleanup] [--autonomy <policy>]");
Console.WriteLine("  advance [goal-id], advance-subscription [goal-id] --confirm-subscription-advance [--autonomy <policy>]");
Console.WriteLine("  run-goal [goal-id] --confirm-batch-start [--confirm-readiness-risk] [--autonomy <policy>]");
Console.WriteLine("  lifecycle-goal <objective> --confirm-batch-start [--autonomy <policy>]");
Console.WriteLine("  lifecycle-simple-goal <objective> --confirm-batch-start [--autonomy <policy>]");
Console.WriteLine("  delegate [goal-id], cancel-goal <goal-id> <reason> [--confirm-goal-stop]");
Console.WriteLine("  supersede-goal <goal-id> <reason> [--confirm-goal-stop]");
Console.WriteLine("  park-goal <goal-id> <reason> [--confirm-goal-park]");
Console.WriteLine("  rollback-goal <goal-id> <reason> [--confirm-goal-rollback]");
Console.WriteLine("  abandon-goal <goal-id> <reason> [--confirm-goal-abandon]");
Console.WriteLine("  task <task-number|task-id-prefix>, tasks [status <status>] [role <role>] [id <id>] [evidence <kind>] [event <kind>]");
Console.WriteLine("  add-task [--goal <goal-prefix>] <role> <description> [--before-role <role>], verification-plan <task-number> [plan], brief <task-number>");
Console.WriteLine("  timeline [goal-id], task-timeline <task-number>, pending");
Console.WriteLine("  goal-events <goal-id-prefix> [--follow]");
Console.WriteLine("  run <task-number> [--confirm-paid-api-run] [--autonomy <policy>]");
Console.WriteLine("  api-run <task-number> [--confirm-paid-api-run] [--autonomy <policy>]");
Console.WriteLine("  retry <task-number> <message> [--autonomy <policy>], note <task-number> <message> [--gate-deliverable <id>...]");
Console.WriteLine("  dispatch <task-number> <worker-name> <command>, worker-profiles, worker-profile <name> <command-template>");
Console.WriteLine("  worker-profile-check [name], worker-profile-export <path>, worker-profile-import <path> [merge|replace]");
Console.WriteLine("  worker-dispatch <task-number> <worker-name> <command-template>, profile-dispatch <task-number> <profile-name>");
Console.WriteLine("  subscription-dispatch <task-number>, subscription-dispatch-ready");
Console.WriteLine("  start-subscription-ready --confirm-batch-start [--autonomy <policy>]");
Console.WriteLine("  execute-dispatch <task-number> --confirm-dispatch-start [--autonomy <policy>]");
Console.WriteLine("  start-dispatch <task-number> --confirm-dispatch-start [--autonomy <policy>]");
Console.WriteLine("  start-dispatches --confirm-batch-start [--autonomy <policy>]");
Console.WriteLine("  refresh-dispatch <task-number> [--history] [--history-limit <n>], refresh-dispatches");
Console.WriteLine("  logs <task-number> [stdout|stderr|exit|all], cancel-dispatch <task-number>");
Console.WriteLine("  verify <task-number> <command>, verify-manual <task-number> <passed|failed> <note>, verifications <task-number>");
Console.WriteLine("  progress <task-number> <status> <message>, ask <task-number> <question>, ask-goal <question>, answer <request-id> <answer> [--gate-deliverable <id>...]");
Console.WriteLine("  gate-satisfied <request-id|task-note-record-id> <deliverable-id> <evidence>");
Console.WriteLine("  goal-plan, retention-plan, build-lease-cleanup --confirm-build-lease-cleanup");
Console.WriteLine("  acceptance-queue [--apply --confirm-acceptance-queue], drain-goals [--apply --confirm-goal-drain --confirm-batch-start]");
Console.WriteLine("  operator-inbox-ack <item-id> [note], cross-goal-start-plan, start-subscription-ready-goals --confirm-batch-start");
Console.WriteLine("  dogfood-eval [goal-id]");
Console.WriteLine("  exit");
Console.WriteLine();
Console.WriteLine("Task commands accept --goal <goal-prefix> <task-number> and, for display-number tasks, <goal-prefix> <task-number>.");

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null || line.Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        return 0;
    }

    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    try
    {
        var command = CliArgumentParser.SplitCommand(line);
        if (command.Count > 0 && command[0].Equals("project", StringComparison.OrdinalIgnoreCase))
        {
            ProjectCliCommand.Execute(command, projectRegistry, repoRoot, projectSelection.ProjectName);
            continue;
        }

        CliPersistentStateRunner.ExecuteCommand(command, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, operatorChannel);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}

static bool SkipsStartupOperatorChannel(IReadOnlyList<string> startupArgs)
{
    if (startupArgs.Count == 0)
        return false;

    var command = startupArgs[0];
    return command.Equals("dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("serve-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("simple-hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("open-dashboard", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("prototype-ui", StringComparison.OrdinalIgnoreCase) ||
        command.Equals("operator-listen", StringComparison.OrdinalIgnoreCase);
}

// Worktree cleanup remains lifecycle-owning. Worker registry setup and orphan classification are safe for
// every command because reaping requires positive dead/recycled owner-process evidence.
static bool RunsStartupCleanup(IReadOnlyList<string> startupArgs) =>
    !CliPersistentStateRunner.SkipsKernelState(startupArgs) &&
    !CliPersistentStateRunner.RequiresKernelBacklogState(startupArgs);

static int ExitCompletedStartupCommand(int exitCode)
{
    Console.Out.Flush();
    Console.Error.Flush();
    Environment.Exit(exitCode);
    return exitCode;
}

static bool IsGoalEventsFollowCommand(IReadOnlyList<string> startupArgs)
{
    return startupArgs.Count >= 3 &&
        startupArgs[0].Equals("goal-events", StringComparison.OrdinalIgnoreCase) &&
        startupArgs.Any(arg => arg.Equals("--follow", StringComparison.OrdinalIgnoreCase));
}

internal static class ProgramStartupLifecycle
{
    internal static int RunGoalBoard(
        IReadOnlyList<string> startupArgs,
        OrchestratorWorkspace workspace,
        Func<string, IOrchestratorStateRepository>? repositoryFactory = null,
        Func<ProcessCommandLineSnapshot>? processSnapshotFactory = null)
    {
        try
        {
            var repository = (repositoryFactory ?? SqliteOrchestratorStateRepository.OpenReadOnly)(workspace.SqliteStatePath);
            GoalBoardCommand.Run(
                startupArgs,
                repository,
                workspace,
                processSnapshotFactory: processSnapshotFactory);
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
            return 1;
        }
    }

    internal static bool IsAuthorityTransferRequested(IReadOnlyList<string> _) =>
        ConductorLoopHandoff.IsAuthorityTransferRequested;

    internal static void EnsureStateDbInitialized(
        IReadOnlyList<string> _startupArgs,
        OrchestratorWorkspace workspace)
    {
        if (StateDbMigrations.IsUpToDate(workspace.SqliteStatePath))
            return;

        // Every supported app path opens state through this boundary before it can
        // query feature tables. Numbered migrations use BEGIN IMMEDIATE and recheck
        // their durable catalog under that write lock, so concurrent startups remain
        // serialized and idempotent without argv-specific schema authority gaps.
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
    }

    internal static void InitializeWorkerProcessTracking(
        bool runsStartupCleanup,
        bool authorityTransferRequested,
        string stateStorePath,
        string executionDirectory)
    {
        WorkerProcessJobs.ConfigureRegistry(stateStorePath);
        if (authorityTransferRequested)
        {
            return;
        }

        WorkerProcessJobs.SweepStartupOrphans();
        if (runsStartupCleanup)
        {
            GoalWorktreeOrphanSweepScheduler.SweepNow(executionDirectory);
        }
    }
}
