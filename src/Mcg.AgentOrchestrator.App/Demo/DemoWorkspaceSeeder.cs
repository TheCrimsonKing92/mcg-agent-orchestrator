using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Prototype;

public static class PrototypeWorkspaceSeeder
{
    public const string PrototypeDirectoryName = ".orchestrator-prototype";
    public const string PrototypeWorkspaceName = "workspace";
    private const long MaxRetainedPrototypeStateBytes = 5 * 1024 * 1024;

    public static string Create(string rootDirectory, AgentCatalog? defaultAgents = null)
    {
        var workspace = GetWorkspacePath(rootDirectory);
        var orchestratorDirectory = Path.Combine(workspace, ".orchestrator");
        Directory.CreateDirectory(orchestratorDirectory);

        var statePath = Path.Combine(orchestratorDirectory, "state.json");
        var agentCatalogPath = Path.Combine(orchestratorDirectory, "agents.json");
        var workerProfilePath = Path.Combine(orchestratorDirectory, "workers.json");
        var agents = LoadPrototypeAgents(agentCatalogPath, defaultAgents).Agents;
        var workerProfiles = LoadPrototypeWorkerProfiles(workerProfilePath);
        if (File.Exists(statePath) && !ShouldResetPrototypeState(statePath))
        {
            AgentCatalogStore.Save(agentCatalogPath, new AgentCatalog(agents));
            WorkerProfileStore.Save(workerProfilePath, workerProfiles);

            return workspace;
        }

        SeedWorkspace(statePath, agentCatalogPath, workerProfilePath, agents, workerProfiles, workspace);
        return workspace;
    }

    private static bool ShouldResetPrototypeState(string statePath)
    {
        var state = new FileInfo(statePath);
        return state.Exists && state.Length > MaxRetainedPrototypeStateBytes;
    }

    private static void SeedWorkspace(
        string statePath,
        string agentCatalogPath,
        string workerProfilePath,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        string workspace)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Prototype: explore the agent orchestrator UI");
        kernel.ActivateGoal(goal.Id, agents);
        SeedGoal(kernel, goal, agents, workspace);

        OrchestratorStateStore.Save(statePath, kernel);
        AgentCatalogStore.Save(agentCatalogPath, new AgentCatalog(agents));
        WorkerProfileStore.Save(workerProfilePath, workerProfiles);
    }

    public static string GetWorkspacePath(string rootDirectory)
    {
        return Path.Combine(rootDirectory, PrototypeDirectoryName, PrototypeWorkspaceName);
    }

    private static AgentCatalog LoadPrototypeAgents(string agentCatalogPath, AgentCatalog? defaultAgents)
    {
        var catalog = AgentCatalogStore.Load(agentCatalogPath, defaultAgents);
        var defaults = defaultAgents ?? AgentCatalog.Default();

        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var desired = defaults.GetRequired(role);
            var current = catalog.Agents.FirstOrDefault(agent => agent.Role == role);

            if (current is null || ShouldRepairPrototypeAgent(current, desired))
            {
                catalog = catalog.UpsertRole(desired);
            }
        }

        return catalog;
    }

    private static bool ShouldRepairPrototypeAgent(AgentDefinition current, AgentDefinition desired)
    {
        return !EqualsIgnoreCase(current.Model.ProviderName, desired.Model.ProviderName) ||
            !EqualsIgnoreCase(current.Model.ModelName, desired.Model.ModelName) ||
            !EqualsIgnoreCase(current.Model.ReasoningEffort, desired.Model.ReasoningEffort) ||
            current.Model.MaxOutputTokens != desired.Model.MaxOutputTokens ||
            current.ExecutionPolicy != AgentExecutionPolicy.PreferSubscription ||
            current.Subscription is null ||
            !EqualsIgnoreCase(current.Subscription.WorkerProfileName, desired.Subscription!.WorkerProfileName) ||
            !EqualsIgnoreCase(current.Subscription.ModelAlias, desired.Subscription.ModelAlias) ||
            !EqualsIgnoreCase(current.Subscription.ReasoningEffort, desired.Subscription.ReasoningEffort) ||
            !ModelsMatch(current.ComplexModel, desired.ComplexModel);
    }

    private static bool ModelsMatch(ModelProfile? current, ModelProfile? desired)
    {
        if (current is null || desired is null)
        {
            return current is null && desired is null;
        }

        return EqualsIgnoreCase(current.ProviderName, desired.ProviderName) &&
            EqualsIgnoreCase(current.ModelName, desired.ModelName) &&
            EqualsIgnoreCase(current.ReasoningEffort, desired.ReasoningEffort) &&
            current.MaxOutputTokens == desired.MaxOutputTokens;
    }

    private static bool EqualsIgnoreCase(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkerProfileCatalog LoadPrototypeWorkerProfiles(string workerProfilePath)
    {
        var profiles = WorkerProfileStore.Load(workerProfilePath);
        var defaults = WorkerProfileCatalog.Default();

        foreach (var profileName in new[] { "codex-cli", "claude-cli" })
        {
            var current = profiles.GetRequired(profileName);
            if (ShouldRepairPrototypeSubscriptionProfile(current))
            {
                profiles = profiles.Upsert(defaults.GetRequired(profileName));
            }
        }

        return profiles;
    }

    private static bool ShouldRepairPrototypeSubscriptionProfile(WorkerProfile profile)
    {
        if (WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate))
        {
            return true;
        }

        if (profile.Name.Equals("codex-cli", StringComparison.OrdinalIgnoreCase))
        {
            return !profile.CommandTemplate.Contains("{sandboxMode}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--cd", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--model {subscriptionModelName}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.OrdinalIgnoreCase);
        }

        return profile.Name.Equals("claude-cli", StringComparison.OrdinalIgnoreCase) &&
            (!profile.CommandTemplate.Contains("--model {subscriptionModelName}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("{permissionMode}", StringComparison.OrdinalIgnoreCase));
    }

    private static void SeedGoal(AgentOrchestratorKernel kernel, Goal goal, IReadOnlyList<AgentDefinition> agents, string workspace)
    {
        var now = DateTimeOffset.UtcNow;
        var planner = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Planner);
        var researcher = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Researcher);
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);

        kernel.SetTaskVerificationPlan(goal.Id, planner.Id, "Review the generated plan and record manual pass/fail evidence.");
        kernel.SetTaskVerificationPlan(goal.Id, researcher.Id, "Confirm the research notes identify risks and open questions.");
        kernel.SetTaskVerificationPlan(goal.Id, developer.Id, "Run the prototype dispatch and check the captured output.");
        kernel.SetTaskVerificationPlan(goal.Id, tester.Id, "Record a failed manual check so the remediation controls are visible.");
        kernel.SetTaskVerificationPlan(goal.Id, reviewer.Id, "Answer the reviewer question, then record final acceptance evidence.");

        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Completed, "Prototype planner created the initial implementation plan.");
        kernel.RecordTaskVerification(
            goal.Id,
            planner.Id,
            ManualVerificationRecorder.Create(true, "Prototype plan was reviewed and accepted.", workspace, now.AddSeconds(1)));

        kernel.RecordTaskDispatch(
            goal.Id,
            developer.Id,
            new TaskDispatchRecord("local-echo", "Write-Output 'prototype implementation complete'", workspace, now.AddSeconds(2)));
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            developer.Id,
            new TaskVerificationRecord(
                "Write-Output 'prototype implementation complete'",
                workspace,
                0,
                "prototype implementation complete",
                string.Empty,
                now.AddSeconds(3)));

        kernel.ReportTaskProgress(goal.Id, tester.Id, WorkTaskStatus.Completed, "Prototype tester completed a sample check.");
        kernel.RecordTaskVerification(
            goal.Id,
            tester.Id,
            ManualVerificationRecorder.Create(false, "Prototype failure: sample test evidence is intentionally marked failed.", workspace, now.AddSeconds(4)));

        kernel.RequestHumanInput(goal.Id, reviewer.Id, "Should the reviewer accept this intentionally failed sample verification?");

        var dispatchTask = kernel.AddTask(
            goal.Id,
            AgentRole.Developer,
            "Prototype: inspect a recorded worker dispatch",
            agents,
            "Execute the recorded dispatch or start it in the background, then refresh logs.");
        kernel.RecordTaskDispatch(
            goal.Id,
            dispatchTask.Id,
            new TaskDispatchRecord("local-echo", "Write-Output 'recorded prototype dispatch'", workspace, now.AddSeconds(5)));
    }
}


