using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static partial class WorkerSubscriptionModelResolver
{
    private const string IntakeRiskLabelsMarker = "risk labels:";

    internal static SubscriptionModelSelection ResolveEffectiveSubscriptionModelSelection(
        AgentDefinition agent,
        Goal goal,
        TaskSpec task,
        WorkerProviderCatalog providers,
        Func<WorkerSandboxOptions> sandboxProbe,
        Func<ClaudeCliAuthState> claudeAuthProbe,
        DispatchModelOverride? modelOverride = null,
        WorkerProfileCatalog? profiles = null,
        Func<string, bool>? commandExists = null,
        bool cascadeTesterCheapFirst = true,
        string? cascadeCheapModelAlias = null, bool cascadeMechanicalReworkCheap = true)
    {
        var fullSelection = ResolveSubscriptionModel(agent, goal, task);
        return modelOverride is not null
            ? fullSelection with { Reason = "override: explicit dispatch profile/model selection" }
            : ResolveRoleModelSelection(agent, goal, task, fullSelection, providers, sandboxProbe, claudeAuthProbe, profiles, commandExists, cascadeTesterCheapFirst, cascadeCheapModelAlias, cascadeMechanicalReworkCheap);
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

    private static SubscriptionModelSelection ResolveRoleModelSelection(
        AgentDefinition agent,
        Goal goal,
        TaskSpec task,
        SubscriptionModelSelection fullSelection,
        WorkerProviderCatalog providers,
        Func<WorkerSandboxOptions> sandboxProbe,
        Func<ClaudeCliAuthState> claudeAuthProbe,
        WorkerProfileCatalog? profiles,
        Func<string, bool>? commandExists,
        bool cascadeTesterCheapFirst,
        string? cascadeCheapModelAlias, bool cascadeMechanicalReworkCheap)
    {
        if (!IsLightReadOnlyRole(task.RequiredRole))
        {
            if (agent.IsProviderRoutingConstrained == true)
            {
                var constrainedSelection = fullSelection with
                {
                    Reason = $"provider-constrained: {task.RequiredRole} remains on {agent.Model.ProviderName}"
                };
                if (task.RequiredRole is (AgentRole.Developer or AgentRole.Tester) &&
                    agent.Model.ProviderName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) &&
                    WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent, constrainedSelection)
                        .Equals(WorkerProfileDispatcher.OpenAiSubscriptionProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (task.RequiredRole == AgentRole.Tester)
                        return RouteTesterCascade(agent, goal, task, constrainedSelection, providers, profiles,
                            cascadeTesterCheapFirst, cascadeCheapModelAlias);
                    if (task.RequiredRole == AgentRole.Developer)
                        return RouteDeveloperCascade(agent, goal, task, constrainedSelection, providers, profiles,
                            cascadeMechanicalReworkCheap, cascadeCheapModelAlias);
                }
                return constrainedSelection;
            }

            return fullSelection with { Reason = "full-profile: role is write-capable or gate-heavy" };
        }

        if (HasCustomWorkerProfileOverride(agent, providers))
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
            !TryValidateLightRoleProfile(profiles, providers, sandboxProbe, claudeAuthProbe, commandExists, out var unavailableReason))
        {
            return fullSelection with { Reason = $"full-profile: light-role profile unavailable ({unavailableReason})" };
        }

        return new SubscriptionModelSelection(
            fullSelection.Complexity,
            new ModelProfile(
                "Anthropic",
                WorkerProfileDispatcher.LightRoleAnthropicModelName,
                agent.Model.Capabilities,
                SubscriptionMode.ApiKey,
                MaxOutputTokens: agent.Model.MaxOutputTokens),
            UsesComplexModel: false,
            UsesSubscriptionLaunchProfile: false,
            Reason: $"light-role: {task.RequiredRole} uses {WorkerProfileDispatcher.AnthropicSubscriptionProfileName}/{WorkerProfileDispatcher.LightRoleAnthropicModelName}");
    }

    private static bool TryValidateLunaProfile(
        WorkerProfileCatalog profiles,
        WorkerProviderCatalog providers,
        out string unavailableReason)
    {
        try
        {
            var profile = profiles.Profiles.FirstOrDefault(profile =>
                string.Equals(LunaLaneNames.NormalizeProfileName(profile.Name), WorkerProfileDispatcher.OpenAiLunaSubscriptionProfileName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                unavailableReason = $"worker profile '{WorkerProfileDispatcher.OpenAiLunaSubscriptionProfileName}' was not found";
                return false;
            }

            if (WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate))
            {
                unavailableReason = $"worker profile '{WorkerProfileDispatcher.OpenAiLunaSubscriptionProfileName}' only echoes prompt path";
                return false;
            }

            if (!WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate))
            {
                unavailableReason = $"worker profile '{WorkerProfileDispatcher.OpenAiLunaSubscriptionProfileName}' does not include {{subscriptionModelName}}";
                return false;
            }

            if (!WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(profile.CommandTemplate))
            {
                unavailableReason = $"worker profile '{WorkerProfileDispatcher.OpenAiLunaSubscriptionProfileName}' does not include {{subscriptionReasoningEffort}}";
                return false;
            }

            var capability = WorkerProfileDiagnostics.EvaluatePatchCapability(
                profile,
                providers.Resolve(ProviderKind.OpenAICodexLuna));
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
        WorkerProviderCatalog providers,
        Func<WorkerSandboxOptions> sandboxProbe,
        Func<ClaudeCliAuthState> claudeAuthProbe,
        Func<string, bool>? commandExists,
        out string unavailableReason)
    {
        var profile = profiles.Profiles.FirstOrDefault(profile =>
            profile.Name.Equals(WorkerProfileDispatcher.AnthropicSubscriptionProfileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            unavailableReason = $"{WorkerProfileDispatcher.AnthropicSubscriptionProfileName} not configured";
            return false;
        }

        if (WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate))
        {
            unavailableReason = $"{WorkerProfileDispatcher.AnthropicSubscriptionProfileName} is echo-only";
            return false;
        }

        if (!WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate))
        {
            unavailableReason = $"{WorkerProfileDispatcher.AnthropicSubscriptionProfileName} does not pin selected model";
            return false;
        }

        var launcher = WorkerProfileDiagnostics.EvaluateRealLauncher(
            profile,
            providers.ResolveProfile(WorkerProfileDispatcher.AnthropicSubscriptionProfileName),
            commandExists);
        if (!launcher.IsRealLauncher)
        {
            unavailableReason = launcher.Detail;
            return false;
        }

        var sandbox = sandboxProbe();
        if (sandbox.Enabled)
        {
            var authState = claudeAuthProbe();
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

    internal static bool HasHighRiskOrComplexIntakeRiskLabel(Goal goal)
    {
        return EnumerateStoredIntakeRiskLabels(goal).Any(label =>
            label.Equals("high-risk", StringComparison.OrdinalIgnoreCase) ||
            label.Equals("complex", StringComparison.OrdinalIgnoreCase));
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

    private static bool HasCustomWorkerProfileOverride(AgentDefinition agent, WorkerProviderCatalog providers)
    {
        if (agent.Subscription?.WorkerProfileName is not { Length: > 0 } profileName)
        {
            return false;
        }

        try
        {
            var defaultProfileName = providers.ResolveModelProvider(agent.Model.ProviderName).ProfileName;
            return !profileName.Equals(defaultProfileName, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool TryFindRoleGuardrailFailure(TaskSpec task, out string reason)
        => TryFindWorkerResultGuardrailFailure(task.RequiredRole, task.LastVerification, out reason);

    private static bool TryFindWorkerResultGuardrailFailure(AgentRole role, TaskVerificationRecord? verification, out string reason)
    {
        reason = string.Empty;
        if (verification is null)
        {
            return false;
        }

        var text = $"{verification.StandardOutput}\n{verification.StandardError}";
        if (!WorkerResultParser.TryParseFields(text, out var fields, out var diagnostic))
        {
            reason = $"prior {role} WORKER_RESULT invalid ({diagnostic})";
            return true;
        }

        var missingFields = WorkerResultRequiredFieldsForLightRole(role)
            .Where(field => !fields.ContainsKey(field))
            .ToArray();
        if (missingFields.Length > 0)
        {
            reason = $"prior {role} WORKER_RESULT missing field(s): {string.Join(", ", missingFields)}";
            return true;
        }

        if (role == AgentRole.Researcher && !HasSubstantiveField(fields, "citations"))
        {
            reason = "prior Researcher WORKER_RESULT missing citations";
            return true;
        }

        if (role == AgentRole.Reviewer && !HasSubstantiveField(fields, "verdict"))
        {
            reason = "prior Reviewer WORKER_RESULT missing verdict";
            return true;
        }

        if (role == AgentRole.Reviewer && !HasPresentField(fields, "blockers"))
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
}
