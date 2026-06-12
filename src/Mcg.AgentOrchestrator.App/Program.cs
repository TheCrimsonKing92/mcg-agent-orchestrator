using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

var executionDirectory = Environment.GetEnvironmentVariable("MCG_ORCHESTRATOR_REPOSITORY_ROOT");
OrchestratorTenantSelection tenantSelection;
try
{
    tenantSelection = OrchestratorTenantSelection.FromArgs(
        args,
        Environment.GetEnvironmentVariable(OrchestratorTenantSelection.TenantEnvironmentVariable));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

var startupArgs = tenantSelection.CommandArgs.Count == 0
    ? []
    : CliArgumentParser.NormalizeArgs(tenantSelection.CommandArgs.ToArray());
var workspace = OrchestratorWorkspace.ForDirectory(
    Environment.CurrentDirectory,
    string.IsNullOrWhiteSpace(executionDirectory) ? null : executionDirectory,
    tenantSelection.TenantName);
var providers = ProviderRegistryFactory.CreateDefaultProviders();
var agentFallback = ProviderRegistryFactory.IsOllamaReachable() ? AgentCatalog.OllamaDefault() : null;
var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath, agentFallback).Agents;
var workerProfiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);

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

var kernel = AppStatePersistence.LoadKernel(workspace.StatePath);
var currentGoal = OrchestratorEntityResolver.GetLatestGoal(kernel);

if (startupArgs.Count > 0)
{
    try
    {
        var shouldSave = CliCommandDispatcher.ExecuteCommand(startupArgs, kernel, workspace, ref agents, providers, ref workerProfiles, ref currentGoal);
        if (shouldSave)
        {
            AppStatePersistence.SaveKernel(workspace.StatePath, kernel);
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

Console.WriteLine("MCG Agent Orchestrator");
Console.WriteLine($"Tenant: {workspace.TenantName}");
Console.WriteLine($"State: {workspace.StatePath}");
Console.WriteLine("Commands: doctor, architecture, tenant, state-rollback --confirm-state-rollback, provider-smoke [openai|anthropic|ollama] [--confirm-paid-smoke] [task-number] (default: local Ollama only), provider-smoke all --confirm-all, prototype [objective sample], prototype-ui [url] [--refresh seconds] [--open] [--no-open], dashboard [path] [--refresh seconds], serve-dashboard [port|url] [--refresh seconds] [--open] [--no-open], hosted-dashboard [port|url] [--refresh seconds] [--open] [--no-open], simple-hosted-dashboard [port|url] [--refresh seconds] [--open] [--no-open], open-dashboard [port|url] [--refresh seconds] [--open] [--no-open], transcript [path], goal <objective>, simple-goal <objective>, goals, agents, agent <role> <provider> <model> [name] [--complex-model <model>], status [goal-id], monitor [goal-id], acceptance [goal-id], workspace [create|merge|remove] [goal-id-prefix], evidence [goal-id], stages [goal-id], gates [goal-id], verify-needed [goal-id], input-needed [goal-id], next [goal-id], subscription-plan [goal-id], advance [goal-id], advance-subscription [goal-id] --confirm-subscription-advance [--confirm-large-paid-subscription-start], run-goal [goal-id] --confirm-batch-start [--confirm-large-paid-subscription-start], delegate [goal-id], cancel-goal <goal-id> <reason> [--confirm-goal-stop], supersede-goal <goal-id> <reason> [--confirm-goal-stop], task <task-number|task-id-prefix>, tasks [status <status>] [role <role>] [id <task-id-prefix>] [evidence <kind>] [event <kind>], add-task <role> <description>, verification-plan <task-number> [plan], brief <task-number>, timeline [goal-id], task-timeline <task-number>, pending, run <task-number> [--confirm-paid-api-run] [--confirm-large-paid-api-prompt], api-run <task-number> [--confirm-paid-api-run] [--confirm-large-paid-api-prompt], retry <task-number> <message>, note <task-number> <message>, dispatch <task-number> <worker-name> <command>, worker-profiles, worker-profile <name> <command-template>, worker-profile-check [name], worker-profile-export <path>, worker-profile-import <path> [merge|replace], worker-dispatch <task-number> <worker-name> <command-template>, profile-dispatch <task-number> <profile-name>, subscription-dispatch <task-number>, subscription-dispatch-ready, start-subscription-ready --confirm-batch-start [--confirm-large-paid-subscription-start], execute-dispatch <task-number> --confirm-dispatch-start [--confirm-large-paid-subscription-start], start-dispatch <task-number> --confirm-dispatch-start [--confirm-large-paid-subscription-start], start-dispatches --confirm-batch-start [--confirm-large-paid-subscription-start], refresh-dispatch <task-number>, refresh-dispatches, logs <task-number> [stdout|stderr|exit|all], cancel-dispatch <task-number>, verify <task-number> <command>, verify-manual <task-number> <passed|failed> <note>, verifications <task-number>, progress <task-number> <running|completed|failed|cancelled> <message>, ask <task-number> <question>, ask-goal <question>, answer <request-id> <answer>, exit");
Console.WriteLine("Dashboard modes: serve-dashboard is local/operator, hosted-dashboard is network/operator, simple-hosted-dashboard is network/read-only.");

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
        var shouldSave = CliCommandDispatcher.ExecuteCommand(CliArgumentParser.SplitCommand(line), kernel, workspace, ref agents, providers, ref workerProfiles, ref currentGoal);
        if (shouldSave)
        {
            AppStatePersistence.SaveKernel(workspace.StatePath, kernel);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
}
