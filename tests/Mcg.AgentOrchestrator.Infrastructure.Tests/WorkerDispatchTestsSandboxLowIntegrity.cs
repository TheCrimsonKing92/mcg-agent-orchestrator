using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text.Json;

[Xunit.Collection("EnvMutation")]
public sealed class WorkerDispatchTestsSandboxLowIntegrity : WorkerDispatchTestSupport
{
    [Xunit.Fact(DisplayName = "CliStartup_sets_protected_pid_before_worker_dispatch")]
    public void CliStartupSetsProtectedPidBeforeWorkerDispatch()
    {
        using var _ = ClearProtectedPidEnvironment();

        CliProtectedProcessEnvironment.EnsureProtectedPid();

        var protectedPid = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable);
        Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), protectedPid);
        var ownTicks = Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedStartTicksVariable);
        Assert.False(string.IsNullOrWhiteSpace(ownTicks));

        Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable, "12345");
        Environment.SetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedStartTicksVariable, null);
        CliProtectedProcessEnvironment.EnsureProtectedPid();

        Assert.Equal(protectedPid, Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedPidVariable));
        Assert.Equal(ownTicks, Environment.GetEnvironmentVariable(CliProtectedProcessEnvironment.ProtectedStartTicksVariable));
    }

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_allows_claude_cli_only_auth_via_sandbox_credential_seeding")]
    public void WorkerProfileDispatcherPreflightAllowsClaudeCliOnlyAuthViaSandboxCredentialSeeding()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verify Claude auth preflight", [new TaskSpec(TaskId.New(), "Test the implementation.", AgentRole.Tester)]);
    var agent = new AgentDefinition(
        new AgentId("tester"),
        "Tester",
        AgentRole.Tester,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    var credentialPath = Path.Combine(root, ".claude", ".credentials.json");
    var authProbe = () => new ClaudeCliAuthState(
        HasAnthropicApiKey: false,
        HasCliCredentialArtifact: true,
        CredentialArtifactPath: credentialPath);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        worktree,
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);
    var findings = string.Join("\n", preflight.Findings);
    Assert.True(preflight.Allowed);
    Assert.Null(preflight.ErrorCode);
    Assert.Contains("ok: Claude CLI Low-IL auth preflight will seed CLI credentials", findings);
    Assert.DoesNotContain("Low-IL Claude subscription dispatch is refused before worker start", findings);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_names_the_rejected_claude_login_source_and_reason")]
    public void WorkerProfileDispatcherPreflightNamesTheRejectedClaudeLoginSourceAndReason()
{
    var root = CreateSeededDispatchRepository();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verify Claude auth preflight diagnostics", [new TaskSpec(TaskId.New(), "Test the implementation.", AgentRole.Tester)]);
    var agent = new AgentDefinition(
        new AgentId("tester"),
        "Tester",
        AgentRole.Tester,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    var rejectedSource = Path.Combine(root, "operator-config-missing");
    var authProbe = () => new ClaudeCliAuthState(
        HasAnthropicApiKey: false,
        HasCliCredentialArtifact: false,
        CredentialArtifactPath: null,
        SelectedSourceDirectory: rejectedSource,
        IsExplicitSource: true,
        UnavailableReason: "sole candidate explicit CLAUDE_CONFIG_DIR login source '" + rejectedSource + "' was rejected because the directory does not exist");

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        worktree,
        DateTimeOffset.Parse("2026-09-12T16:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);

    var authFinding = preflight.Findings.Single(finding =>
        finding.StartsWith("auth: Claude CLI Low-IL auth preflight found no", StringComparison.Ordinal));

    // The finding must name WHICH source was attempted and WHY it was rejected. "found no credential
    // artifact" alone is what let preflight and seeding disagree without anyone noticing.
    Assert.Contains(rejectedSource, authFinding);
    Assert.Contains("CLAUDE_CONFIG_DIR", authFinding);
    Assert.Contains("the directory does not exist", authFinding);

    // Admission policy is frozen: this stays an `auth:` observation. The hard stop for an unusable
    // login is the pre-launch seeding failure, not a `blocked:` admission finding.
    Assert.DoesNotContain("blocked:", authFinding);
    Assert.DoesNotContain("sk-ant-", authFinding);
    Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_records_the_preflight_selected_claude_login_for_dispatch_start")]
    public void WorkerProfileDispatcherRecordsThePreflightSelectedClaudeLoginForDispatchStart()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");

    // Synthetic fixture logins outside the repository: the operator-set CLAUDE_CONFIG_DIR source, and a
    // different usable source at the simulated default profile that must never be selected or recorded.
    // Both tokens are knowingly invalid; this exercises source selection, never authentication.
    var credentialRoot = CreateTempDirectory();
    var explicitDir = Path.Combine(credentialRoot, "operator-config");
    Directory.CreateDirectory(explicitDir);
    File.WriteAllText(
        Path.Combine(explicitDir, ".credentials.json"),
        "{\"claudeAiOauth\":{\"accessToken\":\"sk-ant-oat01-synthetic-not-a-real-token\"}}");
    var defaultHome = Path.Combine(credentialRoot, "default-home");
    Directory.CreateDirectory(Path.Combine(defaultHome, ".claude"));
    File.WriteAllText(
        Path.Combine(defaultHome, ".claude", ".credentials.json"),
        "{\"claudeAiOauth\":{\"accessToken\":\"sk-ant-oat01-WRONG-PROFILE-SENTINEL\"}}");

    var configDirectoryReads = 0;
    Func<string, string?> environmentReader = name =>
    {
        if (!string.Equals(name, "CLAUDE_CONFIG_DIR", StringComparison.Ordinal))
        {
            return null;
        }

        configDirectoryReads++;
        return explicitDir;
    };

    // ONE resolver for this preparation, exactly as the production default probe provides.
    var resolver = new ClaudeCredentialResolver(environmentReader, () => defaultHome);
    var authProbe = () => ClaudeCliAuthProbe.From(resolver);

    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verify Claude credential source handoff", [new TaskSpec(TaskId.New(), "Test the implementation.", AgentRole.Tester)]);
    var agent = new AgentDefinition(
        new AgentId("tester"),
        "Tester",
        AgentRole.Tester,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    var dispatchedAt = DateTimeOffset.Parse("2026-09-12T16:00:00Z");

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        worktree,
        dispatchedAt,
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);
    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        worktree,
        dispatchedAt,
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);

    // The source the auth finding an operator reads names, and the source carried out of preflight, are
    // one resolution - not two computations that happen to agree on this machine.
    var authFinding = preflight.Findings.Single(finding =>
        finding.StartsWith("ok: Claude CLI Low-IL auth preflight will seed", StringComparison.Ordinal));
    Assert.Contains(Path.GetFullPath(explicitDir), authFinding);
    Assert.Equal(Path.GetFullPath(explicitDir), preflight.ClaudeCredentialSelection!.DirectoryPath);
    Assert.True(preflight.ClaudeCredentialSelection.IsExplicitSource);

    // Recorded on the dispatch, because the dispatch start boundary can be a later tick - or a later
    // process, after a conductor restart - that cannot share an object with this preparation. The
    // snapshot round trip is that boundary.
    var reloaded = AgentOrchestratorKernel
        .FromSnapshot(kernel.ExportSnapshot())
        .GetTask(goal.Id, task.Id)
        .LastDispatch;
    Assert.NotNull(reloaded);
    Assert.Equal(preflight.ClaudeCredentialSelection.DirectoryPath, reloaded!.ClaudeCredentialSourceDirectory);
    Assert.True(reloaded.ClaudeCredentialSourceIsExplicit);

    // And dispatch start transports exactly that, without consulting an environment of its own - which
    // here would resolve to the other login.
    Assert.Equal(
        preflight.ClaudeCredentialSelection,
        DispatchProcessHost.TransportedClaudeCredentialSelection(
            reloaded.ClaudeCredentialSourceDirectory,
            reloaded.ClaudeCredentialSourceIsExplicit,
            WorkerSandboxProvider.Claude,
            sandboxLowIntegrity: true,
            environmentReader: _ => throw new InvalidOperationException("selection input was read")));

    // One resolution served model-lane selection, the auth finding, and the recorded selection across
    // both the preflight and the preparation call.
    Assert.Equal(1, resolver.ResolutionCount);
    Assert.Equal(1, configDirectoryReads);
    Assert.DoesNotContain("WRONG-PROFILE-SENTINEL", string.Join("\n", preflight.Findings));
    Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_ready_batch_records_the_preflight_selected_claude_login")]
    public void WorkerProfileDispatcherReadyBatchRecordsThePreflightSelectedClaudeLogin()
{
    // The conductor's own preparation path. Its dispatches are the ones a worker is launched from, so
    // the recorded credential source has to travel from here, not from whatever the start boundary's
    // environment happens to resolve later.
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var credentialRoot = CreateTempDirectory();
    var selectedDir = Path.Combine(credentialRoot, "operator-config");
    Directory.CreateDirectory(selectedDir);
    File.WriteAllText(
        Path.Combine(selectedDir, ".credentials.json"),
        "{\"claudeAiOauth\":{\"accessToken\":\"sk-ant-oat01-synthetic-not-a-real-token\"}}");

    var resolver = new ClaudeCredentialResolver(
        name => string.Equals(name, "CLAUDE_CONFIG_DIR", StringComparison.Ordinal) ? selectedDir : null,
        () => Path.Combine(credentialRoot, "unused-home"));
    var authProbe = () => ClaudeCliAuthProbe.From(resolver);

    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verify ready batch credential handoff", [new TaskSpec(TaskId.New(), "Test the implementation.", AgentRole.Tester)]);
    var agent = new AgentDefinition(
        new AgentId("tester"),
        "Tester",
        AgentRole.Tester,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var batch = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
        kernel,
        goal,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        worktree,
        DateTimeOffset.Parse("2026-09-12T16:00:00Z"),
        sandboxOptions: sandbox,
        claudeAuthProbe: authProbe);

    Assert.Single(batch.Dispatches);
    var dispatch = task.LastDispatch!;
    Assert.Equal(Path.GetFullPath(selectedDir), dispatch.ClaudeCredentialSourceDirectory);
    Assert.True(dispatch.ClaudeCredentialSourceIsExplicit);
    Assert.Equal(1, resolver.ResolutionCount);
    Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_allows_light_role_claude_auth_via_sandbox_credential_seeding")]
    public void WorkerProfileDispatcherPreflightAllowsLightRoleClaudeAuthViaSandboxCredentialSeeding()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verify Claude light-role auth preflight", [new TaskSpec(TaskId.New(), "Review implementation output.", AgentRole.Reviewer)]);
    var agents = AgentCatalog.AnthropicDefault().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    var credentialPath = Path.Combine(root, ".claude", ".credentials.json");
    var authProbe = () => new ClaudeCliAuthState(
        HasAnthropicApiKey: false,
        HasCliCredentialArtifact: true,
        CredentialArtifactPath: credentialPath);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        agents,
        WorkerProfileCatalog.Default(),
        worktree,
        DateTimeOffset.Parse("2026-07-09T00:08:59Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);
    var findings = string.Join("\n", preflight.Findings);
    Assert.True(preflight.Allowed);
    Assert.Equal("claude-cli", preflight.ProfileName);
    Assert.Null(preflight.ErrorCode);
    Assert.Contains("ok: Claude CLI Low-IL auth preflight will seed CLI credentials", findings);
    Assert.DoesNotContain("light-role profile unavailable (Claude CLI Low-IL auth unavailable)", findings);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_skips_claude_auth_guard_for_codex_low_integrity_dispatch")]
    public void WorkerProfileDispatcherPreflightSkipsClaudeAuthGuardForCodexLowIntegrityDispatch()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verify codex auth preflight isolation", [new TaskSpec(TaskId.New(), "Test the implementation.", AgentRole.Tester)]);
    var agent = new AgentDefinition(
        new AgentId("tester"),
        "Tester",
        AgentRole.Tester,
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    Func<ClaudeCliAuthState> authProbe = () => throw new InvalidOperationException("Claude auth probe must not run for codex workers.");

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        worktree,
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);
    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        worktree,
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);

    Assert.True(preflight.Allowed, string.Join("\n", preflight.Findings));
    Assert.Null(preflight.ErrorCode);
    Assert.Equal("codex-cli", task.LastDispatch!.WorkerName);
    Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_allows_claude_low_integrity_when_api_key_is_present")]
    public void WorkerProfileDispatcherPreflightAllowsClaudeLowIntegrityWhenApiKeyIsPresent()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Verify Claude api key auth preflight", [new TaskSpec(TaskId.New(), "Test the implementation.", AgentRole.Tester)]);
    var agent = new AgentDefinition(
        new AgentId("tester"),
        "Tester",
        AgentRole.Tester,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    var authProbe = () => new ClaudeCliAuthState(
        HasAnthropicApiKey: true,
        HasCliCredentialArtifact: true,
        CredentialArtifactPath: Path.Combine(root, ".claude", ".credentials.json"));

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        worktree,
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);
    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        promptRoot,
        worktree,
        DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
        claudeAuthProbe: authProbe,
        sandboxOptions: sandbox);

    Assert.True(preflight.Allowed, string.Join("\n", preflight.Findings));
    Assert.Null(preflight.ErrorCode);
    Assert.Equal("claude-cli", task.LastDispatch!.WorkerName);
    Assert.Null(task.LastProcess);
}

    [Xunit.Fact(DisplayName = "WorkerSandboxOptions_has_no_provider_property")]
    public void WorkerSandboxOptionsHasNoProviderProperty()
{
    Assert.Null(typeof(WorkerSandboxOptions).GetProperty("Provider"));
}

    [Xunit.Fact(DisplayName = "IWorkerProvider_keeps_sandbox_policy_on_IWorkerSandbox")]
    public void IWorkerProviderKeepsSandboxPolicyOnIWorkerSandbox()
{
    Assert.True(typeof(IWorkerSandbox).IsInterface);
    Assert.True(new EnvironmentWorkerSandbox() is IWorkerSandbox);

    Assert.Null(typeof(IWorkerProvider).GetProperty("Options"));
    Assert.Null(typeof(IWorkerProvider).GetProperty("Sandbox"));
    Assert.Null(typeof(WorkerCapabilities).GetProperty("Sandbox"));
    Assert.Null(typeof(WorkerCapabilities).GetProperty("SandboxMode"));
}

    [Xunit.Fact(DisplayName = "DispatchProcessHost_seeds_claude_auth_environment_for_claude_worker_sandbox")]
    public void DispatchProcessHostSeedsClaudeAuthEnvironmentForClaudeWorkerSandbox()
{
    var previousKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    var root = CreateTempDirectory();
    try
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-claude-key");
        var startInfo = CreateSandboxStartInfo(root);
        var sandboxRoot = Path.Combine(root, ".mcg-sandbox");

        DispatchProcessHost.SeedProviderEnvironment(startInfo, WorkerSandboxProvider.Claude, sandboxRoot);

        Assert.Equal("test-claude-key", startInfo.Environment["ANTHROPIC_API_KEY"]);
        Assert.False(startInfo.Environment.ContainsKey("CODEX_HOME"));
        Assert.False(Directory.Exists(Path.Combine(sandboxRoot, "codex-home")));
        Assert.True(startInfo.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var claudeConfigDir));
        Assert.True(Directory.Exists(claudeConfigDir));
        Assert.Equal("{}\n", File.ReadAllText(Path.Combine(claudeConfigDir!, "settings.json")));
    }
    finally
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previousKey);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

    [Xunit.Fact(DisplayName = "DispatchProcessHost_does_not_inject_claude_environment_for_codex_worker_sandbox")]
    public void DispatchProcessHostDoesNotInjectClaudeEnvironmentForCodexWorkerSandbox()
{
    var previousKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    var root = CreateTempDirectory();
    try
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "test-claude-key");
        var startInfo = CreateSandboxStartInfo(root);
        var sandboxRoot = Path.Combine(root, ".mcg-sandbox");

        DispatchProcessHost.SeedProviderEnvironment(startInfo, WorkerSandboxProvider.Codex, sandboxRoot);

        Assert.False(startInfo.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(startInfo.Environment.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Equal(Path.Combine(sandboxRoot, "codex-home"), startInfo.Environment["CODEX_HOME"]);
        Assert.True(Directory.Exists(Path.Combine(sandboxRoot, "codex-home")));
    }
    finally
    {
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previousKey);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_preflight_allows_Claude_repo_scoped_skill_targets_with_OS_confinement")]
    public void WorkerProfileDispatcherPreflightAllowsClaudeRepoScopedSkillTargetsWithOsConfinement()
{
    var root = CreateSeededDispatchRepository();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Create .agents/skills/example/SKILL.md", [new TaskSpec(TaskId.New(), "Author .agents/skills/example/SKILL.md", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6", "medium"));
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var workingDirectory = GoalWorktrees.Ensure(root, goal.Id);
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        WorkerProfileCatalog.Default(),
        workingDirectory,
        DateTimeOffset.Parse("2026-06-13T12:00:00Z"),
        claudeAuthProbe: () => new ClaudeCliAuthState(true, false, null),
        sandboxOptions: sandbox,
        commandExists: _ => true);

    Assert.True(preflight.Allowed);
    Assert.Equal("repo-skill-write", preflight.CapabilityStatus);
    Assert.Contains("repo-scoped .agents/skills", string.Join("\n", preflight.Findings), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_repo_scoped_skill_unknown_launcher_cannot_write_or_commit")]
    public void WorkerProfileDispatcherRepoScopedSkillUnknownLauncherCannotWriteOrCommit()
{
    var root = CreateSeededDispatchRepository();
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Create .agents/skills/smoke/SKILL.md", [new TaskSpec(TaskId.New(), "Author .agents/skills/smoke/SKILL.md and commit it.", AgentRole.Developer)]);
    var agent = new AgentDefinition(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey, "medium"),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("repo-skill-smoke", "claude-sonnet-4-6", "medium"));
    var profiles = new WorkerProfileCatalog(
    [
        new WorkerProfile(
            "repo-skill-smoke",
            "Write-Output 'subscription model {subscriptionModelName}'; Write-Output 'permission --permission-mode bypassPermissions'; New-Item -ItemType Directory -Force '.agents/skills/smoke' | Out-Null; Set-Content -Path '.agents/skills/smoke/SKILL.md' -Value \"---`nname: smoke`ndescription: Smoke test skill.`n---`n`n# Smoke`n\"; git add .agents/skills/smoke/SKILL.md; git commit -m 'Add smoke skill'; Write-Output 'WORKER_RESULT:'; Write-Output 'files: .agents/skills/smoke/SKILL.md'; Write-Output 'commands: git add .agents/skills/smoke/SKILL.md; git commit -m Add smoke skill'; Write-Output 'tests: repo-skill smoke committed'; Write-Output 'blockers: none'; Write-Output 'model_fit: deterministic full-permission profile - adequate - repo skill write smoke'; Write-Output 'skills: skill-creator'; Write-Output 'confidence: high'; Write-Output 'END_WORKER_RESULT'")
    ]);
    kernel.ActivateGoal(goal.Id, [agent]);
    var task = goal.Tasks.Single();
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var dispatchedAt = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
    var sandbox = new WorkerSandboxOptions(true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);
    var headBefore = ReadGit(worktree, ["rev-parse", "HEAD"]);

    var preflight = WorkerProfileDispatcher.PreflightSubscriptionTask(
        goal,
        task,
        [agent],
        profiles,
        worktree,
        dispatchedAt,
        sandboxOptions: sandbox,
        commandExists: _ => true);

    Assert.False(preflight.Allowed);
    Assert.Equal("blocked", preflight.CapabilityStatus);
    Assert.Null(task.LastDispatch);
    Assert.Null(task.LastProcess);
    Assert.False(File.Exists(Path.Combine(worktree, ".agents", "skills", "smoke", "SKILL.md")));
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Equal(headBefore, ReadGit(worktree, ["rev-parse", "HEAD"]));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_low_integrity_dirty_worktree_with_verification_evidence_only_stays_failed")]
    public void BackgroundDispatchRunnerLowIntegrityDirtyWorktreeWithVerificationEvidenceOnlyStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the feature and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "implemented but not committed"),
        sandboxLowIntegrity: true);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains("feature.txt", ReadGit(worktree, ["status", "--short"]), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_self_committing_provider_dirty_verified_without_low_integrity_evidence_stays_failed")]
    public void BackgroundDispatchRunnerSelfCommittingProviderDirtyVerifiedWithoutLowIntegrityEvidenceStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the feature and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "implemented but not committed"),
        workerName: "claude-cli",
        command: "claude prompt");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains("feature.txt", ReadGit(worktree, ["status", "--short"]), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_codex_provider_can_self_commit_false_without_low_integrity_stays_failed")]
    public void BackgroundDispatchRunnerCodexProviderCanSelfCommitFalseWithoutLowIntegrityStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the feature and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "implemented but not committed"));

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains("feature.txt", ReadGit(worktree, ["status", "--short"]), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_typed_provider_can_self_commit_false_without_low_integrity_stays_failed")]
    public void BackgroundDispatchRunnerTypedProviderCanSelfCommitFalseWithoutLowIntegrityStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var workerProfile = "typed-openai-worker";
    var providers = new WorkerProviderCatalog([
        new StaticWorkerProvider(
            new WorkerProviderIdentity(ProviderKind.OpenAICodexCli, UsesCodexExitFileBehavior: true),
            workerProfile,
            "OpenAI",
            new WorkerCapabilities(
                CanSelfCommit: false,
                CanSelfVerify: true,
                SupportsInteractiveSession: true,
                SupportsPlanMode: true))
    ]);
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the feature and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "implemented but not committed"),
        workerName: workerProfile,
        command: "opaque worker prompt",
        workerProviderKind: ProviderKind.OpenAICodexCli);

    new BackgroundDispatchRunner(clock, workerProviders: providers).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    Assert.Contains("left the worktree dirty", task.LastVerification.StandardError, StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains("feature.txt", ReadGit(worktree, ["status", "--short"]), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_typed_non_self_committing_provider_exit1_dirty_with_low_integrity_evidence_commits_on_behalf")]
    public void BackgroundDispatchRunnerTypedNonSelfCommittingProviderExit1DirtyWithLowIntegrityEvidenceCommitsOnBehalf()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var workerProfile = "typed-openai-worker";
    var providers = new WorkerProviderCatalog([
        new StaticWorkerProvider(
            new WorkerProviderIdentity(ProviderKind.OpenAICodexCli, UsesCodexExitFileBehavior: true),
            workerProfile,
            "OpenAI",
            new WorkerCapabilities(
                CanSelfCommit: false,
                CanSelfVerify: true,
                SupportsInteractiveSession: true,
                SupportsPlanMode: true))
    ]);
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the feature and ran the focused tests." + Environment.NewLine +
            WorkerResultBlock("feature.txt", "dotnet test --filter WorkerDispatch", "Passed: 2, Failed: 0"),
        SandboxPrepCompleteEvent(),
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "implemented but codex exited one"),
        workerName: workerProfile,
        command: "codex exec prompt",
        workerProviderKind: ProviderKind.OpenAICodexCli,
        sandboxLowIntegrity: true);

    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock, workerProviders: providers).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(
        "complete non-failing WORKER_RESULT and dirty worktree edits",
        task.LastVerification.StandardError,
        StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Contains("feature.txt", ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_file_role_nonzero_exit_dirty_verified_without_typed_sandbox_evidence_stays_failed")]
    public void BackgroundDispatchRunnerFileRoleNonZeroExitDirtyVerifiedWithoutTypedSandboxEvidenceStaysFailed()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the change and ran the focused tests.\r\nPassed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2.",
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "edited but commit failed under low integrity"),
        sandboxLowIntegrity: true);

    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Failed, task.Status);
    Assert.Equal(1, task.LastVerification!.ExitCode);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains("feature.txt", ReadGit(worktree, ["status", "--short"]), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_git_metadata_permission_failure_is_nonfatal_with_dirty_worker_result")]
    public void BackgroundDispatchRunnerGitMetadataPermissionFailureIsNonfatalWithDirtyWorkerResult()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Edited the requested files, but git commit was blocked." + Environment.NewLine +
            WorkerResultBlock("feature.txt", "git add -A; git commit -m Feature", "not-run"),
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "edited before git metadata failure"),
        sandboxLowIntegrity: true);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    var indexLockPath = Path.GetFullPath(Path.Combine(
        root,
        ".git",
        "worktrees",
        goal.Id.Value[..8],
        "index.lock"));
    File.WriteAllText(
        process.StandardErrorPath,
        $"fatal: Unable to create '{indexLockPath}': Permission denied");
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(
        "Classified worker git metadata write failure as non-fatal",
        task.LastVerification.StandardError,
        StringComparison.Ordinal);
    Assert.Contains("index_lock=", task.LastVerification.StandardError, StringComparison.Ordinal);
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Contains("feature.txt", ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_low_integrity_dotnet_1312_dirty_worker_result_is_committed_by_orchestrator")]
    public void BackgroundDispatchRunnerLowIntegrityDotnet1312DirtyWorkerResultIsCommittedByOrchestrator()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the requested change." + Environment.NewLine +
            WorkerResultBlock("feature.txt", "dotnet test --filter LowIntegrity", "not-run"),
        string.Empty,
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "edited before dotnet 1312 failure"),
        sandboxLowIntegrity: true);
    File.WriteAllText(
        process.StandardErrorPath,
        "dotnet.cmd: CreateProcessAsUserW 1312: A specified logon session does not exist. It may already have been terminated.");
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(
        "Orchestrator committed the worker's verified worktree edits",
        task.LastVerification.StandardError,
        StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Contains("feature.txt", ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]), StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_low_integrity_git_1312_commits_work_without_sandbox_marker")]
    public void BackgroundDispatchRunnerLowIntegrityGit1312CommitsWorkWithoutSandboxMarker()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, process) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented the requested change." + Environment.NewLine +
            WorkerResultBlock("feature.txt", "git status --short", "not-run"),
        string.Empty,
        clock,
        worktree =>
        {
            File.WriteAllText(Path.Combine(worktree, "feature.txt"), "edited before git 1312 failure");
            File.WriteAllText(Path.Combine(worktree, WorkerSandboxPreparer.MarkerFileName), "{}");
        },
        sandboxLowIntegrity: true);
    File.WriteAllText(
        process.StandardErrorPath,
        "git.exe: CreateProcessAsUserW failed 1312: A specified logon session does not exist.");
    File.WriteAllText(process.ExitCodePath, "1");

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.Equal(WorkTaskStatus.Completed, task.Status);
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.Contains(
        "Orchestrator committed the worker's verified worktree edits",
        task.LastVerification.StandardError,
        StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Contains("feature.txt", ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]), StringComparison.Ordinal);
    Assert.DoesNotContain(WorkerSandboxPreparer.MarkerFileName, ReadGit(worktree, ["show", "--name-only", "--pretty=", "HEAD"]), StringComparison.Ordinal);
    Assert.Equal(string.Empty, ReadGit(worktree, ["ls-files", "--", WorkerSandboxPreparer.MarkerFileName]));
}

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_first_round_preps_and_writes_receipt")]
    public void WorkerSandboxPreparerFirstRoundPrepsAndWritesReceipt()
{
    var root = CreateTempDirectory();
    var worktree = Path.Combine(root, "worktree");
    var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
    var labeler = new WorkerDispatchRecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true));

    var result = new WorkerSandboxPreparer(labeler).Prepare(worktree, sandboxRoot);

    Assert.False(result.PrepReceiptHit);
    Assert.True(result.WorktreeRecursiveRelabel);
    Assert.False(result.SandboxRecursiveRelabel);
    Assert.Contains(labeler.SetCalls, call => call.Path == worktree && call.Recursive);
    Assert.Contains(labeler.SetCalls, call => call.Path == sandboxRoot && !call.Recursive);
    Assert.True(File.Exists(Path.Combine(worktree, WorkerSandboxPreparer.ReceiptFileName)));
    Assert.True(File.Exists(Path.Combine(sandboxRoot, WorkerSandboxPreparer.ReceiptFileName)));
}

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_second_round_reuses_prep_receipt")]
    public void WorkerSandboxPreparerSecondRoundReusesPrepReceipt()
{
    var root = CreateTempDirectory();
    var worktree = Path.Combine(root, "worktree");
    var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
    var labeler = new WorkerDispatchRecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true));
    var preparer = new WorkerSandboxPreparer(labeler);
    _ = preparer.Prepare(worktree, sandboxRoot);
    labeler.SetCalls.Clear();

    var result = preparer.Prepare(worktree, sandboxRoot);

    Assert.True(result.PrepReceiptHit);
    Assert.False(result.WorktreeRecursiveRelabel);
    Assert.False(result.SandboxRecursiveRelabel);
    Assert.Empty(labeler.SetCalls);
}

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_existing_labeled_worktree_repreps_without_receipt")]
    public void WorkerSandboxPreparerExistingLabeledWorktreeReprepsWithoutReceipt()
{
    var root = CreateTempDirectory();
    var worktree = Path.Combine(root, "worktree");
    var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
    Directory.CreateDirectory(sandboxRoot);
    var labeler = new WorkerDispatchRecordingIntegrityLabeler(
        new IntegrityLabelState(Exists: true, Low: true, Inheritable: true),
        setResult: false);

    var result = new WorkerSandboxPreparer(labeler).Prepare(worktree, sandboxRoot);

    Assert.False(result.RequiresRecovery);
    Assert.False(result.PrepReceiptHit);
    Assert.False(result.WorktreeRecursiveRelabel);
    Assert.False(result.SandboxRecursiveRelabel);
    Assert.Contains(labeler.SetCalls, call => call.Path == worktree && call.Recursive);
    Assert.Contains(labeler.SetCalls, call => call.Path == sandboxRoot && !call.Recursive);
    Assert.True(File.Exists(Path.Combine(worktree, WorkerSandboxPreparer.ReceiptFileName)));
    Assert.True(File.Exists(Path.Combine(sandboxRoot, WorkerSandboxPreparer.ReceiptFileName)));
}

    [Xunit.Fact(DisplayName = "WorkerSandboxPreparer_recreated_worktree_repreps_receipt")]
    public void WorkerSandboxPreparerRecreatedWorktreeReprepsReceipt()
{
    var root = CreateTempDirectory();
    var worktree = Path.Combine(root, "worktree");
    var sandboxRoot = Path.Combine(worktree, ".mcg-sandbox");
    var labeler = new WorkerDispatchRecordingIntegrityLabeler(new IntegrityLabelState(Exists: true, Low: true, Inheritable: true));
    var preparer = new WorkerSandboxPreparer(labeler);
    _ = preparer.Prepare(worktree, sandboxRoot);
    Directory.Delete(worktree, recursive: true);
    labeler.SetCalls.Clear();

    var result = preparer.Prepare(worktree, sandboxRoot);

    Assert.False(result.PrepReceiptHit);
    Assert.True(result.WorktreeRecursiveRelabel);
    Assert.Contains(labeler.SetCalls, call => call.Path == worktree && call.Recursive);
    Assert.True(File.Exists(Path.Combine(worktree, WorkerSandboxPreparer.ReceiptFileName)));
    Assert.True(File.Exists(Path.Combine(sandboxRoot, WorkerSandboxPreparer.ReceiptFileName)));
}

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_low_integrity_exit_zero_dirty_with_setup_evidence_is_committed_by_orchestrator")]
    public void BackgroundDispatchRunnerLowIntegrityExitZeroDirtyWithSetupEvidenceIsCommittedByOrchestrator()
{
    var root = CreateSeededDispatchRepository();
    var clock = new TestClock(DateTimeOffset.Parse("2026-06-02T12:00:00Z"));
    var (kernel, goal, task, _) = CreateCompletedGoalWorktreeDispatch(
        root,
        AgentRole.Developer,
        "Implemented feature." + Environment.NewLine +
            WorkerResultBlock("feature.txt", "implemented feature", "Passed: 1"),
        SandboxPrepCompleteEvent(),
        clock,
        worktree => File.WriteAllText(Path.Combine(worktree, "feature.txt"), "feature"),
        sandboxLowIntegrity: true);

    new BackgroundDispatchRunner(clock).RefreshLatestProcess(kernel, goal.Id, task.Id);

    Assert.True(
        task.Status == WorkTaskStatus.Completed,
        task.LastVerification?.StandardError ?? "missing verification");
    Assert.Equal(0, task.LastVerification!.ExitCode);
    Assert.True(task.LastVerification.HasCommittedChanges);
    Assert.Contains("Orchestrator committed the worker's verified worktree edits", task.LastVerification.StandardError, StringComparison.Ordinal);
    var worktree = GoalWorktrees.Ensure(root, goal.Id);
    Assert.Equal(string.Empty, ReadGit(worktree, ["status", "--short"]));
    Assert.Equal($"Developer({goal.Id.Value[..8]}): Dispatch evidence goal", ReadGit(worktree, ["log", "-1", "--pretty=%s"]));
    Assert.Equal("1", ReadGit(worktree, ["rev-list", "--count", "HEAD~1..HEAD"]));
    var head = ReadGit(worktree, ["rev-parse", "HEAD"]);
    Assert.Equal(head, task.LastDispatch?.ResultCommit);
    Assert.Contains(goal.Timeline, evt =>
        evt.TaskId == task.Id &&
        evt.Kind == ProgressKind.TaskNote &&
        evt.Message.Contains("TaskOutputCommitted", StringComparison.Ordinal) &&
        evt.Message.Contains($"sha={head}", StringComparison.Ordinal) &&
        evt.Message.Contains("provenance=orchestrator", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_researcher_dispatch_uses_claude_read_only_tool_policy")]
    public void WorkerProfileDispatcherResearcherDispatchUsesClaudeReadOnlyToolPolicy()
{
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Survey the codebase configuration");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    var researcher = goal.Tasks.First(task => task.RequiredRole == AgentRole.Researcher);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        researcher,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt,
        sandboxOptions: new WorkerSandboxOptions(false, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget));

    Assert.Equal("claude-cli", researcher.LastDispatch!.WorkerName);
    Assert.Contains("--permission-mode 'dontAsk'", researcher.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--restricted", researcher.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--tools 'Read,Glob,Grep,Bash,WebFetch,WebSearch,TodoWrite'", researcher.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--allowed-tools 'Read,Glob,Grep,Bash(git log *),Bash(git diff *),Bash(git show *),Bash(git status *),Bash(git merge-base *),Bash(git rev-parse *),Bash(git blame *),Bash(git ls-files *),Bash(git branch *),Bash(git cat-file *),Bash(rg *),WebFetch,WebSearch,TodoWrite'", researcher.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--disallowed-tools 'Edit,Write,NotebookEdit'", researcher.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("--allowed-tools 'Read,Glob,Grep,Bash,", researcher.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain(",Task", researcher.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("--permission-mode 'plan'", researcher.LastDispatch.Command, StringComparison.Ordinal);
    Assert.True(!researcher.LastDispatch.Command.Contains("workspace-write", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_developer_dispatch_uses_workspace_write_codex_sandbox")]
    public void WorkerProfileDispatcherDeveloperDispatchUsesWorkspaceWriteCodexSandbox()
{
    // Hermetic: this asserts the DEFAULT (no-OS-sandbox) dispatch mode, which reads
    // WorkerSandboxOptions.FromEnvironment(). Clear the operator's MCG_WORKER_SANDBOX so the test is
    // deterministic even when the suite is run under `conduct`/acceptance with the var set.
    using var _sandboxEnv = ClearWorkerSandboxEnv();
    var root = CreateTempDirectory();
    var promptRoot = Path.Combine(root, "prompts");
    var workingDirectory = Path.Combine(root, "repo");
    Directory.CreateDirectory(workingDirectory);
    File.WriteAllText(Path.Combine(workingDirectory, ".git"), "gitdir: ..");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var goal = kernel.CreateGoal("Implement the feature");
    var agents = AgentCatalog.Default().Agents;
    kernel.ActivateGoal(goal.Id, agents);
    CompleteResearcherAndPlannerArtifacts(kernel, goal);
    var developer = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);

    WorkerProfileDispatcher.PrepareSubscriptionTask(
        kernel,
        goal,
        developer,
        agents,
        WorkerProfileCatalog.Default(),
        promptRoot,
        workingDirectory,
        dispatchedAt);

    Assert.Contains("--sandbox 'workspace-write'", developer.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.True(!developer.LastDispatch.Command.Contains("read-only", StringComparison.Ordinal));
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_OS_sandbox_uses_danger_full_access_for_read_only_codex_role")]
    public void WorkerProfileDispatcherOsSandboxUsesDangerFullAccessForReadOnlyCodexRole()
    {
        var previous = Environment.GetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable);
        try
        {
            Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, "1");

            var variables = WorkerProfileDispatcher.BuildDispatchVariables(
                AgentRole.Researcher,
                @"C:\repo",
                variables: null);

            Assert.Equal("danger-full-access", variables["sandboxMode"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, previous);
        }
    }

    [Xunit.Theory(DisplayName = "WorkerProfileDispatcher_explicit_sandbox_state_drives_Codex_command_mapping")]
    [Xunit.InlineData(true, "0", "danger-full-access")]
    [Xunit.InlineData(false, "1", "workspace-write")]
    public void WorkerProfileDispatcherExplicitSandboxStateDrivesCodexCommandMapping(
        bool explicitSandboxEnabled,
        string ambientSandboxValue,
        string expectedSandboxMode)
    {
        var previous = Environment.GetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable);
        try
        {
            Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, ambientSandboxValue);
            var sandbox = new WorkerSandboxOptions(
                explicitSandboxEnabled,
                WorkerSandboxOptions.DefaultAccount,
                WorkerSandboxOptions.DefaultCredentialTarget);

            var variables = WorkerProfileDispatcher.BuildDispatchVariables(
                AgentRole.Developer,
                @"C:\repo",
                variables: null,
                sandboxOptions: sandbox);

            Assert.Equal(expectedSandboxMode, variables["sandboxMode"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, previous);
        }
    }

    [Xunit.Fact(DisplayName = "BackgroundDispatchRunner_OS_sandbox_covers_read_only_codex_without_writable_worktree")]
    public void BackgroundDispatchRunnerOsSandboxCoversReadOnlyCodexWithoutWritableWorktree()
    {
        Assert.True(BackgroundDispatchRunner.ShouldUseOsSandbox(
            sandboxEnabled: true,
            isLocalDispatch: false,
            AgentRole.Researcher,
            WorkerSandboxProvider.Codex));
        Assert.False(BackgroundDispatchRunner.IsSandboxWorktreeWritable(AgentRole.Researcher));

        Assert.True(BackgroundDispatchRunner.ShouldUseOsSandbox(
            sandboxEnabled: true,
            isLocalDispatch: false,
            AgentRole.Developer,
            WorkerSandboxProvider.Claude));
        Assert.True(BackgroundDispatchRunner.IsSandboxWorktreeWritable(AgentRole.Developer));

        Assert.False(BackgroundDispatchRunner.ShouldUseOsSandbox(
            sandboxEnabled: true,
            isLocalDispatch: false,
            AgentRole.Researcher,
            WorkerSandboxProvider.Claude));
        Assert.False(BackgroundDispatchRunner.ShouldUseOsSandbox(
            sandboxEnabled: true,
            isLocalDispatch: true,
            AgentRole.Researcher,
            WorkerSandboxProvider.Codex));
    }

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_claude_resolves_read_only_tool_policy_for_reviewer_and_bypassPermissions_for_developer")]
    public void WorkerProfileDispatcherClaudeResolvesReadOnlyToolPolicyForReviewerAndBypassPermissionsForDeveloper()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var developerGoal = kernel.CreateGoal("Implement the change", [new TaskSpec(TaskId.New(), "Add the feature.", AgentRole.Developer)]);
    var reviewerGoal = kernel.CreateGoal("Review the change", [new TaskSpec(TaskId.New(), "Review the implementation.", AgentRole.Reviewer)]);
    var workingDirectory = GoalWorktrees.Ensure(root, developerGoal.Id);
    var reviewerAgent = new AgentDefinition(
        new AgentId("anthropic-reviewer"),
        "Anthropic reviewer",
        AgentRole.Reviewer,
        new ModelProfile("Anthropic", "claude-sonnet-4-20250514", ModelCapability.Text, SubscriptionMode.ApiKey, MaxOutputTokens: AgentCatalog.RoutineApiMaxOutputTokens),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet"));
    var developerAgent = new AgentDefinition(
        new AgentId("anthropic-developer"),
        "Anthropic developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-sonnet-4-20250514", ModelCapability.Text, SubscriptionMode.ApiKey, MaxOutputTokens: AgentCatalog.RoutineApiMaxOutputTokens),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet"));
    kernel.ActivateGoal(developerGoal.Id, [developerAgent]);
    kernel.ActivateGoal(reviewerGoal.Id, [reviewerAgent]);
    var developerTask = developerGoal.Tasks.Single();
    var reviewerTask = reviewerGoal.Tasks.Single();
    var authProbe = () => new ClaudeCliAuthState(
        HasAnthropicApiKey: true,
        HasCliCredentialArtifact: false,
        CredentialArtifactPath: null);

    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, developerGoal, developerTask, [developerAgent], WorkerProfileCatalog.Default(), promptRoot, workingDirectory, dispatchedAt, claudeAuthProbe: authProbe);
    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, reviewerGoal, reviewerTask, [reviewerAgent], WorkerProfileCatalog.Default(), promptRoot, workingDirectory, dispatchedAt, claudeAuthProbe: authProbe);

    Assert.Contains("--permission-mode 'bypassPermissions'", developerTask.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Contains("--permission-mode 'dontAsk'", reviewerTask.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Contains("--restricted", reviewerTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--allowed-tools 'Read,Glob,Grep,Bash(git log *),Bash(git diff *),Bash(git show *),Bash(git status *),Bash(git merge-base *),Bash(git rev-parse *),Bash(git blame *),Bash(git ls-files *),Bash(git branch *),Bash(git cat-file *),Bash(rg *),WebFetch,WebSearch,TodoWrite'", reviewerTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--disallowed-tools 'Edit,Write,NotebookEdit'", reviewerTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain(",Task", reviewerTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("--permission-mode 'plan'", reviewerTask.LastDispatch.Command, StringComparison.Ordinal);
}

    [Xunit.Fact(DisplayName = "WorkerProfileDispatcher_claude_read_only_dispatch_denies_edit_tools_and_developer_dispatch_does_not")]
    public void WorkerProfileDispatcherClaudeReadOnlyDispatchDeniesEditToolsAndDeveloperDispatchDoesNot()
{
    var root = CreateSeededDispatchRepository();
    var promptRoot = Path.Combine(root, "prompts");
    var dispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    var kernel = new AgentOrchestratorKernel();
    var researcherGoal = kernel.CreateGoal("Research the change", [new TaskSpec(TaskId.New(), "Inspect the implementation.", AgentRole.Researcher)]);
    var developerGoal = kernel.CreateGoal("Implement the change", [new TaskSpec(TaskId.New(), "Add the feature.", AgentRole.Developer)]);
    var researcherWorkingDirectory = GoalWorktrees.Ensure(root, researcherGoal.Id);
    var developerWorkingDirectory = GoalWorktrees.Ensure(root, developerGoal.Id);
    var researcherAgent = new AgentDefinition(
        new AgentId("anthropic-researcher"),
        "Anthropic researcher",
        AgentRole.Researcher,
        new ModelProfile("Anthropic", "claude-sonnet-4-20250514", ModelCapability.Text, SubscriptionMode.ApiKey, MaxOutputTokens: AgentCatalog.RoutineApiMaxOutputTokens),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet"));
    var developerAgent = new AgentDefinition(
        new AgentId("anthropic-developer"),
        "Anthropic developer",
        AgentRole.Developer,
        new ModelProfile("Anthropic", "claude-sonnet-4-20250514", ModelCapability.Text, SubscriptionMode.ApiKey, MaxOutputTokens: AgentCatalog.RoutineApiMaxOutputTokens),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet"));
    kernel.ActivateGoal(researcherGoal.Id, [researcherAgent]);
    kernel.ActivateGoal(developerGoal.Id, [developerAgent]);
    var researcherTask = researcherGoal.Tasks.Single();
    var developerTask = developerGoal.Tasks.Single();
    var authProbe = () => new ClaudeCliAuthState(
        HasAnthropicApiKey: true,
        HasCliCredentialArtifact: false,
        CredentialArtifactPath: null);

    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, researcherGoal, researcherTask, [researcherAgent], WorkerProfileCatalog.Default(), promptRoot, researcherWorkingDirectory, dispatchedAt, claudeAuthProbe: authProbe);
    WorkerProfileDispatcher.PrepareSubscriptionTask(kernel, developerGoal, developerTask, [developerAgent], WorkerProfileCatalog.Default(), promptRoot, developerWorkingDirectory, dispatchedAt, claudeAuthProbe: authProbe);

    Assert.Contains("--permission-mode 'dontAsk'", researcherTask.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.Contains("--restricted", researcherTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--tools 'Read,Glob,Grep,Bash,WebFetch,WebSearch,TodoWrite'", researcherTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--allowed-tools 'Read,Glob,Grep,Bash(git log *),Bash(git diff *),Bash(git show *),Bash(git status *),Bash(git merge-base *),Bash(git rev-parse *),Bash(git blame *),Bash(git ls-files *),Bash(git branch *),Bash(git cat-file *),Bash(rg *),WebFetch,WebSearch,TodoWrite'", researcherTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.Contains("--disallowed-tools 'Edit,Write,NotebookEdit'", researcherTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("--allowed-tools 'Read,Glob,Grep,Bash,", researcherTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain(",Task", researcherTask.LastDispatch.Command, StringComparison.Ordinal);
    var allowedTools = researcherTask.LastDispatch.Command
        .Split("--allowed-tools '", 2, StringSplitOptions.None)[1]
        .Split('\'', 2)[0]
        .Split(',');
    Assert.DoesNotContain(
        allowedTools,
        tool => tool.Equals("Bash", StringComparison.Ordinal) ||
            tool.StartsWith("Bash(git commit ", StringComparison.Ordinal) ||
            tool.StartsWith("Bash(git push ", StringComparison.Ordinal) ||
            tool.StartsWith("Bash(git checkout ", StringComparison.Ordinal) ||
            tool.StartsWith("Bash(git reset ", StringComparison.Ordinal) ||
            tool.StartsWith("Bash(git worktree ", StringComparison.Ordinal));
    Assert.Contains("--permission-mode 'bypassPermissions'", developerTask.LastDispatch!.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("--restricted", developerTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("--allowed-tools", developerTask.LastDispatch.Command, StringComparison.Ordinal);
    Assert.DoesNotContain("--disallowed-tools", developerTask.LastDispatch.Command, StringComparison.Ordinal);
}

}
