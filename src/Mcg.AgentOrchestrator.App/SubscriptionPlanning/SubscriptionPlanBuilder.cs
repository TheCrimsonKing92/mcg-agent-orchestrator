using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.SubscriptionPlanning;

internal sealed record SubscriptionPlan(
    string GoalId,
    string Objective,
    GoalStatus Status,
    int ReadyToPrepareCount,
    int ResolvableProfileCount,
    int RetryDeferredCount,
    DateTimeOffset? NextSubscriptionRetryAfter,
    string? ReadyStartCostRisk,
    int? ReadyStartPromptCharacterCount,
    IReadOnlyList<string> ReadyStartCostRiskDetails,
    string? ReadyStartCostRecommendation,
    ProviderCapacitySchedule CapacitySchedule,
    IReadOnlyList<SubscriptionPlanModelSummary> ReadyModelUsage,
    IReadOnlyList<SubscriptionProviderBudgetSummary> ProviderBudgets,
    IReadOnlyList<SubscriptionPlanItem> Items);

internal sealed record SubscriptionProviderBudgetSummary(
    string ProviderName,
    int TaskCount,
    int ReadyCount,
    int DeferredCount,
    int RecoverableLimitFailureCount,
    bool IsCoolingDown,
    DateTimeOffset? RetryAfter,
    int? RetryDelaySeconds,
    int? SourceTaskNumber,
    string Detail);

internal enum ProviderCapacityDisposition
{
    Ready,
    Deferred,
    Blocked,
    Review
}

internal sealed record ProviderCapacitySchedule(
    ProviderCapacityDisposition Disposition,
    string Recommendation,
    int ReadyNowCount,
    int DeferredCount,
    DateTimeOffset? NextRetryAfter,
    bool HasCostRisk,
    IReadOnlyList<ProviderCapacityAction> Actions);

internal sealed record ProviderCapacityAction(
    int TaskNumber,
    string TaskId,
    string? ProviderName,
    ProviderCapacityDisposition Disposition,
    DateTimeOffset? RetryAfter,
    string Recommendation,
    IReadOnlyList<string> Alternatives);

internal enum WorkerRouteDisposition
{
    Selected,
    Deferred,
    Blocked
}

internal sealed record WorkerRouteDecision(
    WorkerRouteDisposition Disposition,
    string Recommendation,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Alternatives);

internal sealed record SubscriptionPlanModelSummary(
    string ProviderName,
    string ModelName,
    int ReadyCount,
    TaskComplexity? TaskComplexity = null,
    string? ReasoningEffort = null,
    bool IsPotentiallyPaidProvider = false,
    int? EstimatedPromptCharacterCount = null,
    int PreviousModelFitNoteCount = 0,
    int PreviousAdequateCount = 0,
    int PreviousOverkillCount = 0,
    int PreviousUnderpoweredCount = 0,
    int PreviousUnknownFitCount = 0,
    IReadOnlyList<string>? PreviousTaskShapes = null,
    string? ModelFitRecommendation = null,
    bool UsesComplexModel = false);

internal sealed record SubscriptionPlanItem(
    int TaskNumber,
    string TaskId,
    AgentRole Role,
    WorkTaskStatus TaskStatus,
    string Description,
    string? AgentId,
    string? AgentName,
    string? ProviderName,
    string? ModelName,
    AgentExecutionPolicy? ExecutionPolicy,
    string? ProfileName,
    string? SubscriptionModelAlias,
    bool ProfileExists,
    bool ProfileIsResolvable,
    bool ProfileIsEchoOnly,
    bool ProfileIsPatchCapable,
    bool CanPrepare,
    string Detail,
    DateTimeOffset? RetryAfter = null,
    int? RetryDelaySeconds = null,
    TaskComplexity? TaskComplexity = null,
    string? SubscriptionModelName = null,
    string? SubscriptionReasoningEffort = null,
    int? EstimatedPromptCharacterCount = null,
    int RecoverableSubscriptionLimitFailureCount = 0,
    bool UsesComplexModel = false,
    int? CostGuardPromptCharacterCount = null,
    int? TaskBriefCharacterBudget = null,
    int? TaskBriefHeadroom = null,
    WorkerRouteDecision? Route = null);

internal static class SubscriptionPlanBuilder
{
    public static SubscriptionPlan Build(
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        Func<TaskSpec, int?>? estimatePromptCharacterCount = null,
        IReadOnlyList<ModelOutcomeRecord>? scorecard = null)
    {
        var validations = OrchestratorHealthInspector
            .InspectCurrentEnvironment(new AgentCatalog(agents), profiles)
            .WorkerProfiles
            .ToDictionary(profile => profile.Name, StringComparer.OrdinalIgnoreCase);

        var scorecardLookup = scorecard?.ToDictionary(
            r => $"{r.ProviderName}/{r.ModelName}",
            StringComparer.OrdinalIgnoreCase);

        var items = goal.Tasks
            .Select(task => BuildItem(goal, task, agents, profiles, validations, estimatePromptCharacterCount, scorecardLookup))
            .ToList();
        var readyModelUsage = BuildModelSummary(goal, items);
        var providerBudgets = BuildProviderBudgetSummary(goal, items);
        var readyStartRisk = SubscriptionPromptCostGuard.EvaluateReadySubscriptionStart(items, readyModelUsage);
        var capacitySchedule = ProviderCapacityScheduler.Build(items, providerBudgets, readyStartRisk);

        return new SubscriptionPlan(
            goal.Id.Value,
            OutputTextPreview.CreateSummary(goal.Objective).Text,
            goal.Status,
            items.Count(item => item.CanPrepare),
            items.Count(item => item.ProfileName is not null && item.ProfileIsResolvable && !item.ProfileIsEchoOnly && item.ProfileIsPatchCapable),
            items.Count(item => item.RetryDelaySeconds is > 0),
            items
                .Where(item => item.RetryDelaySeconds is > 0)
                .Select(item => item.RetryAfter)
                .Where(item => item is not null)
                .OrderBy(item => item)
                .FirstOrDefault(),
            readyStartRisk is null ? null : SubscriptionPromptCostGuard.BuildInlineLabel(readyStartRisk),
            readyStartRisk?.PromptCharacterCount,
            readyStartRisk?.Details ?? [],
            readyStartRisk is null ? null : SubscriptionPromptCostGuard.BuildRecommendation(readyStartRisk),
            capacitySchedule,
            readyModelUsage,
            providerBudgets,
            items);
    }

    public static SubscriptionPlanItem BuildItem(
        Goal goal,
        TaskSpec task,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        IReadOnlyDictionary<string, WorkerProfileValidation> validations,
        Func<TaskSpec, int?>? estimatePromptCharacterCount = null,
        IReadOnlyDictionary<string, ModelOutcomeRecord>? scorecardLookup = null)
    {
        var taskNumber = TaskDisplayNumber.Resolve(goal, task.Id);
        if (task.AssignedAgentId is null)
        {
            return new SubscriptionPlanItem(
                taskNumber,
                task.Id.Value,
                task.RequiredRole,
                task.Status,
                OutputTextPreview.CreateSummary(task.Description).Text,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                false,
                false,
                false,
                "Task is not assigned to an agent.",
                Route: new WorkerRouteDecision(
                    WorkerRouteDisposition.Blocked,
                    "Assign the task to a subscription-capable agent before dispatch.",
                    ["task has no assigned agent"],
                    ["Run delegate or re-delegate before subscription dispatch."]));
        }

        var agent = agents.FirstOrDefault(candidate => candidate.Id == task.AssignedAgentId);
        if (agent is null)
        {
            return new SubscriptionPlanItem(
                taskNumber,
                task.Id.Value,
                task.RequiredRole,
                task.Status,
                OutputTextPreview.CreateSummary(task.Description).Text,
                task.AssignedAgentId.Value,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                false,
                false,
                false,
                $"Assigned agent '{task.AssignedAgentId.Value}' was not found in the agent catalog.",
                Route: new WorkerRouteDecision(
                    WorkerRouteDisposition.Blocked,
                    "Restore the assigned agent or re-delegate the task before dispatch.",
                    [$"assigned agent '{task.AssignedAgentId.Value}' missing from catalog"],
                    ["Run delegate or add the missing agent profile."]));
        }

        var templateVariables = WorkerProfileDispatcher.BuildSubscriptionTemplateVariables(agent, goal, task);
        var effectiveProviderName = GetTemplateValue(templateVariables, "providerName") ?? agent.Model.ProviderName;
        var effectiveModelName = GetTemplateValue(templateVariables, "apiModelName") ?? agent.Model.ModelName;
        var usesComplexModel = UsesComplexModel(agent, effectiveProviderName, effectiveModelName);
        var subscriptionModelName = GetTemplateValue(templateVariables, "subscriptionModelName");
        var subscriptionReasoningEffort = GetTemplateValue(templateVariables, "subscriptionReasoningEffort");
        var taskComplexity = TryParseTaskComplexity(GetTemplateValue(templateVariables, "taskComplexity"));

        var scorecardKey = $"{effectiveProviderName}/{subscriptionModelName ?? effectiveModelName}";
        var scorecardRecord = scorecardLookup is not null && scorecardLookup.TryGetValue(scorecardKey, out var rec) ? rec : null;

        try
        {
            var profileName = WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent, goal, task);
            var profile = profiles.Profiles.FirstOrDefault(candidate => candidate.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
            validations.TryGetValue(profileName, out var validation);
            var hasProfile = profile is not null;
            var isEchoOnly = profile is not null && WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate);
            var pinsSelectedModel = profile is not null &&
                WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate);
            var pinsSelectedReasoning = !RequiresSubscriptionReasoningPlaceholder(effectiveProviderName, subscriptionReasoningEffort) ||
                (profile is not null && WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(profile.CommandTemplate));
            var patchCapability = profile is null
                ? new WorkerProfilePatchCapability(false, "Worker profile was not found.")
                : WorkerProfileDiagnostics.EvaluatePatchCapability(profile.CommandTemplate);
            var requiresPatchCapability = task.RequiredRole == AgentRole.Developer;
            var now = DateTimeOffset.UtcNow;
            var retryDeferred = DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, now, out var retryAfter);
            var providerCoolingDown = DispatchFailureClassifier.TryGetProviderSubscriptionCooldown(
                goal,
                task.Id,
                effectiveProviderName,
                now,
                out var providerCooldown);
            var recoverableLimitFailures = DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures(task);
            var requiresLimitReview = !retryDeferred && DispatchFailureClassifier.RequiresSubscriptionLimitReview(task);
            var retryDelaySeconds = retryDeferred
                ? Math.Max(0, (int)Math.Ceiling((retryAfter - now).TotalSeconds))
                : providerCoolingDown
                    ? Math.Max(0, (int)Math.Ceiling((providerCooldown.RetryAfter - now).TotalSeconds))
                : (int?)null;
            var canPrepare = task.Status == WorkTaskStatus.Assigned &&
                hasProfile &&
                !isEchoOnly &&
                pinsSelectedModel &&
                pinsSelectedReasoning &&
                (!requiresPatchCapability || patchCapability.IsPatchCapable) &&
                AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy) &&
                !retryDeferred &&
                !providerCoolingDown &&
                !requiresLimitReview;
            var estimatedPromptCharacterCount = canPrepare
                ? estimatePromptCharacterCount?.Invoke(task)
                : null;
            var taskBriefCharacterBudget = canPrepare && estimatedPromptCharacterCount is not null
                ? AgentOrchestratorKernel.TaskBriefCharacterBudget(task.RequiredRole, usesFileAccessContext: true)
                : (int?)null;
            var taskBriefHeadroom = taskBriefCharacterBudget is null || estimatedPromptCharacterCount is null
                ? null
                : (int?)(taskBriefCharacterBudget.Value - estimatedPromptCharacterCount.Value);
            int? costGuardPromptCharacterCount = estimatedPromptCharacterCount is null
                ? null
                : PaidPromptThresholds.EffectivePromptCharacterCount(
                    estimatedPromptCharacterCount.Value,
                    AgentOrchestratorKernel.EstimatePriorTaskEvidenceCharacterCount(goal, task.Id));
            var previousLimitFailures = recoverableLimitFailures == 1
                ? "1 previous recoverable subscription usage limit failure"
                : $"{recoverableLimitFailures} previous recoverable subscription usage limit failures";
            var detail = canPrepare
                ? recoverableLimitFailures > 0
                    ? $"Ready to prepare subscription dispatch after {previousLimitFailures}; inspect model, profile, or timing before redispatch."
                    : "Ready to prepare subscription dispatch."
                : !AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy)
                    ? $"Agent execution policy is {agent.ExecutionPolicy}; subscription dispatch is disabled."
                : retryDeferred
                    ? $"Recoverable subscription usage limit ({previousLimitFailures}); retry after {retryAfter:u}."
                : providerCoolingDown
                    ? $"Provider {providerCooldown.ProviderName} is cooling down after a recoverable subscription usage limit on task {TaskDisplayNumber.Resolve(goal, providerCooldown.SourceTaskId)}; retry after {providerCooldown.RetryAfter:u}."
                : requiresLimitReview
                    ? $"Repeated recoverable subscription usage limit ({previousLimitFailures}); inspect model, profile, or timing before redispatch."
                : !hasProfile
                    ? $"Worker profile '{profileName}' was not found."
                : isEchoOnly
                    ? $"Worker profile '{profileName}' only echoes the prompt path; configure a real launcher before subscription dispatch."
                : !pinsSelectedModel
                    ? $"Worker profile '{profileName}' does not include {{subscriptionModelName}}; pin the selected model before subscription dispatch."
                : !pinsSelectedReasoning
                    ? $"Worker profile '{profileName}' does not include {{subscriptionReasoningEffort}}; pin the selected reasoning effort before subscription dispatch."
                : requiresPatchCapability && !patchCapability.IsPatchCapable
                    ? $"Worker profile '{profileName}' is not patch-capable for Developer tasks: {patchCapability.Detail}"
                    : $"Task status is {task.Status}; only assigned tasks are ready for subscription dispatch.";
            var route = BuildRouteDecision(
                task,
                agent,
                profileName,
                effectiveProviderName,
                subscriptionModelName ?? agent.Subscription?.ModelAlias ?? effectiveModelName,
                taskComplexity,
                usesComplexModel,
                canPrepare,
                AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy),
                hasProfile,
                isEchoOnly,
                pinsSelectedModel,
                pinsSelectedReasoning,
                requiresPatchCapability,
                patchCapability,
                retryDeferred,
                providerCoolingDown,
                requiresLimitReview,
                recoverableLimitFailures,
                costGuardPromptCharacterCount,
                taskBriefHeadroom,
                detail,
                scorecardRecord);

            return new SubscriptionPlanItem(
                taskNumber,
                task.Id.Value,
                task.RequiredRole,
                task.Status,
                OutputTextPreview.CreateSummary(task.Description).Text,
                agent.Id.Value,
                agent.Name,
                effectiveProviderName,
                effectiveModelName,
                agent.ExecutionPolicy,
                profileName,
                agent.Subscription?.ModelAlias,
                hasProfile,
                validation?.IsResolvable ?? false,
                isEchoOnly,
                patchCapability.IsPatchCapable,
                canPrepare,
                OutputTextPreview.CreateTimeline(detail).Text,
                retryDeferred ? retryAfter : providerCoolingDown ? providerCooldown.RetryAfter : null,
                retryDelaySeconds,
                taskComplexity,
                subscriptionModelName,
                subscriptionReasoningEffort,
                estimatedPromptCharacterCount,
                recoverableLimitFailures,
                usesComplexModel,
                costGuardPromptCharacterCount,
                taskBriefCharacterBudget,
                taskBriefHeadroom,
                route);
        }
        catch (InvalidOperationException ex)
        {
            var route = new WorkerRouteDecision(
                WorkerRouteDisposition.Blocked,
                "Fix assignment, profile, or provider capability before subscription dispatch.",
                [OutputTextPreview.CreateTimeline(ex.Message).Text],
                []);
            return new SubscriptionPlanItem(
                taskNumber,
                task.Id.Value,
                task.RequiredRole,
                task.Status,
                OutputTextPreview.CreateSummary(task.Description).Text,
                agent.Id.Value,
                agent.Name,
                effectiveProviderName,
                effectiveModelName,
                agent.ExecutionPolicy,
                null,
                agent.Subscription?.ModelAlias,
                false,
                false,
                false,
                false,
                false,
                OutputTextPreview.CreateTimeline(ex.Message).Text,
                TaskComplexity: taskComplexity,
                SubscriptionModelName: subscriptionModelName,
                SubscriptionReasoningEffort: subscriptionReasoningEffort,
                UsesComplexModel: usesComplexModel,
                Route: route);
        }
    }

    private static WorkerRouteDecision BuildRouteDecision(
        TaskSpec task,
        AgentDefinition agent,
        string profileName,
        string providerName,
        string? subscriptionModelName,
        TaskComplexity? taskComplexity,
        bool usesComplexModel,
        bool canPrepare,
        bool subscriptionAllowed,
        bool hasProfile,
        bool isEchoOnly,
        bool pinsSelectedModel,
        bool pinsSelectedReasoning,
        bool requiresPatchCapability,
        WorkerProfilePatchCapability patchCapability,
        bool retryDeferred,
        bool providerCoolingDown,
        bool requiresLimitReview,
        int recoverableLimitFailures,
        int? costGuardPromptCharacterCount,
        int? taskBriefHeadroom,
        string detail,
        ModelOutcomeRecord? scorecardRecord = null)
    {
        var reasons = new List<string>
        {
            $"role={task.RequiredRole}",
            $"provider={providerName}",
            $"model={subscriptionModelName ?? agent.Model.ModelName}",
            $"profile={profileName}",
            $"complexity={taskComplexity?.ToString() ?? "unknown"}"
        };
        if (usesComplexModel)
        {
            reasons.Add("selected complex model from complexity or model-fit evidence");
        }

        if (costGuardPromptCharacterCount is not null)
        {
            reasons.Add($"estimated cost-guard prompt chars={costGuardPromptCharacterCount}");
        }

        if (taskBriefHeadroom is not null)
        {
            reasons.Add($"prompt budget headroom={taskBriefHeadroom}");
        }

        // Budget-aware routing: scorecard-driven lane explanation (reasons only; alternatives added below)
        if (scorecardRecord is not null)
        {
            reasons.Add($"scorecard={scorecardRecord.Recommendation}: {scorecardRecord.Reason}");
        }

        // Budget-aware routing: local-first lane for simple tasks and complex lane confirmation
        if (taskComplexity == TaskComplexity.Simple && !IsPotentiallyPaidProvider(providerName))
        {
            reasons.Add("simple task complexity; local provider is the cost-optimal lane");
        }
        else if (taskComplexity == TaskComplexity.Complex && IsPotentiallyPaidProvider(providerName))
        {
            reasons.Add("complex/high-risk task complexity; paid capable model is the recommended lane");
        }

        var alternatives = new List<string>();
        if (usesComplexModel)
        {
            alternatives.Add("Review prior model-fit evidence before downgrading to the routine model.");
        }
        else if (IsPotentiallyPaidProvider(providerName) && taskComplexity == TaskComplexity.Simple)
        {
            alternatives.Add("Consider local Ollama/qwen for routine low-risk work when available.");
        }

        // Budget-aware routing: scorecard Avoid blocks even a cheap lane
        if (scorecardRecord?.Recommendation == ModelOutcomeRecommendation.Avoid)
        {
            var avoidModel = subscriptionModelName ?? agent.Model.ModelName;
            alternatives.Add($"Scorecard says Avoid for {providerName}/{avoidModel}; route to a different provider/model lane.");
        }

        // Budget-aware routing: budget cooldown fallback to local Ollama
        if ((retryDeferred || providerCoolingDown) && IsPotentiallyPaidProvider(providerName))
        {
            alternatives.Add("Consider routing to local Ollama as a budget fallback while the paid lane is cooling down.");
        }

        if (requiresPatchCapability)
        {
            reasons.Add(patchCapability.IsPatchCapable
                ? "patch-capable profile satisfies file-write task"
                : $"patch capability missing: {patchCapability.Detail}");
        }

        if (!subscriptionAllowed)
        {
            reasons.Add($"execution policy={agent.ExecutionPolicy}");
            alternatives.Add("Use api-run or change the agent execution policy.");
        }

        if (!hasProfile)
        {
            alternatives.Add($"Create or repair worker profile '{profileName}'.");
        }

        if (isEchoOnly)
        {
            alternatives.Add($"Replace echo-only worker profile '{profileName}' with a real launcher.");
        }

        if (!pinsSelectedModel)
        {
            alternatives.Add($"Add {{subscriptionModelName}} to worker profile '{profileName}'.");
        }

        if (!pinsSelectedReasoning)
        {
            alternatives.Add($"Add {{subscriptionReasoningEffort}} to worker profile '{profileName}'.");
        }

        if (retryDeferred || providerCoolingDown)
        {
            alternatives.Add("Wait for retry-after or route to a different provider profile.");
        }

        if (requiresLimitReview)
        {
            alternatives.Add("Acknowledge limit review with notes or route to a different provider.");
        }

        if (recoverableLimitFailures > 0)
        {
            reasons.Add($"recoverable subscription limit failures={recoverableLimitFailures}");
        }

        var disposition = canPrepare
            ? WorkerRouteDisposition.Selected
            : retryDeferred || providerCoolingDown
                ? WorkerRouteDisposition.Deferred
                : WorkerRouteDisposition.Blocked;
        var recommendation = canPrepare
            ? "Selected route is ready for subscription dispatch."
            : detail;
        return new WorkerRouteDecision(
            disposition,
            OutputTextPreview.CreateTimeline(recommendation).Text,
            reasons,
            alternatives.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool UsesComplexModel(AgentDefinition agent, string providerName, string modelName)
    {
        return agent.ComplexModel is not null &&
            agent.ComplexModel.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) &&
            agent.ComplexModel.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetTemplateValue(IReadOnlyDictionary<string, string?> variables, string name)
    {
        return variables.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static TaskComplexity? TryParseTaskComplexity(string? value)
    {
        return Enum.TryParse<TaskComplexity>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    private static List<SubscriptionPlanModelSummary> BuildModelSummary(Goal goal, IReadOnlyList<SubscriptionPlanItem> items)
    {
        var fitByModel = ModelFitEvidence
            .BuildSummary(goal.Tasks.SelectMany(ModelFitEvidence.FindNotes))
            .ToDictionary(fit => BuildModelFitKey(fit.ProviderName, fit.ModelName), StringComparer.OrdinalIgnoreCase);

        return items
            .Where(item => item.CanPrepare && !string.IsNullOrWhiteSpace(item.ProviderName))
            .Select(item => new
            {
                ProviderName = item.ProviderName!,
                ModelName = item.SubscriptionModelName ?? item.SubscriptionModelAlias ?? item.ModelName,
                item.TaskComplexity,
                item.UsesComplexModel,
                item.SubscriptionReasoningEffort,
                item.EstimatedPromptCharacterCount
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.ModelName))
            .GroupBy(item => new
            {
                item.ProviderName,
                item.ModelName,
                item.TaskComplexity,
                item.UsesComplexModel,
                item.SubscriptionReasoningEffort
            })
            .OrderBy(group => group.Key.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.ModelName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.TaskComplexity?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.SubscriptionReasoningEffort ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                fitByModel.TryGetValue(BuildModelFitKey(group.Key.ProviderName, group.Key.ModelName!), out var fit);
                return new SubscriptionPlanModelSummary(
                    group.Key.ProviderName,
                    group.Key.ModelName!,
                    group.Count(),
                    group.Key.TaskComplexity,
                    group.Key.SubscriptionReasoningEffort,
                    IsPotentiallyPaidProvider(group.Key.ProviderName),
                    SumKnownUsage(group.Select(item => item.EstimatedPromptCharacterCount)),
                    fit?.NoteCount ?? 0,
                    fit?.AdequateCount ?? 0,
                    fit?.OverkillCount ?? 0,
                    fit?.UnderpoweredCount ?? 0,
                    fit?.UnknownCount ?? 0,
                    fit?.TaskShapes ?? [],
                    BuildModelFitRecommendation(fit, group.Key.ProviderName, group.Key.ModelName!),
                    group.Key.UsesComplexModel);
            })
            .ToList();
    }

    private static SubscriptionProviderBudgetSummary[] BuildProviderBudgetSummary(
        Goal goal,
        IReadOnlyList<SubscriptionPlanItem> items)
    {
        var now = DateTimeOffset.UtcNow;
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.ProviderName))
            .GroupBy(item => item.ProviderName!, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildProviderBudgetSummary(goal, group.Key, group.ToArray(), now))
            .OrderBy(summary => summary.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SubscriptionProviderBudgetSummary BuildProviderBudgetSummary(
        Goal goal,
        string providerName,
        SubscriptionPlanItem[] items,
        DateTimeOffset now)
    {
        var retryAfter = items
            .Where(item => item.RetryAfter is not null)
            .Select(item => item.RetryAfter)
            .OrderByDescending(item => item)
            .FirstOrDefault();
        var sourceTask = retryAfter is null
            ? null
            : items
                .Where(item => item.RetryAfter == retryAfter)
                .Select(item => (int?)item.TaskNumber)
                .FirstOrDefault();
        var retryDelaySeconds = retryAfter is null
            ? null
            : (int?)Math.Max(0, (int)Math.Ceiling((retryAfter.Value - now).TotalSeconds));
        var recoverableFailures = goal.Tasks
            .Where(task => task.LastDispatch?.ProviderName?.Equals(providerName, StringComparison.OrdinalIgnoreCase) == true)
            .Sum(DispatchFailureClassifier.CountRecoverableSubscriptionLimitFailures);
        var deferredCount = items.Count(item => item.RetryDelaySeconds is > 0);
        var detail = retryAfter is null
            ? "No observed provider cooldown."
            : $"Provider retry-after is active until {retryAfter:u}; source task {sourceTask}.";

        return new SubscriptionProviderBudgetSummary(
            providerName,
            items.Length,
            items.Count(item => item.CanPrepare),
            deferredCount,
            recoverableFailures,
            retryAfter is not null && retryAfter > now,
            retryAfter,
            retryDelaySeconds,
            sourceTask,
            detail);
    }

    private static string BuildModelFitKey(string providerName, string modelName)
    {
        return $"{providerName}/{modelName}";
    }

    private static string? BuildModelFitRecommendation(ModelFitSummary? fit, string providerName, string modelName)
    {
        if (fit is null)
        {
            return null;
        }

        if (fit.UnderpoweredCount > 0)
        {
            return $"Prior evidence says {providerName}/{modelName} was underpowered; choose a stronger model before repeating it.";
        }

        if (fit.OverkillCount > 0 && IsPotentiallyPaidProvider(providerName))
        {
            return CostRecommendationText.PaidStartOverkill(providerName, modelName);
        }

        return null;
    }

    private static int? SumKnownUsage(IEnumerable<int?> values)
    {
        var known = values.Where(value => value is not null).Select(value => value!.Value).ToList();
        return known.Count == 0 ? null : known.Sum();
    }

    private static bool IsPotentiallyPaidProvider(string providerName)
    {
        return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresSubscriptionReasoningPlaceholder(string providerName, string? reasoningEffort)
    {
        return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(reasoningEffort);
    }
}

internal static class ProviderCapacityScheduler
{
    public static ProviderCapacitySchedule Build(
        IReadOnlyList<SubscriptionPlanItem> items,
        IReadOnlyList<SubscriptionProviderBudgetSummary> providerBudgets,
        PaidSubscriptionPromptRisk? readyStartRisk)
    {
        var actions = items
            .Where(item => item.TaskStatus == WorkTaskStatus.Assigned)
            .Select(item => BuildAction(item))
            .ToArray();
        var readyCount = actions.Count(action => action.Disposition == ProviderCapacityDisposition.Ready);
        var deferred = actions
            .Where(action => action.Disposition == ProviderCapacityDisposition.Deferred && action.RetryAfter is not null)
            .OrderBy(action => action.RetryAfter)
            .ToArray();
        var hasCostRisk = readyStartRisk is not null;
        var disposition = readyCount > 0 && !hasCostRisk
            ? ProviderCapacityDisposition.Ready
            : deferred.Length > 0
                ? ProviderCapacityDisposition.Deferred
                : hasCostRisk
                    ? ProviderCapacityDisposition.Review
                    : ProviderCapacityDisposition.Blocked;
        var recommendation = disposition switch
        {
            ProviderCapacityDisposition.Ready => $"Start {readyCount} ready subscription task(s) now.",
            ProviderCapacityDisposition.Deferred => $"Wait until {deferred[0].RetryAfter:u} or route deferred work to an alternate provider.",
            ProviderCapacityDisposition.Review => SubscriptionPromptCostGuard.BuildRecommendation(readyStartRisk!) ??
                "Review paid prompt/model-fit risk before starting subscription work.",
            _ => providerBudgets.Count == 0
                ? "No subscription provider capacity is available for assigned tasks."
                : "Resolve blocked routes before starting subscription work."
        };

        return new ProviderCapacitySchedule(
            disposition,
            OutputTextPreview.CreateTimeline(recommendation).Text,
            readyCount,
            actions.Count(action => action.Disposition == ProviderCapacityDisposition.Deferred),
            deferred.FirstOrDefault()?.RetryAfter,
            hasCostRisk,
            actions);
    }

    private static ProviderCapacityAction BuildAction(SubscriptionPlanItem item)
    {
        var routeDisposition = item.Route?.Disposition;
        var disposition = item.CanPrepare
            ? ProviderCapacityDisposition.Ready
            : routeDisposition == WorkerRouteDisposition.Deferred
                ? ProviderCapacityDisposition.Deferred
                : item.Route is not null &&
                    (item.Route.Reasons.Any(reason => reason.Contains("limit", StringComparison.OrdinalIgnoreCase)) ||
                        item.Route.Recommendation.Contains("inspect model, profile, or timing", StringComparison.OrdinalIgnoreCase))
                    ? ProviderCapacityDisposition.Review
                    : ProviderCapacityDisposition.Blocked;
        var recommendation = disposition switch
        {
            ProviderCapacityDisposition.Ready => "Ready to start with selected provider capacity.",
            ProviderCapacityDisposition.Deferred when item.RetryAfter is not null =>
                $"Retry after {item.RetryAfter:u} or choose an alternate provider route.",
            ProviderCapacityDisposition.Deferred => "Provider route is deferred; choose an alternate provider route.",
            ProviderCapacityDisposition.Review => "Operator review is required before retrying this provider route.",
            _ => item.Detail
        };

        return new ProviderCapacityAction(
            item.TaskNumber,
            item.TaskId,
            item.ProviderName,
            disposition,
            item.RetryAfter,
            OutputTextPreview.CreateTimeline(recommendation).Text,
            item.Route?.Alternatives ?? []);
    }
}
