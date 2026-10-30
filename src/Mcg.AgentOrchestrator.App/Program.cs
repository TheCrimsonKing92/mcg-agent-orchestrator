using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

CliProtectedProcessEnvironment.EnsureProtectedPid();

if (args.Length >= 1 && args[0] == CompletionBuildEvidenceCoordinator.Subcommand)
{
    return CompletionBuildEvidenceCoordinator.RunChild(args);
}

// Hidden detached dispatch-host entrypoint (see DispatchProcessHost). Must run before any tenant,
// workspace, or state setup so the detached worker process stays minimal and self-contained.
if (args.Length >= 2 && args[0] == DispatchProcessHost.SubcommandName)
{
    return DispatchProcessHost.Run(args[1], reference => new WorkerBuildRequestHost(reference), DispatchJobExecutionQos.Start);
}

if (args.Length >= 2 && args[0] == ConductorParallelAcceptanceAttemptCoordinator.OwnedProcessSubcommandName)
{
    return ConductorParallelAcceptanceAttemptCoordinator.RunOwnedProcess(args[1]);
}

if (args.Length >= 2 && args[0] == ConductorGroupedGateAttemptCoordinator.OwnedProcessSubcommandName)
{
    return ConductorGroupedGateAttemptHost.Run(args[1]);
}

if (args.Length >= 1 && args[0] == ConductorSuccessorSelfCheck.SubcommandName)
{
    return ConductorSuccessorSelfCheck.Run(args);
}

if (args.Length >= 1 && args[0] == PostLandingCanaryCommand.SubcommandName)
{
    return PostLandingCanaryCommand.Run(args);
}

// Process inspection and guarded stopping deliberately bypass workspace, provider, and catalog
// startup. These bounded operator primitives must remain available and cheap while unrelated
// orchestrator source is changing; their wrapper only reuses an older build when this early path
// and its process subsystem are unchanged.
if (args.Length >= 1 && args[0].Equals("repo-process-info", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        RepoProcessCliCommand.PrintInfo(args, Console.Out);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

if (args.Length >= 1 && args[0].Equals("repo-process-stop", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        return RepoProcessCliCommand.Stop(args, Console.Out) ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

// Explicit host setup bypasses workspace and conductor startup, including elevated runs.
if (CliHostExclusionsCommand.IsCommand(args) && !CliCommandHelp.IsCommandSpecificHelp(args))
{
    return CliHostExclusionsCommand.Run(args);
}

var executionDirectory = Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable);
WorkspaceInvocationBinding invocationBinding;
OrchestratorProjectSelection projectSelection;
OrchestratorTenantSelection tenantSelection;
try
{
    invocationBinding = WorkspaceInvocationBinding.Parse(args);
    projectSelection = OrchestratorProjectSelection.FromArgs(
        invocationBinding.CommandArgs,
        Environment.GetEnvironmentVariable(OrchestratorProjectSelection.ProjectEnvironmentVariable));
    tenantSelection = OrchestratorTenantSelection.FromArgs(
        projectSelection.CommandArgs,
        Environment.GetEnvironmentVariable(OrchestratorTenantSelection.TenantEnvironmentVariable));
    invocationBinding.ValidateInheritedSelectors(executionDirectory,
        Environment.GetEnvironmentVariable(OrchestratorProjectSelection.ProjectEnvironmentVariable),
        Environment.GetEnvironmentVariable(OrchestratorTenantSelection.TenantEnvironmentVariable), projectSelection, tenantSelection);
    executionDirectory = invocationBinding.RepositoryRoot ?? executionDirectory;
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
    if (!(startupArgs.Count > 0 && startupArgs[0] == AcceptanceJobCustodianHost.Subcommand) &&
        CliCommandHandlers.TryPrintStartupHelp(startupArgs))
    {
        return 0;
    }
    if (CliArgumentParser.IsAskGoal(startupArgs))
        _ = CliArgumentParser.ParseAskGoalArgs(startupArgs);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

try
{
    CliPersistentStateRunner.ValidateGoalRefinementStartupArguments(startupArgs);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
    return 1;
}

var commandCapability = CliCommandCapabilities.Classify(startupArgs);

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
        if (invocationBinding.DisposableFixture || invocationBinding.ExpectedStateDirectory is not null)
            throw new InvalidOperationException("A state-directory binding cannot authorize project registry commands; manage the registry separately, then bind the resolved workspace.");
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

OrchestratorWorkspace ResolveSelectedWorkspace(OrchestratorProject project) => project.Name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
    ? OrchestratorWorkspace.ForDirectory(
        project.RootDirectory,
        string.IsNullOrWhiteSpace(executionDirectory) ? null : executionDirectory,
        tenantSelection.TenantName, projectRegistry)
    : OrchestratorWorkspace.ForProject(
        project.Name,
        project.RootDirectory,
        tenantName: tenantSelection.TenantName,
        integrationBranch: project.IntegrationBranch,
        dataRootDirectory: project.DataRootDirectory);
OrchestratorWorkspace workspace;
try
{
    workspace = ResolveSelectedWorkspace(activeProject);
    invocationBinding.Verify(workspace);
    CliAskGoalCommand.ValidateStartupArguments(startupArgs, workspace);
    GateHeartbeatOwner.Bind(workspace.OrchestratorDirectory);
    HostLedgerStateDirectory.InstallRoute(host => HostLedgerStateDirectoryLocator.Resolve(host, projectRegistry));
}
catch (Exception ex)
{
    Console.Error.WriteLine(ProgramStartupErrorFormatter.Format(ex));
    return 1;
}
void VerifyWorkspaceBinding()
{
    var resolved = ResolveSelectedWorkspace(projectRegistry.ResolveActiveProject(repoRoot, projectSelection.ProjectName));
    invocationBinding.Verify(resolved);
    if (resolved != workspace) throw new InvalidOperationException("Workspace selection changed during startup; invocation refused.");
}
// Read-only prelaunch handshake for detached launchers. It never initializes stores/catalogs.
if (startupArgs.Count == 2 && startupArgs[0] == "__workspace-binding-check" && startupArgs[1] == "--json")
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        repositoryRoot = workspace.RootDirectory, stateDirectory = workspace.OrchestratorDirectory,
        executionDirectory = workspace.ExecutionDirectory, project = workspace.ProjectName, tenant = workspace.TenantName,
        stopFile = workspace.ConductorStopFilePath
    }));
    return 0;
}
if (startupArgs.Count > 0 && startupArgs[0] == AcceptanceJobCustodianHost.Subcommand)
{
    if (invocationBinding.RepositoryRoot is null || invocationBinding.ExpectedStateDirectory is null)
    {
        Console.Error.WriteLine("Acceptance custody requires explicit workspace binding.");
        return 1;
    }
    return AcceptanceJobCustodianHost.Run(startupArgs);
}
OrchestratorHome.ExportForDescendants(OrchestratorHome.ResolveForLaunch(repoRoot));
OrchestratorDataRoot.ExportForDescendants();
CrosswalkExecutableComposition.Install();
try
{
    if (CliCommandHelp.TryPrintStartupHelp(startupArgs))
    {
        return 0;
    }

    if (CliImpactReplayCommand.IsCommand(startupArgs))
    {
        return ExitCompletedStartupCommand(CliImpactReplayCommand.Run(startupArgs, workspace));
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

if (CliOwnerDigestCommand.IsCommand(startupArgs))
{
    return ExitCompletedStartupCommand(CliOwnerDigestCommand.Run(startupArgs, workspace));
}

if (CliLessonCommands.IsCommand(startupArgs))
{
    return ExitCompletedStartupCommand(CliLessonCommands.Run(startupArgs, workspace));
}

if (CliDirectiveCommands.IsCommand(startupArgs))
{
    return ExitCompletedStartupCommand(CliDirectiveCommands.Run(startupArgs, workspace));
}
if (CliExecutorSettingCommands.IsCommand(startupArgs))
{
    return ExitCompletedStartupCommand(CliExecutorSettingCommands.Run(startupArgs, workspace));
}
if (CliGateCancelCommand.IsCommand(startupArgs))
{
    return ExitCompletedStartupCommand(CliGateCancelCommand.Run(startupArgs, workspace));
}

if (CliEscapeCommands.IsCommand(startupArgs))
{
    return ExitCompletedStartupCommand(CliEscapeCommands.Run(startupArgs, workspace));
}

if (CliContextUsageCommand.IsCommand(startupArgs))
{
    return ExitCompletedStartupCommand(CliContextUsageCommand.Run(startupArgs, workspace));
}

if (CliAuthorDraftCommand.IsCommand(startupArgs))
{
    return ExitCompletedStartupCommand(CliAuthorDraftCommand.Run(startupArgs, workspace));
}

if (CliConductorCommand.IsCommand(startupArgs))
{
    return ExitCompletedStartupCommand(CliConductorCommand.Run(startupArgs, workspace, verifyBinding: VerifyWorkspaceBinding,
        invocationBinding: invocationBinding));
}

if (CliRemoteLaneSelfTestCommand.IsCommand(startupArgs))
{
    return ExitCompletedStartupCommand(await CliRemoteLaneSelfTestCommand.RunAsync(startupArgs, workspace));
}

if (CliExecutorScreenCommand.IsCommand(startupArgs))
{
    return ExitCompletedStartupCommand(await CliExecutorScreenCommand.RunAsync(startupArgs, workspace));
}

if (Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleVerb.IsCommand(startupArgs))
    return ExitCompletedStartupCommand(await Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleVerb.RunAsync(startupArgs, workspace,
        owners: Mcg.AgentOrchestrator.App.OwnerConsole.OwnerConsoleHostRowOwner.FromRegistry(repoRoot, projectRegistry),
        registry: projectRegistry, repositoryRoot: repoRoot));
if (CliOperatorIntentRoute.IsServed(startupArgs))
    return ExitCompletedStartupCommand(CliOperatorIntentRoute.Run(startupArgs, workspace));

if (CliGateStatusJsonQueryCommand.IsGateStatusJsonCommand(startupArgs))
{
    CliGateStatusJsonQueryCommand.Execute(workspace: workspace);
    return ExitCompletedStartupCommand(0);
}

if (ConductorContinuitySupervisor.ShouldSupervise(
        startupArgs,
        ProgramStartupLifecycle.IsAuthorityTransferRequested(startupArgs)))
{
    try
    {
        VerifyWorkspaceBinding();
        StoreSetupRunner.RequireCompatible(StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true),
            requireCurrent: invocationBinding.RequireCurrentStores);
        // The supervisor's run-event store is writable too. Finish complete quiet setup
        // before constructing it; the actual loop then holds ownership through startup.
        WorkspaceStoreSetup.Run(workspace, VerifyWorkspaceBinding);
        var maxDurationRestage = Environment.GetEnvironmentVariable(
            "MCG_ORCHESTRATOR_MAX_DURATION_RESTAGE");
        var restageEnabled =
            !string.Equals(maxDurationRestage, "0", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(maxDurationRestage, "false", StringComparison.OrdinalIgnoreCase);
        var conductEventLogWriter = new ConductEventLogWriter(workspace.ConductEventsLogPath);
        Func<Action<string, string, CancellationToken>, CancellationToken, ConductorPreparedSuccessor>?
            stageSuccessorWithHook = null;
        var stageSuccessor = restageEnabled
            ? ConductorSelfRelaunch.CreateSuccessorStagerForHome(
                OrchestratorHome.ResolveForProcess(), workspace,
                line =>
                {
                    Console.WriteLine(line);
                    conductEventLogWriter.Append("loop-handoff", null, line);
                }, out stageSuccessorWithHook)
            : null;

        var supervisorSeam = new SystemConductorSupervisorHandoffSeam(
            Path.Combine(workspace.OrchestratorDirectory, "continuity"));
        var conductorHome = OrchestratorHome.ResolveForProcess();
        var homeSource = conductorHome.SourceRootDirectory;
        var supervisor = new ConductorContinuitySupervisor(
            new SystemConductorSupervisorProcessHost(),
            new SqliteRunEventStore(workspace.RunEventStorePath),
            inspectStores: () => StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true),
            stopFilePath: workspace.ConductorStopFilePath,
            successorExitRetryDelay: TimeSpan.FromSeconds(60),
            stageSuccessor: stageSuccessor,
            stageSuccessorWithHook: stageSuccessorWithHook,
            homeBuildPublisher: homeSource is not null && conductorHome.IsHome(workspace)
                ? new HomeBuildAdoptionPublisher(homeSource, HomeBuildAdoptionRecord.RecordPath(homeSource)) : null,
            homeBuildFollower: homeSource is not null && !conductorHome.IsHome(workspace)
                ? new HomeBuildFollowerSuccessorSource(homeSource) : null,
            schemaUpgrade: new ConductorHandoffSchemaUpgrade(
                workspace.OrchestratorDirectory, workspace.ExecutionDirectory),
            appendConductEvent: (eventKind, goalId, detail) =>
                conductEventLogWriter.Append(eventKind, goalId, detail),
            supervisorHandoff: new ConductorSupervisorHandoffOptions(
                supervisorSeam,
                SystemConductorSupervisorHandoffSeam.ReadOwnBuild(),
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMilliseconds(250)),
            raiseEscalationAttention: (title, body, key) =>
                CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
                    .RaiseAsync(CollaborationItemType.Decision, null, title, body, key).GetAwaiter().GetResult());
        return await supervisor.RunAsync(
            startupArgs.Concat(new[] { "--repository-root", workspace.RootDirectory,
                "--expected-state-directory", workspace.OrchestratorDirectory })
                .Concat(invocationBinding.DisposableFixture ? new[] { "--disposable-fixture" } : Array.Empty<string>()).ToArray(),
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

WorktreeCleanupContext cleanupContext;
try
{
    cleanupContext = new WorktreeCleanupContext(
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

var (providers, agentFallback) = StartupProviderComposition.Create(commandCapability == CliCommandCapability.Execution);
var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath, agentFallback).Agents;
var workerProfiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
var operatorCatalog = OperatorChannelStore.Load(workspace.OperatorChannelPath);
IOperatorChannel operatorChannel;
if (commandCapability != CliCommandCapability.Execution)
{
    operatorChannel = NullOperatorChannel.Instance;
}
else
{
    try
    {
        operatorChannel = OperatorChannelComposition.Create(operatorCatalog, workspace.OrchestratorDirectory);
    }
    catch
    {
        operatorChannel = NullOperatorChannel.Instance;
    }
}

if (startupArgs.Count > 0 && startupArgs[0].Equals("prototype", StringComparison.OrdinalIgnoreCase))
{
    var objective = startupArgs.Count > 1 ? string.Join(' ', startupArgs.Skip(1)) : "Create a Windows-based agent orchestrator";
    var prototypeKernel = new AgentOrchestratorKernel();
    var prototypeGoal = GoalLifecycleCommands.CreateAndActivateGoal(prototypeKernel, agents, objective);
    GoalTextView.PrintGoal(prototypeGoal);
    TaskTimelineTextView.PrintTimeline(prototypeGoal);
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
        CliCommandDispatcher.ExecuteCommand(startupArgs, commandKernel, workspace, ref agents, providers, ref workerProfiles, ref commandCurrentGoal, operatorChannel, cleanupContext: cleanupContext);
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

if (startupArgs.Count > 0 && !CliHandledVerbRegistry.IsHandled(startupArgs[0]))
{
    Console.Error.WriteLine($"Error: {CliArgumentParser.FormatUnknownCommandMessage(startupArgs)}");
    return ExitCompletedStartupCommand(1);
}

ITransactionalOrchestratorStateRepository stateRepository;
ConductorLoopLeaseController? startupLoopLease = null;
using var startupLoopLeaseLifetime = new StartupLeaseLifetime(() => startupLoopLease?.Dispose());
Goal? currentGoal;
bool stateHydrated;
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
        VerifyWorkspaceBinding();
        StoreSetupRunner.RequireCompatible(StartupSqliteRetry.Run(workspace.OrchestratorDirectory, "store-plan-precheck",
                () => StoreSetupRunner.Plan(workspace.OrchestratorDirectory, includeState: true)),
            requireCurrent: invocationBinding.RequireCurrentStores);
        // Transfer the same controller into the loop; never release ownership between migration and startup.
        startupLoopLease = ConductorLoopLeaseController.Acquire(workspace.OrchestratorDirectory);
        VerifyWorkspaceBinding();
        WorkspaceStoreSetup.RunWithOwnership(workspace, invocationBinding.RequireCurrentStores);
    }

    var defersStateUpgrade = !isConductLoop && WorkspaceStoreSetup.DefersStateUpgradeToLiveOwner(workspace, startupArgs);
    if (!isConductLoop && !defersStateUpgrade) ProgramStartupLifecycle.EnsureStateDbInitialized(startupArgs, workspace);

    if (!defersStateUpgrade && commandCapability == CliCommandCapability.Execution &&
        !CliArgumentParser.IsAskGoal(startupArgs) &&
        !CliAttentionQueryCommand.IsAttentionQueryCommand(startupArgs))
    {
        ProgramStartupLifecycle.InitializeWorkerProcessTracking(
            RunsStartupCleanup(startupArgs),
            authorityTransferRequested,
            workspace.SqliteStatePath,
            workspace.ExecutionDirectory,
            cleanupContext);
    }
    stateRepository = StartupSqliteRetry.Run(workspace.SqliteStatePath, "state-repository-open", () => defersStateUpgrade
        ? SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
        : new SqliteOrchestratorStateRepository(workspace.SqliteStatePath));
    (currentGoal, stateHydrated) = await StartupSqliteRetry.RunAsync(workspace.SqliteStatePath, "state-hydration",
        () => CliReadOnlyStartupHydration.PrepareStartupAsync(startupArgs, stateRepository));
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
        CliReadOnlyStartupHydration.ExecuteStartupCommand(startupArgs, stateRepository, workspace,
            ref agents, providers, ref workerProfiles, ref currentGoal, stateHydrated,
            operatorChannel, acceptanceCleanupContext: cleanupContext, startupLoopLease: startupLoopLease,
            onReadOnlyDeclined: CliAttentionQueryCommand.IsAttentionQueryCommand(startupArgs)
                ? () => ProgramStartupLifecycle.InitializeWorkerProcessTracking(
                    RunsStartupCleanup(startupArgs),
                    ProgramStartupLifecycle.IsAuthorityTransferRequested(startupArgs),
                    workspace.SqliteStatePath,
                    workspace.ExecutionDirectory,
                    cleanupContext)
                : null);
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
Console.WriteLine("  goal <objective> [--pipeline auto|scout|developer-reviewer|developer-only] [--simple] [--from-backlog] [--run --confirm-batch-start]");
Console.WriteLine("    Create a goal. --pipeline: automatic Scout selection or an explicit pipeline override. --simple: single Developer task. --from-backlog: read from the backlog store. --run: create and start.");
Console.WriteLine("  accept [goal-id] [--skip-verify] [--autonomy <policy>]");
Console.WriteLine("    Accept a completed goal: run acceptance checks, merge workspace, and clean up worktree.");
Console.WriteLine("  stop <goal-id> <reason>|--text-file <path> --as cancel|park|abandon|supersede [--confirm-goal-stop|--confirm-goal-park|--confirm-goal-abandon]");
Console.WriteLine("    Stop a goal using the specified disposal mode.");
Console.WriteLine("  config <agents|profiles|policy|doctor>");
Console.WriteLine("    View configuration: agents=agent catalog, profiles=worker profiles, policy=autonomy policies, doctor=health check.");
Console.WriteLine("  project list|show [name]|discover [name] [--root <path>]|create <name> --root <path>|select <name>");
Console.WriteLine("    Manage global project registry and active project selection.");
Console.WriteLine();
Console.WriteLine("Advanced/Internal (used by automation, tests, and advanced workflows):");
Console.WriteLine("  Inspection verbs folded into 'next --full': status, monitor, readiness, evidence, stages, gates,");
Console.WriteLine("    verify-needed, input-needed, subscription-plan, model-outcomes, durations, dispatch-value, loop-health, failure-triage,");
Console.WriteLine("    goal-recovery, supervisor, operator-inbox. All still work standalone.");
Console.WriteLine("  readiness [goal-id]: diagnose terminal-goal sweep blockers and print start readiness.");
Console.WriteLine("  readiness-repair [goal-id]: apply the terminal-goal sweep repairs, then print start readiness.");
Console.WriteLine("  doctor, architecture, tenant, project");
Console.WriteLine("  provider-smoke [openai|anthropic|ollama] [--confirm-paid-smoke] [task-number], provider-smoke all --confirm-all");
Console.WriteLine("  prototype [objective]");
Console.WriteLine("  simple-goal <objective>, goal-plan [heading-filter] [--create-goals|--create-simple-goals] [--backlog-coverage full|slice]");
Console.WriteLine("  backlog-intake [heading-filter] [--create-goal [--pipeline auto|scout|developer-reviewer|developer-only]|--create-simple-goal] [--backlog-coverage full|slice] [--force-reclaim]");
Console.WriteLine("  intent-template [template request] [--create-goal|--create-simple-goal]");
Console.WriteLine("  goals, agents");
Console.WriteLine("  agent <role> <provider> <model> [name] [--complex-model <model>] (replace role)");
Console.WriteLine("  agent-add <role> <provider> <model> [name] [--complex-model <model>] (add/replace id)");
Console.WriteLine("  reassign-agent <task-number> <agent-id> (persist exact task agent assignment)");
Console.WriteLine("  goals subscribe [<goal-id>|--goal-prefix <prefix>] [--since <event-id>|--from-cursor <cursor>] [--once] [--format ndjson|human] [--task <id>] [--event-kind <kind,...>] [--wait-terminal]");
Console.WriteLine("  monitor-goal <goal-id> [--since <event-id>|--from-cursor <cursor>] [--once] [--format sse|ndjson|human] [--goal-prefix <prefix>] [--task <id>] [--event-kind <kind,...>] [--wait-terminal]");
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
Console.WriteLine("  repair-task-graph [--goal <goal-prefix>] --plan-file <path> [--idempotency-key <key>]: enqueue an atomic ordered repair; poll operator-intent-status <returned-id>");
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

        CliPersistentStateRunner.ExecuteCommand(command, stateRepository, workspace, ref agents, providers, ref workerProfiles, ref currentGoal, operatorChannel, acceptanceCleanupContext: cleanupContext);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
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
        WorkspaceStoreSetup.EnsureState(workspace);
    }

    internal static void InitializeWorkerProcessTracking(
        bool runsStartupCleanup,
        bool authorityTransferRequested,
        string stateStorePath,
        string executionDirectory,
        WorktreeCleanupContext? cleanupContext = null)
    {
        WorkerProcessJobs.ConfigureRegistry(stateStorePath);
        if (authorityTransferRequested)
        {
            return;
        }

        WorkerProcessJobs.SweepStartupOrphans();
        if (runsStartupCleanup)
        {
            (cleanupContext ?? new WorktreeCleanupContext(GoalWorktreeCleanupOptions.Default))
                .Scheduler.SweepNow(executionDirectory);
        }
    }
}

internal sealed class StartupLeaseLifetime(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}
