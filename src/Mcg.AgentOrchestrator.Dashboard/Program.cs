var executionDirectory = Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable);
try
{
    var projectSelection = OrchestratorProjectSelection.FromArgs(
        args,
        Environment.GetEnvironmentVariable(OrchestratorProjectSelection.ProjectEnvironmentVariable));
    var tenantSelection = OrchestratorTenantSelection.FromArgs(
        projectSelection.CommandArgs,
        Environment.GetEnvironmentVariable(OrchestratorTenantSelection.TenantEnvironmentVariable));
    var command = tenantSelection.CommandArgs.Count == 0
        ? []
        : DashboardApplicationServices.NormalizeArguments(tenantSelection.CommandArgs.ToArray());

    if (DashboardApplicationServices.TryPrintStartupHelp(command))
        return 0;
    DashboardApplicationServices.ThrowIfInvalidFlags(command);
    if (command.Count == 0)
        throw new ArgumentException("A dashboard command is required.");

    var repoRoot = !string.IsNullOrWhiteSpace(executionDirectory)
        ? executionDirectory
        : OrchestratorWorkspace.ResolveRepoRoot(Environment.CurrentDirectory);
    var activeProject = OrchestratorProjectRegistry.CreateDefault()
        .ResolveActiveProject(repoRoot, projectSelection.ProjectName);
    var workspace = activeProject.Name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
        ? OrchestratorWorkspace.ForDirectory(
            activeProject.RootDirectory,
            string.IsNullOrWhiteSpace(executionDirectory) ? null : executionDirectory,
            tenantSelection.TenantName)
        : OrchestratorWorkspace.ForProject(
            activeProject.Name,
            activeProject.RootDirectory,
            tenantName: tenantSelection.TenantName);

    var providers = ProviderRegistryFactory.CreateDefaultProviders();
    var agentFallback = ProviderRegistryFactory.IsLlamaCppReachable() ? AgentCatalog.LlamaCppDefault() : null;

    if (command[0].Equals("prototype-ui", StringComparison.OrdinalIgnoreCase))
        return DashboardHost.RunPrototypeUi(command, providers, agentFallback, tenantSelection.TenantName);

    if (command[0].Equals("dashboard", StringComparison.OrdinalIgnoreCase) && TryMapDashboardMode(command, out var mapped))
        command = mapped;

    if (IsHostedCommand(command[0]))
    {
        var hostArgs = DashboardHost.ParseDashboardHostArgs(
            command,
            command[0],
            defaultOpenBrowser: command[0].Equals("open-dashboard", StringComparison.OrdinalIgnoreCase));
        await DashboardHost.RunDashboardHostAsync(workspace, providers, hostArgs, agentFallback);
        return 0;
    }

    var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath, agentFallback).Agents;
    var workerProfiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
    var kernel = await SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath).LoadAsync();

    if (command[0].Equals("dashboard", StringComparison.OrdinalIgnoreCase))
    {
        var dashboardArgs = DashboardHost.ParseDashboardArgs(command);
        var outputPath = Path.GetFullPath(dashboardArgs.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, DashboardRenderer.Render(
            kernel,
            dashboardArgs.Options with { AgentDefinitions = agents, WorkerProfiles = workerProfiles }));
        Console.WriteLine($"Dashboard: {outputPath}");
        return 0;
    }

    if (command[0].Equals("transcript", StringComparison.OrdinalIgnoreCase))
    {
        var goal = OrchestratorEntityResolver.ResolveGoal(kernel, OrchestratorEntityResolver.GetLatestGoal(kernel), null);
        var outputPath = Path.GetFullPath(command.Count > 1 ? command[1] : workspace.TranscriptPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, GoalTranscriptRenderer.Render(
            kernel, goal, workerProfiles, agents, workspace.ExecutionDirectory));
        Console.WriteLine($"Transcript: {outputPath}");
        return 0;
    }

    throw new ArgumentException($"Unsupported dashboard command '{command[0]}'.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static bool IsHostedCommand(string command) =>
    command.Equals("serve-dashboard", StringComparison.OrdinalIgnoreCase) ||
    command.Equals("hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
    command.Equals("simple-hosted-dashboard", StringComparison.OrdinalIgnoreCase) ||
    command.Equals("open-dashboard", StringComparison.OrdinalIgnoreCase);

static bool TryMapDashboardMode(IReadOnlyList<string> command, out IReadOnlyList<string> mapped)
{
    mapped = command;
    var modeIndex = command.ToList().FindIndex(arg => arg.Equals("--mode", StringComparison.OrdinalIgnoreCase));
    if (modeIndex < 0)
        return false;
    if (modeIndex + 1 >= command.Count)
        throw new ArgumentException("--mode requires local, hosted, or read-only.");

    var name = command[modeIndex + 1].ToLowerInvariant() switch
    {
        "local" => "serve-dashboard",
        "hosted" => "hosted-dashboard",
        "read-only" => "simple-hosted-dashboard",
        var value => throw new ArgumentException($"Unknown dashboard mode '{value}'. Use: local|hosted|read-only")
    };
    var result = command.Where((_, index) => index != modeIndex && index != modeIndex + 1).ToList();
    result[0] = name;
    mapped = result;
    return true;
}
