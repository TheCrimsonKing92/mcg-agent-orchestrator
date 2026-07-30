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
    string? ErrorCode = null,
    IReadOnlyList<string>? ReviewerScopeChangedFiles = null,
    string? ReviewerScopeMergeBase = null,
    int? ReviewerScopeTotalChangedFileCount = null,
    bool? ReviewerMergeTreeClean = null,
    IReadOnlyList<string>? ReviewerMergeTreeConflictPaths = null,
    int? ReviewerMergeTreeTotalConflictPathCount = null);

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
    public const string OpenAiSparkSubscriptionProfileName = "codex-spark";
    public const string OpenAiSparkSubscriptionModelName = "gpt-5.3-codex-spark";
    public const string AnthropicSubscriptionProfileName = "claude-cli";
    public const string LightRoleAnthropicModelName = "claude-haiku-4-5";
    public const string OllamaSubscriptionProfileName = "qwen-code-cli";
    public const string ReviewerScopeUnavailableErrorCode = WorkerGitContext.ReviewerScopeUnavailableErrorCode;
    public const string ReviewerMergeBaseUnavailableErrorCode = WorkerGitContext.ReviewerMergeBaseUnavailableErrorCode;
    public const string ReviewerMergeTreeUnavailableErrorCode = WorkerGitContext.ReviewerMergeTreeUnavailableErrorCode;
    private const string HighRiskReviewerReasoningEffort = "xhigh";
    private const string IntakeRiskLabelsMarker = "risk labels:";
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
        bool allowPendingRecordedDispatchRefresh = false,
        IReadOnlyList<string>? reviewerScopeChangedFiles = null,
        string? reviewerScopeMergeBase = null,
        int? reviewerScopeTotalChangedFileCount = null,
        bool? reviewerMergeTreeClean = null,
        IReadOnlyList<string>? reviewerMergeTreeConflictPaths = null,
        int? reviewerMergeTreeTotalConflictPathCount = null,
        string? dispatchLane = null,
        string? modelSelectionReason = null)
    {
        EnsureTaskNeedsExecution(task, allowPendingRecordedDispatchRefresh);
        EnsureSubscriptionRetryWindowHasPassed(task, dispatchedAt);
        EnsureReviewerScopeForPreparation(
            task,
            workingDirectory,
            ref preflightFindings,
            ref reviewerScopeChangedFiles,
            ref reviewerScopeMergeBase,
            ref reviewerScopeTotalChangedFileCount,
            ref reviewerMergeTreeClean,
            ref reviewerMergeTreeConflictPaths,
            ref reviewerMergeTreeTotalConflictPathCount);

        WorkerCommandTemplate.WriteHandoffFile(goal.Tasks, task.Id, workingDirectory);
        var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory, preflightFindings);
        var targetContext = TryReadCurrentTargetContext(workingDirectory);
        var reviewerRoundTouchedAnchors = ReadReviewerRoundTouchedAnchors(
            kernel,
            goal,
            task,
            workingDirectory,
            targetContext?.HeadCommit);
        var brief = kernel.BuildTaskBrief(
            goal.Id,
            task.Id,
            BuildModelFitTarget(providerName, modelName),
            workingDirectory,
            contextDirectory,
            targetContext?.BranchName,
            targetContext?.HeadCommit,
            reviewerScopeChangedFiles,
            reviewerScopeMergeBase,
            reviewerScopeTotalChangedFileCount,
            reviewerMergeTreeClean,
            reviewerMergeTreeConflictPaths,
            reviewerMergeTreeTotalConflictPathCount,
            reviewerRoundTouchedAnchors);
        var budgetedBrief = WorkerPromptInputBudget.Apply(brief, providerName, modelName).Brief;
        var dispatchVariables = BuildDispatchVariables(task.RequiredRole, workingDirectory, variables);
        var workerProviderKind = DefaultProviders.ResolveProfile(profile.Name).Identity.Kind;
        var commandTemplate = BuildDispatchCommandTemplate(profile, workerProviderKind, dispatchVariables);
        var preparation = WorkerCommandTemplate.Prepare(
            budgetedBrief,
            profile.Name,
            commandTemplate,
            promptRoot,
            dispatchVariables,
            dispatchedAt);
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
            ReasoningEffortReason: reasoningEffortReason,
            DispatchLane: dispatchLane ?? profile.Name,
            ModelSelectionReason: modelSelectionReason,
            ReviewFindingTouchedAnchors: reviewerRoundTouchedAnchors),
            allowPendingRecordedDispatchRefresh);
        return new WorkerProfileDispatchResult(task, preparation.PromptPath);
    }

    private static IReadOnlyList<ReviewFindingLocation> ReadReviewerRoundTouchedAnchors(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        string? currentHeadCommit)
    {
        if (task.RequiredRole != AgentRole.Reviewer)
        {
            return [];
        }

        var resolvedAnchors = kernel.GetReviewFindingState(goal.Id)
            .Where(finding => finding.State == ReviewFindingState.Resolved)
            .Select(finding => finding.Location)
            .ToArray();
        var previousReviewedCommit = task.VerificationHistory
            .OrderByDescending(verification => verification.CompletedAt)
            .Select(verification => verification.ReviewedCommit)
            .FirstOrDefault(commit => !string.IsNullOrWhiteSpace(commit));
        return new WorkerGitContext().ReadReviewerRoundTouchedAnchors(
            workingDirectory,
            previousReviewedCommit,
            currentHeadCommit,
            resolvedAnchors);
    }

    private static void EnsureReviewerScopeForPreparation(
        TaskSpec task,
        string workingDirectory,
        ref IReadOnlyList<string>? preflightFindings,
        ref IReadOnlyList<string>? reviewerScopeChangedFiles,
        ref string? reviewerScopeMergeBase,
        ref int? reviewerScopeTotalChangedFileCount,
        ref bool? reviewerMergeTreeClean,
        ref IReadOnlyList<string>? reviewerMergeTreeConflictPaths,
        ref int? reviewerMergeTreeTotalConflictPathCount)
    {
        if (task.RequiredRole != AgentRole.Reviewer)
        {
            return;
        }

        if (reviewerScopeChangedFiles is not null &&
            !string.IsNullOrWhiteSpace(reviewerScopeMergeBase) &&
            reviewerScopeTotalChangedFileCount.HasValue &&
            reviewerMergeTreeClean.HasValue &&
            reviewerMergeTreeConflictPaths is not null &&
            reviewerMergeTreeTotalConflictPathCount.HasValue)
        {
            return;
        }

        var findings = preflightFindings is null
            ? []
            : preflightFindings.ToList();
        var hasScope = reviewerScopeChangedFiles is not null &&
            !string.IsNullOrWhiteSpace(reviewerScopeMergeBase) &&
            reviewerScopeTotalChangedFileCount.HasValue;
        if (!hasScope)
        {
            var scope = AddReviewerChangedFileScopeFindings(findings, task, workingDirectory);
            if (scope is null)
            {
                var errorCode = ResolvePreflightErrorCode(findings) ?? ReviewerScopeUnavailableErrorCode;
                var codePrefix = string.IsNullOrWhiteSpace(errorCode) ? string.Empty : $"{errorCode}: ";
                throw new WorkerSubscriptionPreflightException(
                    "Reviewer changed-file scope preflight failed: " + codePrefix + string.Join("; ", findings),
                    errorCode,
                    findings);
            }

            reviewerScopeChangedFiles = scope.ChangedFiles;
            reviewerScopeMergeBase = scope.MergeBase;
            reviewerScopeTotalChangedFileCount = scope.TotalChangedFileCount;
        }

        var hasMergeTree = reviewerMergeTreeClean.HasValue &&
            reviewerMergeTreeConflictPaths is not null &&
            reviewerMergeTreeTotalConflictPathCount.HasValue;
        if (hasMergeTree)
        {
            preflightFindings = findings;
            return;
        }

        var mergeTree = AddReviewerMergeTreeStatusFindings(findings, task, workingDirectory);
        if (mergeTree is null)
        {
            var errorCode = ResolvePreflightErrorCode(findings) ?? ReviewerMergeTreeUnavailableErrorCode;
            var codePrefix = string.IsNullOrWhiteSpace(errorCode) ? string.Empty : $"{errorCode}: ";
            throw new WorkerSubscriptionPreflightException(
                "Reviewer merge-tree status preflight failed: " + codePrefix + string.Join("; ", findings),
                errorCode,
                findings);
        }

        preflightFindings = findings;
        reviewerMergeTreeClean = mergeTree.IsClean;
        reviewerMergeTreeConflictPaths = mergeTree.ConflictPaths;
        reviewerMergeTreeTotalConflictPathCount = mergeTree.TotalConflictPathCount;
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
            : ResolveSubscriptionProfile(agent, roleSelection, profiles);
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
            preflight.Findings,
            reviewerScopeChangedFiles: preflight.ReviewerScopeChangedFiles,
            reviewerScopeMergeBase: preflight.ReviewerScopeMergeBase,
            reviewerScopeTotalChangedFileCount: preflight.ReviewerScopeTotalChangedFileCount,
            reviewerMergeTreeClean: preflight.ReviewerMergeTreeClean,
            reviewerMergeTreeConflictPaths: preflight.ReviewerMergeTreeConflictPaths,
            reviewerMergeTreeTotalConflictPathCount: preflight.ReviewerMergeTreeTotalConflictPathCount,
            dispatchLane: roleSelection.DispatchLane ?? profile.Name,
            modelSelectionReason: roleSelection.Reason);
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
        ReviewerChangedFileScope? reviewerScope = null;
        ReviewerMergeTreeStatus? reviewerMergeTree = null;
        string profileName;
        try
        {
            EnsureTaskNeedsExecution(task);
            var agent = ResolveAssignedAgent(null, goal, task, agents);
            var roleSelection = ResolveEffectiveSubscriptionModelSelection(agent, goal, task, modelOverride, profiles, claudeAuthProbe, sandboxOptions, commandExists);
            roleSelection = ApplyReasoningEffortPolicy(agent, goal, task, roleSelection);
            profileName = modelOverride?.ProfileName is { Length: > 0 } overrideProfile
                ? overrideProfile
                : ResolveSubscriptionProfileName(agent, roleSelection);
            var profile = profiles.GetRequired(profileName);
            findings.Add($"profile: {profile.Name}");
            findings.Add($"dispatch-lane: {roleSelection.DispatchLane ?? profile.Name}");
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
            reviewerScope = AddReviewerChangedFileScopeFindings(findings, task, workingDirectory);
            reviewerMergeTree = AddReviewerMergeTreeStatusFindings(findings, task, workingDirectory);

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

            return new WorkerSubscriptionPreflightResult(
                !blocked,
                profileName,
                capability.Status,
                findings,
                ResolvePreflightErrorCode(findings),
                reviewerScope?.ChangedFiles,
                reviewerScope?.MergeBase,
                reviewerScope?.TotalChangedFileCount,
                reviewerMergeTree?.IsClean,
                reviewerMergeTree?.ConflictPaths,
                reviewerMergeTree?.TotalConflictPathCount);
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
            $"ok: Claude CLI Low-IL auth preflight will seed CLI credentials from {artifact} into the sandbox CLAUDE_CONFIG_DIR (subscription auth, proven at Low IL by live probe 2026-07-24)");
    }

    private static string? ResolvePreflightErrorCode(IReadOnlyList<string> findings)
    {
        if (findings.Any(finding => finding.Contains(ClaudeCliAuthProbe.AuthUnavailableErrorCode, StringComparison.Ordinal)))
        {
            return ClaudeCliAuthProbe.AuthUnavailableErrorCode;
        }

        if (findings.Any(finding => finding.Contains(ReviewerMergeBaseUnavailableErrorCode, StringComparison.Ordinal)))
        {
            return ReviewerMergeBaseUnavailableErrorCode;
        }

        if (findings.Any(finding => finding.Contains(ReviewerScopeUnavailableErrorCode, StringComparison.Ordinal)))
        {
            return ReviewerScopeUnavailableErrorCode;
        }

        if (findings.Any(finding => finding.Contains(ReviewerMergeTreeUnavailableErrorCode, StringComparison.Ordinal)))
        {
            return ReviewerMergeTreeUnavailableErrorCode;
        }

        return null;
    }

    private static ReviewerChangedFileScope? AddReviewerChangedFileScopeFindings(
        List<string> findings,
        TaskSpec task,
        string workingDirectory)
    {
        if (task.RequiredRole != AgentRole.Reviewer)
        {
            findings.Add("reviewer-scope: changed-file contract not required for non-Reviewer role");
            return null;
        }

        try
        {
            var scope = new WorkerGitContext().ReadReviewerChangedFileScope(workingDirectory);
            findings.Add(
                $"reviewer-scope: git diff --name-only main...HEAD found {scope.TotalChangedFileCount} changed file(s) from merge-base {ShortSha(scope.MergeBase)}");
            if (scope.Truncated)
            {
                findings.Add(
                    $"reviewer-scope: changed-file prompt list truncated to {scope.ChangedFiles.Count} file(s) from {scope.TotalChangedFileCount}");
            }

            return scope;
        }
        catch (ReviewerChangedFileScopeException ex)
        {
            findings.Add($"blocked: {ex.ErrorCode}: {ex.Message}");
            return null;
        }
    }

    private static string ShortSha(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 12 ? trimmed : trimmed[..12];
    }

    private static ReviewerMergeTreeStatus? AddReviewerMergeTreeStatusFindings(
        List<string> findings,
        TaskSpec task,
        string workingDirectory)
    {
        if (task.RequiredRole != AgentRole.Reviewer)
        {
            findings.Add("reviewer-merge-tree: merge-tree contract not required for non-Reviewer role");
            return null;
        }

        try
        {
            var status = new WorkerGitContext().ReadReviewerMergeTreeStatus(workingDirectory);
            if (status.IsClean)
            {
                findings.Add("reviewer-merge-tree: git merge-tree --write-tree --name-only main HEAD is clean against current main");
            }
            else
            {
                findings.Add(
                    $"reviewer-merge-tree: git merge-tree --write-tree --name-only main HEAD found {status.TotalConflictPathCount} conflict path(s) against current main");
                if (status.Truncated)
                {
                    findings.Add(
                        $"reviewer-merge-tree: conflict path prompt list truncated to {status.ConflictPaths.Count} path(s) from {status.TotalConflictPathCount}");
                }
            }

            return status;
        }
        catch (ReviewerMergeTreeStatusException ex)
        {
            findings.Add($"blocked: {ex.ErrorCode}: {ex.Message}");
            return null;
        }
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
            var profile = ResolveSubscriptionProfile(selection.Agent, roleSelection, profiles);
            var resolvedModelName = ResolveEffectiveSubscriptionModelName(selection.Agent, roleSelection);
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
                resolvedModelName,
                reasoningEffortSelection.Effort,
                roleSelection.Complexity,
                roleSelection.UsesComplexModel,
                reasoningEffortSelection.Reason,
                preflight.Findings,
                reviewerScopeChangedFiles: preflight.ReviewerScopeChangedFiles,
                reviewerScopeMergeBase: preflight.ReviewerScopeMergeBase,
                reviewerScopeTotalChangedFileCount: preflight.ReviewerScopeTotalChangedFileCount,
                reviewerMergeTreeClean: preflight.ReviewerMergeTreeClean,
                reviewerMergeTreeConflictPaths: preflight.ReviewerMergeTreeConflictPaths,
                reviewerMergeTreeTotalConflictPathCount: preflight.ReviewerMergeTreeTotalConflictPathCount,
                dispatchLane: roleSelection.DispatchLane ?? profile.Name,
                modelSelectionReason: roleSelection.Reason));
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

    private static WorkerProfile ResolveSubscriptionProfile(AgentDefinition agent, SubscriptionModelSelection selection, WorkerProfileCatalog profiles)
    {
        if (!AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy))
        {
            throw new InvalidOperationException($"Agent '{agent.Name}' is configured for API execution only.");
        }

        return profiles.GetRequired(ResolveSubscriptionProfileName(agent, selection));
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
        return ResolveSubscriptionProfileName(agent, ResolveEffectiveSubscriptionModelSelection(agent, goal, task));
    }

    public static string ResolveSubscriptionProfileName(
        AgentDefinition agent,
        Goal goal,
        TaskSpec task,
        WorkerProfileCatalog profiles,
        bool allowCheapLane = true,
        WorkerSandboxOptions? sandboxOptions = null,
        Func<string, bool>? commandExists = null)
    {
        return ResolveSubscriptionProfileName(
            agent,
            ResolveEffectiveSubscriptionModelSelection(
                agent,
                goal,
                task,
                profiles: profiles,
                sandboxOptions: sandboxOptions,
                commandExists: commandExists,
                allowCheapLane: allowCheapLane));
    }

    private static string ResolveSubscriptionProfileName(AgentDefinition agent, SubscriptionModelSelection selection)
    {
        return string.IsNullOrWhiteSpace(selection.LaunchProfileName)
            ? ResolveSubscriptionProfileName(agent, selection.Model)
            : selection.LaunchProfileName;
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
        WorkerProfileCatalog profiles,
        bool allowCheapLane = true,
        WorkerSandboxOptions? sandboxOptions = null,
        Func<string, bool>? commandExists = null)
    {
        var selection = ResolveEffectiveSubscriptionModelSelection(
            agent,
            goal,
            task,
            profiles: profiles,
            sandboxOptions: sandboxOptions,
            commandExists: commandExists,
            allowCheapLane: allowCheapLane);
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
            ["dispatchLane"] = selection.DispatchLane,
            ["executionPolicy"] = agent.ExecutionPolicy.ToString()
        };
    }

    internal static string ResolveEffectiveSubscriptionModelName(AgentDefinition agent, SubscriptionModelSelection selection)
    {
        return selection.UsesSubscriptionLaunchProfile
            ? agent.Subscription?.ModelAlias ?? selection.Model.ModelName
            : selection.Model.ModelName;
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
        if (task.RequiredRole == AgentRole.Reviewer && HasHighRiskOrComplexIntakeRiskLabel(goal))
        {
            return new EffectiveReasoningEffortSelection(HighRiskReviewerReasoningEffort, "intake-risk");
        }

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
        Func<string, bool>? commandExists = null,
        bool allowCheapLane = true)
    {
        var fullSelection = ResolveSubscriptionModel(agent, goal, task);
        return modelOverride is not null
            ? fullSelection with { Reason = "override: explicit dispatch profile/model selection" }
            : ResolveRoleModelSelection(agent, goal, task, fullSelection, profiles, claudeAuthProbe, sandboxOptions, commandExists, allowCheapLane);
    }

    private static SubscriptionModelSelection ResolveRoleModelSelection(
        AgentDefinition agent,
        Goal goal,
        TaskSpec task,
        SubscriptionModelSelection fullSelection,
        WorkerProfileCatalog? profiles,
        Func<ClaudeCliAuthState>? claudeAuthProbe,
        WorkerSandboxOptions? sandboxOptions,
        Func<string, bool>? commandExists,
        bool allowCheapLane)
    {
        if (!IsLightReadOnlyRole(task.RequiredRole))
        {
            if (agent.IsProviderRoutingConstrained == true)
            {
                return fullSelection with
                {
                    Reason = $"provider-constrained: {task.RequiredRole} remains on {agent.Model.ProviderName}"
                };
            }

            if (allowCheapLane &&
                TrySelectCheapLane(agent, goal, task, fullSelection, profiles, out var cheapSelection))
            {
                return cheapSelection;
            }

            return fullSelection with { Reason = "full-profile: role is write-capable or gate-heavy" };
        }

        if (HasCustomWorkerProfileOverride(agent))
        {
            return fullSelection with { Reason = "full-profile: role has custom subscription worker profile" };
        }

        if (task.RequiredRole == AgentRole.Reviewer && HasHighRiskOrComplexIntakeRiskLabel(goal))
        {
            return fullSelection with { Reason = "full-profile: Reviewer high-risk/complex intake labels require exhaustive review" };
        }

        if (TryFindRoleGuardrailFailure(task, out var guardrailFailure))
        {
            return fullSelection with { Reason = $"fallback-full-profile: {guardrailFailure}" };
        }

        if (agent.IsProviderRoutingConstrained == true)
        {
            return fullSelection with
            {
                Reason = $"provider-constrained: {task.RequiredRole} remains on {agent.Model.ProviderName}"
            };
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

    private static bool TrySelectCheapLane(
        AgentDefinition agent,
        Goal goal,
        TaskSpec task,
        SubscriptionModelSelection fullSelection,
        WorkerProfileCatalog? profiles,
        out SubscriptionModelSelection selection)
    {
        selection = fullSelection;
        var mechanicalRetry = task.PendingRetryRoundKind == RetryRoundKind.Mechanical;
        if (!agent.Model.ProviderName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            HasCustomWorkerProfileOverride(agent))
        {
            return false;
        }

        if (!mechanicalRetry && task.RequiredRole != AgentRole.Developer)
        {
            return false;
        }

        if (!mechanicalRetry &&
            fullSelection.UsesComplexModel &&
            fullSelection.Complexity is not TaskComplexity.Complex)
        {
            return false;
        }

        var smallTask = (fullSelection.Complexity is not TaskComplexity.Complex || HasExplicitSmallTaskIntakeLabel(goal)) &&
            !HasHighRiskOrComplexIntakeRiskLabel(goal);
        if (!mechanicalRetry && !smallTask)
        {
            return false;
        }

        var triggerReason = mechanicalRetry
            ? "mechanical-retry"
            : fullSelection.Complexity is TaskComplexity.Complex
                ? "small-task-label"
                : "small-task";

        var unavailableReason = "worker profile catalog unavailable";
        if (profiles is null ||
            !TryValidateSparkProfile(profiles, out unavailableReason))
        {
            selection = fullSelection with
            {
                Reason = $"fallback-default-lane: spark unavailable ({unavailableReason})",
                DispatchLane = ResolveSubscriptionProfileName(agent, fullSelection)
            };
            return true;
        }

        selection = new SubscriptionModelSelection(
            fullSelection.Complexity,
            new ModelProfile(
                "OpenAI",
                OpenAiSparkSubscriptionModelName,
                agent.Model.Capabilities,
                SubscriptionMode.ApiKey,
                AgentCatalog.RoutineSubscriptionReasoningEffort,
                agent.Model.MaxOutputTokens),
            UsesComplexModel: false,
            UsesSubscriptionLaunchProfile: false,
            Reason: $"cheap-lane: {task.RequiredRole} {triggerReason} uses {OpenAiSparkSubscriptionProfileName}/{OpenAiSparkSubscriptionModelName}",
            LaunchProfileName: OpenAiSparkSubscriptionProfileName,
            DispatchLane: OpenAiSparkSubscriptionProfileName);
        return true;
    }

    private static bool TryValidateSparkProfile(WorkerProfileCatalog profiles, out string unavailableReason)
    {
        try
        {
            var profile = profiles.Profiles.FirstOrDefault(profile =>
                profile.Name.Equals(OpenAiSparkSubscriptionProfileName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                unavailableReason = $"worker profile '{OpenAiSparkSubscriptionProfileName}' was not found";
                return false;
            }

            if (WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate))
            {
                unavailableReason = $"worker profile '{OpenAiSparkSubscriptionProfileName}' only echoes prompt path";
                return false;
            }

            if (!WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate))
            {
                unavailableReason = $"worker profile '{OpenAiSparkSubscriptionProfileName}' does not include {{subscriptionModelName}}";
                return false;
            }

            if (!WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(profile.CommandTemplate))
            {
                unavailableReason = $"worker profile '{OpenAiSparkSubscriptionProfileName}' does not include {{subscriptionReasoningEffort}}";
                return false;
            }

            var capability = WorkerProfileDiagnostics.EvaluatePatchCapability(
                profile,
                DefaultProviders.Resolve(ProviderKind.OpenAICodexSpark));
            if (!capability.IsPatchCapable)
            {
                unavailableReason = capability.Detail;
                return false;
            }

            unavailableReason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            unavailableReason = ex.Message;
            return false;
        }
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
            if (!authState.HasAnthropicApiKey && !authState.HasCliCredentialArtifact)
            {
                unavailableReason = "Claude CLI auth unavailable: no ANTHROPIC_API_KEY and no CLI credential artifact to seed";
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

    private static bool HasHighRiskOrComplexIntakeRiskLabel(Goal goal)
    {
        return EnumerateStoredIntakeRiskLabels(goal).Any(label =>
            label.Equals("high-risk", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("complex", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasExplicitSmallTaskIntakeLabel(Goal goal)
    {
        return EnumerateStoredIntakeRiskLabels(goal).Any(label =>
            label.Equals("small-task", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("small", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("simple", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateStoredIntakeRiskLabels(Goal goal)
    {
        foreach (var evt in goal.Timeline.Where(evt => evt.Kind == ProgressKind.GoalPolicyDecision))
        {
            var markerIndex = evt.Message.IndexOf(IntakeRiskLabelsMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            var labelsText = evt.Message[(markerIndex + IntakeRiskLabelsMarker.Length)..].Trim();
            var sentenceEnd = labelsText.IndexOf('.');
            if (sentenceEnd >= 0)
            {
                labelsText = labelsText[..sentenceEnd];
            }

            foreach (var label in labelsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                yield return label;
            }
        }
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

    internal sealed record SubscriptionModelSelection(
        TaskComplexity Complexity,
        ModelProfile Model,
        bool UsesComplexModel,
        bool UsesSubscriptionLaunchProfile = true,
        string Reason = "full-profile: default subscription model selection",
        string? ReasoningEffortOverride = null,
        string ReasoningEffortReason = "base",
        string? LaunchProfileName = null,
        string? DispatchLane = null);

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
        // When the OS worker sandbox is active, Codex's nested sandbox is disabled so it does not run
        // the expensive Windows sandbox setup helper. MIC remains the enforcement boundary: file roles
        // receive a Low writable worktree, while read-only Codex roles keep the worktree Medium.
        var osSandbox = WorkerSandboxOptions.FromEnvironment().Enabled;
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["workingDirectory"] = workingDirectory,
            ["sandboxMode"] = osSandbox
                ? "danger-full-access"
                : (isWriteCapable ? "workspace-write" : "read-only"),
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

    internal static string BuildDispatchCommandTemplate(
        WorkerProfile profile,
        ProviderKind providerKind,
        IReadOnlyDictionary<string, string?> dispatchVariables)
    {
        if (!ShouldUseTypedBuiltInCommand(profile, providerKind) ||
            !HasRequiredBuiltInVariables(providerKind, dispatchVariables))
        {
            // Missing variables must remain as placeholders so the legacy preparation path
            // reports them before it writes the prompt or mutates dispatch state.
            return profile.CommandTemplate;
        }

        return string.Join(
            ' ',
            ProviderCommandBuilder.Build(
                providerKind,
                GetDispatchVariable(dispatchVariables, "subscriptionModelName"),
                GetDispatchVariable(dispatchVariables, "subscriptionReasoningEffort"),
                GetDispatchVariable(dispatchVariables, "permissionMode"),
                GetDispatchVariable(dispatchVariables, "sandboxMode"),
                GetDispatchVariable(dispatchVariables, "workingDirectory")));
    }

    private static bool HasRequiredBuiltInVariables(
        ProviderKind providerKind,
        IReadOnlyDictionary<string, string?> variables)
    {
        return providerKind switch
        {
            ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexSpark =>
                HasKeys(
                    variables,
                    "subscriptionModelName",
                    "subscriptionReasoningEffort",
                    "sandboxMode",
                    "workingDirectory"),
            ProviderKind.OpenAICodexOssCli =>
                HasKeys(variables, "subscriptionModelName", "workingDirectory"),
            ProviderKind.AnthropicClaudeCli =>
                HasKeys(variables, "subscriptionModelName", "permissionMode"),
            ProviderKind.OllamaQwenCodeCli =>
                HasKeys(variables, "subscriptionModelName", "workingDirectory"),
            _ => false
        };
    }

    private static bool HasKeys(
        IReadOnlyDictionary<string, string?> variables,
        params string[] names) =>
        names.All(variables.ContainsKey);

    private static bool ShouldUseTypedBuiltInCommand(
        WorkerProfile profile,
        ProviderKind providerKind)
    {
        if (!ProviderCommandBuilder.IsBuiltIn(providerKind))
        {
            return false;
        }

        // The legacy shim remains authoritative for user-persisted and test-local command overrides
        // until Slice 3 rejects custom templates that reuse a built-in profile name.
        return profile.CommandTemplate.Equals(
            WorkerProfileCatalog.Default().GetRequired(profile.Name).CommandTemplate,
            StringComparison.Ordinal);
    }

    private static string GetDispatchVariable(
        IReadOnlyDictionary<string, string?> variables,
        string name) =>
        variables.TryGetValue(name, out var value) ? value ?? string.Empty : string.Empty;
}
