using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProviderCommandBuilderParityTests
{
    // Full tests-directory Contains/DoesNotContain command-shape audit:
    // built-in/default dispatch assertions are in WorkerProfileTests, WorkerDispatchTestsDispatchPreparation,
    // WorkerDispatchTestsModelSelection, WorkerDispatchTestsModelSelectionEnvMutation, and
    // WorkerDispatchTestsSandboxLowIntegrity. InquiryDispatcherTests covers the separate inquiry path.
    // AdvanceLoopTests, ProgressiveReviewGlanceTests, ProgressiveReviewSteeringTests,
    // RealWorkerProcessGuardTests, StatePersistenceAndPerformanceTests, WorkerDispatchJobAccountingTests,
    // and Core RepositoryChangeClassifierTests use test-local commands/profiles rather than built-in dispatch.
    public static IEnumerable<object[]> BuiltInParityCases()
    {
        foreach (var providerKind in new[]
                 {
                     ProviderKind.OpenAICodexCli,
                     ProviderKind.OpenAICodexLuna,
                     ProviderKind.OpenAICodexOssCli,
                     ProviderKind.AnthropicClaudeCli,
                     ProviderKind.OllamaQwenCodeCli
                 })
        {
            foreach (var isWriteCapable in new[] { false, true })
            {
                foreach (var osSandbox in new[] { false, true })
                {
                    yield return [providerKind, isWriteCapable, osSandbox, "populated"];
                    yield return [providerKind, isWriteCapable, osSandbox, "blank"];
                    yield return [providerKind, isWriteCapable, osSandbox, "null"];
                }
            }
        }
    }

    [Xunit.Theory(DisplayName = "ProviderCommandBuilder_matches_legacy_template_for_each_builtin_provider")]
    [Xunit.MemberData(nameof(BuiltInParityCases))]
    public void ProviderCommandBuilderMatchesLegacyTemplateForEachBuiltInProvider(
        ProviderKind providerKind,
        bool isWriteCapable,
        bool osSandbox,
        string valueState)
    {
        var provider = WorkerProviderCatalog.Default().Resolve(providerKind);
        var profile = WorkerProfileCatalog.Default().GetRequired(provider.ProfileName);
        string? modelAlias = providerKind == ProviderKind.OllamaQwenCodeCli
            ? "qwen3:8b"
            : providerKind == ProviderKind.AnthropicClaudeCli
                ? "claude-sonnet-4-6"
                : AgentCatalog.OpenAiSubscriptionModelAlias;
        string? reasoningEffort = "high";
        string? permissionMode = isWriteCapable ? "bypassPermissions" : "plan";
        string? sandboxMode = osSandbox
            ? "danger-full-access"
            : isWriteCapable ? "workspace-write" : "read-only";
        string? workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "provider command parity",
            "worker's repo");
        if (valueState == "blank")
        {
            modelAlias = string.Empty;
            reasoningEffort = string.Empty;
            permissionMode = string.Empty;
            sandboxMode = string.Empty;
            workingDirectory = string.Empty;
        }
        else if (valueState == "null")
        {
            modelAlias = null;
            reasoningEffort = null;
            permissionMode = null;
            sandboxMode = null;
            workingDirectory = null;
        }

        var promptRoot = Path.Combine(
            Path.GetTempPath(),
            $"provider-command-builder-{Guid.NewGuid():N}");
        string? openaiBaseUrl = LlamaCppDefaults.BuildOpenAiCompatibleBaseUrl(LlamaCppDefaults.BaseUrl);
        string? openaiApiKey = LlamaCppDefaults.OpenAiApiKey;
        string? approvalMode = isWriteCapable ? "yolo" : "plan";
        if (valueState == "blank")
        {
            openaiBaseUrl = string.Empty;
            openaiApiKey = string.Empty;
            approvalMode = string.Empty;
        }
        else if (valueState == "null")
        {
            openaiBaseUrl = null;
            openaiApiKey = null;
            approvalMode = null;
        }

        var variables = new Dictionary<string, string?>
        {
            ["subscriptionModelName"] = modelAlias,
            ["subscriptionReasoningEffort"] = reasoningEffort,
            ["permissionMode"] = permissionMode,
            ["sandboxMode"] = sandboxMode,
            ["workingDirectory"] = workingDirectory,
            ["openaiBaseUrl"] = openaiBaseUrl,
            ["openaiApiKey"] = openaiApiKey,
            ["approvalMode"] = approvalMode
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
            var routedTemplate = WorkerProfileDispatcher.BuildDispatchCommandTemplate(
                profile,
                providerKind,
                variables);
            var routed = routedTemplate.Replace(
                "{promptPath}",
                Quote(legacy.PromptPath),
                StringComparison.OrdinalIgnoreCase);

            if (providerKind == ProviderKind.AnthropicClaudeCli &&
                !isWriteCapable &&
                valueState == "populated")
            {
                Assert.Contains("--permission-mode 'dontAsk'", routed, StringComparison.Ordinal);
                Assert.Contains("--restricted", routed, StringComparison.Ordinal);
                Assert.Contains(
                    "--tools 'Read,Glob,Grep,Bash,WebFetch,WebSearch,TodoWrite'",
                    routed,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "--allowed-tools 'Read,Glob,Grep,Bash(git log *),Bash(git diff *),Bash(git show *),Bash(git status *),Bash(git merge-base *),Bash(git rev-parse *),Bash(git blame *),Bash(git ls-files *),Bash(git branch *),Bash(git cat-file *),Bash(rg *),WebFetch,WebSearch,TodoWrite'",
                    routed,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "--disallowed-tools 'Edit,Write,NotebookEdit'",
                    routed,
                    StringComparison.Ordinal);
                Assert.DoesNotContain("--allowed-tools 'Read,Glob,Grep,Bash,", routed, StringComparison.Ordinal);
                Assert.DoesNotContain(",Task", routed, StringComparison.Ordinal);
                Assert.DoesNotContain("--permission-mode 'plan'", routed, StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(legacy.Command, routed);
            }
        }
        finally
        {
            if (Directory.Exists(promptRoot))
            {
                Directory.Delete(promptRoot, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ProviderCommandBuilder_qwen_reads_prompt_from_stdin")]
    public void ProviderCommandBuilderQwenReadsPromptFromStdin()
    {
        var command = string.Join(
            ' ',
            ProviderCommandBuilder.Build(
                ProviderKind.OllamaQwenCodeCli,
                "qwen3.6-35b-a3b",
                reasoningEffort: null,
                resolvedPermissionMode: "bypassPermissions",
                resolvedSandboxMode: "workspace-write",
                workingDirectory: @"C:\worker repo",
                openaiBaseUrl: "http://127.0.0.1:8080/v1",
                openaiApiKey: "llamacpp",
                approvalMode: "yolo"));

        Assert.Contains("--input-format text", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-p", command, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content", command, StringComparison.Ordinal);
        Assert.DoesNotContain("{promptPath}", command, StringComparison.Ordinal);
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
            "claude -p --model 'claude-sonnet-4-6' --permission-mode 'dontAsk' --restricted --tools 'Read,Glob,Grep,Bash,WebFetch,WebSearch,TodoWrite' --allowed-tools 'Read,Glob,Grep,Bash(git log *),Bash(git diff *),Bash(git show *),Bash(git status *),Bash(git merge-base *),Bash(git rev-parse *),Bash(git blame *),Bash(git ls-files *),Bash(git branch *),Bash(git cat-file *),Bash(rg *),WebFetch,WebSearch,TodoWrite' --disallowed-tools 'Edit,Write,NotebookEdit' --session-id 12345678-1234-1234-1234-123456789abc",
            string.Join(' ', command));
    }

    [Xunit.Theory(DisplayName = "ProviderCommandBuilder_composes_structured_output_for_subscription_codex")]
    [Xunit.InlineData(ProviderKind.OpenAICodexCli)]
    [Xunit.InlineData(ProviderKind.OpenAICodexLuna)]
    public void ProviderCommandBuilderComposesStructuredOutputForSubscriptionCodex(
        ProviderKind providerKind)
    {
        var profile = WorkerProfileCatalog.Default().GetRequired(
            WorkerProviderCatalog.Default().Resolve(providerKind).ProfileName);
        var command = WorkerProfileDispatcher.BuildDispatchCommandTemplate(
            profile,
            providerKind,
            new Dictionary<string, string?>
            {
                ["subscriptionModelName"] = AgentCatalog.OpenAiSolSubscriptionModelAlias,
                ["subscriptionReasoningEffort"] = "high",
                ["permissionMode"] = "plan",
                ["sandboxMode"] = "read-only",
                ["workingDirectory"] = @"C:\worker repo"
            });

        Assert.Equal(
            $"codex exec --json --skip-git-repo-check --model '{AgentCatalog.OpenAiSolSubscriptionModelAlias}' -c model_reasoning_effort='high' -c project_doc_max_bytes=65536 --sandbox 'read-only' --cd 'C:\\worker repo'",
            command);
    }

    [Xunit.Fact(DisplayName = "ProviderCommandBuilder_preserves_blank_codex_template_literals")]
    public void ProviderCommandBuilderPreservesBlankCodexTemplateLiterals()
    {
        var command = string.Join(
            ' ',
            ProviderCommandBuilder.Build(
                ProviderKind.OpenAICodexCli,
                AgentCatalog.OpenAiSubscriptionModelAlias,
                reasoningEffort: null,
                resolvedPermissionMode: "plan",
                resolvedSandboxMode: "read-only",
                workingDirectory: null));

        Assert.Equal(
            $"codex exec --json --skip-git-repo-check --model '{AgentCatalog.OpenAiSubscriptionModelAlias}' -c model_reasoning_effort= --sandbox 'read-only' --cd ",
            command);
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''")}'";
}
