using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    public string ToLine()
    {
        var details = Details?
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .Take(5)
            .Select(detail =>
            {
                const int maxDetailLength = 256;
                var sanitized = detail.Replace(' ', '_').Replace('\t', '_').Replace('\r', '_').Replace('\n', '_');
                return sanitized.Length > maxDetailLength ? sanitized[..maxDetailLength] : sanitized;
            })
            .ToArray() ?? [];
        return $"READY_BLOCKED goal={GoalPrefix} task={TaskNumber} provider={Provider} reason={Reason}" +
            (details.Length == 0 ? string.Empty : $" details={string.Join('|', details)}");
    }
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
    public const string XaiSubscriptionProfileName = "grok-cli";
    public const string QwenCodeCliProfileName = "qwen-code-cli";
    public const string LlamaCppSubscriptionProfileName = QwenCodeCliProfileName;
    public const string LightRoleAnthropicModelName = "claude-haiku-4-5";
    public const string OllamaSubscriptionProfileName = QwenCodeCliProfileName;
    public const string ReviewerScopeUnavailableErrorCode = WorkerGitContext.ReviewerScopeUnavailableErrorCode;
    public const string ReviewerMergeBaseUnavailableErrorCode = WorkerGitContext.ReviewerMergeBaseUnavailableErrorCode;
    public const string ReviewerMergeTreeUnavailableErrorCode = WorkerGitContext.ReviewerMergeTreeUnavailableErrorCode;
    public const string MissingResearchArtifactErrorCode = "missing-research-artifact";
    public const string MissingPlannerArtifactErrorCode = "missing-planner-artifact";
    public const string ArtifactTooLargeErrorCode = "artifact-too-large";
    private const string HighRiskReviewerReasoningEffort = "xhigh";
    private const string IntakeRiskLabelsMarker = "risk labels:";
    private static readonly WorkerProviderCatalog DefaultProviders = WorkerProviderCatalog.Default();
    private static readonly IReadOnlyDictionary<string, RegistryArtifactSource> RegistryArtifactSources =
        new Dictionary<string, RegistryArtifactSource>(StringComparer.Ordinal)
        {
            ["artifact-registry.json"] = new(ContextArtifactKind.ContextManifest, RegistryArtifactDisposition.CanonicalManifestAlias),
            ["context-package.json"] = new(ContextArtifactKind.ContextManifest, RegistryArtifactDisposition.CanonicalManifestAlias),
            ["manifest.md"] = new(ContextArtifactKind.ContextManifest, RegistryArtifactDisposition.CanonicalManifestAlias),
            ["digest.md"] = new(ContextArtifactKind.RegisteredContext, RegistryArtifactDisposition.DomainProjection),
            ["objective.md"] = new(ContextArtifactKind.RegisteredContext, RegistryArtifactDisposition.DomainProjection),
            ["current-task.md"] = new(ContextArtifactKind.RegisteredContext, RegistryArtifactDisposition.DomainProjection),
            ["diff-summary.md"] = new(ContextArtifactKind.RegisteredContext, RegistryArtifactDisposition.DomainProjection),
            ["prior-task-summaries.md"] = new(ContextArtifactKind.RegisteredContext, RegistryArtifactDisposition.DomainProjection),
            ["research-notes.md"] = new(ContextArtifactKind.ResearcherEvidence, RegistryArtifactDisposition.Deliver),
            ["planner-plan.md"] = new(ContextArtifactKind.PlannerPlan, RegistryArtifactDisposition.Deliver),
            ["prior-task-evidence.md"] = new(ContextArtifactKind.PriorTaskEvidence, RegistryArtifactDisposition.Deliver),
            ["prior-goal-evidence.md"] = new(ContextArtifactKind.PriorTaskEvidence, RegistryArtifactDisposition.Deliver),
            ["AGENTS.md"] = new(ContextArtifactKind.OperatorInstructions, RegistryArtifactDisposition.Deliver),
            ["deterministic-verification.md"] = new(ContextArtifactKind.RegisteredContext, RegistryArtifactDisposition.Deliver),
            ["workflow-brokers.md"] = new(ContextArtifactKind.RegisteredContext, RegistryArtifactDisposition.Deliver),
            ["context-budget.md"] = new(ContextArtifactKind.RegisteredContext, RegistryArtifactDisposition.Deliver),
            ["selected-skills.md"] = new(ContextArtifactKind.RegisteredContext, RegistryArtifactDisposition.Deliver),
            ["source-survey.md"] = new(ContextArtifactKind.RegisteredContext, RegistryArtifactDisposition.Deliver),
            ["subscription-preflight.md"] = new(ContextArtifactKind.RegisteredContext, RegistryArtifactDisposition.Deliver)
        };

    internal static IReadOnlyList<string> DeliverableRegistryArtifactPaths => RegistryArtifactSources
        .Where(entry => entry.Value.Disposition == RegistryArtifactDisposition.Deliver)
        .Select(entry => entry.Key)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();

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
        string? modelSelectionReason = null,
        ReviewRetryCapReceipt? reviewRetryCap = null,
        CitedPriorEvidenceResolver? citedPriorEvidenceResolver = null,
        WorkerSandboxOptions? sandboxOptions = null,
        int plannerSampleCount = 1,
        PaidRouteClassification paidRoute = PaidRouteClassification.Unknown)
    {
        EnsureTaskNeedsExecution(task, allowPendingRecordedDispatchRefresh);
        EnsureSubscriptionRetryWindowHasPassed(task, dispatchedAt);
        var priorDispatch = task.LastDispatch;
        var durableArtifactFindings = new List<string>();
        AddDurableArtifactDependencyFindings(
            durableArtifactFindings,
            goal,
            task,
            providerName ?? "unknown",
            modelName ?? "unknown",
            profile.Name);
        if (durableArtifactFindings.Any(finding => finding.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase)))
        {
            var errorCode = ResolvePreflightErrorCode(durableArtifactFindings);
            throw new WorkerSubscriptionPreflightException(
                "Worker dispatch blocked: " + string.Join("; ", durableArtifactFindings),
                errorCode,
                durableArtifactFindings);
        }

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
        var citedPriorEvidence = citedPriorEvidenceResolver?.Resolve(goal, task);
        var contextDirectory = WorkerContextArtifacts.Write(
            goal,
            task,
            workingDirectory,
            preflightFindings,
            citedPriorEvidence,
            providerName,
            modelName);
        var targetContext = TryReadCurrentTargetContext(workingDirectory);
        var currentMainIdentity = ReadCurrentMainIdentityForRetry(workingDirectory);
        var reviewerRoundTouchScope = ReadReviewRoundTouchScope(
            goal,
            task,
            workingDirectory,
            targetContext?.HeadCommit);
        var effectiveReviewRetryCap = task.RequiredRole == AgentRole.Reviewer
            ? reviewRetryCap ?? ReviewRetryCapReceipt.Create(
                goal,
                ConductorAutonomyPolicy.Default.ReviewAutoRetryStopRound)
            : null;
        var usesTypedContextPackage = WorkerContextHelpers.UsesTypedContextPackage(
            task.RequiredRole,
            providerName,
            modelName);
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
            reviewerRoundTouchScope.TouchedAnchors,
            reviewerRoundTouchScope.Diagnostic,
            effectiveReviewRetryCap,
            emitTypedSourceBoundaries: usesTypedContextPackage);
        WorkerContextPackageReceipt? contextPackageReceipt = null;
        var packagedBrief = brief;
        if (usesTypedContextPackage)
        {
            var contextPackage = BuildContextPackage(
                goal,
                task,
                workingDirectory,
                contextDirectory,
                brief,
                humanInputRequests: kernel.HumanInputRequests
                    .Where(request => request.GoalId == goal.Id)
                    .ToArray(),
                reviewerScopeChangedFiles: reviewerScopeChangedFiles,
                reviewerScopeMergeBase: reviewerScopeMergeBase,
                reviewerScopeTotalChangedFileCount: reviewerScopeTotalChangedFileCount,
                reviewerMergeTreeClean: reviewerMergeTreeClean,
                reviewerMergeTreeConflictPaths: reviewerMergeTreeConflictPaths,
                reviewerMergeTreeTotalConflictPathCount: reviewerMergeTreeTotalConflictPathCount);
            packagedBrief = brief with { Content = WorkerContextPackageBuilder.Render(contextPackage) };
            contextPackageReceipt = WorkerContextPackageBuilder.CreateReceipt(contextPackage);
        }
        TaskBrief budgetedBrief;
        try
        {
            budgetedBrief = WorkerPromptInputBudget.Apply(
                packagedBrief,
                providerName,
                modelName,
                workerProfileName: profile.Name).Brief;
        }
        catch (WorkerPromptInputBudgetExceededException error)
            when (UsesResearchFirstArtifactHandoff(goal, task))
        {
            var findings = new[]
            {
                $"blocked: {ArtifactTooLargeErrorCode}: complete durable artifacts plus the required {task.RequiredRole} brief are {error.TokenCount} tokens, exceeding {error.ProviderName}/{error.ModelName} input budget {error.TokenBudget}; artifacts will not be truncated"
            };
            throw new WorkerSubscriptionPreflightException(
                "Worker dispatch blocked: " + findings[0],
                ArtifactTooLargeErrorCode,
                findings);
        }
        var dispatchVariables = BuildDispatchVariables(task.RequiredRole, workingDirectory, variables, sandboxOptions, providerName);
        var workerProviderKind = DefaultProviders.ResolveProfile(profile.Name).Identity.Kind;
        var commandTemplate = BuildDispatchCommandTemplate(profile, workerProviderKind, dispatchVariables);
        var preparation = WorkerCommandTemplate.Prepare(
            budgetedBrief,
            profile.Name,
            commandTemplate,
            promptRoot,
            dispatchVariables,
            dispatchedAt);
        var retryContextFingerprint = RetryContextFingerprintFactory.Build(
            goal,
            task,
            providerName,
            modelName,
            paidRoute,
            priorDispatch?.ResultCommit ?? priorDispatch?.BaseCommit ?? task.LastVerification?.ReviewedCommit,
            targetContext?.HeadCommit ?? priorDispatch?.BaseCommit,
            currentMainIdentity);
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
            ReviewFindingTouchedAnchors: reviewerRoundTouchScope.TouchedAnchors,
            ReviewFindingTouchProofDiagnostic: reviewerRoundTouchScope.Diagnostic,
            ReviewRetryCap: effectiveReviewRetryCap,
            ContextPackageReceipt: contextPackageReceipt,
            PlannerSampleCount: PlannerSamplingPolicy.EffectiveSampleCount(task.RequiredRole, plannerSampleCount),
            RetryContextFingerprint: retryContextFingerprint,
            PaidRoute: paidRoute),
            allowPendingRecordedDispatchRefresh);
        return new WorkerProfileDispatchResult(task, preparation.PromptPath);
    }

    internal static ReviewerRoundTouchScope ReadReviewRoundTouchScope(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        string? currentHeadCommit)
    {
        if (task.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester))
        {
            return new ReviewerRoundTouchScope([]);
        }

        var carriedRound = goal.Tasks
            .Where(candidate => candidate.RequiredRole == task.RequiredRole)
            .SelectMany(candidate => candidate.VerificationHistory)
            .Where(verification => verification.MergedReviewFindings is not null)
            .OrderByDescending(verification => verification.CompletedAt)
            .FirstOrDefault();
        var anchors = (carriedRound?.MergedReviewFindings ?? [])
            .Select(finding => finding.Location)
            .ToArray();
        return new WorkerGitContext().ReadReviewerRoundTouchScope(
            workingDirectory,
            carriedRound?.ReviewedCommit,
            currentHeadCommit,
            anchors);
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
        DateTimeOffset dispatchedAt,
        CitedPriorEvidenceResolver? citedPriorEvidenceResolver = null,
        WorkerSandboxOptions? sandboxOptions = null)
    {
        var results = new List<WorkerProfileDispatchResult>();
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Assigned).ToList())
        {
            results.Add(PrepareTask(
                kernel,
                goal,
                task,
                profile,
                promptRoot,
                workingDirectory,
                dispatchedAt,
                citedPriorEvidenceResolver: citedPriorEvidenceResolver,
                sandboxOptions: sandboxOptions));
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
        Func<string, bool>? commandExists = null,
        int? reviewAutoRetryStopRound = null,
        CitedPriorEvidenceResolver? citedPriorEvidenceResolver = null,
        int plannerSampleCount = 1)
    {
        EnsureTaskNeedsExecution(task);
        var sandbox = sandboxOptions ?? WorkerSandboxOptions.FromEnvironment();

        var agent = ResolveAssignedAgent(kernel, goal, task, agents);
        var roleSelection = ResolveEffectiveSubscriptionModelSelection(agent, goal, task, modelOverride, profiles, claudeAuthProbe, sandbox, commandExists);
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
            sandbox,
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
            modelSelectionReason: roleSelection.Reason,
            reviewRetryCap: task.RequiredRole == AgentRole.Reviewer
                ? ReviewRetryCapReceipt.Create(
                    goal,
                    reviewAutoRetryStopRound ?? ConductorAutonomyPolicy.Default.ReviewAutoRetryStopRound)
                : null,
            citedPriorEvidenceResolver: citedPriorEvidenceResolver,
            sandboxOptions: sandbox,
            plannerSampleCount: plannerSampleCount,
            paidRoute: ClassifyPaidRoute(roleSelection.Model.SubscriptionMode));
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
            var sandbox = sandboxOptions ?? WorkerSandboxOptions.FromEnvironment();
            var agent = ResolveAssignedAgent(null, goal, task, agents);
            var roleSelection = ResolveEffectiveSubscriptionModelSelection(agent, goal, task, modelOverride, profiles, claudeAuthProbe, sandbox, commandExists);
            roleSelection = ApplyReasoningEffortPolicy(agent, goal, task, roleSelection);
            profileName = modelOverride?.ProfileName is { Length: > 0 } overrideProfile
                ? overrideProfile
                : ResolveSubscriptionProfileName(agent, roleSelection);
            var profile = profiles.GetRequired(profileName);
            findings.Add($"profile: {profile.Name}");
            findings.Add($"dispatch-lane: {roleSelection.DispatchLane ?? profile.Name}");
            findings.Add($"model-selection: {roleSelection.Reason}");
            AddClaudeLowIntegrityAuthFinding(findings, task.RequiredRole, DefaultProviders.ResolveProfile(profile.Name), sandbox, claudeAuthProbe);
            var effectiveModelName = modelOverride?.ModelName is { Length: > 0 } overrideModel
                ? overrideModel
                : ResolveEffectiveSubscriptionModelName(agent, roleSelection);
            var dispatchProviderName = ResolveDispatchProviderName(roleSelection.Model.ProviderName, profile.Name, modelOverride);
            findings.Add($"model: {dispatchProviderName}/{effectiveModelName}");
            findings.Add($"complexity: {roleSelection.Complexity}");
            AddDurableArtifactDependencyFindings(
                findings,
                goal,
                task,
                dispatchProviderName,
                effectiveModelName,
                profile.Name);

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

            var capability = WorkerSandboxCapabilityPlanner.Evaluate(
                goal,
                task,
                profile,
                workingDirectory,
                allowGitReference,
                sandbox,
                commandExists);
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
                findings.Add(
                    $"blocked: subscription retry deferred until {retryAfter:u}; source: {DispatchFailureClassifier.DescribeSubscriptionRetrySource(task)}");
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
        if (findings.Any(finding => finding.Contains(MissingResearchArtifactErrorCode, StringComparison.Ordinal)))
        {
            return MissingResearchArtifactErrorCode;
        }

        if (findings.Any(finding => finding.Contains(MissingPlannerArtifactErrorCode, StringComparison.Ordinal)))
        {
            return MissingPlannerArtifactErrorCode;
        }

        if (findings.Any(finding => finding.Contains(ArtifactTooLargeErrorCode, StringComparison.Ordinal)))
        {
            return ArtifactTooLargeErrorCode;
        }

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

    private static void AddDurableArtifactDependencyFindings(
        List<string> findings,
        Goal goal,
        TaskSpec task,
        string providerName,
        string modelName,
        string? workerProfileName = null)
    {
        var plannerIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.RequiredRole == AgentRole.Planner);
        var researcherIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.RequiredRole == AgentRole.Researcher);
        var taskIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.Id == task.Id);
        var usesResearchFirstPipeline = researcherIndex >= 0 && plannerIndex > researcherIndex;
        if (!usesResearchFirstPipeline || taskIndex <= researcherIndex)
        {
            findings.Add("artifact-dependency: research-first durable handoff not required for this persisted task graph position");
            return;
        }

        var researchTask = goal.Tasks[researcherIndex];
        if (researchTask.Status != WorkTaskStatus.Completed ||
            !TryResolveLatestResearchArtifact(researchTask, out var research))
        {
            findings.Add(
                $"blocked: {MissingResearchArtifactErrorCode}: Planner task {goal.Tasks[plannerIndex].Id.Value} requires complete Researcher task {researchTask.Id.Value} artifact; keep Planner assigned and retry the existing Researcher stage");
            return;
        }

        var artifacts = new List<(string Role, string Text)> { ("Researcher", research) };
        findings.Add($"ok: research-artifact: task={researchTask.Id.Value} sha256:{Sha256(research)} chars={research.Length}");

        if (taskIndex > plannerIndex)
        {
            var plannerTask = goal.Tasks[plannerIndex];
            if (plannerTask.Status != WorkTaskStatus.Completed ||
                !TryResolveLatestPlannerArtifact(plannerTask, out var plan))
            {
                findings.Add(
                    $"blocked: {MissingPlannerArtifactErrorCode}: downstream {task.RequiredRole} task {task.Id.Value} requires complete Planner task {plannerTask.Id.Value} artifact");
                return;
            }

            artifacts.Add(("Planner", plan));
            findings.Add($"ok: planner-artifact: task={plannerTask.Id.Value} sha256:{Sha256(plan)} chars={plan.Length}");
        }

        var artifactCharacters = artifacts.Sum(artifact => artifact.Text.Length);
        var artifactTokens = WorkerPromptInputBudget.CountTokens(
            string.Join(Environment.NewLine, artifacts.Select(artifact => artifact.Text)));
        var tokenBudget = WorkerPromptInputBudget.InputTokenBudget(providerName, modelName, workerProfileName);
        if (artifactTokens > tokenBudget)
        {
            findings.Add(
                $"blocked: {ArtifactTooLargeErrorCode}: complete {string.Join("+", artifacts.Select(artifact => artifact.Role))} artifacts are {artifactCharacters} characters/{artifactTokens} tokens, exceeding {providerName}/{modelName} input budget {tokenBudget}; artifacts will not be truncated");
        }
    }

    private static bool TryResolveLatestResearchArtifact(TaskSpec task, out string research)
    {
        foreach (var verification in task.VerificationHistory.Reverse())
        {
            if (WorkerArtifactWriter.TryResolveDurableResearch(
                    verification,
                    out research,
                    out _))
            {
                return true;
            }
        }

        research = string.Empty;
        return false;
    }

    private static bool TryResolveLatestPlannerArtifact(TaskSpec task, out string plan)
    {
        foreach (var verification in task.VerificationHistory.Reverse())
        {
            if (WorkerArtifactWriter.TryResolveDurablePlannerPlan(
                    verification,
                    out plan,
                    out _))
            {
                return true;
            }
        }

        plan = string.Empty;
        return false;
    }

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static bool UsesResearchFirstArtifactHandoff(Goal goal, TaskSpec task)
    {
        var plannerIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.RequiredRole == AgentRole.Planner);
        var researcherIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.RequiredRole == AgentRole.Researcher);
        var taskIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.Id == task.Id);
        return researcherIndex >= 0 && plannerIndex > researcherIndex && taskIndex > researcherIndex;
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

        var inspection = GitCli.InspectWorktreeStatus(workingDirectory);
        if (!inspection.Succeeded)
        {
            findings.Add($"warning: worktree cleanliness unavailable before dispatch ({inspection.Error}); verify git status from the goal workspace");
            return;
        }

        if (!inspection.IsDirty)
        {
            findings.Add("ok: worktree clean before dispatch");
            return;
        }

        const int pathLimit = 5;
        var displayedPaths = inspection.CommitWorthyPaths.Take(pathLimit).ToArray();
        var omittedCount = inspection.CommitWorthyPaths.Count - displayedPaths.Length;
        var omitted = omittedCount > 0 ? $", +{omittedCount} more" : string.Empty;
        findings.Add(
            $"blocked: worktree has {inspection.CommitWorthyPaths.Count} uncommitted change(s) before dispatch; " +
            $"paths=[{string.Join(", ", displayedPaths)}{omitted}]; commit, stash, or clean the goal workspace first");
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
        return WorkerPromptInputBudget.Apply(
            brief,
            selection.Model.ProviderName,
            modelName,
            workerProfileName: agent.Subscription?.WorkerProfileName).Brief.Content.Length;
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
        Func<string, bool>? commandExists = null,
        int? reviewAutoRetryStopRound = null,
        CitedPriorEvidenceResolver? citedPriorEvidenceResolver = null,
        WorkerSandboxOptions? sandboxOptions = null,
        int plannerSampleCount = 1)
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
            commandExists,
            reviewAutoRetryStopRound,
            citedPriorEvidenceResolver,
            sandboxOptions,
            plannerSampleCount).Dispatches;
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
        Func<string, bool>? commandExists = null,
        int? reviewAutoRetryStopRound = null,
        CitedPriorEvidenceResolver? citedPriorEvidenceResolver = null,
        WorkerSandboxOptions? sandboxOptions = null,
        int plannerSampleCount = 1)
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
        var sandbox = sandboxOptions ?? WorkerSandboxOptions.FromEnvironment();
        var sandboxConfinesWrites = sandbox.Enabled;

        var results = new List<WorkerProfileDispatchResult>();
        var blocked = new List<ReadyBlockedDiagnostic>();
        foreach (var selection in selections)
        {
            var roleSelection = ResolveEffectiveSubscriptionModelSelection(
                selection.Agent,
                goal,
                selection.Task,
                profiles: profiles,
                sandboxOptions: sandbox,
                commandExists: commandExists);
            roleSelection = ApplyReasoningEffortPolicy(selection.Agent, goal, selection.Task, roleSelection);
            var profile = ResolveSubscriptionProfile(selection.Agent, roleSelection, profiles);
            var resolvedModelName = ResolveEffectiveSubscriptionModelName(selection.Agent, roleSelection);
            var reasoningEffortSelection = new EffectiveReasoningEffortSelection(
                ResolveEffectiveSubscriptionReasoningEffort(selection.Agent, roleSelection),
                roleSelection.ReasoningEffortReason);
            var preflight = PreflightSubscriptionTask(
                goal, selection.Task, agents, profiles, workingDirectory, dispatchedAt,
                allowGitReference: sandboxConfinesWrites,
                sandboxOptions: sandbox,
                commandExists: commandExists);
            if (!preflight.Allowed)
            {
                TryResolveMissingArtifactDependency(kernel, goal, selection.Task, preflight);
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
                modelSelectionReason: roleSelection.Reason,
                reviewRetryCap: selection.Task.RequiredRole == AgentRole.Reviewer
                    ? ReviewRetryCapReceipt.Create(
                        goal,
                        reviewAutoRetryStopRound ?? ConductorAutonomyPolicy.Default.ReviewAutoRetryStopRound)
                    : null,
                citedPriorEvidenceResolver: citedPriorEvidenceResolver,
                sandboxOptions: sandbox,
                plannerSampleCount: plannerSampleCount,
                paidRoute: ClassifyPaidRoute(roleSelection.Model.SubscriptionMode)));
        }

        return new WorkerProfileReadyBatchResult(results, blocked);
    }

    private static PaidRouteClassification ClassifyPaidRoute(SubscriptionMode subscriptionMode) =>
        subscriptionMode == SubscriptionMode.LocalBridge
            ? PaidRouteClassification.NonPaid
            : PaidRouteClassification.Paid;

    private static bool TryResolveMissingArtifactDependency(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec blockedTask,
        WorkerSubscriptionPreflightResult preflight)
    {
        var dependency = ResolveMissingArtifactDependency(goal, blockedTask, preflight.ErrorCode);
        if (dependency is null)
        {
            return false;
        }

        var (upstreamTask, artifactRole, errorCode) = dependency.Value;
        if (upstreamTask.Status is WorkTaskStatus.Running or WorkTaskStatus.Assigned)
        {
            return false;
        }

        var rerouteMarker = $"{errorCode}: dependency reroute for downstream {blockedTask.Id.Value}";
        var alreadyRerouted = goal.Timeline.Any(item =>
            item.Kind == ProgressKind.TaskRetried &&
            item.TaskId == upstreamTask.Id &&
            item.Message.Contains(rerouteMarker, StringComparison.Ordinal));
        if (!alreadyRerouted)
        {
            kernel.RetryTask(
                goal.Id,
                upstreamTask.Id,
                $"{rerouteMarker}; {blockedTask.RequiredRole} is held until {artifactRole} task {upstreamTask.Id.Value} produces a complete durable artifact.",
                retryCause: RetryCause.CriterionEvidenceOwnerMismatch);
            return true;
        }

        kernel.EscalateTaskFailure(
            goal.Id,
            upstreamTask.Id,
            $"{errorCode}: unmet durable artifact dependency after one reroute; {blockedTask.RequiredRole} task {blockedTask.Id.Value} requires a complete {artifactRole} artifact from task {upstreamTask.Id.Value}. Operator recovery: repair the {artifactRole} output contract, then retry task {upstreamTask.Id.Value}; manual downstream verification cannot override this gate.");
        return true;
    }

    private static (TaskSpec Task, string ArtifactRole, string ErrorCode)? ResolveMissingArtifactDependency(
        Goal goal,
        TaskSpec blockedTask,
        string? errorCode)
    {
        var blockedIndex = goal.Tasks.ToList().FindIndex(candidate => candidate.Id == blockedTask.Id);
        if (blockedIndex <= 0)
        {
            return null;
        }

        if (blockedTask.RequiredRole == AgentRole.Planner &&
            string.Equals(errorCode, MissingResearchArtifactErrorCode, StringComparison.Ordinal))
        {
            var researcher = goal.Tasks
                .Take(blockedIndex)
                .LastOrDefault(candidate => candidate.RequiredRole == AgentRole.Researcher);
            return researcher is null
                ? null
                : (researcher, AgentRole.Researcher.ToString(), MissingResearchArtifactErrorCode);
        }

        if (string.Equals(errorCode, MissingPlannerArtifactErrorCode, StringComparison.Ordinal))
        {
            var planner = goal.Tasks
                .Take(blockedIndex)
                .LastOrDefault(candidate => candidate.RequiredRole == AgentRole.Planner);
            return planner is null
                ? null
                : (planner, AgentRole.Planner.ToString(), MissingPlannerArtifactErrorCode);
        }

        return null;
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
        if (blockedFindings.Any(finding => finding.Contains(MissingResearchArtifactErrorCode, StringComparison.Ordinal)))
            return MissingResearchArtifactErrorCode;
        if (blockedFindings.Any(finding => finding.Contains(MissingPlannerArtifactErrorCode, StringComparison.Ordinal)))
            return MissingPlannerArtifactErrorCode;
        if (blockedFindings.Any(finding => finding.Contains(ArtifactTooLargeErrorCode, StringComparison.Ordinal)))
            return ArtifactTooLargeErrorCode;
        if (blockedFindings.Any(finding => finding.Contains("uncommitted change", StringComparison.OrdinalIgnoreCase)))
            return "dirty-worktree";
        if (blockedFindings.Any(finding => finding.Contains("cleanliness unavailable", StringComparison.OrdinalIgnoreCase)))
            return "worktree-status-unavailable";
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

        throw new InvalidOperationException(
            $"Task '{task.Id}' subscription retry deferred until {retryAfter:u}; source: {DispatchFailureClassifier.DescribeSubscriptionRetrySource(task)}.");
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
            ["executionPolicy"] = agent.ExecutionPolicy.ToString(),
            ["openaiBaseUrl"] = OpenAiCompatibleCliBackend.ResolveBaseUrl(selection.Model.ProviderName),
            ["openaiApiKey"] = OpenAiCompatibleCliBackend.ResolveApiKey(selection.Model.ProviderName)
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

    internal static string? ReadCurrentMainIdentityForRetry(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return null;

        var main = GitCli.Run(workingDirectory, "rev-parse", "main");
        if (main.ExitCode != 0)
            return null;
        var identity = main.Output.Trim();
        return string.IsNullOrWhiteSpace(identity) ? null : identity;
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

    internal static WorkerContextPackage BuildContextPackage(
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        string contextDirectory,
        TaskBrief brief,
        ICollection<SemanticSourceObservation>? observedSources = null,
        IReadOnlyList<HumanInputRequest>? humanInputRequests = null,
        IReadOnlyList<string>? reviewerScopeChangedFiles = null,
        string? reviewerScopeMergeBase = null,
        int? reviewerScopeTotalChangedFileCount = null,
        bool? reviewerMergeTreeClean = null,
        IReadOnlyList<string>? reviewerMergeTreeConflictPaths = null,
        int? reviewerMergeTreeTotalConflictPathCount = null)
    {
        var targetRole = task.RequiredRole;
        var registryPath = Path.Combine(contextDirectory, "artifact-registry.json");
        var registry = JsonSerializer.Deserialize<ContextArtifactRegistryDocument>(
            File.ReadAllText(registryPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Worker context artifact registry was empty.");
        var artifacts = new List<WorkerContextArtifact>();
        var goalHumanInputRequests = humanInputRequests ?? [];
        var clarificationAnswerHistory = goal.RefinedSpec?.ClarificationAnswerHistory ?? [];

        void AddSource(
            WorkerContextSemanticSource source,
            string identityValue,
            ContextArtifactKind kind,
            byte[] bytes,
            IReadOnlyList<AgentRole>? roleVisibility = null,
            ContextDeliveryMode? deliveryMode = null)
        {
            var identity = new LogicalArtifactIdentity(identityValue);
            bytes = ApplyHumanInputRetractions(bytes, goalHumanInputRequests, clarificationAnswerHistory);
            var mode = deliveryMode ?? WorkerContextPackageBuilder.SelectDeliveryMode(kind);
            string? relativePath = null;
            if (mode == ContextDeliveryMode.MandatoryFile)
            {
                relativePath = $".orchestrator-context/{goal.Id.Value}/authoritative/{task.Id.Value}/typed/{identity.Value}";
                var materializationPath = Path.Combine(
                    workingDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(materializationPath)!);
                File.WriteAllBytes(materializationPath, bytes);
            }

            artifacts.Add(WorkerContextArtifact.Create(
                identity,
                kind,
                bytes,
                roleVisibility ?? [targetRole],
                mode,
                ContextContractVersion.V1,
                relativePath));
            observedSources?.Add(new SemanticSourceObservation(source, identity.Value));
        }

        AddSource(WorkerContextSemanticSource.GoalObjective, "goal/objective.md", ContextArtifactKind.TaskObjective, Encoding.UTF8.GetBytes(goal.Objective));
        AddSource(WorkerContextSemanticSource.TaskDescription, "task/description.md", ContextArtifactKind.RoleOutputContract, Encoding.UTF8.GetBytes(task.Description));
        AddSource(
            WorkerContextSemanticSource.TaskMetadata,
            "task/metadata.json",
            ContextArtifactKind.RoleOutputContract,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                GoalId = goal.Id.Value,
                GoalStatus = goal.Status.ToString(),
                TaskId = task.Id.Value,
                TaskRole = task.RequiredRole.ToString(),
                TaskStatus = task.Status.ToString()
            }));
        if (!string.IsNullOrWhiteSpace(task.VerificationPlan))
        {
            AddSource(WorkerContextSemanticSource.TaskVerificationPlan, "task/verification-plan.md", ContextArtifactKind.OperatorInstructions, Encoding.UTF8.GetBytes(task.VerificationPlan));
        }
        AddSource(
            WorkerContextSemanticSource.CriterionRetryFeedback,
            "task/criterion-retry-feedback.json",
            ContextArtifactKind.AcceptanceCriteria,
            JsonSerializer.SerializeToUtf8Bytes(task.CriterionRetryFeedback));
        if (goal.RefinedSpec is { } refinedSpec)
        {
            AddSource(
                WorkerContextSemanticSource.RefinedSpec,
                "goal/refined-spec.json",
                ContextArtifactKind.AcceptanceCriteria,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    refinedSpec.BehavioralContract,
                    refinedSpec.AcceptanceCriteria,
                    VerificationClass = refinedSpec.VerificationClass.ToString(),
                    refinedSpec.Decisions,
                    refinedSpec.OpenQuestions,
                    refinedSpec.OperatorOwnedAcceptanceCriteria,
                    ClarificationAnswerHistory = refinedSpec.AuthoritativeClarificationAnswerHistory.Select(answer => new
                    {
                        answer.Id,
                        answer.Text,
                        Origin = answer.Origin.ToString(),
                        answer.SupersededByAnswerId,
                        answer.BriefVersion
                    }).ToArray()
                }));
        }
        if (goal.EffectiveAcceptanceCriteriaCorrections.Count > 0)
        {
            AddSource(
                WorkerContextSemanticSource.EffectiveAcceptanceCriteriaCorrections,
                "goal/effective-acceptance-criteria-corrections.json",
                ContextArtifactKind.AcceptanceCriteria,
                JsonSerializer.SerializeToUtf8Bytes(goal.EffectiveAcceptanceCriteriaCorrections
                    .OrderBy(correction => correction.SupersededCriterion, StringComparer.Ordinal)
                    .ThenBy(correction => correction.Correction, StringComparer.Ordinal)
                    .ThenBy(correction => correction.Actor, StringComparer.Ordinal)
                    .Select(correction => new
                    {
                        correction.SupersededCriterion,
                        correction.Correction,
                        correction.Actor,
                        SourceTaskId = correction.SourceTaskId?.Value,
                        SourceKind = correction.SourceKind.ToString(),
                        correction.IsWaiver,
                        correction.CapturedAcceptanceCriteriaHash
                    }).ToArray()));
        }
        if (goal.RetainedAcceptanceFailure is { } acceptanceFailure)
        {
            AddSource(
                WorkerContextSemanticSource.LatestAcceptanceFailure,
                "goal/latest-acceptance-failure.json",
                ContextArtifactKind.AcceptanceCriteria,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    acceptanceFailure.FailedChecks,
                    acceptanceFailure.BranchHeadSha,
                    acceptanceFailure.MainHeadSha,
                    acceptanceFailure.CheckAttributions,
                    acceptanceFailure.BaselineAttestation
                }));
        }
        if (targetRole == AgentRole.Reviewer)
        {
            AddCompleteReviewerScopeArtifactWhenPreviewIsCapped(
                WorkerContextSemanticSource.ReviewerChangedFileScope,
                "reviewer/changed-files.v1.json",
                "git-diff-name-only-main-three-dot-head",
                reviewerScopeMergeBase,
                reviewerScopeChangedFiles,
                reviewerScopeTotalChangedFileCount,
                AddSource);
            if (reviewerMergeTreeClean is false)
            {
                AddCompleteReviewerScopeArtifactWhenPreviewIsCapped(
                    WorkerContextSemanticSource.ReviewerMergeConflictScope,
                    "reviewer/merge-conflict-paths.v1.json",
                    "git-merge-tree-write-tree-name-only-main-head",
                    null,
                    reviewerMergeTreeConflictPaths,
                    reviewerMergeTreeTotalConflictPathCount,
                    AddSource);
            }
        }
        var timeline = goal.Timeline.ToArray();
        AddSource(
            WorkerContextSemanticSource.Timeline,
            "goal/timeline.json",
            ContextArtifactKind.RegisteredContext,
            SerializeSemanticTimeline(timeline, workingDirectory, contextDirectory));
        var reviewFindingHistory = WorkerContextPackageBuilder.DistinctBySerializedValue(goal.Tasks
            .SelectMany(candidate => candidate.VerificationHistory.Select(verification => new
            {
                TaskId = candidate.Id.Value,
                Role = candidate.RequiredRole.ToString(),
                Findings = verification.MergedReviewFindings ?? [],
                EvidenceReceipts = verification.FindingEvidenceReceipts ?? []
            }))
            .Where(item => item.Findings.Count > 0 || item.EvidenceReceipts.Count > 0)
        );
        if (reviewFindingHistory.Length > 0)
        {
            AddSource(
                WorkerContextSemanticSource.ReviewFindingHistory,
                "goal/review-finding-history.json",
                ContextArtifactKind.AcceptanceCriteria,
                JsonSerializer.SerializeToUtf8Bytes(reviewFindingHistory));
        }

        if (task.LastExecution is not null)
        {
            var identity = new LogicalArtifactIdentity("task/last-model-output.txt");
            var output = task.LastExecution.AuthoritativeOutput
                ?? throw new WorkerContextPreparationException(
                    identity,
                    "authoritative-execution-output-unavailable",
                    "Complete model output is unavailable; the bounded execution preview is not authoritative evidence.");
            AddSource(WorkerContextSemanticSource.LastModelOutput, identity.Value, ContextArtifactKind.RegisteredContext, Encoding.UTF8.GetBytes(output));
        }

        if (task.LastDispatch is not null)
        {
            AddSource(
                WorkerContextSemanticSource.LastDispatch,
                "task/last-dispatch.json",
                ContextArtifactKind.RegisteredContext,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    task.LastDispatch.WorkerName,
                    task.LastDispatch.Command,
                    task.LastDispatch.WorkingDirectory,
                    task.LastDispatch.DispatchedAt,
                    task.LastDispatch.ProviderName,
                    task.LastDispatch.ModelName,
                    task.LastDispatch.ReasoningEffort,
                    task.LastDispatch.TaskComplexity,
                    task.LastDispatch.BaseCommit,
                    task.LastDispatch.ResultCommit
                }));
        }

        if (task.LastVerification is not null)
        {
            var currentIdentity = new LogicalArtifactIdentity("task/last-verification/stdout");
            var currentOutput = WorkerVerificationEvidence.ResolveStandardOutputForContext(task.LastVerification, currentIdentity);
            AddSource(WorkerContextSemanticSource.LastVerificationOutput, currentIdentity.Value, ContextArtifactKind.RegisteredContext, Encoding.UTF8.GetBytes(currentOutput.Content));
            if (task.LastVerification.AuthoritativeStandardError is { } currentError)
            {
                AddSource(WorkerContextSemanticSource.LastVerificationError, "task/last-verification/stderr", ContextArtifactKind.RegisteredContext, Encoding.UTF8.GetBytes(currentError));
            }
        }

        foreach (var priorTask in goal.Tasks.TakeWhile(candidate => candidate.Id != task.Id)
            .Where(candidate => candidate.LastVerification is not null))
        {
            var identity = new LogicalArtifactIdentity($"prior/{priorTask.Id.Value}/verification-output");
            var output = WorkerVerificationEvidence.ResolveStandardOutputForContext(priorTask.LastVerification!, identity);
            AddSource(WorkerContextSemanticSource.PriorTaskVerificationOutput, identity.Value, ContextArtifactKind.PriorTaskEvidence, Encoding.UTF8.GetBytes(output.Content));
        }

        foreach (var entry in registry.Artifacts.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            var source = ClassifyRegistryArtifact(entry.Path);
            if (source.Disposition != RegistryArtifactDisposition.Deliver)
            {
                continue;
            }

            var visibility = ParseRoleVisibility(entry.RoleVisibility);
            if (!visibility.Contains(targetRole))
            {
                continue;
            }

            RequireRegistryArtifactReady(entry.Path, entry.Exists, entry.HashVerified);

            var path = Path.Combine(contextDirectory, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var bytes = File.ReadAllBytes(path);
            var actualHash = WorkerContextArtifact.Hash(bytes);
            if (!actualHash.Equals(entry.Sha256, StringComparison.Ordinal))
            {
                throw new WorkerContextPreparationException(
                    new LogicalArtifactIdentity($"context/{entry.Path.Replace('\\', '/') }"),
                    "registry-hash-mismatch",
                    $"Registry expected {entry.Sha256}, found {actualHash}.");
            }

            AddSource(
                WorkerContextSemanticSource.RegistryArtifact,
                $"context/{entry.Path.Replace('\\', '/')}",
                source.Kind,
                bytes,
                visibility,
                source.DeliveryModeOverride);
        }

        var handoffPath = Path.Combine(workingDirectory, ".orchestrator-handoff.md");
        if (File.Exists(handoffPath))
        {
            var authoritativeByIdentity = goal.Tasks
                .TakeWhile(candidate => candidate.Id != task.Id)
                .Where(candidate => candidate.LastVerification is not null)
                .ToDictionary(
                    candidate => $"prior/{candidate.Id.Value}/verification-output",
                    candidate =>
                    {
                        var identity = new LogicalArtifactIdentity($"prior/{candidate.Id.Value}/verification-output");
                        var output = WorkerVerificationEvidence.ResolveStandardOutputForContext(candidate.LastVerification!, identity);
                        return Encoding.UTF8.GetBytes(output.Content);
                    },
                    StringComparer.Ordinal);
            var selfVerificationIdentity = $"prior/{task.Id.Value}/verification-output";
            var resolver = new LegacyHandoffCompatibilityResolver(
                identity => authoritativeByIdentity.TryGetValue(identity, out var bytes) ? bytes : null,
                workingDirectory,
                selfVerificationIdentity,
                File.ReadAllBytes);
            foreach (var recovered in resolver.ResolveArtifactsFromMarkdown(File.ReadAllText(handoffPath)))
            {
                var existing = artifacts.FirstOrDefault(artifact =>
                    artifact.Identity.Value.Equals(recovered.LogicalIdentity, StringComparison.Ordinal));
                if (existing is not null)
                {
                    if (!existing.ContentHash.Equals(WorkerContextArtifact.Hash(recovered.Bytes), StringComparison.Ordinal))
                    {
                        throw new WorkerContextPreparationException(
                            existing.Identity,
                            "legacy-alias-hash-mismatch",
                            "The compatibility representation does not match the typed authoritative artifact.");
                    }

                    continue;
                }

                AddSource(WorkerContextSemanticSource.LegacyHandoffArtifact, recovered.LogicalIdentity, ContextArtifactKind.PriorTaskEvidence, recovered.Bytes);
            }
        }

        var headerResidual = ExtractCanonicalHeaderResidual(brief.Content);
        if (!string.IsNullOrWhiteSpace(headerResidual))
        {
            AddSource(WorkerContextSemanticSource.HeaderResidual, "brief/header-residual.md", ContextArtifactKind.OperatorInstructions, Encoding.UTF8.GetBytes(headerResidual));
        }

        var residualBrief = RemoveTypedSourceProjections(brief.Content, targetRole);
        if (targetRole == AgentRole.Reviewer)
        {
            residualBrief = RemoveLargeReviewerScopeInlinePreviews(
                residualBrief,
                reviewerScopeTotalChangedFileCount > WorkerGitContext.ReviewerChangedFilePromptMaxFiles,
                reviewerMergeTreeTotalConflictPathCount > WorkerGitContext.ReviewerChangedFilePromptMaxFiles);
        }
        AddSource(WorkerContextSemanticSource.CurrentBrief, "brief/current.md", ContextArtifactKind.OperatorInstructions, Encoding.UTF8.GetBytes(residualBrief));

        var builder = new WorkerContextPackageBuilder();
        var preparedWithoutManifest = builder.Prepare(targetRole, workingDirectory, artifacts);
        return FinalizeContextPackageWithManifest(builder, preparedWithoutManifest, observedSources);
    }

    private static void AddCompleteReviewerScopeArtifactWhenPreviewIsCapped(
        WorkerContextSemanticSource source,
        string logicalIdentity,
        string sourceCommand,
        string? baseline,
        IReadOnlyList<string>? paths,
        int? totalPathCount,
        Action<WorkerContextSemanticSource, string, ContextArtifactKind, byte[], IReadOnlyList<AgentRole>?, ContextDeliveryMode?> addSource)
    {
        var normalizedPaths = (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim().Replace('\\', '/').Normalize(NormalizationForm.FormC))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var total = totalPathCount ?? normalizedPaths.Length;
        var identity = new LogicalArtifactIdentity(logicalIdentity);
        if (normalizedPaths.Length != total)
        {
            throw new WorkerContextPreparationException(
                identity,
                "reviewer-scope-incomplete",
                $"Expected {total} complete path entries but received {normalizedPaths.Length}; a capped preview cannot be used as authoritative scope.");
        }
        if (total <= WorkerGitContext.ReviewerChangedFilePromptMaxFiles)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewerPathScopeDocument(
            ContextContractVersion.V1.Value,
            sourceCommand,
            string.IsNullOrWhiteSpace(baseline) ? null : baseline.Trim(),
            total,
            normalizedPaths));
        addSource(
            source,
            identity.Value,
            ContextArtifactKind.RegisteredContext,
            bytes,
            [AgentRole.Reviewer],
            ContextDeliveryMode.MandatoryFile);
    }

    internal static WorkerContextPackage FinalizeContextPackageWithManifest(
        WorkerContextPackageBuilder builder,
        WorkerContextPackage preparedWithoutManifest,
        ICollection<SemanticSourceObservation>? observedSources = null)
    {
        var inventory = preparedWithoutManifest.Artifacts.Select(artifact => new ContextArtifactInventoryEntry(
            artifact.Identity.Value,
            artifact.ContentHash,
            artifact.RoleVisibility.Select(role => role.ToString()).ToArray(),
            artifact.DeliveryMode.ToString(),
            artifact.ContractVersion.Value)).ToArray();
        var authoritativeInventoryBytes = JsonSerializer.SerializeToUtf8Bytes(new ContextArtifactInventoryDocument(
            ContextContractVersion.V1.Value,
            ["artifact-registry.json", "context-package.json", "manifest.md"],
            inventory));
        var manifestArtifact = WorkerContextArtifact.Create(
            new LogicalArtifactIdentity("context/manifest.v1.json"),
            ContextArtifactKind.ContextManifest,
            authoritativeInventoryBytes,
            [preparedWithoutManifest.TargetRole],
            ContextDeliveryMode.InlineFull,
            ContextContractVersion.V1);

        observedSources?.Add(new SemanticSourceObservation(
            WorkerContextSemanticSource.ContextManifest,
            manifestArtifact.Identity.Value));
        return builder.AppendFinalizedInlineArtifact(preparedWithoutManifest, manifestArtifact);
    }

    internal static string RemoveTypedSourceProjections(string content, AgentRole targetRole)
    {
        ArgumentNullException.ThrowIfNull(content);
        _ = targetRole;

        var instructions = FindBriefHeading(content, "## Instructions", 0);
        if (instructions < 0)
        {
            throw new InvalidOperationException("Typed context brief is missing its Instructions source boundary.");
        }

        var residual = content[instructions..];
        return WorkerContextProjectionResidual.RemoveProjectionBlocks(residual).Trim();
    }

    internal static string RemoveLargeReviewerScopeInlinePreviews(
        string content,
        bool removeChangedPaths,
        bool removeConflictPaths)
    {
        if (!removeChangedPaths && !removeConflictPaths)
        {
            return content;
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>(lines.Length);
        var inChangedFileScope = false;
        var inChangedPathList = false;
        var inConflictPathList = false;
        var inConvergenceChangedFiles = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inChangedFileScope = line.Equals("## Reviewer Changed-File Scope", StringComparison.Ordinal);
                inChangedPathList = false;
                inConflictPathList = false;
                inConvergenceChangedFiles = false;
            }

            if (inChangedFileScope)
            {
                if (removeConflictPaths && line.StartsWith("Conflicting paths:", StringComparison.Ordinal))
                {
                    output.Add(ReplaceShowingCount(line));
                    inConflictPathList = true;
                    continue;
                }
                if (line.StartsWith("Staleness policy:", StringComparison.Ordinal))
                {
                    inConflictPathList = false;
                    inChangedPathList = removeChangedPaths;
                }
                if (line.StartsWith("Independent scope checks", StringComparison.Ordinal))
                {
                    inChangedPathList = false;
                }
                if (removeChangedPaths && line.StartsWith("Changed files:", StringComparison.Ordinal))
                {
                    output.Add(ReplaceShowingCount(line));
                    continue;
                }
                if ((inConflictPathList &&
                     (line.StartsWith("- conflict: ", StringComparison.Ordinal) ||
                      line.Contains("additional conflict path", StringComparison.Ordinal))) ||
                    (inChangedPathList && line.StartsWith("- ", StringComparison.Ordinal)))
                {
                    continue;
                }
            }

            if (removeChangedPaths &&
                line.StartsWith("GOAL_DIFF_CHANGED_FILES ", StringComparison.Ordinal))
            {
                inConvergenceChangedFiles = true;
                output.Add(line);
                continue;
            }
            if (inConvergenceChangedFiles && line.StartsWith("Actively check ", StringComparison.Ordinal))
            {
                inConvergenceChangedFiles = false;
            }
            if (inConvergenceChangedFiles && line.StartsWith("- ", StringComparison.Ordinal))
            {
                continue;
            }

            output.Add(line);
        }

        return string.Join(Environment.NewLine, output).Trim();
    }

    private static string ReplaceShowingCount(string line)
    {
        var separator = line.IndexOf(';');
        return separator < 0
            ? line
            : line[..separator] + "; complete path list is delivered only by its typed MandatoryFile artifact.";
    }

    private static string RemoveMarkedBriefBlock(string content, string startMarker, string endMarker)
    {
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return content;
        }

        var end = content.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"Typed context brief block '{startMarker}' has no closing marker '{endMarker}'.");
        }

        end += endMarker.Length;
        while (end < content.Length && (content[end] == '\r' || content[end] == '\n'))
        {
            end++;
        }

        return content.Remove(start, end - start);
    }

    private static string RemoveBriefSection(string content, string startHeading, string? endHeading)
    {
        var start = FindBriefHeading(content, startHeading, startIndex: 0);
        if (start < 0)
        {
            return content;
        }

        var end = endHeading is null
            ? content.Length
            : FindBriefHeading(content, endHeading, start + startHeading.Length);
        if (end < 0)
        {
            throw new InvalidOperationException($"Typed context brief section '{startHeading}' has no expected boundary '{endHeading}'.");
        }

        return content.Remove(start, end - start);
    }

    private static int FindBriefHeading(string content, string heading, int startIndex)
    {
        var candidate = content.IndexOf(heading, startIndex, StringComparison.Ordinal);
        while (candidate >= 0)
        {
            if (candidate == 0 || content[candidate - 1] == '\n')
            {
                return candidate;
            }

            candidate = content.IndexOf(heading, candidate + heading.Length, StringComparison.Ordinal);
        }

        return -1;
    }

    private static string RequireAuthoritativeOutput(
        TaskVerificationRecord verification,
        LogicalArtifactIdentity identity) =>
        WorkerVerificationEvidence.RequireAuthoritativeStandardOutput(verification, identity);

    private static RegistryArtifactSource ClassifyRegistryArtifact(string path) =>
        RegistryArtifactSources.TryGetValue(path, out var source)
            ? source
            : new RegistryArtifactSource(
                ContextArtifactKind.RegisteredContext,
                RegistryArtifactDisposition.Deliver,
                ContextDeliveryMode.InlineFull);

    private static byte[] ApplyHumanInputRetractions(
        byte[] bytes,
        IReadOnlyList<HumanInputRequest> requests,
        IReadOnlyList<HumanInputAnswerRecord> clarificationAnswerHistory)
    {
        string content;
        try
        {
            content = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return bytes;
        }

        var filtered = HumanInputRetractionPolicy.Apply(content, requests, clarificationAnswerHistory);
        return string.Equals(content, filtered, StringComparison.Ordinal)
            ? bytes
            : Encoding.UTF8.GetBytes(filtered);
    }

    internal static void RequireRegistryArtifactReady(string logicalPath, bool exists, bool hashVerified)
    {
        var identity = new LogicalArtifactIdentity($"context/{logicalPath.Replace('\\', '/')}");
        if (!exists)
        {
            throw new WorkerContextPreparationException(
                identity,
                "registry-artifact-missing",
                "A required registry artifact is marked missing; dispatch cannot omit it.");
        }

        if (!hashVerified)
        {
            throw new WorkerContextPreparationException(
                identity,
                "registry-artifact-hash-unverified",
                "A required registry artifact is not hash-verified; dispatch cannot omit it.");
        }
    }

    internal static string ExtractCanonicalHeaderResidual(string content)
    {
        var instructions = FindBriefHeading(content, "## Instructions", 0);
        if (instructions < 0)
        {
            throw new InvalidOperationException("Typed context brief is missing its Instructions source boundary.");
        }

        var residual = WorkerContextProjectionResidual.RestoreLiterals(content[..instructions]);
        residual = RemoveMarkedBriefBlock(
            residual,
            "<!-- ACCUMULATED_RETRY_FEEDBACK_START -->",
            "<!-- ACCUMULATED_RETRY_FEEDBACK_END -->");
        residual = RemoveMarkedBriefBlock(
            residual,
            "<!-- EFFECTIVE_ACCEPTANCE_CRITERIA_CORRECTIONS_START -->",
            "<!-- EFFECTIVE_ACCEPTANCE_CRITERIA_CORRECTIONS_END -->");
        residual = RemoveMarkedBriefBlock(
            residual,
            "<!-- ACCEPTANCE_FAILURE_START -->",
            "<!-- ACCEPTANCE_FAILURE_END -->");
        residual = RemoveLineRange(residual, "Goal: ", "Goal id: ");
        residual = RemoveLineRange(residual, "Task: ", "Task role: ");
        foreach (var prefix in new[]
        {
            "# Agent Task Brief",
            "Goal id: ",
            "Goal status: ",
            "Task role: ",
            "Task status: ",
            "Task id: ",
            "Working directory, use absolute paths: ",
            "Context files: read "
        })
        {
            residual = RemoveLineWithPrefix(residual, prefix);
        }

        return residual.Trim();
    }

    private static string RemoveLineRange(string content, string startPrefix, string endPrefix)
    {
        var start = FindLineWithPrefix(content, startPrefix, 0);
        if (start < 0)
        {
            return content;
        }

        var end = FindLineWithPrefix(content, endPrefix, start + startPrefix.Length);
        if (end < 0)
        {
            throw new InvalidOperationException($"Typed context brief source '{startPrefix}' has no boundary '{endPrefix}'.");
        }

        return content.Remove(start, end - start);
    }

    private static string RemoveLineWithPrefix(string content, string prefix)
    {
        var start = FindLineWithPrefix(content, prefix, 0);
        if (start < 0)
        {
            return content;
        }

        var end = content.IndexOf('\n', start);
        return content.Remove(start, end < 0 ? content.Length - start : end + 1 - start);
    }

    private static int FindLineWithPrefix(string content, string prefix, int startIndex)
    {
        var candidate = content.IndexOf(prefix, startIndex, StringComparison.Ordinal);
        while (candidate >= 0)
        {
            if (candidate == 0 || content[candidate - 1] == '\n')
            {
                return candidate;
            }

            candidate = content.IndexOf(prefix, candidate + prefix.Length, StringComparison.Ordinal);
        }

        return -1;
    }

    internal static byte[] SerializeSemanticTimeline(
        IEnumerable<ProgressEvent> timeline,
        string workingDirectory,
        string contextDirectory) =>
        // Goal state retains the operational timestamps and original paths for audit. The package's
        // authoritative timeline bytes retain causal order/content while canonicalizing those fields.
        JsonSerializer.SerializeToUtf8Bytes(timeline.Select(evt => new
        {
            TaskId = evt.TaskId?.Value,
            Kind = evt.Kind.ToString(),
            Message = NormalizeSemanticTimelineText(evt.Message, workingDirectory, contextDirectory),
            evt.RequeueSkipped,
            OperatorGates = evt.OperatorGates?.Select(gate => new
            {
                gate.DeliverableId,
                gate.SourceRecordId,
                SatisfactionEvidence = NormalizeSemanticTimelineText(
                    gate.SatisfactionEvidence,
                    workingDirectory,
                    contextDirectory)
            }).ToArray()
        }));

    private static string? NormalizeSemanticTimelineText(
        string? value,
        string workingDirectory,
        string contextDirectory)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Replace('\\', '/');
        var roots = new[]
        {
            (Path: contextDirectory, Token: "${context-root}"),
            (Path: workingDirectory, Token: "${workspace-root}")
        }
            .Select(root => (Path: root.Path.Replace('\\', '/').TrimEnd('/'), root.Token))
            .Where(root => root.Path.Length > 0)
            .OrderByDescending(root => root.Path.Length);
        foreach (var root in roots)
        {
            normalized = normalized.Replace(root.Path, root.Token, StringComparison.OrdinalIgnoreCase);
        }

        return normalized;
    }

    private static AgentRole[] ParseRoleVisibility(IReadOnlyList<string> values) => values
        .Select(value => Enum.TryParse<AgentRole>(value, ignoreCase: true, out var role)
            ? role
            : throw new InvalidOperationException($"Artifact registry contains unknown role visibility '{value}'."))
        .ToArray();

    private sealed record ContextArtifactRegistryDocument(IReadOnlyList<ContextArtifactRegistryItem> Artifacts);
    private sealed record ContextArtifactRegistryItem(
        string Path,
        bool Exists,
        string Sha256,
        IReadOnlyList<string> RoleVisibility,
        bool HashVerified);
    private sealed record ContextArtifactInventoryEntry(
        string LogicalIdentity,
        string Sha256,
        IReadOnlyList<string> RoleVisibility,
        string DeliveryMode,
        int ContractVersion);
    private sealed record ContextArtifactInventoryDocument(
        int ContractVersion,
        IReadOnlyList<string> CompatibilityAliases,
        IReadOnlyList<ContextArtifactInventoryEntry> Artifacts);
    private sealed record RegistryArtifactSource(
        ContextArtifactKind Kind,
        RegistryArtifactDisposition Disposition,
        ContextDeliveryMode? DeliveryModeOverride = null);
    internal sealed record SemanticSourceObservation(
        WorkerContextSemanticSource Source,
        string LogicalIdentity);
    internal enum WorkerContextSemanticSource
    {
        GoalObjective,
        TaskDescription,
        TaskMetadata,
        TaskVerificationPlan,
        CriterionRetryFeedback,
        RefinedSpec,
        EffectiveAcceptanceCriteriaCorrections,
        LatestAcceptanceFailure,
        ReviewerChangedFileScope,
        ReviewerMergeConflictScope,
        Timeline,
        ReviewFindingHistory,
        LastModelOutput,
        LastDispatch,
        LastVerificationOutput,
        LastVerificationError,
        PriorTaskVerificationOutput,
        RegistryArtifact,
        LegacyHandoffArtifact,
        HeaderResidual,
        CurrentBrief,
        ContextManifest
    }
    private sealed record ReviewerPathScopeDocument(
        int ContractVersion,
        string SourceCommand,
        string? Baseline,
        int TotalPathCount,
        IReadOnlyList<string> Paths);
    private enum RegistryArtifactDisposition
    {
        Deliver,
        DomainProjection,
        CanonicalManifestAlias
    }

    public static Dictionary<string, string?> BuildDispatchVariables(
        AgentRole role,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? variables,
        WorkerSandboxOptions? sandboxOptions = null,
        string? providerName = null)
    {
        var isWriteCapable = role == AgentRole.Developer || role == AgentRole.Tester;
        // When the OS worker sandbox is active, Codex's nested sandbox is disabled so it does not run
        // the expensive Windows sandbox setup helper. MIC remains the enforcement boundary: file roles
        // receive a Low writable worktree, while read-only Codex roles keep the worktree Medium.
        var osSandbox = (sandboxOptions ?? WorkerSandboxOptions.FromEnvironment()).Enabled;
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["workingDirectory"] = workingDirectory,
            ["sandboxMode"] = osSandbox
                ? "danger-full-access"
                : (isWriteCapable ? "workspace-write" : "read-only"),
            ["permissionMode"] = isWriteCapable ? "bypassPermissions" : "plan",
            ["approvalMode"] = isWriteCapable ? "yolo" : "plan",
            ["openaiBaseUrl"] = OpenAiCompatibleCliBackend.ResolveBaseUrl(providerName),
            ["openaiApiKey"] = OpenAiCompatibleCliBackend.ResolveApiKey(providerName)
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
                GetDispatchVariable(dispatchVariables, "workingDirectory"),
                openaiBaseUrl: GetDispatchVariable(dispatchVariables, "openaiBaseUrl"),
                openaiApiKey: GetDispatchVariable(dispatchVariables, "openaiApiKey"),
                approvalMode: GetDispatchVariable(dispatchVariables, "approvalMode")));
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
                HasKeys(variables, "subscriptionModelName", "sandboxMode", "workingDirectory"),
            ProviderKind.AnthropicClaudeCli =>
                HasKeys(variables, "subscriptionModelName", "permissionMode"),
            ProviderKind.OllamaQwenCodeCli =>
                HasKeys(variables, "subscriptionModelName", "workingDirectory", "openaiBaseUrl", "openaiApiKey", "approvalMode"),
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
