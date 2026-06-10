using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ProviderConfigurationStatus(string ProviderName, bool IsConfigured, string Mode, string Detail);

public sealed record AgentConfigurationValidation(
    AgentRole Role,
    string AgentName,
    string ProviderName,
    string ModelName,
    string? ReasoningEffort,
    int? MaxOutputTokens,
    AgentExecutionPolicy ExecutionPolicy,
    string? SubscriptionProfileName,
    string? SubscriptionModelAlias,
    string? SubscriptionReasoningEffort,
    bool IsValid,
    string Detail,
    string? ComplexProviderName = null,
    string? ComplexModelName = null,
    int? ComplexMaxOutputTokens = null,
    string? ComplexReasoningEffort = null);

public sealed record WorkerProfileValidation(
    string Name,
    string CommandTemplate,
    string Executable,
    bool IsResolvable,
    bool IsEchoOnly,
    bool IsPatchCapable,
    bool IsOptional,
    string Detail);

public sealed record OrchestratorHealthReport(
    IReadOnlyList<ProviderConfigurationStatus> Providers,
    IReadOnlyList<AgentConfigurationValidation> Agents,
    IReadOnlyList<WorkerProfileValidation> WorkerProfiles)
{
    public bool IsReady =>
        Providers.Any(provider => provider.IsConfigured) &&
        Agents.All(agent => agent.IsValid) &&
        WorkerProfiles.Where(profile => !profile.IsOptional).All(profile => profile.IsResolvable);
}

public static class OrchestratorHealthInspector
{
    public static OrchestratorHealthReport Inspect(
        IReadOnlyDictionary<string, string?> environment,
        AgentCatalog agents,
        WorkerProfileCatalog workerProfiles,
        Func<string, bool> commandExists)
    {
        var providers = new[]
        {
            InspectProvider("OpenAI", "OPENAI_API_KEY", "codex", environment, commandExists),
            InspectProvider("Anthropic", "ANTHROPIC_API_KEY", "claude", environment, commandExists),
            InspectOllamaProvider(environment)
        };

        var profiles = workerProfiles.Profiles
            .Select(profile => InspectProfile(profile, commandExists))
            .ToList();
        var agentValidations = InspectAgents(agents, providers, profiles).ToList();

        return new OrchestratorHealthReport(providers, agentValidations, profiles);
    }

    public static OrchestratorHealthReport InspectCurrentEnvironment(AgentCatalog agents, WorkerProfileCatalog workerProfiles)
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase);

        return Inspect(environment, agents, workerProfiles, LocalCommandExists);
    }

    private static ProviderConfigurationStatus InspectProvider(
        string providerName,
        string apiKeyName,
        string localBridgeExecutable,
        IReadOnlyDictionary<string, string?> environment,
        Func<string, bool> commandExists)
    {
        var configured = environment.TryGetValue(apiKeyName, out var value) && !string.IsNullOrWhiteSpace(value);
        if (configured)
        {
            return new ProviderConfigurationStatus(providerName, true, "ApiKey", $"{apiKeyName} is set.");
        }

        if (commandExists(localBridgeExecutable))
        {
            return new ProviderConfigurationStatus(
                providerName,
                true,
                "LocalBridge",
                $"{apiKeyName} is not set; '{localBridgeExecutable}' is available for local subscription-backed dispatch.");
        }

        return new ProviderConfigurationStatus(providerName, false, "Offline", $"{apiKeyName} is not set and '{localBridgeExecutable}' was not found; offline scripted provider will be used.");
    }

    private static ProviderConfigurationStatus InspectOllamaProvider(IReadOnlyDictionary<string, string?> environment)
    {
        var baseUrl = OllamaDefaults.ResolveBaseUrl(
            environment.TryGetValue("OLLAMA_BASE_URL", out var url) ? url : null);

        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = probe.GetAsync($"{baseUrl.TrimEnd('/')}/api/version").GetAwaiter().GetResult();
            if (response.IsSuccessStatusCode)
            {
                var model = environment.TryGetValue("OLLAMA_MODEL", out var m) && !string.IsNullOrWhiteSpace(m) ? m : "qwen3:8b";
                return new ProviderConfigurationStatus("Ollama", true, "LocalBridge", $"Ollama is running at {baseUrl}; default model is '{model}'.");
            }
        }
        catch
        {
            // Ollama is not reachable
        }

        return new ProviderConfigurationStatus("Ollama", false, "Offline", $"Ollama is not reachable at {baseUrl}.");
    }

    private static IEnumerable<AgentConfigurationValidation> InspectAgents(
        AgentCatalog agents,
        IReadOnlyList<ProviderConfigurationStatus> providers,
        IReadOnlyList<WorkerProfileValidation> workerProfiles)
    {
        var providersByName = providers.ToDictionary(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase);
        var profilesByName = workerProfiles.ToDictionary(profile => profile.Name, StringComparer.OrdinalIgnoreCase);
        var localOllamaAvailable = providers.Any(provider =>
            provider.ProviderName.Equals("Ollama", StringComparison.OrdinalIgnoreCase) &&
            provider.IsConfigured &&
            provider.Mode.Equals("LocalBridge", StringComparison.OrdinalIgnoreCase));
        var roles = Enum.GetValues<AgentRole>();

        foreach (var role in roles)
        {
            var agent = agents.Agents.FirstOrDefault(candidate => candidate.Role == role);
            if (agent is null)
            {
                yield return new AgentConfigurationValidation(
                    role,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    null,
                    AgentExecutionPolicy.ApiOnly,
                    null,
                    null,
                    null,
                    false,
                    "No agent is configured for this role.");
                continue;
            }

            providersByName.TryGetValue(agent.Model.ProviderName, out var provider);
            var profileName = ResolveSubscriptionProfileName(agent);
            WorkerProfileValidation? profile = null;
            var profileKnown = profileName is not null && profilesByName.TryGetValue(profileName, out profile);
            var apiAllowed = AgentExecutionPolicies.AllowsApi(agent.ExecutionPolicy);
            var subscriptionAllowed = AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy);
            var apiUsable = apiAllowed && IsApiRouteUsable(provider);
            var subscriptionUsable = subscriptionAllowed && IsSubscriptionRouteUsable(agent, profileKnown ? profile : null);
            var isValid = agent.ExecutionPolicy switch
            {
                AgentExecutionPolicy.ApiOnly => apiUsable,
                AgentExecutionPolicy.SubscriptionOnly => subscriptionUsable,
                AgentExecutionPolicy.PreferSubscription => subscriptionUsable || apiUsable,
                AgentExecutionPolicy.AnyAvailable => subscriptionUsable || apiUsable,
                _ => apiUsable
            };
            yield return new AgentConfigurationValidation(
                role,
                agent.Name,
                agent.Model.ProviderName,
                agent.Model.ModelName,
                agent.Model.ReasoningEffort,
                agent.Model.MaxOutputTokens,
                agent.ExecutionPolicy,
                profileName,
                agent.Subscription?.ModelAlias,
                agent.Subscription?.ReasoningEffort,
                isValid,
                BuildAgentValidationDetail(agent, provider, profileKnown ? profile : null, apiAllowed, subscriptionAllowed, profileName, localOllamaAvailable),
                agent.ComplexModel?.ProviderName,
                agent.ComplexModel?.ModelName,
                agent.ComplexModel?.MaxOutputTokens,
                agent.ComplexModel?.ReasoningEffort);
        }
    }

    private static bool IsApiRouteUsable(ProviderConfigurationStatus? provider)
    {
        if (provider is null || !provider.IsConfigured)
        {
            return false;
        }

        if (provider.Mode.Equals("ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return provider.ProviderName.Equals("Ollama", StringComparison.OrdinalIgnoreCase) &&
            provider.Mode.Equals("LocalBridge", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSubscriptionRouteUsable(AgentDefinition agent, WorkerProfileValidation? profile)
    {
        if (profile is null || !profile.IsResolvable || profile.IsEchoOnly)
        {
            return false;
        }

        if (!WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate))
        {
            return false;
        }

        if (RequiresSubscriptionReasoningPlaceholder(agent.Model.ProviderName, agent.Subscription?.ReasoningEffort ?? agent.Model.ReasoningEffort) &&
            !WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(profile.CommandTemplate))
        {
            return false;
        }

        return agent.Role != AgentRole.Developer || profile.IsPatchCapable;
    }

    private static string BuildAgentValidationDetail(
        AgentDefinition agent,
        ProviderConfigurationStatus? provider,
        WorkerProfileValidation? profile,
        bool apiAllowed,
        bool subscriptionAllowed,
        string? profileName,
        bool localOllamaAvailable)
    {
        var api = apiAllowed
            ? BuildApiValidationDetail(agent, provider)
            : "API execution disabled";
        var subscription = subscriptionAllowed
            ? BuildSubscriptionValidationDetail(agent, profile, profileName)
            : "subscription execution disabled";
        return $"{api}; {subscription}{BuildLocalModelRecommendation(agent, localOllamaAvailable)}.";
    }

    private static string BuildLocalModelRecommendation(AgentDefinition agent, bool localOllamaAvailable)
    {
        return localOllamaAvailable && IsPotentiallyPaidProvider(agent.Model.ProviderName)
            ? "; local Ollama is available, consider switching this role to Ollama before paid work"
            : string.Empty;
    }

    private static string BuildApiValidationDetail(AgentDefinition agent, ProviderConfigurationStatus? provider)
    {
        if (provider is null)
        {
            return $"API provider '{agent.Model.ProviderName}' is not registered";
        }

        if (IsApiRouteUsable(provider))
        {
            return provider.Mode.Equals("ApiKey", StringComparison.OrdinalIgnoreCase)
                ? $"API provider '{provider.ProviderName}' is configured with an API key"
                : $"API provider '{provider.ProviderName}' is reachable locally";
        }

        if (!provider.IsConfigured)
        {
            return $"API provider '{provider.ProviderName}' is registered but offline";
        }

        return $"API provider '{provider.ProviderName}' is registered as a local subscription bridge, not API execution";
    }

    private static string BuildSubscriptionValidationDetail(
        AgentDefinition agent,
        WorkerProfileValidation? profile,
        string? profileName)
    {
        if (profile is null)
        {
            return $"subscription profile '{profileName ?? "none"}' is not configured";
        }

        if (!profile.IsResolvable)
        {
            return $"subscription profile '{profile.Name}' is not executable";
        }

        if (profile.IsEchoOnly)
        {
            return $"subscription profile '{profile.Name}' only echoes the prompt path";
        }

        if (!WorkerProfileDiagnostics.UsesSubscriptionModelPlaceholder(profile.CommandTemplate))
        {
            return $"subscription profile '{profile.Name}' does not pin the selected model";
        }

        if (RequiresSubscriptionReasoningPlaceholder(agent.Model.ProviderName, agent.Subscription?.ReasoningEffort ?? agent.Model.ReasoningEffort) &&
            !WorkerProfileDiagnostics.UsesSubscriptionReasoningPlaceholder(profile.CommandTemplate))
        {
            return $"subscription profile '{profile.Name}' does not pin the selected reasoning effort";
        }

        if (agent.Role == AgentRole.Developer && !profile.IsPatchCapable)
        {
            return $"subscription profile '{profile.Name}' cannot patch Developer tasks";
        }

        return $"subscription profile '{profile.Name}' is executable";
    }

    private static bool RequiresSubscriptionReasoningPlaceholder(string providerName, string? reasoningEffort)
    {
        return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(reasoningEffort);
    }

    private static bool IsPotentiallyPaidProvider(string providerName)
    {
        return providerName.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) ||
            providerName.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveSubscriptionProfileName(AgentDefinition agent)
    {
        try
        {
            return WorkerProfileDispatcher.ResolveSubscriptionProfileName(agent);
        }
        catch (InvalidOperationException)
        {
            return agent.Subscription?.WorkerProfileName;
        }
    }

    private static WorkerProfileValidation InspectProfile(WorkerProfile profile, Func<string, bool> commandExists)
    {
        var executable = ExtractExecutable(profile.CommandTemplate);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new WorkerProfileValidation(profile.Name, profile.CommandTemplate, string.Empty, false, false, false, IsOptionalProfile(profile.Name), "No executable was found in the command template.");
        }

        var exists = commandExists(executable);
        var optional = IsOptionalProfile(profile.Name);
        var echoOnly = WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate);
        var patchCapability = WorkerProfileDiagnostics.EvaluatePatchCapability(profile.CommandTemplate);
        var diagnosticEcho = IsDiagnosticEchoProfile(profile.Name);
        return new WorkerProfileValidation(
            profile.Name,
            profile.CommandTemplate,
            executable,
            exists,
            echoOnly,
            patchCapability.IsPatchCapable,
            optional,
            echoOnly && diagnosticEcho
                ? "Diagnostic echo profile; it prints the prompt path instead of executing worker work."
                : echoOnly
                ? "Command only echoes the prompt path; subscription dispatch would not execute worker work."
                : exists
                    ? patchCapability.Detail
                    : "Command was not found on PATH or as a PowerShell command.");
    }

    private static bool IsOptionalProfile(string name)
    {
        return name.Equals("codex-cli", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("codex-oss-cli", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("qwen-code-cli", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("claude-cli", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDiagnosticEchoProfile(string name)
    {
        return name.Equals("local-echo", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractExecutable(string commandTemplate)
    {
        var trimmed = commandTemplate.Trim();
        if (trimmed.StartsWith("& ", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..].TrimStart();
        }

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed[0] is '\'' or '"')
        {
            var quote = trimmed[0];
            var end = trimmed.IndexOf(quote, 1);
            return end > 1 ? trimmed[1..end] : string.Empty;
        }

        var separator = trimmed.IndexOfAny([' ', '\t']);
        return separator < 0 ? trimmed : trimmed[..separator];
    }

    private static bool LocalCommandExists(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add($"if (Get-Command {Quote(executable)} -ErrorAction SilentlyContinue) {{ exit 0 }} else {{ exit 1 }}");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(3000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
