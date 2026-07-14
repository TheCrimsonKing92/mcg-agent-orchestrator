using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerProfileDispatchResult(TaskSpec Task, string PromptPath);

public sealed record ReadyBlockedDiagnostic(
    string GoalPrefix,
    int TaskNumber,
    string TaskId,
    string Provider,
    string Reason,
    IReadOnlyList<string>? Details = null)
{
    public string ToLine() =>
        $"READY_BLOCKED goal={GoalPrefix} task={TaskNumber} provider={Provider} reason={Reason}";
}

public sealed record WorkerProfileReadyBatchResult(
    IReadOnlyList<WorkerProfileDispatchResult> Dispatches,
    IReadOnlyList<ReadyBlockedDiagnostic> Blocked);

public sealed record WorkerSubscriptionPreflightResult(
    bool Allowed,
    string ProfileName,
    string CapabilityStatus,
    IReadOnlyList<string> Findings,
    string? ErrorCode = null);

public sealed class WorkerSubscriptionPreflightException : InvalidOperationException
{
    public WorkerSubscriptionPreflightException(string message, string? errorCode, IReadOnlyList<string> findings)
        : base(message)
    {
        ErrorCode = errorCode;
        Findings = findings;
    }

    public string? ErrorCode { get; }

    public IReadOnlyList<string> Findings { get; }
}

public sealed record ClaudeCliAuthState(
    bool HasAnthropicApiKey,
    bool HasCliCredentialArtifact,
    string? CredentialArtifactPath);

public static class ClaudeCliAuthProbe
{
    public const string AuthUnavailableErrorCode = "ERR_CLAUDE_AUTH_UNAVAILABLE";

    public static ClaudeCliAuthState FromEnvironment()
    {
        var hasApiKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        var artifactPath = FindCredentialArtifactPath();
        return new ClaudeCliAuthState(hasApiKey, artifactPath is not null, artifactPath);
    }

    private static string? FindCredentialArtifactPath()
    {
        foreach (var path in EnumerateCandidateCredentialPaths())
        {
            if (File.Exists(path))
            {
                return path;
            }

            if (Directory.Exists(path) &&
                Directory.EnumerateFileSystemEntries(path).Any(IsClaudeCredentialArtifact))
            {
                return path;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidateCredentialPaths()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".claude.json");
            yield return Path.Combine(userProfile, ".claude");
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".claude.json");
            yield return Path.Combine(home, ".claude");
        }
    }

    private static bool IsClaudeCredentialArtifact(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals(".credentials.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("credentials.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("config.json", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("settings.json", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record DispatchModelOverride(string? ProfileName, string? ModelName, string? ReasoningEffort);

public static class WorkerProfileDispatcher
{
    public const string OpenAiSubscriptionProfileName = "codex-cli";
    public const string AnthropicSubscriptionProfileName = "claude-cli";
    public const string LightRoleAnthropicModelName = "claude-haiku-4-5";
    public const string OllamaSubscriptionProfileName = "qwen-code-cli";
    private static readonly WorkerProviderCatalog DefaultProviders = WorkerProviderCatalog.Default();

    public static WorkerProfileDispatchResult PrepareTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        WorkerProfile profile,
        string promptRoot,
        string workingDirectory,
        DateTimeOffset dispatchedAt,
        IReadOnlyDictionary<string, string?>? variables = null,
        string? providerName = null,
        string? modelName = null,
        string? reasoningEffort = null,
        TaskComplexity? taskComplexity = null,
        bool usesComplexModel = false,
        string? reasoningEffortReason = null,
        IReadOnlyList<string>? preflightFindings = null,
        bool allowPendingRecordedDispatchRefresh = false)
    {
        EnsureTaskNeedsExecution(task, allowPendingRecordedDispatchRefresh);
        EnsureSubscriptionRetryWindowHasPassed(task, dispatchedAt);

        WorkerCommandTemplate.WriteHandoffFile(goal.Tasks, task.Id, workingDirectory);
        var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory, preflightFindings);
        var targetContext = TryReadCurrentTargetContext(workingDirectory);
        var brief = kernel.BuildTaskBrief(
            goal.Id,
            task.Id,
            BuildModelFitTarget(providerName, modelName),
            workingDirectory,
            contextDirectory,
            targetContext?.BranchName,
            targetContext?.HeadCommit);
        var budgetedBrief = WorkerPromptInputBudget.Apply(brief, providerName, modelName).Brief;
        var preparation = WorkerCommandTemplate.Prepare(
            budgetedBrief,
            profile.Name,
            profile.CommandTemplate,
            promptRoot,
            BuildDispatchVariables(task.RequiredRole, workingDirectory, variables),
            dispatchedAt);
        var workerProviderKind = DefaultProviders.ResolveProfile(profile.Name).Identity.Kind;
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            profile.Name,
            preparation.Command,
            workingDirectory,
            dispatchedAt,
            providerName,
            modelName,
            reasoningEffort,
            taskComplexity,
            preparation.PromptCharacterCount,
            usesComplexModel,
            PromptPath: preparation.PromptPath,
            WorkerProviderKind: workerProviderKind,
            ReasoningEffortReason: reasoningEffortReason),
            allowPendingRecordedDispatchRefresh);
        return new WorkerProfileDispatchResult(task, preparation.PromptPath);
    }

    public static IReadOnlyList<WorkerProfileDispatchResult> PrepareReadyTasks(
        AgentOrchestratorKernel kernel,
        Goal goal,
        WorkerProfile profile,
        string promptRoot,
        string workingDirectory,
        DateTimeOffset dispatchedAt)
    {
        var results = new List<WorkerProfileDispatchResult>();
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned).ToList())
        {
            results.Add(PrepareTask(kernel, goal, task, profile, promptRoot, workingDirectory, dispatchedAt));
        }

        return results;
    }

    public static WorkerProfileDispatchResult PrepareSubscriptionTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        string promptRoot,
        string workingDirectory,
        DateTimeOffset dispatchedAt,
        DispatchModelOverride? modelOverride = null,
        bool allowGitReference = false,
        Func<ClaudeCliAuthState>? claudeAuthProbe = null,
        WorkerSandboxOptions? sandboxOptions = null,
        Func<string, bool>? commandExists = null)
    {
        EnsureTaskNeedsExecution(task);

        var agent = ResolveAssignedAgent(kernel, goal, task, agents);
        var roleSelection = ResolveEffectiveSubscriptionModelSelection(agent, goal, task, modelOverride, profiles, claudeAuthProbe, sandboxOptions, commandExists);
        roleSelection = ApplyReasoningEffortPolicy(agent, goal, task, roleSelection);
        var profile = modelOverride?.ProfileName is { Length: > 0 } overrideProfile
            ? profiles.GetRequired(overrideProfile)
            : ResolveSubscriptionProfile(agent, roleSelection.Model, profiles);
        var resolvedModelName = modelOverride?.ModelName is { Length: > 0 } overrideModel
            ? overrideModel
            : ResolveEffectiveSubscriptionModelName(agent, roleSelection);
        var resolvedReasoning = modelOverride?.ReasoningEffort is not null
            ? modelOverride.ReasoningEffort
            : ResolveEffectiveSubscriptionReasoningEffort(agent, roleSelection);
        var reasoningEffortReason = modelOverride?.ReasoningEffort is not null
            ? "override"
            : roleSelection.ReasoningEffortReason;
        var dispatchProviderName = ResolveDispatchProviderName(roleSelection.Model.ProviderName, profile.Name, modelOverride);
        var variables = BuildSubscriptionTemplateVariables(agent, roleSelection);
        if (modelOverride?.ModelName is { Length: > 0 })
            variables["subscriptionModelName"] = resolvedModelName;
        variables["subscriptionReasoningEffort"] = resolvedReasoning;
        variables["reasoningEffortSelectionReason"] = reasoningEffortReason;
        var preflight = PreflightSubscriptionTask(
            goal,
            task,
            agents,
            profiles,
            workingDirectory,
            dispatchedAt,
            modelOverride,
            allowGitReference,
            claudeAuthProbe,
            sandboxOptions,
            commandExists);
        ThrowIfPreflightBlocked(preflight);
        return PrepareTask(
            kernel,
            goal,
            task,
            profile,
            promptRoot,
            workingDirectory,
            dispatchedAt,
            variables,
            dispatchProviderName,
            resolvedModelName,
            resolvedReasoning,
            roleSelection.Complexity,
            roleSelection.UsesComplexModel,
            reasoningEffortReason,
            preflight.Findings);
    }

    public static WorkerSubscriptionPreflightResult PreflightSubscriptionTask(
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        string workingDirectory,
        DateTimeOffset now,
        DispatchModelOverride? modelOverride = null,
        bool allowGitReference = false,
        Func<ClaudeCliAuthState>? claudeAuthProbe = null,
        WorkerSandboxOptions? sandboxOptions = null,
        Func<string, bool>? commandExists = null)
    {
        var findings = new List<string>();
        string profileName;
        try
        {
            EnsureTaskNeedsExecution(task);
            var agent = ResolveAssignedAgent(null, goal, task, agents);
            var roleSelection = ResolveEffectiveSubscriptionModelSelection(agent, goal, task, modelOverride, profiles, claudeAuthProbe, sandboxOptions, commandExists);
            roleSelection = ApplyReasoningEffortPolicy(agent, goal, task, roleSelection);
            profileName = modelOverride?.ProfileName is { Length: > 0 } overrideProfile
                ? overrideProfile
                : ResolveSubscriptionProfileName(agent, roleSelection.Model);
            var profile = profiles.GetRequired(profileName);
            findings.Add($"profile: {profile.Name}");
            findings.Add($"model-selection: {roleSelection.Reason}");
            var sandbox = sandboxOptions ?? WorkerSandboxOptions.FromEnvironment();
            AddClaudeLowIntegrityAuthFinding(findings, task.RequiredRole, DefaultProviders.ResolveProfile(profile.Name), sandbox, claudeAuthProbe);
            var effectiveModelName = modelOverride?.ModelName is { Length: > 0 } overrideModel
                ? overrideModel
                : ResolveEffectiveSubscriptionModelName(agent, roleSelection);
            var dispatchProviderName = ResolveDispatchProviderName(roleSelection.Model.ProviderName, profile.Name, modelOverride);
            findings.Add($"model: {dispatchProviderName}/{effectiveModelName}");
            findings.Add($"complexity: {roleSelection.Complexity}");

            AddProfileFinding(
                findings,
                WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate),
                $"worker profile '{profile.Name}' only echoes prompt path",
                $"worker profile '{profile.Name}' is a real launcher");
            AddProfileFinding(
                findings,
                !WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate),
                $"worker profile '{profile.Name}' does not include {{subscriptionModelName}}",
                $"worker profile '{profile.Name}' pins selected model");
            var reasoningEffort = modelOverride?.ReasoningEffort is not null
                ? modelOverride.ReasoningEffort
                : ResolveEffectiveSubscriptionReasoningEffort(agent, roleSelection);
            var reasoningEffortReason = modelOverride?.ReasoningEffort is not null
                ? "override"
                : roleSelection.ReasoningEffortReason;
            findings.Add($"reasoning-effort: {reasoningEffort ?? "none"} ({reasoningEffortReason})");
            AddProfileFinding(
                findings,
                RequiresSubscriptionReasoningPlaceholder(roleSelection.Model.ProviderName, reasoningEffort) &&
                    !WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(profile.CommandTemplate),
                $"worker profile '{profile.Name}' does not include {{subscriptionReasoningEffort}}",
                $"worker profile '{profile.Name}' pins selected reasoning when required");

            var capability = WorkerSandboxCapabilityPlanner.Evaluate(goal, task, profile, workingDirectory, allowGitReference);
            findings.Add($"capability: {capability.Status} - {capability.Detail}");
            if (!capability.Allowed)
            {
                findings.Add($"blocked: {capability.Detail}");
            }

            AddSkillAvailabilityFindings(findings, goal, task, workingDirectory);
            AddBuildEnvironmentFinding(findings, goal, task);
            AddWorktreeCleanlinessFinding(findings, task, workingDirectory);
            AddGitMetadataAccessFinding(findings, task, workingDirectory, sandbox);

            if (IsTaskRetryDeferred(task, now, out var retryAfter))
            {
                findings.Add($"blocked: subscription retry deferred until {retryAfter:u}");
            }

            if (DispatchFailureClassifier.TryGetProviderSubscriptionCooldown(
                goal,
                task.Id,
                roleSelection.Model.ProviderName,
                now,
                out var providerCooldown))
            {
                findings.Add(
                    $"blocked: provider {providerCooldown.ProviderName} is cooling down after task {TaskDisplayNumber.Resolve(goal, providerCooldown.SourceTaskId)} until {providerCooldown.RetryAfter:u}");
            }

            if (DispatchFailureClassifier.RequiresSubscriptionLimitReview(task))
            {
                findings.Add("blocked: repeated recoverable subscription limits require operator review");
            }

            var blocked = findings.Any(finding => finding.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase));
            if (!blocked)
            {
                findings.Add("ready: profile, sandbox, worktree, and retry state passed deterministic preflight");
            }

            return new WorkerSubscriptionPreflightResult(!blocked, profileName, capability.Status, findings, ResolvePreflightErrorCode(findings));
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            profileName = "unknown";
            findings.Add($"blocked: {ex.Message}");
            return new WorkerSubscriptionPreflightResult(false, profileName, "blocked", findings);
        }
    }

    private static void AddClaudeLowIntegrityAuthFinding(
        List<string> findings,
        AgentRole role,
        IWorkerProvider provider,
        WorkerSandboxOptions sandbox,
        Func<ClaudeCliAuthState>? claudeAuthProbe)
    {
        if (provider.Identity.Kind != ProviderKind.AnthropicClaudeCli)
        {
            findings.Add("auth: Claude CLI Low-IL auth preflight not applicable for this worker profile");
            return;
        }

        if (!sandbox.Enabled)
        {
            findings.Add("auth: Claude CLI Low-IL auth preflight not required because worker sandbox is disabled");
            return;
        }

        var authState = (claudeAuthProbe ?? ClaudeCliAuthProbe.FromEnvironment)();
        if (authState.HasAnthropicApiKey)
        {
            findings.Add("ok: Claude CLI Low-IL auth preflight found ANTHROPIC_API_KEY");
            return;
        }

        if (!authState.HasCliCredentialArtifact)
        {
            findings.Add("auth: Claude CLI Low-IL auth preflight found no API key and no CLI credential artifact");
            return;
        }

        var artifact = string.IsNullOrWhiteSpace(authState.CredentialArtifactPath)
            ? "canonical Claude CLI credential path"
            : authState.CredentialArtifactPath;
        findings.Add(
            $"blocked: {ClaudeCliAuthProbe.AuthUnavailableErrorCode}: Claude CLI credentials exist at {artifact}, but ANTHROPIC_API_KEY is not set; Low-IL Claude subscription dispatch is refused before worker start because persisted CLI login/trust is not available inside the sandbox");
    }

    private static string? ResolvePreflightErrorCode(IReadOnlyList<string> findings)
    {
        if (findings.Any(finding => finding.Contains(ClaudeCliAuthProbe.AuthUnavailableErrorCode, StringComparison.Ordinal)))
        {
            return ClaudeCliAuthProbe.AuthUnavailableErrorCode;
        }

        return null;
    }

    private static void AddGitMetadataAccessFinding(
        List<string> findings,
        TaskSpec task,
        string workingDirectory,
        WorkerSandboxOptions sandbox)
    {
        if (task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester))
        {
            findings.Add("git metadata: write check not required for read-only role");
            return;
        }

        if (!File.Exists(Path.Combine(workingDirectory, ".git")))
        {
            findings.Add("git metadata: unavailable before dispatch; goal workspace is not a linked worktree");
            return;
        }

        var access = GoalWorktrees.InspectGitMetadataAccess(workingDirectory, sandbox);
        var status = access.Error is null ? "ok" : "warn";
        findings.Add(
            $"{status}: git metadata index_lock={access.IndexLockPath}; " +
            $"current_identity={access.CurrentIdentity}; " +
            $"current_process_can_write={access.CurrentProcessCanWriteIndexLock}; " +
            $"worker_git_write={access.WorkerWriteDisposition}; " +
            $"commit_contract={access.CommitContract}");
        if (access.Error is not null)
        {
            findings.Add($"git metadata warning: {access.Error}");
        }
    }

    private static void AddProfileFinding(List<string> findings, bool blocked, string blockedMessage, string okMessage)
    {
        findings.Add(blocked ? $"blocked: {blockedMessage}" : $"ok: {okMessage}");
    }

    private static void AddSkillAvailabilityFindings(List<string> findings, Goal goal, TaskSpec task, string workingDirectory)
    {
        var selectedSkills = WorkerContextArtifacts.SelectSkillRequirements(goal, task, workingDirectory);
        if (selectedSkills.Count == 0)
        {
            findings.Add("skills: no deterministic skill rule matched this task");
            return;
        }

        var skillRoot = Path.Combine(workingDirectory, ".agents", "skills");
        if (!Directory.Exists(skillRoot))
        {
            findings.Add("skills: local skill catalog not present; selected skills will be listed as missing in context artifacts");
            return;
        }

        var missing = selectedSkills.Where(skill => !skill.Available).ToArray();
        if (missing.Length == 0)
        {
            findings.Add($"ok: selected skill manifest available ({string.Join(", ", selectedSkills.Select(skill => skill.Name))})");
            return;
        }

        findings.Add(
            "blocked: missing required local skill(s): " +
            string.Join(", ", missing.Select(skill => $"{skill.Name} at {skill.RelativePath}")) +
            "; add the SKILL.md file(s) or adjust the task so the router no longer selects them");
    }

    private static void AddBuildEnvironmentFinding(List<string> findings, Goal goal, TaskSpec task)
    {
        if (task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester))
        {
            findings.Add("build environment: not required for read-only role");
            return;
        }

        var rootPath = DotnetBuildEnvironmentManager.GoalRoot(goal.Id);
        var artifactsPath = DotnetBuildEnvironmentManager.GoalArtifactsPath(goal.Id);
        var leaseMetadataPath = Path.Combine(rootPath, "lease", "lease.json");
        var leaseExists = File.Exists(leaseMetadataPath);
        findings.Add(
            $"build environment: goal lease {(leaseExists ? "exists" : "not yet created")} artifacts={artifactsPath}");
    }

    private static void AddWorktreeCleanlinessFinding(List<string> findings, TaskSpec task, string workingDirectory)
    {
        if (task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester))
        {
            findings.Add("worktree: clean check not required for read-only role");
            return;
        }

        var statusResult = GitCli.Run(workingDirectory, "status", "--porcelain");
        if (statusResult.ExitCode != 0)
        {
            findings.Add("worktree: cleanliness unavailable before dispatch; verify git status from the goal workspace if this is unexpected");
            return;
        }

        var filteredStatusOutput = GitCli.FilterCommitWorthyStatus(statusResult.Output);
        if (string.IsNullOrWhiteSpace(filteredStatusOutput))
        {
            findings.Add("ok: worktree clean before dispatch");
            return;
        }

        var changedLineCount = filteredStatusOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        findings.Add($"blocked: worktree has {changedLineCount} uncommitted change(s) before dispatch; commit, stash, or clean the goal workspace first");
    }

    private static void ThrowIfPreflightBlocked(WorkerSubscriptionPreflightResult preflight)
    {
        if (preflight.Allowed)
        {
            return;
        }

        var codePrefix = string.IsNullOrWhiteSpace(preflight.ErrorCode)
            ? string.Empty
            : $"{preflight.ErrorCode}: ";
        throw new WorkerSubscriptionPreflightException(
            "Subscription preflight failed: " + codePrefix + string.Join("; ", preflight.Findings),
            preflight.ErrorCode,
            preflight.Findings);
    }

    public static int EstimateSubscriptionPromptCharacters(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition> agents)
    {
        var agent = ResolveAssignedAgent(null, null, task, agents);
        var selection = ResolveEffectiveSubscriptionModelSelection(agent, goal, task);
        var modelName = ResolveEffectiveSubscriptionModelName(agent, selection);
        var brief = kernel.BuildTaskBrief(
            goal.Id,
            task.Id,
            BuildModelFitTarget(selection.Model.ProviderName, modelName));
        return WorkerPromptInputBudget.Apply(brief, selection.Model.ProviderName, modelName).Brief.Content.Length;
    }

    public static IReadOnlyList<WorkerProfileDispatchResult> PrepareSubscriptionReadyTasks(
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        string promptRoot,
        string workingDirectory,
        DateTimeOffset dispatchedAt,
        IReadOnlySet<TaskId>? taskIdsToPrepare = null,
        Func<string, bool>? commandExists = null)
    {
        return PrepareSubscriptionReadyBatch(
            kernel,
            goal,
            agents,
            profiles,
            promptRoot,
            workingDirectory,
            dispatchedAt,
            taskIdsToPrepare,
            commandExists).Dispatches;
    }

    public static WorkerProfileReadyBatchResult PrepareSubscriptionReadyBatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        string promptRoot,
        string workingDirectory,
        DateTimeOffset dispatchedAt,
        IReadOnlySet<TaskId>? taskIdsToPrepare = null,
        Func<string, bool>? commandExists = null)
    {
        var selections = goal.Tasks
            .Where(task => task.Status == WorkTaskStatus.Assigned)
            .Select(task => new
            {
                Task = task,
                Agent = ResolveAssignedAgent(kernel, goal, task, agents)
            })
            .ToList();

        // When the Low-IL sandbox is active, the worker runs at low integrity and physically cannot
        // write the medium-integrity .git (it lives outside the worktree). The .git-reference preflight
        // guard exists to stop a worker from targeting VCS internals — a risk the OS already eliminates
        // here — so it is redundant under the sandbox. Relaxing it lets the conductor autonomously
        // dispatch tasks whose briefs legitimately mention .git (e.g. repo-root resolution goals)
        // instead of dropping them from the ready batch and escalating a generic "no ready tasks".
        var sandboxConfinesWrites = WorkerSandboxOptions.FromEnvironment().Enabled;

        var results = new List<WorkerProfileDispatchResult>();
        var blocked = new List<ReadyBlockedDiagnostic>();
        foreach (var selection in selections)
        {
            var roleSelection = ResolveEffectiveSubscriptionModelSelection(selection.Agent, goal, selection.Task, profiles: profiles, commandExists: commandExists);
            roleSelection = ApplyReasoningEffortPolicy(selection.Agent, goal, selection.Task, roleSelection);
            var profile = ResolveSubscriptionProfile(selection.Agent, roleSelection.Model, profiles);
            var reasoningEffortSelection = new EffectiveReasoningEffortSelection(
                ResolveEffectiveSubscriptionReasoningEffort(selection.Agent, roleSelection),
                roleSelection.ReasoningEffortReason);
            var preflight = PreflightSubscriptionTask(
                goal, selection.Task, agents, profiles, workingDirectory, dispatchedAt,
                allowGitReference: sandboxConfinesWrites,
                commandExists: commandExists);
            if (!preflight.Allowed)
            {
                blocked.Add(BuildReadyBlockedDiagnostic(goal, selection.Task, preflight));
                continue;
            }

            if (taskIdsToPrepare is not null && !taskIdsToPrepare.Contains(selection.Task.Id))
            {
                continue;
            }

            var dispatchProviderName = ResolveDispatchProviderName(roleSelection.Model.ProviderName, profile.Name, null);

            results.Add(PrepareTask(
                kernel,
                goal,
                selection.Task,
                profile,
                promptRoot,
                workingDirectory,
                dispatchedAt,
                BuildSubscriptionTemplateVariables(selection.Agent, roleSelection),
                dispatchProviderName,
                ResolveEffectiveSubscriptionModelName(selection.Agent, roleSelection),
                reasoningEffortSelection.Effort,
                roleSelection.Complexity,
                roleSelection.UsesComplexModel,
                reasoningEffortSelection.Reason,
                preflight.Findings));
        }

        return new WorkerProfileReadyBatchResult(results, blocked);
    }

    public static ReadyBlockedDiagnostic BuildReadyBlockedDiagnostic(
        Goal goal,
        TaskSpec task,
        WorkerSubscriptionPreflightResult preflight)
    {
        return new ReadyBlockedDiagnostic(
            goal.Id.Value[..8],
            TaskDisplayNumber.Resolve(goal, task.Id),
            task.Id.Value,
            string.IsNullOrWhiteSpace(preflight.ProfileName) ? "unknown" : preflight.ProfileName,
            ResolveReadyBlockedReason(preflight.Findings),
            preflight.Findings
                .Where(finding => finding.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase))
                .ToArray());
    }

    private static string ResolveReadyBlockedReason(IReadOnlyList<string> findings)
    {
        var blockedFindings = findings
            .Where(finding => finding.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (blockedFindings.Any(finding => finding.Contains("uncommitted change", StringComparison.OrdinalIgnoreCase)))
            return "dirty-worktree";
        if (blockedFindings.Any(finding => finding.Contains("worker profile", StringComparison.OrdinalIgnoreCase)))
            return "worker-profile";
        if (blockedFindings.Any(finding => finding.Contains("capability", StringComparison.OrdinalIgnoreCase) ||
                finding.Contains(".git", StringComparison.OrdinalIgnoreCase)))
            return "start-gate";
        if (blockedFindings.Any(finding => finding.Contains("subscription retry", StringComparison.OrdinalIgnoreCase) ||
                finding.Contains("cooling down", StringComparison.OrdinalIgnoreCase) ||
                finding.Contains("subscription limits", StringComparison.OrdinalIgnoreCase)))
            return "subscription-preflight";
        if (blockedFindings.Any(finding => finding.Contains("missing required local skill", StringComparison.OrdinalIgnoreCase)))
            return "missing-skill";

        return "preflight-blocked";
    }

    public static bool IsTaskRetryDeferred(TaskSpec task, DateTimeOffset now, out DateTimeOffset retryAfter)
    {
        if (task.SubscriptionRetryAfter is { } taskRetryAfter &&
            taskRetryAfter > now)
        {
            retryAfter = taskRetryAfter;
            return true;
        }

        return DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, now, out retryAfter);
    }

    private static void EnsureWorktreeForFileRole(AgentRole role, string workingDirectory)
    {
        if (role != AgentRole.Developer && role != AgentRole.Tester)
        {
            return;
        }

        if (File.Exists(Path.Combine(workingDirectory, ".git")))
        {
            return;
        }

        throw new InvalidOperationException(
            $"A goal workspace is required to dispatch a {role} task; run 'workspace create' before subscription-dispatch.");
    }

    private static void EnsureTaskNeedsExecution(TaskSpec task, bool allowPendingRecordedDispatchRefresh = false)
    {
        if (task.LastVerification?.Succeeded is true)
        {
            throw new InvalidOperationException($"Task '{task.Id}' already has passing verification; retry the task before dispatching it again.");
        }

        if (allowPendingRecordedDispatchRefresh &&
            task.Status == WorkTaskStatus.Running &&
            task.LastDispatch is not null &&
            task.LastProcess is null)
        {
            return;
        }

        if (task.Status != WorkTaskStatus.Assigned)
        {
            throw new InvalidOperationException($"Task '{task.Id}' status is {task.Status}; retry or assign it before dispatching it again.");
        }
    }

    public static WorkerProfile ResolveSubscriptionProfile(TaskSpec task, IReadOnlyList<AgentDefinition> agents, WorkerProfileCatalog profiles)
    {
        var agent = ResolveAssignedAgent(null, null, task, agents);
        return ResolveSubscriptionProfile(agent, profiles);
    }

    public static WorkerProfile ResolveSubscriptionProfile(AgentDefinition agent, WorkerProfileCatalog profiles)
    {
        return ResolveSubscriptionProfile(agent, agent.Model, profiles);
    }

    private static WorkerProfile ResolveSubscriptionProfile(AgentDefinition agent, ModelProfile model, WorkerProfileCatalog profiles)
    {
        if (!AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy))
        {
            throw new InvalidOperationException($"Agent '{agent.Name}' is configured for API execution only.");
        }

        return profiles.GetRequired(ResolveSubscriptionProfileName(agent, model));
    }

    private static string ResolveDispatchProviderName(string selectedProviderName, string profileName, DispatchModelOverride? modelOverride)
    {
        var provider = DefaultProviders.ResolveProfile(profileName);
        return provider.Identity.Kind == ProviderKind.Unknown
            ? selectedProviderName
            : provider.ProviderName;
    }

    public static string ResolveSubscriptionProfileName(AgentDefinition agent)
    {
        return ResolveSubscriptionProfileName(agent, agent.Model);
    }

    public static string ResolveSubscriptionProfileName(AgentDefinition agent, Goal goal, TaskSpec task)
    {
        return ResolveSubscriptionProfileName(agent, ResolveEffectiveSubscriptionModelSelection(agent, goal, task).Model);
    }

    public static string ResolveSubscriptionProfileName(AgentDefinition agent, Goal goal, TaskSpec task, WorkerProfileCatalog profiles)
    {
        return ResolveSubscriptionProfileName(agent, ResolveEffectiveSubscriptionModelSelection(agent, goal, task, profiles: profiles).Model);
    }

    private static string ResolveSubscriptionProfileName(AgentDefinition agent, ModelProfile model)
    {
        if (model.ProviderName.Equals(agent.Model.ProviderName, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(agent.Subscription?.WorkerProfileName))
        {
            return agent.Subscription.WorkerProfileName;
        }

        return DefaultProviders.ResolveModelProvider(model.ProviderName).ProfileName;
    }

    private static void EnsureSubscriptionRetryWindowHasPassed(TaskSpec task, DateTimeOffset dispatchedAt)
    {
        if (!IsTaskRetryDeferred(task, dispatchedAt, out var retryAfter))
        {
            return;
        }

        throw new InvalidOperationException($"Task '{task.Id}' subscription retry deferred until {retryAfter:u}.");
    }

    private static void EnsureRepeatedSubscriptionLimitReviewed(TaskSpec task)
    {
        if (!DispatchFailureClassifier.RequiresSubscriptionLimitReview(task))
        {
            return;
        }

        var failures = DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task);
        throw new InvalidOperationException(
            $"Task '{task.Id}' hit a recoverable subscription usage limit {failures} time(s); inspect model, profile, or timing before redispatch.");
    }

    public static IReadOnlyDictionary<string, string?> BuildSubscriptionTemplateVariables(AgentDefinition agent)
    {
        return BuildSubscriptionTemplateVariables(agent, new SubscriptionModelSelection(TaskComplexity.Simple, agent.Model, UsesComplexModel: false));
    }

    public static IReadOnlyDictionary<string, string?> BuildSubscriptionTemplateVariables(
        AgentDefinition agent,
        Goal goal,
        TaskSpec task)
    {
        var selection = ResolveEffectiveSubscriptionModelSelection(agent, goal, task);
        return BuildSubscriptionTemplateVariables(agent, ApplyReasoningEffortPolicy(agent, goal, task, selection));
    }

    public static IReadOnlyDictionary<string, string?> BuildSubscriptionTemplateVariables(
        AgentDefinition agent,
        Goal goal,
        TaskSpec task,
        WorkerProfileCatalog profiles)
    {
        var selection = ResolveEffectiveSubscriptionModelSelection(agent, goal, task, profiles: profiles);
        return BuildSubscriptionTemplateVariables(agent, ApplyReasoningEffortPolicy(agent, goal, task, selection));
    }

    private static Dictionary<string, string?> BuildSubscriptionTemplateVariables(
        AgentDefinition agent,
        SubscriptionModelSelection selection)
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["providerName"] = selection.Model.ProviderName,
            ["apiModelName"] = selection.Model.ModelName,
            ["apiReasoningEffort"] = selection.Model.ReasoningEffort,
            ["subscriptionModelName"] = ResolveEffectiveSubscriptionModelName(agent, selection),
            ["subscriptionReasoningEffort"] = ResolveEffectiveSubscriptionReasoningEffort(agent, selection),
            ["reasoningEffortSelectionReason"] = selection.ReasoningEffortReason,
            ["taskComplexity"] = selection.Complexity.ToString(),
            ["modelSelectionReason"] = selection.Reason,
            ["executionPolicy"] = agent.ExecutionPolicy.ToString()
        };
    }

    private static string ResolveEffectiveSubscriptionModelName(AgentDefinition agent, SubscriptionModelSelection selection)
    {
        return selection.UsesComplexModel
            ? selection.Model.ModelName
            : !selection.UsesSubscriptionLaunchProfile
                ? selection.Model.ModelName
            : agent.Subscription?.ModelAlias ?? selection.Model.ModelName;
    }

    private static string? ResolveEffectiveSubscriptionReasoningEffort(AgentDefinition agent, SubscriptionModelSelection selection)
    {
        if (selection.ReasoningEffortOverride is not null)
        {
            return selection.ReasoningEffortOverride;
        }

        return selection.UsesComplexModel
            ? selection.Model.ReasoningEffort ?? agent.Subscription?.ReasoningEffort ?? agent.Model.ReasoningEffort
            : !selection.UsesSubscriptionLaunchProfile
                ? selection.Model.ReasoningEffort
            : agent.Subscription?.ReasoningEffort ?? selection.Model.ReasoningEffort;
    }

    private static EffectiveReasoningEffortSelection ResolveEffectiveSubscriptionReasoningEffortSelection(
        AgentDefinition agent,
        Goal goal,
        TaskSpec task,
        SubscriptionModelSelection selection)
    {
        if (!selection.UsesSubscriptionLaunchProfile)
        {
            return new EffectiveReasoningEffortSelection(ResolveEffectiveSubscriptionReasoningEffort(agent, selection), "base");
        }

        var baseEffort = selection.UsesComplexModel
            ? selection.Model.ReasoningEffort ?? agent.Subscription?.ReasoningEffort ?? agent.Model.ReasoningEffort
            : agent.Subscription?.ReasoningEffort ?? selection.Model.ReasoningEffort;
        var policy = agent.ReasoningEffortPolicy ?? new ReasoningEffortPolicy();

        if (HasClassFindingRetryFeedback(task))
        {
            return new EffectiveReasoningEffortSelection(policy.ClassFindingEffort ?? baseEffort, "class-finding");
        }

        if (policy.RetryDepthThreshold > 0 && task.CriterionRetryCount + 1 >= policy.RetryDepthThreshold)
        {
            return new EffectiveReasoningEffortSelection(policy.RetryDepthEffort ?? baseEffort, "retry-depth");
        }

        if (IsComplexOrHighRiskGoal(goal, agent.Role))
        {
            return new EffectiveReasoningEffortSelection(policy.ComplexityEffort ?? baseEffort, "complexity");
        }

        return new EffectiveReasoningEffortSelection(baseEffort, "base");
    }

    private static SubscriptionModelSelection ApplyReasoningEffortPolicy(
        AgentDefinition agent,
        Goal goal,
        TaskSpec task,
        SubscriptionModelSelection selection)
    {
        var effortSelection = ResolveEffectiveSubscriptionReasoningEffortSelection(agent, goal, task, selection);
        return selection with
        {
            ReasoningEffortOverride = effortSelection.Effort,
            ReasoningEffortReason = effortSelection.Reason
        };
    }

    private static bool IsComplexOrHighRiskGoal(Goal goal, AgentRole role)
    {
        if (TaskComplexityEstimator.Estimate(goal.Objective, goal.Objective, role) == TaskComplexity.Complex)
        {
            return true;
        }

        var objective = goal.Objective;
        return objective.Contains("high-risk", StringComparison.OrdinalIgnoreCase) ||
            objective.Contains("security-risk", StringComparison.OrdinalIgnoreCase) ||
            objective.Contains("state-and-worktree-mutation", StringComparison.OrdinalIgnoreCase) ||
            objective.Contains("subscription-cost", StringComparison.OrdinalIgnoreCase) ||
            objective.Contains("multi-scope", StringComparison.OrdinalIgnoreCase) ||
            objective.Contains("concurrency", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasClassFindingRetryFeedback(TaskSpec task)
    {
        return task.CriterionRetryFeedback.Any(feedback =>
            feedback.Contains("every call site", StringComparison.OrdinalIgnoreCase) ||
            feedback.Contains("all call sites", StringComparison.OrdinalIgnoreCase) ||
            feedback.Contains("all persist paths", StringComparison.OrdinalIgnoreCase) ||
            feedback.Contains("every persist path", StringComparison.OrdinalIgnoreCase) ||
            feedback.Contains("all paths", StringComparison.OrdinalIgnoreCase) ||
            feedback.Contains("every path", StringComparison.OrdinalIgnoreCase) ||
            feedback.Contains("all instances", StringComparison.OrdinalIgnoreCase) ||
            feedback.Contains("every instance", StringComparison.OrdinalIgnoreCase) ||
            feedback.Contains("all occurrences", StringComparison.OrdinalIgnoreCase) ||
            feedback.Contains("every occurrence", StringComparison.OrdinalIgnoreCase) ||
            feedback.Contains("siblings", StringComparison.OrdinalIgnoreCase));
    }

    private static string? BuildModelFitTarget(string? providerName, string? modelName)
    {
        return string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(modelName)
            ? null
            : $"{providerName.Trim()}/{modelName.Trim()}";
    }

    private static TargetContext? TryReadCurrentTargetContext(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var branch = GitCli.Run(workingDirectory, "branch", "--show-current");
        var head = GitCli.Run(workingDirectory, "rev-parse", "HEAD");
        if (branch.ExitCode != 0 || head.ExitCode != 0)
        {
            return null;
        }

        var branchName = branch.Output.Trim();
        var headCommit = head.Output.Trim();
        if (string.IsNullOrWhiteSpace(branchName) && string.IsNullOrWhiteSpace(headCommit))
        {
            return null;
        }

        return new TargetContext(
            string.IsNullOrWhiteSpace(branchName) ? null : branchName,
            string.IsNullOrWhiteSpace(headCommit) ? null : headCommit);
    }

    private static SubscriptionModelSelection ResolveSubscriptionModel(AgentDefinition agent, Goal goal, TaskSpec task)
    {
        var complexity = TaskComplexityEstimator.Estimate(task.Description, goal.Objective, agent.Role);
        var model = TaskComplexityEstimator.ResolveModel(
            agent,
            complexity,
            task.Description,
            goal.Objective,
            ModelFitEvidence.BuildSummary(goal.Tasks.SelectMany(ModelFitEvidence.FindNotes)));
        return new SubscriptionModelSelection(complexity, model, UsesComplexModel(agent, model));
    }

    private static SubscriptionModelSelection ResolveEffectiveSubscriptionModelSelection(
        AgentDefinition agent,
        Goal goal,
        TaskSpec task,
        DispatchModelOverride? modelOverride = null,
        WorkerProfileCatalog? profiles = null,
        Func<ClaudeCliAuthState>? claudeAuthProbe = null,
        WorkerSandboxOptions? sandboxOptions = null,
        Func<string, bool>? commandExists = null)
    {
        var fullSelection = ResolveSubscriptionModel(agent, goal, task);
        return modelOverride is not null
            ? fullSelection with { Reason = "override: explicit dispatch profile/model selection" }
            : ResolveRoleModelSelection(agent, task, fullSelection, profiles, claudeAuthProbe, sandboxOptions, commandExists);
    }

    private static SubscriptionModelSelection ResolveRoleModelSelection(
        AgentDefinition agent,
        TaskSpec task,
        SubscriptionModelSelection fullSelection,
        WorkerProfileCatalog? profiles,
        Func<ClaudeCliAuthState>? claudeAuthProbe,
        WorkerSandboxOptions? sandboxOptions,
        Func<string, bool>? commandExists)
    {
        if (!IsLightReadOnlyRole(task.RequiredRole))
        {
            return fullSelection with { Reason = "full-profile: role is write-capable or gate-heavy" };
        }

        if (HasCustomWorkerProfileOverride(agent))
        {
            return fullSelection with { Reason = "full-profile: role has custom subscription worker profile" };
        }

        if (TryFindRoleGuardrailFailure(task, out var guardrailFailure))
        {
            return fullSelection with { Reason = $"fallback-full-profile: {guardrailFailure}" };
        }

        if (profiles is not null &&
            !TryValidateLightRoleProfile(profiles, claudeAuthProbe, sandboxOptions, commandExists, out var unavailableReason))
        {
            return fullSelection with { Reason = $"full-profile: light-role profile unavailable ({unavailableReason})" };
        }

        return new SubscriptionModelSelection(
            fullSelection.Complexity,
            new ModelProfile(
                "Anthropic",
                LightRoleAnthropicModelName,
                agent.Model.Capabilities,
                SubscriptionMode.ApiKey,
                MaxOutputTokens: agent.Model.MaxOutputTokens),
            UsesComplexModel: false,
            UsesSubscriptionLaunchProfile: false,
            Reason: $"light-role: {task.RequiredRole} uses {AnthropicSubscriptionProfileName}/{LightRoleAnthropicModelName}");
    }

    private static bool TryValidateLightRoleProfile(
        WorkerProfileCatalog profiles,
        Func<ClaudeCliAuthState>? claudeAuthProbe,
        WorkerSandboxOptions? sandboxOptions,
        Func<string, bool>? commandExists,
        out string unavailableReason)
    {
        var profile = profiles.Profiles.FirstOrDefault(profile =>
            profile.Name.Equals(AnthropicSubscriptionProfileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            unavailableReason = $"{AnthropicSubscriptionProfileName} not configured";
            return false;
        }

        if (WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate))
        {
            unavailableReason = $"{AnthropicSubscriptionProfileName} is echo-only";
            return false;
        }

        if (!WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate))
        {
            unavailableReason = $"{AnthropicSubscriptionProfileName} does not pin selected model";
            return false;
        }

        var launcher = WorkerProfileDiagnostics.EvaluateRealLauncher(
            profile,
            DefaultProviders.ResolveProfile(AnthropicSubscriptionProfileName),
            commandExists);
        if (!launcher.IsRealLauncher)
        {
            unavailableReason = launcher.Detail;
            return false;
        }

        var sandbox = sandboxOptions ?? WorkerSandboxOptions.FromEnvironment();
        if (sandbox.Enabled)
        {
            var authState = (claudeAuthProbe ?? ClaudeCliAuthProbe.FromEnvironment)();
            if (!authState.HasAnthropicApiKey && authState.HasCliCredentialArtifact)
            {
                unavailableReason = "Claude CLI Low-IL auth unavailable";
                return false;
            }
        }

        unavailableReason = string.Empty;
        return true;
    }

    private static bool IsLightReadOnlyRole(AgentRole role)
    {
        return role is AgentRole.Planner or AgentRole.Researcher or AgentRole.Reviewer;
    }

    private static bool HasCustomWorkerProfileOverride(AgentDefinition agent)
    {
        if (agent.Subscription?.WorkerProfileName is not { Length: > 0 } profileName)
        {
            return false;
        }

        try
        {
            var defaultProfileName = DefaultProviders.ResolveModelProvider(agent.Model.ProviderName).ProfileName;
            return !profileName.Equals(defaultProfileName, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool TryFindRoleGuardrailFailure(TaskSpec task, out string reason)
    {
        reason = string.Empty;
        if (task.LastVerification is not { } verification)
        {
            return false;
        }

        var text = $"{verification.StandardOutput}\n{verification.StandardError}";
        if (!WorkerResultParser.TryParseFields(text, out var fields, out var diagnostic))
        {
            reason = $"prior {task.RequiredRole} WORKER_RESULT invalid ({diagnostic})";
            return true;
        }

        var missingFields = WorkerResultRequiredFieldsForLightRole(task.RequiredRole)
            .Where(field => !fields.ContainsKey(field))
            .ToArray();
        if (missingFields.Length > 0)
        {
            reason = $"prior {task.RequiredRole} WORKER_RESULT missing field(s): {string.Join(", ", missingFields)}";
            return true;
        }

        if (task.RequiredRole == AgentRole.Researcher && !HasSubstantiveField(fields, "citations"))
        {
            reason = "prior Researcher WORKER_RESULT missing citations";
            return true;
        }

        if (task.RequiredRole == AgentRole.Reviewer && !HasSubstantiveField(fields, "verdict"))
        {
            reason = "prior Reviewer WORKER_RESULT missing verdict";
            return true;
        }

        if (task.RequiredRole == AgentRole.Reviewer && !HasPresentField(fields, "blockers"))
        {
            reason = "prior Reviewer WORKER_RESULT missing blockers";
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> WorkerResultRequiredFieldsForLightRole(AgentRole role)
    {
        return AgentOutputDirectives.RequiredWorkerResultFieldNamesForRole(role);
    }

    private static bool HasPresentField(IReadOnlyDictionary<string, string> fields, string fieldName)
    {
        return fields.TryGetValue(fieldName, out var value) &&
            !string.IsNullOrWhiteSpace(value) &&
            !value.StartsWith('<') &&
            !value.EndsWith('>');
    }

    private static bool HasSubstantiveField(IReadOnlyDictionary<string, string> fields, string fieldName)
    {
        return fields.TryGetValue(fieldName, out var value) &&
            !string.IsNullOrWhiteSpace(value) &&
            !value.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith('<') &&
            !value.EndsWith('>');
    }

    private static bool UsesComplexModel(AgentDefinition agent, ModelProfile model)
    {
        return agent.ComplexModel is not null &&
            agent.ComplexModel.ProviderName.Equals(model.ProviderName, StringComparison.OrdinalIgnoreCase) &&
            agent.ComplexModel.ModelName.Equals(model.ModelName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SubscriptionModelSelection(
        TaskComplexity Complexity,
        ModelProfile Model,
        bool UsesComplexModel,
        bool UsesSubscriptionLaunchProfile = true,
        string Reason = "full-profile: default subscription model selection",
        string? ReasoningEffortOverride = null,
        string ReasoningEffortReason = "base");

    private sealed record EffectiveReasoningEffortSelection(string? Effort, string Reason);

    private sealed record TargetContext(string? BranchName, string? HeadCommit);

    private static AgentDefinition ResolveAssignedAgent(AgentOrchestratorKernel? kernel, Goal? goal, TaskSpec task, IReadOnlyList<AgentDefinition> agents)
    {
        if (task.AssignedAgentId is null)
        {
            throw new InvalidOperationException($"Task '{task.Id}' is not assigned to an agent.");
        }

        var assignedAgent = agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId);
        if (assignedAgent is not null)
        {
            return assignedAgent;
        }

        var replacement = agents.FirstOrDefault(agent => agent.Role == task.RequiredRole)
            ?? throw new KeyNotFoundException($"Assigned agent '{task.AssignedAgentId}' was not found and no current {task.RequiredRole} agent is registered.");
        var staleAgentId = task.AssignedAgentId.Value;
        var warning = $"Warning: assigned agent '{staleAgentId}' for {task.RequiredRole} task '{task.Id}' was not found; using current role agent '{replacement.Id.Value}'.";
        Console.Error.WriteLine(warning);
        if (kernel is not null && goal is not null)
        {
            kernel.ReassignTaskAgent(
                goal.Id,
                task.Id,
                replacement,
                $"Warning: repaired stale {task.RequiredRole} assignment from missing agent '{staleAgentId}' to '{replacement.Id.Value}'.");
        }

        return replacement;
    }

    private static void EnsureRealSubscriptionProfile(WorkerProfile profile)
    {
        if (!WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Subscription worker profile '{profile.Name}' only echoes the prompt path; configure a real launcher before dispatch.");
    }

    private static void EnsureSubscriptionProfileCanExecuteTask(WorkerProfile profile, TaskSpec task)
    {
        EnsureRealSubscriptionProfile(profile);

        if (task.RequiredRole != AgentRole.Developer)
        {
            return;
        }

        var capability = WorkerProfileDiagnostics.EvaluatePatchCapability(
            profile,
            DefaultProviders.ResolveProfile(profile.Name));
        if (capability.IsPatchCapable)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Subscription worker profile '{profile.Name}' is not patch-capable for Developer tasks: {capability.Detail}");
    }

    private static void EnsureSubscriptionProfilePinsSelectedModel(WorkerProfile profile)
    {
        if (WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Subscription worker profile '{profile.Name}' does not include {{subscriptionModelName}}; pin the selected model before subscription dispatch.");
    }

    private static void EnsureSubscriptionProfilePinsSelectedReasoning(WorkerProfile profile, string providerName, string? reasoningEffort)
    {
        if (!RequiresSubscriptionReasoningPlaceholder(providerName, reasoningEffort) ||
            WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(profile.CommandTemplate))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Subscription worker profile '{profile.Name}' does not include {{subscriptionReasoningEffort}}; pin the selected reasoning effort before subscription dispatch.");
    }

    private static bool RequiresSubscriptionReasoningPlaceholder(string providerName, string? reasoningEffort)
    {
        if (!DefaultProviders.TryResolveModelProvider(providerName, out var provider))
        {
            return false;
        }

        return provider.Identity.Kind is ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexSpark or ProviderKind.OpenAICodexOssCli &&
            !string.IsNullOrWhiteSpace(reasoningEffort);
    }

    public static Dictionary<string, string?> BuildDispatchVariables(
        AgentRole role,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? variables)
    {
        var isWriteCapable = role == AgentRole.Developer || role == AgentRole.Tester;
        // When the OS worker sandbox is active, codex's own sandbox is set to danger-full-access so it
        // uses ordinary CreateProcess (no CreateProcessAsUserW poisoning); the OS account+ACL enforces
        // confinement instead. Otherwise keep codex's enforced workspace-write sandbox.
        var osSandbox = WorkerSandboxOptions.FromEnvironment().Enabled;
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["workingDirectory"] = workingDirectory,
            ["sandboxMode"] = isWriteCapable
                ? (osSandbox ? "danger-full-access" : "workspace-write")
                : "read-only",
            ["permissionMode"] = isWriteCapable ? "bypassPermissions" : "plan"
        };

        if (variables is not null)
        {
            foreach (var (key, value) in variables)
            {
                merged[key] = value;
            }
        }

        return merged;
    }
}
