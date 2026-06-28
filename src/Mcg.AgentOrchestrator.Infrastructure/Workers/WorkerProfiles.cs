using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

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

    public static bool IsPatchCapableCommand(WorkerProfile profile, IWorkerProvider provider)
    {
        return EvaluatePatchCapability(profile, provider).IsPatchCapable;
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

        if (IsClaudeCommand(normalized))
        {
            return EvaluateClaudePatchCapability(normalized);
        }

        if (!IsCodexCommand(normalized))
        {
            return new WorkerProfilePatchCapability(
                true,
                "Command is not a recognized Codex launcher; patch capability cannot be inferred beyond executing the prompt.");
        }

        var hasWorkspaceWrite = normalized.Contains("--sandbox workspace-write", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("--sandbox {sandboxMode}", StringComparison.OrdinalIgnoreCase);
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

    public static WorkerProfilePatchCapability EvaluatePatchCapability(WorkerProfile profile, IWorkerProvider provider)
    {
        var normalized = profile.CommandTemplate.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new WorkerProfilePatchCapability(false, "Command template is empty; it cannot patch source.");
        }

        if (IsEchoOnlyCommand(normalized))
        {
            return new WorkerProfilePatchCapability(false, "Command only echoes the prompt path; it cannot patch source.");
        }

        return provider.Identity.Kind switch
        {
            ProviderKind.AnthropicClaudeCli => EvaluateClaudePatchCapability(normalized),
            ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexSpark or ProviderKind.OpenAIJudge => EvaluateCodexPatchCapability(normalized),
            _ => new WorkerProfilePatchCapability(
                true,
                "Provider is not a typed Codex or Claude launcher; patch capability cannot be inferred beyond executing the prompt.")
        };
    }

    private static WorkerProfilePatchCapability EvaluateCodexPatchCapability(string normalized)
    {
        var hasWorkspaceWrite = normalized.Contains("--sandbox workspace-write", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("--sandbox {sandboxMode}", StringComparison.OrdinalIgnoreCase);
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

    private static WorkerProfilePatchCapability EvaluateClaudePatchCapability(string normalized)
    {
        var hasNonInteractivePermissionMode =
            normalized.Contains("--permission-mode acceptEdits", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("--permission-mode bypassPermissions", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("--dangerously-skip-permissions", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("--permission-mode {permissionMode}", StringComparison.OrdinalIgnoreCase);
        var readsPromptContent = normalized.Contains("Get-Content -Raw {promptPath}", StringComparison.OrdinalIgnoreCase);

        if (hasNonInteractivePermissionMode && readsPromptContent)
        {
            return new WorkerProfilePatchCapability(
                true,
                "Claude launcher is patch-capable: non-interactive permission mode and prompt content are configured.");
        }

        var missing = new List<string>();
        if (!hasNonInteractivePermissionMode)
        {
            // In print mode Claude denies file edits without a permission mode and still exits 0,
            // so a missing flag silently completes Developer tasks without doing the work.
            missing.Add("--permission-mode acceptEdits|bypassPermissions");
        }

        if (!readsPromptContent)
        {
            missing.Add("Get-Content -Raw {promptPath}");
        }

        return new WorkerProfilePatchCapability(
            false,
            $"Claude launcher is not patch-capable; missing {string.Join(", ", missing)}.");
    }

    private static bool IsCodexCommand(string commandTemplate)
    {
        return IsLauncherCommand(commandTemplate, "codex");
    }

    private static bool IsClaudeCommand(string commandTemplate)
    {
        return IsLauncherCommand(commandTemplate, "claude");
    }

    private static bool IsLauncherCommand(string commandTemplate, string launcher)
    {
        var trimmed = commandTemplate.TrimStart();
        if (trimmed.StartsWith("& ", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..].TrimStart();
        }

        if (trimmed.StartsWith($"\"{launcher}\"", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith($"'{launcher}'", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return trimmed.StartsWith($"{launcher} ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals(launcher, StringComparison.OrdinalIgnoreCase);
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
            new WorkerProfile("codex-cli", "codex exec --skip-git-repo-check --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory} (Get-Content -Raw {promptPath})"),
            new WorkerProfile("codex-oss-cli", "codex exec --skip-git-repo-check --oss --local-provider ollama --model {subscriptionModelName} --sandbox workspace-write --cd {workingDirectory} (Get-Content -Raw {promptPath})"),
            new WorkerProfile("qwen-code-cli", "$env:OPENAI_BASE_URL='http://127.0.0.1:11434/v1'; $env:OPENAI_API_KEY='ollama'; $env:OPENAI_MODEL={subscriptionModelName}; Set-Location {workingDirectory}; qwen --yolo -p (Get-Content -Raw {promptPath})"),
            new WorkerProfile("claude-cli", "'' | claude --model {subscriptionModelName} --permission-mode {permissionMode} -p (Get-Content -Raw {promptPath})")
        ]);
    }
}

public static class WorkerProfileStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static WorkerProfileCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            return WorkerProfileCatalog.Default();
        }

        var catalog = TryDeserialize(path);
        if (catalog?.Profiles is not null && catalog.Profiles.Count > 0)
        {
            return RepairBuiltInSubscriptionProfiles(WorkerProfileCatalog.Default().Merge(catalog));
        }

        var bak = path + ".bak";
        if (File.Exists(bak))
        {
            Console.Error.WriteLine($"[WorkerProfileStore] WARNING: '{Path.GetFileName(path)}' is corrupt or empty; recovering from backup.");
            var bakCatalog = TryDeserialize(bak);
            if (bakCatalog?.Profiles is not null && bakCatalog.Profiles.Count > 0)
            {
                return RepairBuiltInSubscriptionProfiles(WorkerProfileCatalog.Default().Merge(bakCatalog));
            }
            Console.Error.WriteLine("[WorkerProfileStore] WARNING: backup is also corrupt; falling back to built-in defaults.");
        }
        else
        {
            Console.Error.WriteLine($"[WorkerProfileStore] WARNING: '{Path.GetFileName(path)}' is corrupt or empty and no backup exists; falling back to built-in defaults.");
        }

        return RepairBuiltInSubscriptionProfiles(WorkerProfileCatalog.Default());
    }

    public static WorkerProfileCatalog LoadRequired(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Worker profile file was not found.", path);
        }

        var catalog = TryDeserialize(path);
        if (catalog?.Profiles is not null && catalog.Profiles.Count > 0)
        {
            return new WorkerProfileCatalog([]).Merge(catalog);
        }

        var bak = path + ".bak";
        if (File.Exists(bak))
        {
            Console.Error.WriteLine($"[WorkerProfileStore] WARNING: '{Path.GetFileName(path)}' is corrupt or empty; recovering from backup.");
            var bakCatalog = TryDeserialize(bak);
            if (bakCatalog?.Profiles is not null && bakCatalog.Profiles.Count > 0)
            {
                return new WorkerProfileCatalog([]).Merge(bakCatalog);
            }
        }

        throw new InvalidDataException("Worker profile file did not contain any profiles.");
    }

    public static void Save(string path, WorkerProfileCatalog catalog)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmp = path + ".tmp";
        var bak = path + ".bak";
        File.WriteAllText(tmp, JsonSerializer.Serialize(catalog, _jsonOptions));
        if (File.Exists(path))
            File.Replace(tmp, path, bak);
        else
            File.Move(tmp, path);
    }

    private static WorkerProfileCatalog? TryDeserialize(string path)
    {
        try { return JsonSerializer.Deserialize<WorkerProfileCatalog>(File.ReadAllText(path), _jsonOptions); }
        catch { return null; }
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
            return !profile.CommandTemplate.Contains("{sandboxMode}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--cd", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--model {subscriptionModelName}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.OrdinalIgnoreCase);
        }

        return profile.Name.Equals("claude-cli", StringComparison.OrdinalIgnoreCase) &&
            (!profile.CommandTemplate.Contains("--model {subscriptionModelName}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("{permissionMode}", StringComparison.OrdinalIgnoreCase) ||
                !WorkerProfileDiagnostics.EvaluatePatchCapability(profile.CommandTemplate).IsPatchCapable);
    }
}
