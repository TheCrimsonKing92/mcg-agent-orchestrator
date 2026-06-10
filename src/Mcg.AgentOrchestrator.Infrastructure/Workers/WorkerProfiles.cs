using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerProfile(string Name, string CommandTemplate);

public static class WorkerProfileDiagnostics
{
    public static bool IsEchoOnlyCommand(string commandTemplate)
    {
        var normalized = commandTemplate.Trim();
        return normalized.Equals("Write-Output {promptPath}", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Write-Host {promptPath}", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("echo {promptPath}", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPatchCapableCommand(string commandTemplate)
    {
        return EvaluatePatchCapability(commandTemplate).IsPatchCapable;
    }

    public static bool UsesSubscriptionModelPlaceholder(string commandTemplate)
    {
        return commandTemplate.Contains("{subscriptionModelName}", StringComparison.OrdinalIgnoreCase);
    }

    public static bool UsesSubscriptionReasoningPlaceholder(string commandTemplate)
    {
        return commandTemplate.Contains("{subscriptionReasoningEffort}", StringComparison.OrdinalIgnoreCase);
    }

    public static WorkerProfilePatchCapability EvaluatePatchCapability(string commandTemplate)
    {
        var normalized = commandTemplate.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new WorkerProfilePatchCapability(false, "Command template is empty; it cannot patch source.");
        }

        if (IsEchoOnlyCommand(normalized))
        {
            return new WorkerProfilePatchCapability(false, "Command only echoes the prompt path; it cannot patch source.");
        }

        if (!IsCodexCommand(normalized))
        {
            return new WorkerProfilePatchCapability(
                true,
                "Command is not a recognized Codex launcher; patch capability cannot be inferred beyond executing the prompt.");
        }

        var hasWorkspaceWrite = normalized.Contains("--sandbox workspace-write", StringComparison.OrdinalIgnoreCase);
        var hasWorkingDirectory = normalized.Contains("--cd {workingDirectory}", StringComparison.OrdinalIgnoreCase);
        var readsPromptContent = normalized.Contains("Get-Content -Raw {promptPath}", StringComparison.OrdinalIgnoreCase);

        if (hasWorkspaceWrite && hasWorkingDirectory && readsPromptContent)
        {
            return new WorkerProfilePatchCapability(
                true,
                "Codex launcher is patch-capable: workspace-write sandbox, repository working directory, and prompt content are configured.");
        }

        var missing = new List<string>();
        if (!hasWorkspaceWrite)
        {
            missing.Add("--sandbox workspace-write");
        }

        if (!hasWorkingDirectory)
        {
            missing.Add("--cd {workingDirectory}");
        }

        if (!readsPromptContent)
        {
            missing.Add("Get-Content -Raw {promptPath}");
        }

        return new WorkerProfilePatchCapability(
            false,
            $"Codex launcher is not patch-capable; missing {string.Join(", ", missing)}.");
    }

    private static bool IsCodexCommand(string commandTemplate)
    {
        var trimmed = commandTemplate.TrimStart();
        if (trimmed.StartsWith("& ", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..].TrimStart();
        }

        if (trimmed.StartsWith("\"codex\"", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("'codex'", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return trimmed.StartsWith("codex ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("codex", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record WorkerProfilePatchCapability(bool IsPatchCapable, string Detail);

public sealed record WorkerProfileCatalog(IReadOnlyList<WorkerProfile> Profiles)
{
    public WorkerProfile GetRequired(string name)
    {
        return Profiles.FirstOrDefault(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Worker profile '{name}' was not found.");
    }

    public WorkerProfileCatalog Upsert(WorkerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new ArgumentException("Worker profile name cannot be empty.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.CommandTemplate))
        {
            throw new ArgumentException("Worker profile command template cannot be empty.", nameof(profile));
        }

        var profiles = Profiles
            .Where(existing => !existing.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
            .Append(new WorkerProfile(profile.Name.Trim(), profile.CommandTemplate.Trim()))
            .OrderBy(existing => existing.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkerProfileCatalog(profiles);
    }

    public WorkerProfileCatalog Merge(WorkerProfileCatalog catalog)
    {
        var merged = this;
        foreach (var profile in catalog.Profiles)
        {
            merged = merged.Upsert(profile);
        }

        return merged;
    }

    public static WorkerProfileCatalog Default()
    {
        return new WorkerProfileCatalog(
        [
            new WorkerProfile("local-echo", "Write-Output {promptPath}"),
            new WorkerProfile("codex-cli", "codex exec --skip-git-repo-check --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})"),
            new WorkerProfile("codex-oss-cli", "codex exec --skip-git-repo-check --oss --local-provider ollama --model {subscriptionModelName} --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})"),
            new WorkerProfile("claude-cli", "claude --model {subscriptionModelName} -p (Get-Content -Raw {promptPath})")
        ]);
    }
}

public static class WorkerProfileStore
{
    public static WorkerProfileCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            return WorkerProfileCatalog.Default();
        }

        var catalog = JsonSerializer.Deserialize<WorkerProfileCatalog>(File.ReadAllText(path), JsonOptions());
        var merged = catalog?.Profiles is null || catalog.Profiles.Count == 0
            ? WorkerProfileCatalog.Default()
            : WorkerProfileCatalog.Default().Merge(catalog);
        return RepairBuiltInSubscriptionProfiles(merged);
    }

    public static WorkerProfileCatalog LoadRequired(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Worker profile file was not found.", path);
        }

        var catalog = JsonSerializer.Deserialize<WorkerProfileCatalog>(File.ReadAllText(path), JsonOptions());
        if (catalog?.Profiles is null || catalog.Profiles.Count == 0)
        {
            throw new InvalidDataException("Worker profile file did not contain any profiles.");
        }

        return new WorkerProfileCatalog([]).Merge(catalog);
    }

    public static void Save(string path, WorkerProfileCatalog catalog)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(catalog, JsonOptions()));
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
    }

    private static WorkerProfileCatalog RepairBuiltInSubscriptionProfiles(WorkerProfileCatalog catalog)
    {
        var repaired = catalog;
        var defaults = WorkerProfileCatalog.Default();
        foreach (var profileName in new[] { "codex-cli", "claude-cli" })
        {
            var current = repaired.GetRequired(profileName);
            if (ShouldRepairBuiltInSubscriptionProfile(current))
            {
                repaired = repaired.Upsert(defaults.GetRequired(profileName));
            }
        }

        return repaired;
    }

    private static bool ShouldRepairBuiltInSubscriptionProfile(WorkerProfile profile)
    {
        if (WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate))
        {
            return true;
        }

        if (profile.Name.Equals("codex-cli", StringComparison.OrdinalIgnoreCase))
        {
            return !profile.CommandTemplate.Contains("--sandbox workspace-write", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--cd", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--model {subscriptionModelName}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.OrdinalIgnoreCase);
        }

        return profile.Name.Equals("claude-cli", StringComparison.OrdinalIgnoreCase) &&
            !profile.CommandTemplate.Contains("--model {subscriptionModelName}", StringComparison.OrdinalIgnoreCase);
    }
}
