using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProviderCommandBuilderParityTests
{
    // Full tests-directory command-shape audit:
    // built-in/default dispatch assertions are in WorkerProfileTests,
    // WorkerDispatchTestsDispatchPreparation, WorkerDispatchTestsModelSelection,
    // WorkerDispatchTestsModelSelectionEnvMutation, and WorkerDispatchTestsSandboxLowIntegrity.
    // InquiryDispatcherTests covers the separate inquiry path; other matches use test-local commands.
    public static IEnumerable<object[]> BuiltInParityCases()
    {
        foreach (var providerKind in new[]
                 {
                     ProviderKind.OpenAICodexCli,
                     ProviderKind.OpenAICodexSpark,
                     ProviderKind.OpenAICodexOssCli,
                     ProviderKind.AnthropicClaudeCli,
                     ProviderKind.OllamaQwenCodeCli
                 })
        {
            yield return [providerKind, false, false];
            yield return [providerKind, true, false];
            if (providerKind is ProviderKind.OpenAICodexCli
                or ProviderKind.OpenAICodexSpark
                or ProviderKind.OpenAICodexOssCli)
            {
                yield return [providerKind, false, true];
                yield return [providerKind, true, true];
            }
        }
    }

    [Xunit.Theory(DisplayName = "ProviderCommandBuilder_matches_legacy_template_for_each_builtin_provider")]
    [Xunit.MemberData(nameof(BuiltInParityCases))]
    public void ProviderCommandBuilderMatchesLegacyTemplateForEachBuiltInProvider(
        ProviderKind providerKind,
        bool isWriteCapable,
        bool osSandbox)
    {
        var provider = WorkerProviderCatalog.Default().Resolve(providerKind);
        var profile = WorkerProfileCatalog.Default().GetRequired(provider.ProfileName);
        var modelAlias = providerKind == ProviderKind.OllamaQwenCodeCli
            ? "qwen3:8b"
            : providerKind == ProviderKind.AnthropicClaudeCli
                ? "claude-sonnet-4-6"
                : "gpt-5.5";
        var reasoningEffort = "high";
        var permissionMode = isWriteCapable ? "bypassPermissions" : "plan";
        var sandboxMode = osSandbox
            ? "danger-full-access"
            : isWriteCapable ? "workspace-write" : "read-only";
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "provider command parity",
            "worker's repo");
        var promptRoot = Path.Combine(
            Path.GetTempPath(),
            $"provider-command-builder-{Guid.NewGuid():N}");
        var variables = new Dictionary<string, string?>
        {
            ["subscriptionModelName"] = modelAlias,
            ["subscriptionReasoningEffort"] = reasoningEffort,
            ["permissionMode"] = permissionMode,
            ["sandboxMode"] = sandboxMode,
            ["workingDirectory"] = workingDirectory
        };

        try
        {
            var legacy = WorkerCommandTemplate.Prepare(
                new TaskBrief(
                    new GoalId("goal123456789"),
                    new TaskId("task123456789"),
                    isWriteCapable ? AgentRole.Developer : AgentRole.Researcher,
                    "Provider command parity",
                    "Parity prompt."),
                profile.Name,
                profile.CommandTemplate,
                promptRoot,
                variables,
                DateTimeOffset.Parse("2026-07-28T12:00:00Z"));
            var typedTemplate = string.Join(
                ' ',
                ProviderCommandBuilder.Build(
                    providerKind,
                    modelAlias,
                    reasoningEffort,
                    permissionMode,
                    sandboxMode,
                    workingDirectory));
            var typed = typedTemplate.Replace(
                "{promptPath}",
                Quote(legacy.PromptPath),
                StringComparison.OrdinalIgnoreCase);

            Assert.Equal(legacy.Command, typed);
        }
        finally
        {
            if (Directory.Exists(promptRoot))
            {
                Directory.Delete(promptRoot, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ProviderCommandBuilder_appends_optional_claude_session_id")]
    public void ProviderCommandBuilderAppendsOptionalClaudeSessionId()
    {
        var command = ProviderCommandBuilder.Build(
            ProviderKind.AnthropicClaudeCli,
            "claude-sonnet-4-6",
            reasoningEffort: null,
            resolvedPermissionMode: "plan",
            resolvedSandboxMode: "read-only",
            workingDirectory: null,
            sessionId: "12345678-1234-1234-1234-123456789abc");

        Assert.Equal(
            "claude -p --model 'claude-sonnet-4-6' --permission-mode 'plan' --session-id 12345678-1234-1234-1234-123456789abc",
            string.Join(' ', command));
    }

    [Xunit.Fact(DisplayName = "ProviderCommandBuilder_omits_unset_codex_optional_arguments")]
    public void ProviderCommandBuilderOmitsUnsetCodexOptionalArguments()
    {
        var command = string.Join(
            ' ',
            ProviderCommandBuilder.Build(
                ProviderKind.OpenAICodexCli,
                "gpt-5.5",
                reasoningEffort: null,
                resolvedPermissionMode: "plan",
                resolvedSandboxMode: "read-only",
                workingDirectory: null));

        Assert.Equal(
            "codex exec --skip-git-repo-check --model 'gpt-5.5' --sandbox 'read-only'",
            command);
        Assert.DoesNotContain("model_reasoning_effort", command, StringComparison.Ordinal);
        Assert.DoesNotContain("--cd", command, StringComparison.Ordinal);
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";
}
