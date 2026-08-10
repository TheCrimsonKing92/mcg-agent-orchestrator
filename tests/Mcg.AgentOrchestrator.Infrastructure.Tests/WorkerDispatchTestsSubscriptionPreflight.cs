using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json;

[Xunit.Collection("EnvMutation")]
public sealed class WorkerDispatchTestsSubscriptionPreflight : WorkerDispatchTestSupport
{
    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_allows_swept_terminal_goal_without_starting_worker")]
    public void WorkerProfileDispatcherPreflightAllowsSweptTerminalGoalWithoutStartingWorker()
    {
        var root = CreateTempDirectory();
        var promptRoot = Path.Combine(root, "prompts");
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan swept preflight.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Repair terminal preflight desync", [task]);
        var agent = SubscriptionPlannerAgent("planner", "Planner");
        kernel.ActivateGoal(goal.Id, [agent]);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);

        var sweep = TerminalGoalSweep.Run(kernel, root, goal.Id);
        var repairedGoal = kernel.GetGoal(goal.Id);
        var repairedTask = repairedGoal.Tasks.Single(candidate => candidate.Id == task.Id);
        Assert.Equal(GoalStatus.Active, repairedGoal.Status);
        Assert.Equal(WorkTaskStatus.Assigned, repairedTask.Status);

        var prepared = WorkerProfileDispatcher.PrepareSubscriptionTask(
            kernel,
            repairedGoal,
            repairedTask,
            [agent],
            DispatchTestProfiles(),
            promptRoot,
            root,
            DateTimeOffset.UtcNow,
            claudeAuthProbe: () => new ClaudeCliAuthState(
                HasAnthropicApiKey: true,
                HasCliCredentialArtifact: false,
                CredentialArtifactPath: null));

        Assert.Contains(sweep.Goals.Single().Repairs, repair => repair.Kind == "terminal-task-desync");
        Assert.NotNull(prepared.PromptPath);
        Assert.Null(repairedTask.LastProcess);
    }

    [Xunit.Fact(DisplayName = "Headless_monitor_goal_preflight_does_not_start_paid_worker_dispatch")]
    public async Task HeadlessMonitorGoalPreflightDoesNotStartPaidWorkerDispatch()
{
    var root = CreateTempDirectory();
    var workspace = OrchestratorWorkspace.ForDirectory(root);
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Do paid subscription work.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Observe paid-capable goal without dispatch", [task]);
    var agent = new AgentDefinition(
        new AgentId("subscription-developer"),
        "Subscription developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    using var output = new StringWriter();

    await GoalMonitoringSubscriptionCommand.RunAsync(
        ["monitor-goal", goal.Id.Value[..8], "--once"],
        output,
        kernel,
        workspace,
        [agent],
        WorkerProfileCatalog.Default());

    Assert.Contains("event: goal.snapshot", output.ToString());
    Assert.Null(task.LastDispatch);
    Assert.Null(task.LastProcess);
    Assert.False(Directory.Exists(Path.Combine(workspace.OrchestratorDirectory, "prompts")));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_reports_goal_worktree_git_metadata_access_without_worker_start")]
    public void WorkerProfileDispatcherPreflightReportsGoalWorktreeGitMetadataAccessWithoutWorkerStart()
{
    var previousSandbox = Environment.GetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable);
    var root = CreateSeededDispatchRepository();
    try
    {
        Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, "1");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Inspect worker git metadata permissions", [new TaskSpec(TaskId.New(), "Inspect worker git metadata permissions.", AgentRole.Developer)]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        var worktree = GoalWorktrees.Ensure(root, goal.Id);

        var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
            goal,
            task,
            [agent],
            WorkerProfileCatalog.Default(),
            worktree,
            DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            allowGitReference: true);

        var findings = string.Join("\n", preflight.Findings);
        Assert.True(preflight.Allowed, findings);
        Assert.Null(task.LastDispatch);
        Assert.Contains("git metadata index_lock=", findings);
        Assert.Contains(Path.Combine(".git", "worktrees", goal.Id.Value[..8], "index.lock"), findings.Replace('/', Path.DirectorySeparatorChar));
        Assert.Contains("current_process_can_write=True", findings);
        Assert.Contains(
            OperatingSystem.IsWindows() ? "worker_git_write=blocked-by-low-integrity" : "worker_git_write=same-as-orchestrator",
            findings);
        Assert.Contains("commit_contract=workers edit worktree files; orchestrator commits verified dirty edits on behalf", findings);
        Assert.Contains("ok: worktree clean before dispatch", findings);
    }
    finally
    {
        Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, previousSandbox);
    }
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_does_not_start_worker_or_mutate_owned_ephemeral_cleanup")]
    public void WorkerProfileDispatcherPreflightDoesNotStartWorkerOrMutateOwnedEphemeralCleanup()
    {
    var root = CreateSeededDispatchRepository();
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Update src/example.txt.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Preflight cleanup boundary", [task]);
    var agent = SubscriptionDeveloperAgent();
    kernel.ActivateGoal(goal.Id, [agent]);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var contextPath = Path.Combine(root, ".orchestrator-context", goal.Id.Value);
    var tempPath = Path.Combine(root, ".t", goal.Id.Value[..8] + "-preflight");
    Directory.CreateDirectory(contextPath);
    Directory.CreateDirectory(tempPath);
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        worktree,
        DateTimeOffset.Parse("2026-07-02T19:00:00Z"),
        sandboxOptions: sandbox);

    Assert.True(preflight.Allowed, string.Join("\n", preflight.Findings));
    Assert.Contains(preflight.Findings, finding => finding.Contains("ready: profile, sandbox, worktree, and retry state passed deterministic preflight", StringComparison.Ordinal));
    Assert.Null(task.LastDispatch);
    Assert.Null(task.LastProcess);
    Assert.True(Directory.Exists(contextPath));
    Assert.True(Directory.Exists(tempPath));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_blocks_repo_scoped_skill_targets")]
    public void WorkerProfileDispatcherPreflightBlocksRepoScopedSkillTargets()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Create .agents/skills/example/SKILL.md", [new TaskSpec(TaskId.New(), "Author .agents/skills/example/SKILL.md", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var sandbox = new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z"),
        sandboxOptions: sandbox);
    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z")));

    Assert.False(preflight.Allowed);
    Assert.Equal("blocked", preflight.CapabilityStatus);
    Assert.Contains(".agents/skills", string.Join("\n", preflight.Findings), StringComparison.Ordinal);
    Assert.Contains("Subscription preflight failed", ex.Message, StringComparison.Ordinal);
    Assert.False(Directory.Exists(promptRoot));
    Assert.True(task.LastDispatch is null);
}

    [Xunit.Theory(DisplayName = "WorkerProfileDispatcher_preflight_blocks_missing_required_local_skills_with_or_without_catalog_root")]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void WorkerProfileDispatcherPreflightBlocksMissingRequiredLocalSkills(bool createCatalogRoot)
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "missing-repo");
    if (createCatalogRoot)
    {
        Directory.CreateDirectory(Path.Combine(workingDirectory, ".agents", "skills"));
    }
    else
    {
        Directory.CreateDirectory(workingDirectory);
    }
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Implement .NET build verification.", [new TaskSpec(TaskId.New(), "Run dotnet test for the implementation.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z"));
    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z")));

    Assert.False(preflight.Allowed);
    Assert.Contains("missing required local skill", string.Join("\n", preflight.Findings), StringComparison.Ordinal);
    Assert.Contains("dotnet-windows-build-hygiene", string.Join("\n", preflight.Findings), StringComparison.Ordinal);
    Assert.Contains("missing required local skill", ex.Message, StringComparison.Ordinal);
    Assert.False(Directory.Exists(promptRoot));
    Assert.True(task.LastDispatch is null);
}

    [Xunit.Theory(DisplayName = "WorkerProfileDispatcher_preflight_blocks_Codex_repo_skills_regardless_of_sandbox_state")]
    [Xunit.InlineData(false, "1", "blocked")]
    [Xunit.InlineData(true, "0", "blocked")]
    public void WorkerProfileDispatcherPreflightBlocksCodexRepoSkillsRegardlessOfSandboxState(
        bool explicitSandboxEnabled,
        string ambientSandboxValue,
        string expectedCapabilityStatus)
    {
        var previousSandbox = Environment.GetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable);
        try
        {
            Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, ambientSandboxValue);
            var workingDirectory = CreateTempDirectory();
            File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
            WriteSkill(workingDirectory, "dotnet-windows-build-hygiene");
            WriteSkill(workingDirectory, "skill-authoring");
            WriteSkill(workingDirectory, "verification-before-completion");
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(
                TaskId.New(),
                "Update .agents/skills/example/SKILL.md.",
                AgentRole.Developer);
            var goal = kernel.CreateGoal("Maintain repo-scoped procedures", [task]);
            var agent = new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
            kernel.ActivateGoal(goal.Id, [agent]);
            var sandbox = new WorkerSandboxOptions(
                explicitSandboxEnabled,
                WorkerSandboxOptions.DefaultAccount,
                WorkerSandboxOptions.DefaultCredentialTarget);

            var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
                goal,
                task,
                [agent],
                WorkerProfileCatalog.Default(),
                workingDirectory,
                DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
                sandboxOptions: sandbox);

            Assert.Equal(expectedCapabilityStatus, preflight.CapabilityStatus);
            Assert.Contains(
                preflight.Findings,
                finding => finding.Contains($"capability: {expectedCapabilityStatus}", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, previousSandbox);
        }
    }

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_ready_batch_skips_preflight_blocked_tasks")]
    public void WorkerProfileDispatcherReadyBatchSkipsPreflightBlockedTasks()
{
    using var _sandboxEnv = ClearWorkerSandboxEnv();
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    WriteSkill(workingDirectory, "dotnet-windows-build-hygiene");
    WriteSkill(workingDirectory, "skill-authoring");
    WriteSkill(workingDirectory, "verification-before-completion");
    var kernel = new AgentOrchestratorKernel();
    var blockedTask = new TaskSpec(TaskId.New(), "Author .agents/skills/example/SKILL.md", AgentRole.Developer);
    var allowedTask = new TaskSpec(TaskId.New(), "Update src/example.txt", AgentRole.Developer);
    var goal = kernel.CreateGoal("Batch preflight", [blockedTask, allowedTask]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z"));

    Assert.Equal(1, results.Count);
    Assert.Equal(allowedTask.Id, results.Single().Task.Id);
    Assert.True(goal.Tasks.Single(task => task.Id == blockedTask.Id).LastDispatch is null);
    Assert.True(goal.Tasks.Single(task => task.Id == allowedTask.Id).LastDispatch is not null);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_marks_echo_only_profiles_not_preparable")]
    public void SubscriptionPlanMarksEchoOnlyProfilesNotPreparable()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan echo subscription profile");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "Write-Output {promptPath}"));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

    var developer = plan.Items.First(item => item.Role == AgentRole.Developer);
    Assert.True(developer.ProfileExists);
    Assert.True(developer.ProfileIsEchoOnly);
    Assert.False(developer.ProfileIsPatchCapable);
    Assert.False(developer.CanPrepare);
    Assert.Contains("only echoes the prompt path", developer.Detail, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_marks_unpinned_model_profiles_not_preparable")]
    public void SubscriptionPlanMarksUnpinnedModelProfilesNotPreparable()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan unpinned subscription model");
    var agents =
        AgentCatalog.Default()
            .UpsertRole(new AgentDefinition(
                new AgentId("openai-reviewer"),
                "OpenAI reviewer",
                AgentRole.Reviewer,
                new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.3-codex")))
            .Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("custom-agent", "agent-cli {promptPath}"));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

    var reviewer = plan.Items.First(item => item.Role == AgentRole.Reviewer);
    Assert.True(reviewer.ProfileExists);
    Assert.False(reviewer.CanPrepare);
    Assert.Contains("{subscriptionModelName}", reviewer.Detail, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_marks_unpinned_reasoning_profiles_not_preparable")]
    public void SubscriptionPlanMarksUnpinnedReasoningProfilesNotPreparable()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan unpinned subscription reasoning");
    var agents =
        AgentCatalog.Default()
            .UpsertRole(new AgentDefinition(
                new AgentId("openai-reviewer"),
                "OpenAI reviewer",
                AgentRole.Reviewer,
                new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
                ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
                Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.3-codex", "low")))
            .Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("custom-agent", "agent-cli --model {subscriptionModelName} {promptPath}"));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

    var reviewer = plan.Items.First(item => item.Role == AgentRole.Reviewer);
    Assert.True(reviewer.ProfileExists);
    Assert.False(reviewer.CanPrepare);
    Assert.Contains("{subscriptionReasoningEffort}", reviewer.Detail, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_marks_non_patching_developer_profiles_not_preparable")]
    public void SubscriptionPlanMarksNonPatchingDeveloperProfilesNotPreparable()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan non-patching subscription profile");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} {promptPath}"));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

    var developer = plan.Items.First(item => item.Role == AgentRole.Developer);
    var planner = plan.Items.First(item => item.Role == AgentRole.Planner);
    Assert.True(developer.ProfileExists);
    Assert.False(developer.ProfileIsPatchCapable);
    Assert.False(developer.CanPrepare);
    Assert.Contains("not patch-capable", developer.Detail, StringComparison.Ordinal);
    Assert.True(planner.CanPrepare);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_includes_selected_worker_route_for_ready_paid_task")]
    public void SubscriptionPlanIncludesSelectedWorkerRouteForReadyPaidTask()
{
    var kernel = new AgentOrchestratorKernel();
    var task = new TaskSpec(TaskId.New(), "Update a dashboard label.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Route ready paid task", [task]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var plan = SubscriptionPlanBuilder.Build(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        _ => 1200);

    var item = plan.Items.Single();
    Assert.True(item.CanPrepare);
    Xunit.Assert.NotNull(item.Route);
    Assert.Equal(WorkerRouteDisposition.Selected, item.Route!.Disposition);
    Xunit.Assert.Contains("ready for subscription dispatch", item.Route.Recommendation, StringComparison.Ordinal);
    Xunit.Assert.Contains(item.Route.Reasons, text => text.Contains("provider=OpenAI", StringComparison.Ordinal));
    Xunit.Assert.Contains(item.Route.Reasons, text => text.Contains("profile=codex-cli", StringComparison.Ordinal));
    Xunit.Assert.Contains(item.Route.Reasons, text => text.Contains("estimated cost-guard prompt chars=1200", StringComparison.Ordinal));
    Xunit.Assert.Contains(item.Route.Reasons, text => text.Contains("patch-capable profile", StringComparison.Ordinal));
    Xunit.Assert.Contains(item.Route.Alternatives, text => text.Contains("local Ollama/qwen", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_blocks_developer_route_without_patch_capable_profile")]
    public void SubscriptionPlanBlocksDeveloperRouteWithoutPatchCapableProfile()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Route blocked developer profile");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("codex-cli", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} {promptPath}"));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

    var developer = plan.Items.First(item => item.Role == AgentRole.Developer);
    Assert.False(developer.CanPrepare);
    Xunit.Assert.NotNull(developer.Route);
    Assert.Equal(WorkerRouteDisposition.Blocked, developer.Route!.Disposition);
    Xunit.Assert.Contains(developer.Route.Reasons, text => text.Contains("provider=OpenAI", StringComparison.Ordinal));
    Xunit.Assert.Contains(developer.Route.Reasons, text => text.Contains("patch capability missing", StringComparison.Ordinal));
    Xunit.Assert.Contains("not patch-capable", developer.Route.Recommendation, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_routes_equivalent_tasks_differently_for_patch_capability")]
    public void SubscriptionPlanRoutesEquivalentTasksDifferentlyForPatchCapability()
{
    var kernel = new AgentOrchestratorKernel();
    var developerTask = new TaskSpec(TaskId.New(), "Inspect the same source file.", AgentRole.Developer);
    var reviewerTask = new TaskSpec(TaskId.New(), "Inspect the same source file.", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Route by required capability", [developerTask, reviewerTask]);
    IReadOnlyList<AgentDefinition> agents =
    [
        new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.5", "low")),
        new AgentDefinition(
            new AgentId("reviewer"),
            "Reviewer",
            AgentRole.Reviewer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("custom-agent", "gpt-5.5", "low"))
    ];
    var profiles = WorkerProfileCatalog.Default().Upsert(new WorkerProfile("custom-agent", "codex exec --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} {promptPath}"));
    kernel.ActivateGoal(goal.Id, agents);

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles);

    var developer = plan.Items.Single(item => item.Role == AgentRole.Developer);
    var reviewer = plan.Items.Single(item => item.Role == AgentRole.Reviewer);
    Assert.False(developer.CanPrepare);
    Assert.True(reviewer.CanPrepare);
    Assert.Equal(WorkerRouteDisposition.Blocked, developer.Route!.Disposition);
    Assert.Equal(WorkerRouteDisposition.Selected, reviewer.Route!.Disposition);
    Xunit.Assert.Contains(developer.Route.Reasons, text => text.Contains("patch capability missing", StringComparison.Ordinal));
    Xunit.Assert.DoesNotContain(reviewer.Route.Reasons, text => text.Contains("patch capability missing", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_reports_effective_models_before_subscription_dispatch")]
    public void SubscriptionPlanReportsEffectiveModelsBeforeSubscriptionDispatch()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Maintain dashboard views",
        [
            new TaskSpec(TaskId.New(), "Update a tooltip label.", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture with end-to-end distributed integration and horizontal scaling.", AgentRole.Developer)
        ]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        ComplexModel: new ModelProfile("Anthropic", "claude-opus-4.1", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var plan = SubscriptionPlanBuilder.Build(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        task => kernel.BuildTaskBrief(goal.Id, task.Id).Content.Length);

    var simple = plan.Items.First(item => item.Description.Contains("tooltip", StringComparison.Ordinal));
    var complex = plan.Items.First(item => item.Description.Contains("multi-tenant", StringComparison.Ordinal));
    var simpleTask = goal.Tasks.First(task => task.Description.Contains("tooltip", StringComparison.Ordinal));
    var complexTask = goal.Tasks.First(task => task.Description.Contains("multi-tenant", StringComparison.Ordinal));
    var simplePromptCharacters = kernel.BuildTaskBrief(goal.Id, simpleTask.Id).Content.Length;
    var complexPromptCharacters = kernel.BuildTaskBrief(goal.Id, complexTask.Id).Content.Length;
    Assert.Equal(TaskComplexity.Simple, simple.TaskComplexity);
    Assert.Equal("OpenAI", simple.ProviderName);
    Assert.Equal("gpt-5-mini", simple.ModelName);
    Assert.Equal("codex-cli", simple.ProfileName);
    Assert.Equal("gpt-5-mini", simple.SubscriptionModelName);
    Assert.Equal("low", simple.SubscriptionReasoningEffort);
    Assert.Equal(simplePromptCharacters, simple.EstimatedPromptCharacterCount);
    Assert.Equal(TaskComplexity.Complex, complex.TaskComplexity);
    Assert.Equal("Anthropic", complex.ProviderName);
    Assert.Equal("claude-opus-4.1", complex.ModelName);
    Assert.Equal("claude-cli", complex.ProfileName);
    Assert.Equal("claude-opus-4.1", complex.SubscriptionModelName);
    Assert.Equal("high", complex.SubscriptionReasoningEffort);
    Assert.Equal(complexPromptCharacters, complex.EstimatedPromptCharacterCount);
    Assert.Equal(2, plan.ReadyModelUsage.Count);
    var simpleSummary = plan.ReadyModelUsage.Single(item => item.TaskComplexity == TaskComplexity.Simple);
    Assert.Equal("OpenAI", simpleSummary.ProviderName);
    Assert.Equal("gpt-5-mini", simpleSummary.ModelName);
    Assert.Equal("low", simpleSummary.ReasoningEffort);
    Assert.Equal(1, simpleSummary.ReadyCount);
    Assert.Equal(simplePromptCharacters, simpleSummary.EstimatedPromptCharacterCount);
    Assert.True(simpleSummary.IsPotentiallyPaidProvider);
    var complexSummary = plan.ReadyModelUsage.Single(item => item.TaskComplexity == TaskComplexity.Complex);
    Assert.Equal("Anthropic", complexSummary.ProviderName);
    Assert.Equal("claude-opus-4.1", complexSummary.ModelName);
    Assert.Equal("high", complexSummary.ReasoningEffort);
    Assert.Equal(1, complexSummary.ReadyCount);
    Assert.Equal(complexPromptCharacters, complexSummary.EstimatedPromptCharacterCount);
    Assert.True(complexSummary.IsPotentiallyPaidProvider);
    Xunit.Assert.Null(plan.ReadyStartCostRisk);
    Xunit.Assert.Null(plan.ReadyStartCostRecommendation);
    Xunit.Assert.Null(plan.ReadyStartPromptCharacterCount);
    Xunit.Assert.Empty(plan.ReadyStartCostRiskDetails);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_surfaces_adaptive_reasoning_effort_reason")]
    public void SubscriptionPlanSurfacesAdaptiveReasoningEffortReason()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Implement high-risk multi-scope persistence migration across every store",
        [new TaskSpec(TaskId.New(), "Implement the scoped slice.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("adaptive-developer"),
        "Adaptive Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "medium"),
        ReasoningEffortPolicy: new ReasoningEffortPolicy(RetryDepthThreshold: 3, RetryDepthEffort: "high", ComplexityEffort: "high", ClassFindingEffort: "high"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var plan = SubscriptionPlanBuilder.Build(goal, [agent], WorkerProfileCatalog.Default());

    var item = Assert.Single(plan.Items);
    Assert.Equal("high", item.SubscriptionReasoningEffort);
    Assert.Equal("complexity", item.ReasoningEffortReason);
    var summary = Assert.Single(plan.ReadyModelUsage);
    Assert.Equal("high", summary.ReasoningEffort);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_surfaces_prior_model_fit_for_ready_models")]
    public void SubscriptionPlanSurfacesPriorModelFitForReadyModels()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Update the old button label.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Update the next button label.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Tune model choice from fit evidence", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-mini - overkill - copy-only change.",
        string.Empty,
        DateTimeOffset.UtcNow));

    var plan = SubscriptionPlanBuilder.Build(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        task => kernel.BuildTaskBrief(goal.Id, task.Id).Content.Length);

    var summary = plan.ReadyModelUsage.Single();
    Assert.Equal("OpenAI", summary.ProviderName);
    Assert.Equal("gpt-5-mini", summary.ModelName);
    Assert.Equal(1, summary.ReadyCount);
    Assert.Equal(1, summary.PreviousModelFitNoteCount);
    Assert.Equal(0, summary.PreviousAdequateCount);
    Assert.Equal(1, summary.PreviousOverkillCount);
    Assert.Equal(0, summary.PreviousUnderpoweredCount);
    Assert.Equal(0, summary.PreviousUnknownFitCount);
    Assert.True(summary.PreviousTaskShapes?.Contains("copy-only change") == true);
    Assert.True(summary.ModelFitRecommendation?.Contains("try local Ollama/qwen3:8b", StringComparison.Ordinal) == true);
    Assert.Equal("prior overkill model", plan.ReadyStartCostRisk);
    Assert.True(plan.ReadyStartCostRiskDetails.Any(detail => detail.Contains("prior overkill model-fit note", StringComparison.Ordinal)));
    Assert.True(plan.ReadyStartCostRiskDetails.Any(detail => detail.Contains("shapes copy-only change", StringComparison.Ordinal)));
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_retains_earlier_model_fit_attempts_for_ready_models")]
    public void SubscriptionPlanRetainsEarlierModelFitAttemptsForReadyModels()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Update the old button label.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Update the next button label.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Tune model choice from all fit evidence", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-mini - overkill - label-only change.",
        string.Empty,
        DateTimeOffset.UtcNow));
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification passed",
        "C:\\repo",
        0,
        "Evidence checked.\nModel fit: OpenAI/gpt-5.4-mini - adequate - focused parser fix.",
        string.Empty,
        DateTimeOffset.UtcNow.AddMinutes(1)));

    var plan = SubscriptionPlanBuilder.Build(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(kernel, goal, task, [agent]));

    var summary = plan.ReadyModelUsage.Single();
    Assert.Equal("OpenAI", summary.ProviderName);
    Assert.Equal("gpt-5-mini", summary.ModelName);
    Assert.Equal(1, summary.PreviousModelFitNoteCount);
    Assert.Equal(1, summary.PreviousOverkillCount);
    Assert.True(summary.PreviousTaskShapes?.Contains("label-only change") == true);
    Assert.True(summary.ModelFitRecommendation?.Contains("try local Ollama/qwen3:8b", StringComparison.Ordinal) == true);
    Assert.Equal("prior overkill model", plan.ReadyStartCostRisk);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_flags_prior_underpowered_model_fit_for_ready_models")]
    public void SubscriptionPlanFlagsPriorUnderpoweredModelFitForReadyModels()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Fix failed parser behavior.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Fix another parser behavior.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Avoid repeating underpowered model choice", [priorTask, nextTask]);
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification failed",
        "C:\\repo",
        1,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-mini - underpowered - missed regression path.",
        string.Empty,
        DateTimeOffset.UtcNow));

    var plan = SubscriptionPlanBuilder.Build(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        task => kernel.BuildTaskBrief(goal.Id, task.Id).Content.Length);

    var summary = plan.ReadyModelUsage.Single();
    Assert.Equal(1, summary.PreviousUnderpoweredCount);
    Assert.True(summary.PreviousTaskShapes?.Contains("missed regression path") == true);
    Assert.True(summary.ModelFitRecommendation?.Contains("choose a stronger model", StringComparison.Ordinal) == true);
    Assert.Equal("prior underpowered model", plan.ReadyStartCostRisk);
    Assert.True(plan.ReadyStartCostRiskDetails.Any(detail => detail.Contains("prior underpowered model-fit note", StringComparison.Ordinal)));
    Assert.True(plan.ReadyStartCostRiskDetails.Any(detail => detail.Contains("shapes missed regression path", StringComparison.Ordinal)));
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_escalates_underpowered_routine_model_to_complex_model")]
    public void SubscriptionPlanEscalatesUnderpoweredRoutineModelToComplexModel()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTask = new TaskSpec(TaskId.New(), "Fix failed parser behavior.", AgentRole.Developer);
    var nextTask = new TaskSpec(TaskId.New(), "Fix another parser behavior.", AgentRole.Developer);
    var goal = kernel.CreateGoal("Avoid repeating underpowered model choice", [priorTask, nextTask]);
    const string subscriptionAlias = "gpt-5-mini-codex";
    var agent = new AgentDefinition(
        new AgentId("cost-aware-developer"),
        "Cost-aware Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", subscriptionAlias, "low"),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
    kernel.ActivateGoal(goal.Id, [agent]);
    kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
    kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
        "manual-verification failed",
        "C:\\repo",
        1,
        "Evidence checked.\nModel fit: OpenAI/gpt-5-mini - underpowered - missed regression path.",
        string.Empty,
        DateTimeOffset.UtcNow));

    var plan = SubscriptionPlanBuilder.Build(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(kernel, goal, task, [agent]));
    var thresholdRisk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        _ => 5000);
    var dispatchRoot = CreateTempDirectory();
    File.WriteAllText(Path.Combine(dispatchRoot, ".git"), "gitdir: ..");
    WriteSkill(dispatchRoot, "dotnet-windows-build-hygiene");
    WriteSkill(dispatchRoot, "verification-before-completion");

    var item = plan.Items.Single(candidate => candidate.TaskId == nextTask.Id.Value);
    var summary = plan.ReadyModelUsage.Single();
    Assert.Equal(TaskComplexity.Simple, item.TaskComplexity);
    Assert.True(item.UsesComplexModel);
    Assert.Equal("OpenAI", item.ProviderName);
    Assert.Equal("gpt-5.5", item.ModelName);
    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Equal(subscriptionAlias, item.SubscriptionModelName);
    Assert.Equal("high", item.SubscriptionReasoningEffort);
    Assert.True(summary.UsesComplexModel);
    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Equal(subscriptionAlias, summary.ModelName);
    Xunit.Assert.Null(plan.ReadyStartCostRisk);
    Xunit.Assert.Empty(plan.ReadyStartCostRiskDetails);
    Xunit.Assert.Null(thresholdRisk);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        nextTask,
        [agent],
        WorkerProfileCatalog.Default(),
        Path.Combine(dispatchRoot, "prompts"),
        dispatchRoot,
        DateTimeOffset.UtcNow);
    var preparedRisk = SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, nextTask);

    Assert.True(nextTask.LastDispatch!.UsesComplexModel);
    Assert.Equal(TaskComplexity.Simple, nextTask.LastDispatch.TaskComplexity);
    // Subscription launch profiles always pin the configured alias; complexity only changes API-side model/effort.
    Assert.Equal(subscriptionAlias, nextTask.LastDispatch.ModelName);
    Xunit.Assert.Null(preparedRisk);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_marks_usage_limited_tasks_not_preparable_until_retry_time")]
    public void SubscriptionPlanMarksUsageLimitedTasksNotPreparableUntilRetryTime()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan retry-later subscription task");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var now = DateTimeOffset.UtcNow;
    var retryTime = now.AddHours(1);
    kernel.RecordTaskDispatch(
        goal.Id,
        developer.Id,
        new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            "C:\\repo",
            now,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec",
        "C:\\repo",
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryTime:h:mm tt}.",
        now));
    kernel.RetryTask(goal.Id, developer.Id, "Retry after provider window.");
    kernel.RecordTaskDispatch(
        goal.Id,
        developer.Id,
        new TaskDispatchRecord(
            "codex-cli",
            "codex exec retry",
            "C:\\repo",
            now.AddMinutes(5),
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec retry",
        "C:\\repo",
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryTime:h:mm tt}.",
        now.AddMinutes(5)));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, WorkerProfileCatalog.Default());

    var item = plan.Items.First(item => item.Role == AgentRole.Developer);
    Assert.Equal(1, plan.RetryDeferredCount);
    Assert.Equal(developer.SubscriptionRetryAfter, plan.NextSubscriptionRetryAfter);
    Assert.Equal(developer.SubscriptionRetryAfter, plan.CapacitySchedule.NextRetryAfter);
    Assert.Equal(1, plan.CapacitySchedule.DeferredCount);
    Assert.False(item.CanPrepare);
    Assert.Equal(developer.SubscriptionRetryAfter, item.RetryAfter);
    var capacityAction = plan.CapacitySchedule.Actions.Single(action => action.TaskId == developer.Id.Value);
    Assert.Equal(ProviderCapacityDisposition.Deferred, capacityAction.Disposition);
    Assert.True(capacityAction.Alternatives.Any(alternative => alternative.Contains("different provider", StringComparison.OrdinalIgnoreCase)));
    Assert.True(item.RetryDelaySeconds is > 0);
    Assert.Equal(2, item.RecoverableSubscriptionLimitFailureCount);
    Assert.Contains("Recoverable subscription usage limit", item.Detail, StringComparison.Ordinal);
    Assert.Contains("2 previous recoverable subscription usage limit failures", item.Detail, StringComparison.Ordinal);
    Assert.Contains("retry after", item.Detail, StringComparison.Ordinal);
    Assert.Equal(developer.SubscriptionRetryAfter, DashboardResponseMapper.ToTaskSummaryDto(goal, developer).SubscriptionRetryAfter);
}

    [Xunit.Fact(DisplayName = "Provider_connectivity_retry_backoff_defers_subscription_dispatch_until_not_before")]
    public void ProviderConnectivityRetryBackoffDefersSubscriptionDispatchUntilNotBefore()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(promptRoot);
    Directory.CreateDirectory(workingDirectory);
    var failureAt = DateTimeOffset.UtcNow.AddMinutes(5);
    var retryAttemptAt = failureAt.AddSeconds(30);
    var kernel = new AgentOrchestratorKernel(new TestClock(failureAt));
    var goal = kernel.CreateGoal(
        "Retry planner dispatch after provider connectivity",
        [new TaskSpec(TaskId.New(), "Plan retry.", AgentRole.Planner)]);
    var agent = SubscriptionPlannerAgent("planner", "Planner");
    var agents = new[] { agent };
    var profiles = DispatchTestProfiles();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec attempt 1",
        workingDirectory,
        failureAt,
        ProviderName: "OpenAI",
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, ProviderConnectivityVerification("codex exec attempt 1", workingDirectory, failureAt));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles, now: retryAttemptAt);
    var item = plan.Items.Single();
    var parallelPlan = GoalManagementCommandService.BuildReadyTaskParallelPlan(goal, agents);
    var batch = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
        kernel,
        goal,
        agents,
        profiles,
        promptRoot,
        workingDirectory,
        retryAttemptAt);
    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        agents,
        profiles,
        workingDirectory,
        retryAttemptAt);

    Assert.Equal(failureAt.AddMinutes(1), task.SubscriptionRetryAfter);
    Assert.False(item.CanPrepare);
    Assert.Equal(task.SubscriptionRetryAfter, item.RetryAfter);
    Assert.Equal(1, plan.RetryDeferredCount);
    Assert.Empty(parallelPlan.Batches);
    Assert.Empty(batch.Dispatches);
    Assert.Contains(batch.Blocked, blocked =>
        blocked.Reason == "subscription-preflight" &&
        blocked.Details?.Any(detail => detail.Contains("subscription retry deferred", StringComparison.Ordinal)) == true);
    Assert.False(preflight.Allowed);
    Assert.Contains(preflight.Findings, finding => finding.Contains("subscription retry deferred", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "Provider_connectivity_retry_backoff_allows_subscription_dispatch_after_not_before")]
    public void ProviderConnectivityRetryBackoffAllowsSubscriptionDispatchAfterNotBefore()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(promptRoot);
    Directory.CreateDirectory(workingDirectory);
    WriteSkill(workingDirectory, "orchestrator-dogfood");
    var failureAt = DateTimeOffset.UtcNow.AddMinutes(-5);
    var retryAttemptAt = failureAt.AddMinutes(2);
    var kernel = new AgentOrchestratorKernel(new TestClock(failureAt));
    var goal = kernel.CreateGoal(
        "Resume planner dispatch after provider connectivity",
        [new TaskSpec(TaskId.New(), "Plan after retry.", AgentRole.Planner)]);
    var agent = SubscriptionPlannerAgent("planner", "Planner");
    var agents = new[] { agent };
    var profiles = DispatchTestProfiles();
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
        "codex-cli",
        "codex exec attempt 1",
        workingDirectory,
        failureAt,
        ProviderName: "OpenAI",
        WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, task.Id, ProviderConnectivityVerification("codex exec attempt 1", workingDirectory, failureAt));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, profiles, now: retryAttemptAt);
    var item = plan.Items.Single();
    var parallelPlan = GoalManagementCommandService.BuildReadyTaskParallelPlan(goal, agents);
    var expiredRetryAfter = task.SubscriptionRetryAfter;
    var batch = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
        kernel,
        goal,
        agents,
        profiles,
        promptRoot,
        workingDirectory,
        retryAttemptAt);

    Assert.Equal(failureAt.AddMinutes(1), expiredRetryAfter);
    Assert.True(expiredRetryAfter < retryAttemptAt);
    Assert.True(item.CanPrepare);
    Assert.Null(item.RetryAfter);
    Assert.Equal(0, plan.RetryDeferredCount);
    Assert.Contains(parallelPlan.Batches, candidate => candidate.IntentIds.Contains(task.Id.Value, StringComparer.OrdinalIgnoreCase));
    Assert.Single(batch.Dispatches);
    Assert.Equal(task.Id, batch.Dispatches.Single().Task.Id);
    Assert.Empty(batch.Blocked);
}

    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_blocks_large_paid_ready_subscription_start_before_dispatch")]
    public void SubscriptionPromptCostGuardBlocksLargePaidReadySubscriptionStartBeforeDispatch()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Plan expensive subscription start",
        [new TaskSpec(TaskId.New(), "Do paid subscription work.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-codex"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();

    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        _ => 12001);
    var ex = Assert.ThrowsAny<InvalidOperationException>(() => SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false));

    Assert.True(risk is not null);
    Assert.Equal(12001, risk!.PromptCharacterCount);
    Assert.False(risk.PromptExceedsBatchThreshold);
    Assert.True(risk.HasOversizedPrompt);
    Assert.True(risk.IsAnomalous);
    Assert.False(risk.TaskCountExceedsThreshold);
    Assert.False(risk.UsesComplexPaidModel);
    Assert.Equal("large paid subscription start", SubscriptionPromptCostGuard.BuildInlineLabel(risk));
    Assert.Contains("--confirm-large-paid-subscription-start", ex.Message, StringComparison.Ordinal);
    Assert.Contains("thresholds 18000 chars or 3 task(s)", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Paid subscription start requires explicit confirmation", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Inspect the generated prompt before paid subscription start", ex.Message, StringComparison.Ordinal);
    Assert.True(task.LastDispatch is null);
}

private static TaskVerificationRecord ProviderConnectivityVerification(
    string command,
    string workingDirectory,
    DateTimeOffset completedAt) =>
    new(
        command,
        workingDirectory,
        1,
        string.Empty,
        "Falling back from WebSockets to HTTPS transport failed. stream disconnected",
        completedAt,
        ProviderFailureKind: ProviderFailureKind.Connectivity);

    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_paid_batch_fanout_with_small_prompts_is_advisory_not_blocking")]
    public void SubscriptionPromptCostGuardPaidBatchFanoutWithSmallPromptsIsAdvisoryNotBlocking()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Plan paid batch subscription start");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);

    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        _ => 500);
    Assert.True(risk is not null);
    Assert.Equal(5, risk!.TaskCount);
    Assert.Equal(2500, risk.PromptCharacterCount);
    Assert.False(risk.PromptExceedsBatchThreshold);
    Assert.False(risk.HasOversizedPrompt);
    Assert.True(risk.TaskCountExceedsThreshold);
    Assert.False(risk.UsesComplexPaidModel);
    Assert.False(risk.IsAnomalous);
    Assert.Equal("paid subscription fanout", SubscriptionPromptCostGuard.BuildInlineLabel(risk));
    Assert.True(risk.Details.Any(detail => detail.Contains("Paid task count 5 exceeds 3", StringComparison.Ordinal)));
    // Fan-out with small prompts is advisory, not an anomaly, so the start is not blocked.
    SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false);
}

    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_allows_complex_paid_start_under_size_threshold")]
    public void SubscriptionPromptCostGuardAllowsComplexPaidStartUnderSizeThreshold()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Plan complex paid subscription start",
        [new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture with distributed rollback and data integrity checks.", AgentRole.Developer)]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);

    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        _ => 500);

    Xunit.Assert.Null(risk);
}

    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_applies_prior_evidence_allowance_to_ready_prompt")]
    public void SubscriptionPromptCostGuardAppliesPriorEvidenceAllowanceToReadyPrompt()
{
    var kernel = new AgentOrchestratorKernel();
    var priorTasks = Enumerable.Range(1, 3)
        .Select(index => new TaskSpec(TaskId.New(), $"Report prior result {index}.", AgentRole.Researcher))
        .ToList();
    var nextTask = new TaskSpec(TaskId.New(), "Report next result.", AgentRole.Researcher);
    var goal = kernel.CreateGoal("Collect pipeline reports", [.. priorTasks, nextTask]);
    var agent = new AgentDefinition(
        new AgentId("researcher"),
        "Researcher",
        AgentRole.Researcher,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "low"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "low"));
    kernel.ActivateGoal(goal.Id, [agent]);
    foreach (var priorTask in priorTasks)
    {
        kernel.ReportTaskProgress(goal.Id, priorTask.Id, WorkTaskStatus.Completed, "Done.");
        kernel.RecordTaskVerification(goal.Id, priorTask.Id, new TaskVerificationRecord(
            "manual-verification passed",
            "C:\\repo",
            0,
            new string('a', 900),
            string.Empty,
            DateTimeOffset.UtcNow));
    }

    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        _ => 7600,
        nextTask);

    // Substantial prior evidence engages the allowance. The estimate is capped near the allowance
    // and is line-ending-sensitive (CRLF on Windows vs LF on Linux), so assert it is clearly
    // substantial rather than pinned to the exact boundary; the behavioral check is risk == null.
    Assert.True(AgentOrchestratorKernel.EstimatePriorTaskEvidenceCharacterCount(goal, nextTask.Id) > PaidPromptThresholds.PriorTaskEvidenceAllowance / 2);
    Xunit.Assert.Null(risk);
}

    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_proportionate_batch_total_is_advisory_not_blocking")]
    public void SubscriptionPromptCostGuardProportionateBatchTotalIsAdvisoryNotBlocking()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Plan complex batch subscription work",
        [
            new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture with distributed rollback and data integrity checks for service one.", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture with distributed rollback and data integrity checks for service two.", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Design and implement a production multi-tenant architecture with distributed rollback and data integrity checks for service three.", AgentRole.Developer)
        ]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-mini", "medium"),
        ComplexModel: new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey, "high"));
    kernel.ActivateGoal(goal.Id, [agent]);

    var risk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        _ => 8000);
    // 24000 across 3 Complex tasks (each within the complexity band) exceeds the soft batch ceiling
    // but is below the anomaly batch ceiling (2x), so it is advisory only and does not block.
    Assert.True(risk is not null);
    Assert.Equal(24000, risk!.PromptCharacterCount);
    Assert.True(risk.PromptExceedsBatchThreshold);
    Assert.False(risk.HasOversizedPrompt);
    Assert.False(risk.TaskCountExceedsThreshold);
    Assert.True(risk.UsesComplexPaidModel);
    Assert.False(risk.IsAnomalous);
    Assert.Equal("large paid subscription start", SubscriptionPromptCostGuard.BuildInlineLabel(risk));
    SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(risk, confirmed: false);
}

    [Xunit.Fact(DisplayName = "SubscriptionPromptCostGuard_large_but_proportionate_prompt_is_advisory_anomalous_prompt_blocks")]
    public void SubscriptionPromptCostGuardLargeButProportionatePromptIsAdvisoryAnomalousPromptBlocks()
{
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal(
        "Plan a paid subscription task",
        [new TaskSpec(TaskId.New(), "Do substantial paid subscription work.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-codex"));
    kernel.ActivateGoal(goal.Id, [agent]);

    // 11000 chars: over the soft prompt ceiling but under the anomaly ceiling for either complexity
    // classification -> advisory only, does NOT block (the behavior change: no rote confirm flag).
    var proportionate = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal, [agent], WorkerProfileCatalog.Default(), _ => 11000);
    Assert.True(proportionate is not null);
    Assert.True(proportionate!.HasOversizedPrompt);
    Assert.False(proportionate.IsAnomalous);
    SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(proportionate, confirmed: false);

    // 20000 chars: disproportionate to complexity -> anomaly -> requires explicit confirmation.
    var anomalous = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(
        goal, [agent], WorkerProfileCatalog.Default(), _ => 20000);
    Assert.True(anomalous!.IsAnomalous);
    Assert.ThrowsAny<InvalidOperationException>(() => SubscriptionPromptCostGuard.ThrowIfConfirmationRequired(anomalous, confirmed: false));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_skips_usage_limited_tasks_before_retry_time")]
    public void WorkerProfileDispatcherSkipsUsageLimitedTasksBeforeRetryTime()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var failureAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Skip retry-later subscription dispatch");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    kernel.RecordTaskDispatch(
        goal.Id,
        developer.Id,
        new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            workingDirectory,
            failureAt,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec",
        workingDirectory,
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
        failureAt));

    var snapshot = kernel.ExportSnapshot();
    var goalSnapshot = snapshot.Goals.Single(candidate => candidate.Id == goal.Id.Value);
    var taskSnapshots = goalSnapshot.Tasks
        .Select(candidate => candidate.Id == developer.Id.Value
            ? candidate with { SubscriptionRetryAfter = null }
            : candidate)
        .ToArray();
    kernel.ReplaceWithSnapshot(snapshot with
    {
        Goals = snapshot.Goals
            .Select(candidate => candidate.Id == goal.Id.Value
                ? candidate with { Tasks = taskSnapshots }
                : candidate)
            .ToArray()
    });
    goal = kernel.GetGoal(goal.Id);
    developer = goal.Tasks.Single(candidate => candidate.Id == developer.Id);

    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        failureAt.AddMinutes(30));

    Assert.False(results.Any(result => result.Task.Id == developer.Id));
    Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
    Assert.True(developer.LastVerification is null);

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        developer,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        failureAt.AddMinutes(30)));
    Assert.Contains("Subscription preflight failed", ex.Message, StringComparison.Ordinal);
    Assert.Contains("subscription retry deferred until", ex.Message, StringComparison.Ordinal);
    Assert.Contains("source: verification history record 1 of 1", ex.Message, StringComparison.Ordinal);
    Assert.Contains($"completed {failureAt:u}", ex.Message, StringComparison.Ordinal);

    kernel.RetryTask(goal.Id, developer.Id, "Operator cleared the history-derived retry deferral.");

    Assert.Single(developer.VerificationHistory);
    Assert.False(WorkerProfileDispatcher.IsTaskRetryDeferred(developer, failureAt.AddMinutes(30), out _));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_blocks_same_provider_tasks_during_provider_cooldown")]
    public void WorkerProfileDispatcherBlocksSameProviderTasksDuringProviderCooldown()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var failureAt = DateTimeOffset.UtcNow;
    var retryTime = failureAt.AddHours(2);
    var kernel = new AgentOrchestratorKernel();
    var limitedTask = new TaskSpec(TaskId.New(), "Hit a subscription usage limit", AgentRole.Developer);
    var sameProviderTask = new TaskSpec(TaskId.New(), "Continue work on the same provider", AgentRole.Developer);
    var goal = kernel.CreateGoal("Block same provider while cooling down", [limitedTask, sameProviderTask]);
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    kernel.RecordTaskDispatch(
        goal.Id,
        limitedTask.Id,
        new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            workingDirectory,
            failureAt,
            "OpenAI",
            "gpt-5.5",
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, limitedTask.Id, new TaskVerificationRecord(
        "codex exec",
        workingDirectory,
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryTime:h:mm tt}.",
        failureAt));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, WorkerProfileCatalog.Default());
    var sameProviderItem = plan.Items.Single(item => item.TaskId == sameProviderTask.Id.Value);
    var providerBudget = plan.ProviderBudgets.Single(item => item.ProviderName == "OpenAI");
    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        sameProviderTask,
        agents,
        WorkerProfileCatalog.Default(),
        workingDirectory,
        failureAt.AddMinutes(10));
    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        failureAt.AddMinutes(10));

    Assert.False(sameProviderItem.CanPrepare);
    Assert.Equal(limitedTask.SubscriptionRetryAfter, sameProviderItem.RetryAfter);
    Assert.Equal(limitedTask.SubscriptionRetryAfter, plan.CapacitySchedule.NextRetryAfter);
    Assert.True(plan.CapacitySchedule.Actions.Any(action =>
        action.TaskId == sameProviderTask.Id.Value &&
        action.Disposition == ProviderCapacityDisposition.Deferred &&
        action.Alternatives.Any(alternative => alternative.Contains("different provider", StringComparison.OrdinalIgnoreCase))));
    Assert.True(providerBudget.IsCoolingDown);
    Assert.Equal(limitedTask.SubscriptionRetryAfter, providerBudget.RetryAfter);
    Assert.Equal(TaskDisplayNumber.Resolve(goal, limitedTask.Id), providerBudget.SourceTaskNumber);
    Assert.Equal(1, providerBudget.RecoverableLimitFailureCount);
    Assert.Contains("Provider OpenAI is cooling down", sameProviderItem.Detail, StringComparison.Ordinal);
    Assert.Contains(TaskDisplayNumber.Resolve(goal, limitedTask.Id).ToString(), sameProviderItem.Detail, StringComparison.Ordinal);
    Assert.False(preflight.Allowed);
    Assert.True(preflight.Findings.Any(finding => finding.Contains("provider OpenAI is cooling down", StringComparison.Ordinal)));
    Assert.False(results.Any(result => result.Task.Id == sameProviderTask.Id));
    Assert.True(sameProviderTask.LastDispatch is null);
}

    [Xunit.Fact(DisplayName = "SubscriptionPlan_keeps_unrelated_provider_out_of_cooldown")]
    public void SubscriptionPlanKeepsUnrelatedProviderOutOfCooldown()
{
    var workingDirectory = CreateTempDirectory();
    var failureAt = DateTimeOffset.UtcNow;
    var retryTime = failureAt.AddHours(2);
    var kernel = new AgentOrchestratorKernel();
    var openAiTask = new TaskSpec(TaskId.New(), "Hit OpenAI usage limit", AgentRole.Developer);
    var anthropicTask = new TaskSpec(TaskId.New(), "Continue on Anthropic", AgentRole.Reviewer);
    var goal = kernel.CreateGoal("Keep unrelated provider usable", [openAiTask, anthropicTask]);
    IReadOnlyList<AgentDefinition> agents =
    [
        new AgentDefinition(
            new AgentId("openai-developer"),
            "OpenAI developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5")),
        new AgentDefinition(
            new AgentId("anthropic-reviewer"),
            "Anthropic reviewer",
            AgentRole.Reviewer,
            new ModelProfile("Anthropic", "claude-haiku-4-5", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-haiku-4-5"))
    ];
    kernel.ActivateGoal(goal.Id, agents);
    kernel.RecordTaskDispatch(
        goal.Id,
        openAiTask.Id,
        new TaskDispatchRecord(
            "codex-cli",
            "codex exec",
            workingDirectory,
            failureAt,
            "OpenAI",
            "gpt-5.5"));
    kernel.RecordDispatchExecutionResult(goal.Id, openAiTask.Id, new TaskVerificationRecord(
        "codex exec",
        workingDirectory,
        1,
        string.Empty,
        $"ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at {retryTime:h:mm tt}.",
        failureAt));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, WorkerProfileCatalog.Default());
    var openAiBudget = plan.ProviderBudgets.Single(item => item.ProviderName == "OpenAI");
    var anthropicBudget = plan.ProviderBudgets.Single(item => item.ProviderName == "Anthropic");
    var anthropicItem = plan.Items.Single(item => item.TaskId == anthropicTask.Id.Value);

    Assert.True(openAiBudget.IsCoolingDown);
    Assert.False(anthropicBudget.IsCoolingDown);
    Xunit.Assert.Null(anthropicBudget.RetryAfter);
    Assert.Equal(0, anthropicBudget.DeferredCount);
    Xunit.Assert.Null(anthropicItem.RetryAfter);
    Assert.False(anthropicItem.Detail.Contains("cooling down", StringComparison.OrdinalIgnoreCase));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_requires_review_after_repeated_usage_limits")]
    public void WorkerProfileDispatcherRequiresReviewAfterRepeatedUsageLimits()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    WriteSkill(workingDirectory, "dotnet-windows-build-hygiene");
    WriteSkill(workingDirectory, "orchestrator-dogfood");
    WriteSkill(workingDirectory, "orchestrator-worker-verification");
    WriteSkill(workingDirectory, "verification-before-completion");
    var firstFailureAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
    var secondFailureAt = DateTimeOffset.Parse("2026-06-01T13:00:00Z");
    var retryWindowPassed = DateTimeOffset.Parse("2026-06-01T18:00:00Z");
    var kernel = new AgentOrchestratorKernel(new TestClock(firstFailureAt));
    var goal = kernel.CreateGoal("Review repeated subscription usage limits");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    CompleteResearcherAndPlannerArtifacts(kernel, goal);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    kernel.RecordTaskDispatch(
        goal.Id,
        developer.Id,
        new TaskDispatchRecord(
            "codex-cli",
            "codex exec attempt 1",
            workingDirectory,
            firstFailureAt,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec attempt 1",
        workingDirectory,
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
        firstFailureAt));
    kernel.RetryTask(goal.Id, developer.Id, "Retry after first usage limit.");
    kernel.RecordTaskDispatch(
        goal.Id,
        developer.Id,
        new TaskDispatchRecord(
            "codex-cli",
            "codex exec attempt 2",
            workingDirectory,
            secondFailureAt,
            WorkerProviderKind: ProviderKind.OpenAICodexCli));
    kernel.RecordDispatchExecutionResult(goal.Id, developer.Id, new TaskVerificationRecord(
        "codex exec attempt 2",
        workingDirectory,
        1,
        string.Empty,
        "ERROR: You've hit your usage limit. Visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at 4:58 PM.",
        secondFailureAt));

    var plan = SubscriptionPlanBuilder.Build(goal, agents, WorkerProfileCatalog.Default());
    var item = plan.Items.First(item => item.Role == AgentRole.Developer);
    var results = WorkerProfileDispatcher.PrepareSubscriptionReadyTasks(
        kernel,
        goal,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        retryWindowPassed);

    Assert.Equal(2, item.RecoverableSubscriptionLimitFailureCount);
    Assert.False(item.CanPrepare);
    Assert.Contains("Repeated recoverable subscription usage limit", item.Detail, StringComparison.Ordinal);
    Assert.Contains("inspect model, profile, or timing", item.Detail, StringComparison.Ordinal);
    Assert.False(results.Any(result => result.Task.Id == developer.Id));

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        developer,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        retryWindowPassed));
    Assert.Contains("Subscription preflight failed", ex.Message, StringComparison.Ordinal);
    Assert.Contains("repeated recoverable subscription limits require operator review", ex.Message, StringComparison.Ordinal);

    kernel.AcknowledgeSubscriptionLimitReview(goal.Id, developer.Id, "Reviewed profile and provider timing.");
    var reviewedPlan = SubscriptionPlanBuilder.Build(goal, agents, WorkerProfileCatalog.Default());
    var reviewedItem = reviewedPlan.Items.First(item => item.Role == AgentRole.Developer);
    var result = WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        developer,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        retryWindowPassed);

    Assert.True(reviewedItem.CanPrepare);
    Assert.Equal(developer.Id, result.Task.Id);
    Assert.Equal(WorkTaskStatus.Running, developer.Status);
    Assert.Equal("Reviewed profile and provider timing.", developer.SubscriptionLimitReviewNote);
    Assert.Equal(2, developer.SubscriptionLimitReviewedFailureCount);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_rejects_subscription_dispatch_for_developer_without_worktree")]
    public void WorkerProfileDispatcherRejectsSubscriptionDispatchForDeveloperWithoutWorktree()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Implement the feature without workspace");
    var agent = new AgentDefinition(
        new AgentId("openai-developer"),
        "OpenAI Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt));

    Assert.Contains("Subscription preflight failed", ex.Message, StringComparison.Ordinal);
    Assert.Contains("goal workspace", ex.Message, StringComparison.Ordinal);
    Assert.Contains("Developer", ex.Message, StringComparison.Ordinal);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastDispatch is null);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_allows_subscription_dispatch_for_researcher_without_worktree")]
    public void WorkerProfileDispatcherAllowsSubscriptionDispatchForResearcherWithoutWorktree()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    WriteSkill(workingDirectory, "dotnet-windows-build-hygiene");
    WriteSkill(workingDirectory, "orchestrator-dogfood");
    WriteSkill(workingDirectory, "orchestrator-worker-verification");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Survey the codebase without workspace");
    var agent = new AgentDefinition(
        new AgentId("openai-researcher"),
        "OpenAI Researcher",
        AgentRole.Researcher,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.PreferSubscription);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Equal(WorkTaskStatus.Running, task.Status);
    Assert.True(task.LastDispatch is not null);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_blocks_dirty_file_role_worktree")]
    public void WorkerProfileDispatcherPreflightBlocksDirtyFileRoleWorktree()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Implement the feature with a clean workspace");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    File.WriteAllText(Path.Combine(worktree, "dirty.txt"), "uncommitted");

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        worktree,
        dispatchedAt);
    var ex = Assert.ThrowsAny<InvalidOperationException>(() => WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        worktree,
        dispatchedAt));

    Assert.False(preflight.Allowed);
    Assert.True(preflight.Findings.Any(finding =>
        finding.Contains("worktree has 1 uncommitted change", StringComparison.Ordinal) &&
        finding.Contains("dirty.txt", StringComparison.Ordinal)));
    Assert.True(preflight.Findings.Any(finding => finding.Contains("build environment: goal lease not yet created", StringComparison.Ordinal)));
    Assert.True(preflight.Findings.Any(finding => finding.Contains(Path.Combine("goals", goal.Id.Value[..8], "artifacts"), StringComparison.OrdinalIgnoreCase)));
    Assert.Contains("worktree has 1 uncommitted change", ex.Message, StringComparison.Ordinal);
    Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    Assert.True(task.LastDispatch is null);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_allows_receipt_only_worktree")]
    public void WorkerProfileDispatcherPreflightAllowsReceiptOnlyWorktree()
{
    var root = CreateSeededDispatchRepository();
    var dispatchedAt = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Implement the feature with a prepped workspace");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    CompleteResearcherAndPlannerArtifacts(kernel, goal);
    var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    File.WriteAllText(Path.Combine(worktree, WorkerSandboxPreparer.ReceiptFileName), "{}");

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        worktree,
        dispatchedAt);

    Assert.True(preflight.Allowed);
    Assert.Contains("ok: worktree clean before dispatch", preflight.Findings);
    Assert.DoesNotContain(preflight.Findings, finding => finding.Contains("uncommitted change", StringComparison.Ordinal));
    Assert.Equal($"?? {WorkerSandboxPreparer.ReceiptFileName}", ReadGit(worktree, ["status", "--short"]));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_allows_exact_legacy_PowerShell_cache_but_blocks_sibling")]
    public void WorkerProfileDispatcherPreflightAllowsExactLegacyPowerShellCacheButBlocksSibling()
{
    var root = CreateSeededDispatchRepository();
    var dispatchedAt = DateTimeOffset.Parse("2026-06-12T10:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Implement with isolated PowerShell cache");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    CompleteResearcherAndPlannerArtifacts(kernel, goal);
    var task = goal.Tasks.First(candidate => candidate.RequiredRole == AgentRole.Developer);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var cachePath = Path.Combine(worktree, "Microsoft", "Windows", "PowerShell", "ModuleAnalysisCache");
    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
    File.WriteAllText(cachePath, "cache");

    var cacheOnly = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal, task, agents, WorkerProfileCatalog.Default(), worktree, dispatchedAt);

    Assert.True(cacheOnly.Allowed);
    File.WriteAllText(cachePath + ".source", "real work");

    var withSibling = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal, task, agents, WorkerProfileCatalog.Default(), worktree, dispatchedAt);

    Assert.False(withSibling.Allowed);
    Assert.Contains(withSibling.Findings, finding =>
        finding.Contains("Microsoft/Windows/PowerShell/ModuleAnalysisCache.source", StringComparison.Ordinal));
    Assert.Null(task.LastDispatch);
    Assert.Null(task.LastProcess);
}

}
