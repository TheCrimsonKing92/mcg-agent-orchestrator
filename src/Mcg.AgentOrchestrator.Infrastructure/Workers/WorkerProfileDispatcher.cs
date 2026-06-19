using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerProfileDispatchResult(TaskSpec Task, string PromptPath);

public sealed record WorkerSubscriptionPreflightResult(
    bool Allowed,
    string ProfileName,
    string CapabilityStatus,
    IReadOnlyList<string> Findings);

public sealed record DispatchModelOverride(string? ProfileName, string? ModelName, string? ReasoningEffort);

public static class WorkerProfileDispatcher
{
    public const string OpenAiSubscriptionProfileName = "codex-cli";
    public const string AnthropicSubscriptionProfileName = "claude-cli";
    public const string OllamaSubscriptionProfileName = "qwen-code-cli";

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
        IReadOnlyList<string>? preflightFindings = null)
    {
        EnsureTaskNeedsExecution(task);

        WorkerCommandTemplate.WriteHandoffFile(goal.Tasks, task.Id, workingDirectory);
        var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory, preflightFindings);
        var brief = kernel.BuildTaskBrief(
            goal.Id,
            task.Id,
            BuildModelFitTarget(providerName, modelName),
            workingDirectory,
            contextDirectory);
        var preparation = WorkerCommandTemplate.Prepare(
            brief,
            profile.Name,
            profile.CommandTemplate,
            promptRoot,
            BuildDispatchVariables(task.RequiredRole, workingDirectory, variables));
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
            usesComplexModel));
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
        bool allowGitReference = false)
    {
        EnsureTaskNeedsExecution(task);

        var agent = ResolveAssignedAgent(task, agents);
        var selection = ResolveSubscriptionModel(agent, goal, task);
        var profile = modelOverride?.ProfileName is { Length: > 0 } overrideProfile
            ? profiles.GetRequired(overrideProfile)
            : ResolveSubscriptionProfile(agent, selection.Model, profiles);
        var resolvedModelName = modelOverride?.ModelName is { Length: > 0 } overrideModel
            ? overrideModel
            : ResolveEffectiveSubscriptionModelName(agent, selection);
        var resolvedReasoning = modelOverride?.ReasoningEffort is not null
            ? modelOverride.ReasoningEffort
            : ResolveEffectiveSubscriptionReasoningEffort(agent, selection);
        var variables = BuildSubscriptionTemplateVariables(agent, selection);
        if (modelOverride?.ModelName is { Length: > 0 })
            variables["subscriptionModelName"] = resolvedModelName;
        if (modelOverride?.ReasoningEffort is not null)
            variables["subscriptionReasoningEffort"] = resolvedReasoning;
        var preflight = PreflightSubscriptionTask(goal, task, agents, profiles, workingDirectory, dispatchedAt, modelOverride, allowGitReference);
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
            selection.Model.ProviderName,
            resolvedModelName,
            resolvedReasoning,
            selection.Complexity,
            selection.UsesComplexModel,
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
        bool allowGitReference = false)
    {
        var findings = new List<string>();
        string profileName;
        try
        {
            EnsureTaskNeedsExecution(task);
            var agent = ResolveAssignedAgent(task, agents);
            var selection = ResolveSubscriptionModel(agent, goal, task);
            profileName = modelOverride?.ProfileName is { Length: > 0 } overrideProfile
                ? overrideProfile
                : ResolveSubscriptionProfileName(agent, selection.Model);
            var profile = profiles.GetRequired(profileName);
            findings.Add($"profile: {profile.Name}");
            var effectiveModelName = modelOverride?.ModelName is { Length: > 0 } overrideModel
                ? overrideModel
                : ResolveEffectiveSubscriptionModelName(agent, selection);
            findings.Add($"model: {selection.Model.ProviderName}/{effectiveModelName}");
            findings.Add($"complexity: {selection.Complexity}");

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
                : ResolveEffectiveSubscriptionReasoningEffort(agent, selection);
            AddProfileFinding(
                findings,
                RequiresSubscriptionReasoningPlaceholder(selection.Model.ProviderName, reasoningEffort) &&
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

            if (DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, now, out var retryAfter))
            {
                findings.Add($"blocked: subscription retry deferred until {retryAfter:u}");
            }

            if (DispatchFailureClassifier.TryGetProviderSubscriptionCooldown(
                goal,
                task.Id,
                selection.Model.ProviderName,
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

            return new WorkerSubscriptionPreflightResult(!blocked, profileName, capability.Status, findings);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            profileName = "unknown";
            findings.Add($"blocked: {ex.Message}");
            return new WorkerSubscriptionPreflightResult(false, profileName, "blocked", findings);
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

        if (string.IsNullOrWhiteSpace(statusResult.Output))
        {
            findings.Add("ok: worktree clean before dispatch");
            return;
        }

        var changedLineCount = statusResult.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        findings.Add($"blocked: worktree has {changedLineCount} uncommitted change(s) before dispatch; commit, stash, or clean the goal workspace first");
    }

    private static void ThrowIfPreflightBlocked(WorkerSubscriptionPreflightResult preflight)
    {
        if (preflight.Allowed)
        {
            return;
        }

        throw new InvalidOperationException("Subscription preflight failed: " + string.Join("; ", preflight.Findings));
    }

    public static int EstimateSubscriptionPromptCharacters(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition> agents)
    {
        var agent = ResolveAssignedAgent(task, agents);
        var selection = ResolveSubscriptionModel(agent, goal, task);
        return kernel
            .BuildTaskBrief(
                goal.Id,
                task.Id,
                BuildModelFitTarget(selection.Model.ProviderName, ResolveEffectiveSubscriptionModelName(agent, selection)))
            .Content
            .Length;
    }

    public static IReadOnlyList<WorkerProfileDispatchResult> PrepareSubscriptionReadyTasks(
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        string promptRoot,
        string workingDirectory,
        DateTimeOffset dispatchedAt,
        IReadOnlySet<TaskId>? taskIdsToPrepare = null)
    {
        var selections = goal.Tasks
            .Where(task => task.Status == WorkTaskStatus.Assigned &&
                (taskIdsToPrepare is null || taskIdsToPrepare.Contains(task.Id)))
            .Select(task => new
            {
                Task = task,
                Agent = ResolveAssignedAgent(task, agents)
            })
            .ToList();

        var results = new List<WorkerProfileDispatchResult>();
        foreach (var selection in selections)
        {
            var subscriptionModel = ResolveSubscriptionModel(selection.Agent, goal, selection.Task);
            var profile = ResolveSubscriptionProfile(selection.Agent, subscriptionModel.Model, profiles);
            var reasoningEffort = ResolveEffectiveSubscriptionReasoningEffort(selection.Agent, subscriptionModel);
            var preflight = PreflightSubscriptionTask(goal, selection.Task, agents, profiles, workingDirectory, dispatchedAt);
            if (!preflight.Allowed)
            {
                continue;
            }

            results.Add(PrepareTask(
                kernel,
                goal,
                selection.Task,
                profile,
                promptRoot,
                workingDirectory,
                dispatchedAt,
                BuildSubscriptionTemplateVariables(selection.Agent, subscriptionModel),
                subscriptionModel.Model.ProviderName,
                ResolveEffectiveSubscriptionModelName(selection.Agent, subscriptionModel),
                reasoningEffort,
                subscriptionModel.Complexity,
                subscriptionModel.UsesComplexModel,
                preflight.Findings));
        }

        return results;
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

    private static void EnsureTaskNeedsExecution(TaskSpec task)
    {
        if (task.LastVerification?.Succeeded is true)
        {
            throw new InvalidOperationException($"Task '{task.Id}' already has passing verification; retry the task before dispatching it again.");
        }

        if (task.Status != WorkTaskStatus.Assigned)
        {
            throw new InvalidOperationException($"Task '{task.Id}' status is {task.Status}; retry or assign it before dispatching it again.");
        }
    }

    public static WorkerProfile ResolveSubscriptionProfile(TaskSpec task, IReadOnlyList<AgentDefinition> agents, WorkerProfileCatalog profiles)
    {
        var agent = ResolveAssignedAgent(task, agents);
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

    public static string ResolveSubscriptionProfileName(AgentDefinition agent)
    {
        return ResolveSubscriptionProfileName(agent, agent.Model);
    }

    public static string ResolveSubscriptionProfileName(AgentDefinition agent, Goal goal, TaskSpec task)
    {
        return ResolveSubscriptionProfileName(agent, ResolveSubscriptionModel(agent, goal, task).Model);
    }

    private static string ResolveSubscriptionProfileName(AgentDefinition agent, ModelProfile model)
    {
        if (!string.IsNullOrWhiteSpace(agent.Subscription?.WorkerProfileName))
        {
            return agent.Subscription.WorkerProfileName;
        }

        if (model.ProviderName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return OpenAiSubscriptionProfileName;
        }

        if (model.ProviderName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return AnthropicSubscriptionProfileName;
        }

        if (model.ProviderName.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return OllamaSubscriptionProfileName;
        }

        throw new InvalidOperationException($"Provider '{model.ProviderName}' does not have a default subscription worker profile.");
    }

    private static void EnsureSubscriptionRetryWindowHasPassed(TaskSpec task, DateTimeOffset dispatchedAt)
    {
        if (!DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, dispatchedAt, out var retryAfter))
        {
            return;
        }

        throw new InvalidOperationException($"Task '{task.Id}' hit a recoverable subscription usage limit; retry after {retryAfter:u}.");
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
        return BuildSubscriptionTemplateVariables(agent, ResolveSubscriptionModel(agent, goal, task));
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
            ["taskComplexity"] = selection.Complexity.ToString(),
            ["executionPolicy"] = agent.ExecutionPolicy.ToString()
        };
    }

    private static string ResolveEffectiveSubscriptionModelName(AgentDefinition agent, SubscriptionModelSelection selection)
    {
        return selection.UsesComplexModel
            ? selection.Model.ModelName
            : agent.Subscription?.ModelAlias ?? selection.Model.ModelName;
    }

    private static string? ResolveEffectiveSubscriptionReasoningEffort(AgentDefinition agent, SubscriptionModelSelection selection)
    {
        return selection.UsesComplexModel
            ? selection.Model.ReasoningEffort ?? agent.Subscription?.ReasoningEffort ?? agent.Model.ReasoningEffort
            : agent.Subscription?.ReasoningEffort ?? selection.Model.ReasoningEffort;
    }

    private static string? BuildModelFitTarget(string? providerName, string? modelName)
    {
        return string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(modelName)
            ? null
            : $"{providerName.Trim()}/{modelName.Trim()}";
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

    private static bool UsesComplexModel(AgentDefinition agent, ModelProfile model)
    {
        return agent.ComplexModel is not null &&
            agent.ComplexModel.ProviderName.Equals(model.ProviderName, StringComparison.OrdinalIgnoreCase) &&
            agent.ComplexModel.ModelName.Equals(model.ModelName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SubscriptionModelSelection(TaskComplexity Complexity, ModelProfile Model, bool UsesComplexModel);

    private static AgentDefinition ResolveAssignedAgent(TaskSpec task, IReadOnlyList<AgentDefinition> agents)
    {
        if (task.AssignedAgentId is null)
        {
            throw new InvalidOperationException($"Task '{task.Id}' is not assigned to an agent.");
        }

        return agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId)
            ?? throw new KeyNotFoundException($"Assigned agent '{task.AssignedAgentId}' was not found.");
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

        var capability = WorkerProfileDiagnostics.EvaluatePatchCapability(profile.CommandTemplate);
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
        return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(reasoningEffort);
    }

    public static Dictionary<string, string?> BuildDispatchVariables(
        AgentRole role,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? variables)
    {
        var isWriteCapable = role == AgentRole.Developer || role == AgentRole.Tester;
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["workingDirectory"] = workingDirectory,
            ["sandboxMode"] = isWriteCapable ? "workspace-write" : "read-only",
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
