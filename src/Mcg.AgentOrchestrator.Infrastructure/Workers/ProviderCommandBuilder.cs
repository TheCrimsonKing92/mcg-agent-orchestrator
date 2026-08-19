using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ProviderCommandBuilder
{
    public static IReadOnlyList<string> Build(
        ProviderKind providerKind,
        string modelAlias,
        string? reasoningEffort,
        string resolvedPermissionMode,
        string resolvedSandboxMode,
        string? workingDirectory,
        string? sessionId = null,
        string? openaiBaseUrl = null,
        string? openaiApiKey = null,
        string? approvalMode = null)
    {
        return providerKind switch
        {
            ProviderKind.OpenAICodexCli => BuildCodex(
                modelAlias,
                reasoningEffort,
                resolvedSandboxMode,
                workingDirectory),
            ProviderKind.OpenAICodexSpark => BuildCodex(
                modelAlias,
                reasoningEffort,
                resolvedSandboxMode,
                workingDirectory),
            ProviderKind.OpenAICodexOssCli => BuildCodexOss(
                modelAlias,
                resolvedSandboxMode,
                workingDirectory),
            ProviderKind.AnthropicClaudeCli => BuildClaude(
                modelAlias,
                resolvedPermissionMode,
                sessionId),
            ProviderKind.OllamaQwenCodeCli => BuildQwen(modelAlias, workingDirectory, openaiBaseUrl, openaiApiKey, approvalMode),
            _ => throw new ArgumentOutOfRangeException(
                nameof(providerKind),
                providerKind,
                "Provider command building is supported only for built-in worker providers.")
        };
    }

    public static bool IsBuiltIn(ProviderKind providerKind) =>
        providerKind is ProviderKind.OpenAICodexCli
            or ProviderKind.OpenAICodexSpark
            or ProviderKind.OpenAICodexOssCli
            or ProviderKind.AnthropicClaudeCli
            or ProviderKind.OllamaQwenCodeCli;

    private static IReadOnlyList<string> BuildCodex(
        string modelAlias,
        string? reasoningEffort,
        string resolvedSandboxMode,
        string? workingDirectory)
    {
        var command = new List<string>
        {
            "codex",
            "exec",
            "--json",
            "--skip-git-repo-check",
            "--model",
            Expand(modelAlias)
        };
        command.Add("-c");
        command.Add($"model_reasoning_effort={Expand(reasoningEffort)}");
        command.Add("--sandbox");
        command.Add(Expand(resolvedSandboxMode));
        AddWorkingDirectory(command, workingDirectory);
        return command;
    }

    private static IReadOnlyList<string> BuildCodexOss(
        string modelAlias,
        string resolvedSandboxMode,
        string? workingDirectory)
    {
        var command = new List<string>
        {
            "codex",
            "exec",
            "--skip-git-repo-check",
            "--oss",
            "--local-provider",
            "ollama",
            "--model",
            Expand(modelAlias),
            "--sandbox",
            Expand(resolvedSandboxMode)
        };
        AddWorkingDirectory(command, workingDirectory);
        return command;
    }

    private static IReadOnlyList<string> BuildClaude(
        string modelAlias,
        string resolvedPermissionMode,
        string? sessionId)
    {
        var command = new List<string>
        {
            "claude",
            "-p",
            "--model",
            Expand(modelAlias),
            "--permission-mode",
            Expand(resolvedPermissionMode)
        };
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            command.Add("--session-id");
            command.Add(sessionId);
        }

        return command;
    }

    private static IReadOnlyList<string> BuildQwen(
        string modelAlias,
        string? workingDirectory,
        string? openaiBaseUrl,
        string? openaiApiKey,
        string? approvalMode)
    {
        var command = new List<string>
        {
            $"$env:OPENAI_BASE_URL={Expand(openaiBaseUrl)};",
            $"$env:OPENAI_API_KEY={Expand(openaiApiKey)};",
            $"$env:OPENAI_MODEL={Expand(modelAlias)};"
        };
        command.Add("Set-Location");
        command.Add($"{Expand(workingDirectory)};");
        command.Add("qwen");
        command.Add("--bare");
        command.Add("--approval-mode");
        command.Add(Expand(approvalMode));
        command.Add("--input-format");
        command.Add("text");
        return command;
    }

    private static void AddWorkingDirectory(List<string> command, string? workingDirectory)
    {
        command.Add("--cd");
        command.Add(Expand(workingDirectory));
    }

    private static string Expand(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Quote(value);

    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";
}
