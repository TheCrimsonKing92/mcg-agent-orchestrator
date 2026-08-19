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
        var normalized = commandTemplate.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && !IsEchoOnlyCommand(normalized);
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

    public static WorkerProfileLauncherValidation EvaluateRealLauncher(
        WorkerProfile profile,
        IWorkerProvider provider,
        Func<string, bool>? commandExists = null)
    {
        var executable = ExtractExecutable(profile.CommandTemplate);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return new WorkerProfileLauncherValidation(false, executable, "No executable was found in the command template.");
        }

        if (IsEchoOnlyCommand(profile.CommandTemplate))
        {
            return new WorkerProfileLauncherValidation(false, executable, "Command only echoes the prompt path; it does not execute worker work.");
        }

        var expected = ExpectedExecutableNames(provider);
        var executableName = Path.GetFileNameWithoutExtension(executable);
        if (expected.Length > 0 &&
            !expected.Any(name => executableName.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return new WorkerProfileLauncherValidation(
                false,
                executable,
                $"Launcher executable '{executable}' is not the expected {string.Join("/", expected)} CLI.");
        }

        var exists = (commandExists ?? LocalCommandExists)(executable);
        return exists
            ? new WorkerProfileLauncherValidation(true, executable, "Launcher executable exists and matches the provider CLI.")
            : new WorkerProfileLauncherValidation(false, executable, $"Launcher executable '{executable}' was not found on PATH or as a file.");
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

        return new WorkerProfilePatchCapability(
            true,
            "Untyped worker command capability cannot be inferred beyond executing the prompt.");
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
            ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexSpark or ProviderKind.OpenAICodexOssCli => EvaluateCodexPatchCapability(normalized),
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
        var readsPromptFromStdin = !normalized.Contains("{promptPath}", StringComparison.OrdinalIgnoreCase);

        if (hasWorkspaceWrite && hasWorkingDirectory && readsPromptFromStdin)
        {
            return new WorkerProfilePatchCapability(
                true,
                "Codex launcher is patch-capable: workspace-write sandbox, repository working directory, and stdin prompt delivery are configured.");
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

        if (!readsPromptFromStdin)
        {
            missing.Add("stdin prompt delivery");
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
        var readsPromptFromStdin = !normalized.Contains("{promptPath}", StringComparison.OrdinalIgnoreCase);

        if (hasNonInteractivePermissionMode && readsPromptFromStdin)
        {
            return new WorkerProfilePatchCapability(
                true,
                "Claude launcher is patch-capable: non-interactive permission mode and stdin prompt delivery are configured.");
        }

        var missing = new List<string>();
        if (!hasNonInteractivePermissionMode)
        {
            // In print mode Claude denies file edits without a permission mode and still exits 0,
            // so a missing flag silently completes Developer tasks without doing the work.
            missing.Add("--permission-mode acceptEdits|bypassPermissions");
        }

        if (!readsPromptFromStdin)
        {
            missing.Add("stdin prompt delivery");
        }

        return new WorkerProfilePatchCapability(
            false,
            $"Claude launcher is not patch-capable; missing {string.Join(", ", missing)}.");
    }

    public static string ExtractExecutable(string commandTemplate)
    {
        var trimmed = ExtractExecutableStatement(commandTemplate);
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

    public static bool LocalCommandExists(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        if (IsKnownPowerShellCommand(executable))
        {
            return true;
        }

        if (Path.IsPathRooted(executable))
        {
            return File.Exists(executable) || CandidateExecutablePaths(executable).Any(File.Exists);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in CandidateExecutablePaths(Path.Combine(directory, executable)))
            {
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ExtractExecutableStatement(string commandTemplate)
    {
        foreach (var statement in SplitPowerShellStatements(commandTemplate))
        {
            var trimmed = statement.Trim();
            if (trimmed.Length == 0 || IsPowerShellSetupStatement(trimmed))
            {
                continue;
            }

            return trimmed;
        }

        return string.Empty;
    }

    private static IEnumerable<string> SplitPowerShellStatements(string commandTemplate)
    {
        var start = 0;
        char? quote = null;
        for (var index = 0; index < commandTemplate.Length; index++)
        {
            var current = commandTemplate[index];
            if (quote is not null)
            {
                if (current == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }

            if (current == ';')
            {
                yield return commandTemplate[start..index];
                start = index + 1;
            }
        }

        yield return commandTemplate[start..];
    }

    private static bool IsPowerShellSetupStatement(string statement) =>
        IsPowerShellEnvironmentAssignment(statement) ||
        IsPowerShellLocationStatement(statement);

    private static bool IsPowerShellEnvironmentAssignment(string statement)
    {
        if (!statement.StartsWith("$env:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separator = statement.IndexOf('=');
        return separator > "$env:".Length;
    }

    private static bool IsPowerShellLocationStatement(string statement) =>
        statement.StartsWith("Set-Location ", StringComparison.OrdinalIgnoreCase) ||
        statement.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) ||
        statement.StartsWith("Push-Location ", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownPowerShellCommand(string executable) =>
        executable.Equals("Write-Output", StringComparison.OrdinalIgnoreCase) ||
        executable.Equals("Write-Host", StringComparison.OrdinalIgnoreCase) ||
        executable.Equals("echo", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> CandidateExecutablePaths(string basePath)
    {
        if (!string.IsNullOrWhiteSpace(Path.GetExtension(basePath)))
        {
            yield return basePath;
            yield break;
        }

        yield return basePath;
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        string[] extensions = string.IsNullOrWhiteSpace(pathExt)
            ? [".COM", ".EXE", ".BAT", ".CMD", ".PS1"]
            : pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var extension in extensions)
        {
            yield return basePath + extension;
        }
    }

    private static string[] ExpectedExecutableNames(IWorkerProvider provider) =>
        provider.Identity.Kind switch
        {
            ProviderKind.AnthropicClaudeCli => ["claude"],
            ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexSpark or ProviderKind.OpenAICodexOssCli => ["codex"],
            ProviderKind.OllamaQwenCodeCli => ["qwen"],
            _ => []
        };
}

public sealed record WorkerProfilePatchCapability(bool IsPatchCapable, string Detail);
public sealed record WorkerProfileLauncherValidation(bool IsRealLauncher, string Executable, string Detail);

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
            new WorkerProfile("codex-cli", "codex exec --json --skip-git-repo-check --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory}"),
            new WorkerProfile("codex-spark", "codex exec --json --skip-git-repo-check --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory}"),
            new WorkerProfile("codex-oss-cli", "codex exec --skip-git-repo-check --oss --local-provider ollama --model {subscriptionModelName} --sandbox {sandboxMode} --cd {workingDirectory}"),
            new WorkerProfile("qwen-code-cli", "$env:OPENAI_BASE_URL={openaiBaseUrl}; $env:OPENAI_API_KEY={openaiApiKey}; $env:OPENAI_MODEL={subscriptionModelName}; Set-Location {workingDirectory}; qwen --bare --approval-mode {approvalMode} --input-format text"),
            // -p = headless print mode; without it Claude opens the interactive REPL and emits nothing (exits 0 empty, so the task is wrongly classified Failed). The prompt is piped via stdin and --session-id is appended by the spawn layer.
            new WorkerProfile("claude-cli", "claude -p --model {subscriptionModelName} --permission-mode {permissionMode}"),
            new WorkerProfile("grok-cli", "grok --prompt-file {promptPath} --model {subscriptionModelName} --permission-mode {permissionMode} --cwd {workingDirectory} --output-format plain --no-subagents --verbatim --max-turns 32")
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
        var providers = WorkerProviderCatalog.Default();
        foreach (var profileName in new[]
                  {
                      WorkerProfileDispatcher.OpenAiSubscriptionProfileName,
                      providers.Resolve(ProviderKind.OpenAICodexSpark).ProfileName,
                      providers.Resolve(ProviderKind.OpenAICodexOssCli).ProfileName,
                      WorkerProfileDispatcher.AnthropicSubscriptionProfileName,
                      WorkerProfileDispatcher.QwenCodeCliProfileName
                  })
        {
            var current = repaired.GetRequired(profileName);
            if (ShouldRepairBuiltInSubscriptionProfile(current, providers.ResolveProfile(profileName)))
            {
                repaired = repaired.Upsert(defaults.GetRequired(profileName));
            }
        }

        return repaired;
    }

    private static bool ShouldRepairBuiltInSubscriptionProfile(WorkerProfile profile, IWorkerProvider provider)
    {
        if (WorkerProfileDiagnostics.IsEchoOnlyCommand(profile.CommandTemplate))
        {
            return true;
        }

        if (provider.Identity.Kind is ProviderKind.OpenAICodexCli or ProviderKind.OpenAICodexSpark)
        {
            const string legacyStructuredOutputTemplate =
                "codex exec --skip-git-repo-check --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} --sandbox {sandboxMode} --cd {workingDirectory}";
            var missingStructuredOutputFromKnownBuiltIn =
                !profile.CommandTemplate.Contains("--json", StringComparison.OrdinalIgnoreCase) &&
                profile.CommandTemplate.Equals(legacyStructuredOutputTemplate, StringComparison.OrdinalIgnoreCase);
            return !profile.CommandTemplate.Contains("{sandboxMode}", StringComparison.OrdinalIgnoreCase) ||
                missingStructuredOutputFromKnownBuiltIn ||
                !profile.CommandTemplate.Contains("--cd", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--model {subscriptionModelName}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("model_reasoning_effort={subscriptionReasoningEffort}", StringComparison.OrdinalIgnoreCase) ||
                profile.CommandTemplate.Contains("{promptPath}", StringComparison.OrdinalIgnoreCase);
        }

        if (provider.Identity.Kind is ProviderKind.OpenAICodexOssCli)
        {
            return !profile.CommandTemplate.Contains("--oss", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--local-provider ollama", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--sandbox {sandboxMode}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--cd", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--model {subscriptionModelName}", StringComparison.OrdinalIgnoreCase) ||
                profile.CommandTemplate.Contains("{promptPath}", StringComparison.OrdinalIgnoreCase);
        }

        if (provider.Identity.Kind is ProviderKind.OllamaQwenCodeCli)
        {
            return !profile.CommandTemplate.Contains("{openaiBaseUrl}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("{openaiApiKey}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("{approvalMode}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("--bare", StringComparison.OrdinalIgnoreCase) ||
                profile.CommandTemplate.Contains("11434", StringComparison.Ordinal) ||
                profile.CommandTemplate.Contains("--yolo", StringComparison.OrdinalIgnoreCase) ||
                profile.CommandTemplate.Contains("{promptPath}", StringComparison.OrdinalIgnoreCase) ||
                profile.CommandTemplate.Contains("-p (Get-Content", StringComparison.OrdinalIgnoreCase);
        }

        return provider.Identity.Kind is ProviderKind.AnthropicClaudeCli &&
            (!profile.CommandTemplate.Contains("--model {subscriptionModelName}", StringComparison.OrdinalIgnoreCase) ||
                !profile.CommandTemplate.Contains("{permissionMode}", StringComparison.OrdinalIgnoreCase) ||
                !WorkerProfileDiagnostics.EvaluatePatchCapability(profile, provider).IsPatchCapable);
    }
}
