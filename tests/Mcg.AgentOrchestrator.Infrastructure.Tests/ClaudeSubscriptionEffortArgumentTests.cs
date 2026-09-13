using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Claude subscription invocations used to drop the configured reasoning-effort policy: the built-in
// claude-cli template carried no effort argument, so the {subscriptionReasoningEffort} substitution that
// both consumers perform was a no-op. These facts pin the repaired seam - substitution is the only
// injection path, an unset effort renders nothing, a superseded built-in is repaired in memory, and an
// operator's custom template is never rewritten.
public sealed class ClaudeSubscriptionEffortArgumentTests
{
    private const string CurrentBuiltInTemplate =
        "claude -p --model {subscriptionModelName} --permission-mode {permissionMode} --effort {subscriptionReasoningEffort}";
    private const string ObservedStaleBuiltInTemplate =
        "claude -p --model {subscriptionModelName} --permission-mode {permissionMode}";

    [Xunit.Fact(DisplayName = "ClaudeEffort_builtin_dispatch_command_materializes_configured_effort_exactly_once")]
    public void BuiltInDispatchCommandMaterializesConfiguredEffortExactlyOnce()
    {
        var command = BuildClaudeDispatchCommand(reasoningEffort: "high");

        Assert.Equal(
            "claude -p --model 'claude-opus-5' --permission-mode 'bypassPermissions' --effort 'high'",
            command);
        Assert.Equal(1, CountOccurrences(command, "--effort"));
        // Single quotes are how every subscription value in this repository is rendered; PowerShell
        // strips them, so the process argv is the `--effort high` pair the recorded control used.
        Assert.Equal(1, CountOccurrences(command.Replace("'", string.Empty), "--effort high"));
        Assert.Contains("--model 'claude-opus-5'", command, StringComparison.Ordinal);
        Assert.Contains("--permission-mode 'bypassPermissions'", command, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_unset_effort_renders_no_flag_and_no_dangling_argument")]
    public void UnsetEffortRendersNoFlagAndNoDanglingArgument()
    {
        foreach (var unset in new[] { null, string.Empty, "   " })
        {
            var command = BuildClaudeDispatchCommand(unset);

            Assert.Equal(
                "claude -p --model 'claude-opus-5' --permission-mode 'bypassPermissions'",
                command);
            Assert.DoesNotContain("--effort", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{subscriptionReasoningEffort}", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("  ", command, StringComparison.Ordinal);
            Assert.Equal(command.TrimEnd(), command);
        }
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_unset_effort_elides_segment_on_the_legacy_substitution_path")]
    public void UnsetEffortElidesSegmentOnTheLegacySubstitutionPath()
    {
        // The legacy path renders a blank variable as string.Empty, so without the elision pre-pass an
        // unset effort would leave `--effort` with no operand - and put this path out of parity with
        // ProviderCommandBuilder, which emits no tokens at all.
        var promptRoot = Path.Combine(Path.GetTempPath(), $"claude-effort-{Guid.NewGuid():N}");
        try
        {
            var unsetCommand = PrepareLegacyCommand(promptRoot, CurrentBuiltInTemplate, reasoningEffort: null);
            var absentKeyCommand = PrepareLegacyCommand(promptRoot, CurrentBuiltInTemplate, variables: new Dictionary<string, string?>
            {
                ["subscriptionModelName"] = "claude-opus-5",
                ["permissionMode"] = "bypassPermissions"
            });
            var configuredCommand = PrepareLegacyCommand(promptRoot, CurrentBuiltInTemplate, reasoningEffort: "xhigh");

            Assert.Equal("claude -p --model 'claude-opus-5' --permission-mode 'bypassPermissions'", unsetCommand);
            // An absent key and an unset value are deliberately indistinguishable.
            Assert.Equal(unsetCommand, absentKeyCommand);
            Assert.Equal(
                "claude -p --model 'claude-opus-5' --permission-mode 'bypassPermissions' --effort 'xhigh'",
                configuredCommand);
        }
        finally
        {
            if (Directory.Exists(promptRoot))
            {
                Directory.Delete(promptRoot, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_stale_builtin_is_repaired_at_invocation_time_and_never_persisted")]
    public void StaleBuiltInIsRepairedAtInvocationTimeAndNeverPersisted()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var path = Path.Combine(root, "workers.json");
        WorkerProfileStore.Save(
            path,
            WorkerProfileCatalog.Default().Upsert(new WorkerProfile("claude-cli", ObservedStaleBuiltInTemplate)));
        var savedBytes = File.ReadAllBytes(path);
        Assert.Contains(ObservedStaleBuiltInTemplate, File.ReadAllText(path), StringComparison.Ordinal);

        var restored = WorkerProfileStore.Load(path);

        // Load must not repair: the catalog it returns is exactly what the dashboard profile editor and the
        // CLI worker-profile/export/import verbs hand back to Save, so a repair here would be written to the
        // operator's file by the next unrelated profile edit.
        Assert.Equal(ObservedStaleBuiltInTemplate, restored.GetRequired("claude-cli").CommandTemplate);
        Assert.Equal(savedBytes, File.ReadAllBytes(path));

        // An unrelated profile edit, saved from the loaded catalog, still carries the operator's bytes.
        WorkerProfileStore.Save(path, restored.Upsert(new WorkerProfile("local-echo", "Write-Host {promptPath}")));
        Assert.Equal(
            ObservedStaleBuiltInTemplate,
            WorkerProfileStore.LoadRequired(path).GetRequired("claude-cli").CommandTemplate);
        Assert.DoesNotContain(ClaudeCliEffortPolicy.EffortSegment, File.ReadAllText(path), StringComparison.Ordinal);

        // Repair happens at invocation time instead, on both construction seams, from the saved bytes.
        var dispatchDiagnostics = new List<string>();
        var dispatchCommand = WorkerProfileDispatcher.BuildDispatchCommandTemplate(
            restored.GetRequired("claude-cli"),
            ProviderKind.AnthropicClaudeCli,
            ClaudeDispatchVariables("high"),
            dispatchDiagnostics.Add);
        var completerDiagnostics = new List<string>();
        var completerCommand = SubscriptionCliCompleter.SubstitutePlaceholders(
            restored.GetRequired("claude-cli").CommandTemplate,
            "claude-cli",
            "prompt.md",
            "claude-opus-5",
            "high",
            @"C:\work",
            completerDiagnostics.Add);

        Assert.Equal(
            "claude -p --model 'claude-opus-5' --permission-mode 'bypassPermissions' --effort 'high'",
            dispatchCommand);
        Assert.Contains("--effort 'high'", completerCommand, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(dispatchCommand, "--effort"));
        Assert.Equal(1, CountOccurrences(completerCommand, "--effort"));
        // One diagnostic per seam, naming the profile and the repair.
        foreach (var diagnostic in new[] { Assert.Single(dispatchDiagnostics), Assert.Single(completerDiagnostics) })
        {
            Assert.Contains("claude-cli", diagnostic, StringComparison.Ordinal);
            Assert.Contains("superseded built-in", diagnostic, StringComparison.Ordinal);
            Assert.Contains("not modified", diagnostic, StringComparison.Ordinal);
        }

        // Constructing those invocations wrote nothing back to the store.
        Assert.Equal(
            ObservedStaleBuiltInTemplate,
            WorkerProfileStore.LoadRequired(path).GetRequired("claude-cli").CommandTemplate);
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_stale_builtin_repair_is_recomputed_per_invocation_not_cached_or_persisted")]
    public void StaleBuiltInRepairIsRecomputedPerInvocationNotCachedOrPersisted()
    {
        // Repeated invocations from the same saved bytes each materialize the effort, and the profile record
        // handed in is never mutated: the repair lives only in the string this seam returns. The per-process
        // console de-duplication of the repair line is deliberately not asserted here - the guard is
        // process-global static state shared with every other test in this assembly, so an assertion on it
        // would be order-dependent. Diagnostic content is asserted through the sink instead.
        var profile = new WorkerProfile("claude-cli", ObservedStaleBuiltInTemplate);

        for (var invocation = 0; invocation < 3; invocation++)
        {
            var diagnostics = new List<string>();
            var command = WorkerProfileDispatcher.BuildDispatchCommandTemplate(
                profile,
                ProviderKind.AnthropicClaudeCli,
                ClaudeDispatchVariables("high"),
                diagnostics.Add);

            Assert.Equal(1, CountOccurrences(command, "--effort"));
            Assert.Contains("--effort 'high'", command, StringComparison.Ordinal);
            Assert.Single(diagnostics);
            Assert.Equal(ObservedStaleBuiltInTemplate, profile.CommandTemplate);
        }
    }

    [Xunit.Theory(DisplayName = "ClaudeEffort_custom_command_template_is_preserved_byte_for_byte")]
    // Resembles the observed stale built-in but is not equal to it: exact matching keeps this a
    // customization rather than a repair target.
    [Xunit.InlineData("claude -p --model {subscriptionModelName} --permission-mode {permissionMode} --verbose")]
    [Xunit.InlineData("claude -p --model {subscriptionModelName} --permission-mode {permissionMode} --effort xhigh")]
    [Xunit.InlineData("claude    -p  --model {subscriptionModelName}   --permission-mode {permissionMode}   --debug")]
    public void CustomCommandTemplateIsPreservedByteForByte(string customTemplate)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        var path = Path.Combine(root, "workers.json");
        WorkerProfileStore.Save(
            path,
            WorkerProfileCatalog.Default().Upsert(new WorkerProfile("claude-cli", customTemplate)));

        var restored = WorkerProfileStore.Load(path).GetRequired("claude-cli").CommandTemplate;

        Assert.Equal(customTemplate, restored);
        Assert.DoesNotContain("{subscriptionReasoningEffort}", restored, StringComparison.OrdinalIgnoreCase);
        // Custom templates also bypass the typed builder, so nothing downstream rewrites them either.
        Assert.Equal(
            customTemplate,
            WorkerProfileDispatcher.BuildDispatchCommandTemplate(
                new WorkerProfile("claude-cli", customTemplate),
                ProviderKind.AnthropicClaudeCli,
                ClaudeDispatchVariables("high")));
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_never_produces_a_second_or_conflicting_effort_flag")]
    public void NeverProducesASecondOrConflictingEffortFlag()
    {
        const string customWithLiteralEffort =
            "claude -p --model {subscriptionModelName} --permission-mode {permissionMode} --effort xhigh";
        const string customWithEffortVariable =
            "claude -p --effort {subscriptionReasoningEffort} --model {subscriptionModelName} --permission-mode {permissionMode}";

        var repaired = SubstituteCompleterCommand(CurrentBuiltInTemplate, "high");
        var literal = SubstituteCompleterCommand(customWithLiteralEffort, "high");
        var variable = SubstituteCompleterCommand(customWithEffortVariable, "high");

        Assert.Equal(1, CountOccurrences(repaired, "--effort"));
        Assert.Equal(1, CountOccurrences(literal, "--effort"));
        Assert.Equal(1, CountOccurrences(variable, "--effort"));
        Assert.Contains("--effort 'high'", repaired, StringComparison.Ordinal);
        // The template-provided occurrence is authoritative; substitution appends nothing.
        Assert.Contains("--effort xhigh", literal, StringComparison.Ordinal);
        Assert.DoesNotContain("'high'", literal, StringComparison.Ordinal);
        Assert.Contains("--effort 'high'", variable, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_supported_values_come_from_a_static_allowlist")]
    public void SupportedValuesComeFromAStaticAllowlist()
    {
        // Vocabulary from `claude --help` at Claude Code CLI 2.1.269:
        // "--effort <level>  Effort level for the current session (low, medium, high, xhigh, max)".
        Assert.Equal(
            new[] { "low", "medium", "high", "xhigh", "max" },
            ClaudeCliEffortPolicy.SupportedValues);
        Assert.All(
            ClaudeCliEffortPolicy.SupportedValues,
            value => Assert.True(ClaudeCliEffortPolicy.IsSupported(value)));
        Assert.False(ClaudeCliEffortPolicy.IsSupported("ultra"));
        Assert.False(ClaudeCliEffortPolicy.IsSupported("HIGHEST"));
        // Exact tokens only: a value the CLI would reject must not reach the command line.
        Assert.False(ClaudeCliEffortPolicy.IsSupported("High"));
        Assert.False(ClaudeCliEffortPolicy.IsSupported(" high "));
        Assert.False(ClaudeCliEffortPolicy.IsSupported(null));
        Assert.False(ClaudeCliEffortPolicy.IsSupported("  "));
        Assert.Equal("low, medium, high, xhigh, max", ClaudeCliEffortPolicy.SupportedValuesDisplay);
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_unsupported_value_on_the_internal_completer_path_elides_without_failing")]
    public void UnsupportedValueOnTheInternalCompleterPathElidesWithoutFailing()
    {
        // The orchestrator-internal path has no preflight to refuse at, and criterion 7 forbids a failed
        // invocation there: refinement, review glance, and acceptance evaluation must keep running.
        const string unsupported = "ultra";
        var diagnostics = new List<string>();

        var command = SubscriptionCliCompleter.SubstitutePlaceholders(
            CurrentBuiltInTemplate,
            "claude-cli",
            "prompt.md",
            "claude-opus-5",
            unsupported,
            @"C:\work",
            diagnostics.Add);

        Assert.Equal("claude -p --model 'claude-opus-5' --permission-mode 'default'", command);
        Assert.DoesNotContain("--effort", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(unsupported, command, StringComparison.Ordinal);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("claude-cli", diagnostic, StringComparison.Ordinal);
        Assert.Contains($"'{unsupported}'", diagnostic, StringComparison.Ordinal);
        Assert.Contains("low, medium, high, xhigh, max", diagnostic, StringComparison.Ordinal);
        // Never clamped or remapped to a nearby supported value.
        Assert.DoesNotContain("--effort 'high'", command, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_custom_template_without_the_effort_variable_warns_and_still_runs")]
    public void CustomTemplateWithoutTheEffortVariableWarnsAndStillRuns()
    {
        var diagnostics = new List<string>();
        // Not the observed stale built-in: that one is repaired at invocation time. A template the operator
        // authored is preserved instead, so the effort has nowhere to land.
        const string customTemplate =
            "claude -p --model {subscriptionModelName} --permission-mode {permissionMode} --verbose";

        // Supported value, preserved custom template: the invocation must still be constructed, and the
        // unmaterialized policy must be named rather than silently dropped.
        var command = SubscriptionCliCompleter.SubstitutePlaceholders(
            customTemplate,
            "claude-cli",
            "prompt.md",
            "claude-opus-5",
            "xhigh",
            @"C:\work",
            diagnostics.Add);

        Assert.Equal("claude -p --model 'claude-opus-5' --permission-mode 'default' --verbose", command);
        Assert.DoesNotContain("--effort", command, StringComparison.OrdinalIgnoreCase);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("claude-cli", diagnostic, StringComparison.Ordinal);
        Assert.Contains("'xhigh'", diagnostic, StringComparison.Ordinal);
        Assert.Contains("not materialized", diagnostic, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_materialized_builtin_emits_no_completer_diagnostic")]
    public void MaterializedBuiltInEmitsNoCompleterDiagnostic()
    {
        var diagnostics = new List<string>();

        var command = SubscriptionCliCompleter.SubstitutePlaceholders(
            CurrentBuiltInTemplate,
            "claude-cli",
            "prompt.md",
            "claude-opus-5",
            "high",
            @"C:\work",
            diagnostics.Add);

        Assert.Contains("--effort 'high'", command, StringComparison.Ordinal);
        Assert.Empty(diagnostics);
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_every_subscription_command_consumer_resolves_effort_through_the_same_seam")]
    public void EverySubscriptionCommandConsumerResolvesEffortThroughTheSameSeam()
    {
        // Consumer inventory as an executable table: each construction site that claims a reasoning-effort
        // policy must materialize it from the same profile/command seam.
        var builtIn = WorkerProfileCatalog.Default().GetRequired("claude-cli");
        var consumers = new Dictionary<string, Func<string?, string>>(StringComparer.Ordinal)
        {
            ["worker-dispatch"] = effort => WorkerProfileDispatcher.BuildDispatchCommandTemplate(
                builtIn,
                ProviderKind.AnthropicClaudeCli,
                ClaudeDispatchVariables(effort)),
            ["orchestrator-internal-completer"] = effort =>
                SubstituteCompleterCommand(builtIn.CommandTemplate, effort)
        };

        foreach (var (consumer, construct) in consumers)
        {
            var configured = construct("xhigh");
            Assert.True(
                CountOccurrences(configured, "--effort") == 1,
                $"Consumer '{consumer}' did not materialize configured effort exactly once: {configured}");
            Assert.Contains("--effort 'xhigh'", configured, StringComparison.Ordinal);

            var unset = construct(null);
            Assert.DoesNotContain("--effort", unset, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{subscriptionReasoningEffort}", unset, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_codex_rendering_is_unchanged_for_set_and_unset_effort")]
    public void CodexRenderingIsUnchangedForSetAndUnsetEffort()
    {
        // The Codex templates embed the same variable inside `-c model_reasoning_effort=...`; the Claude
        // elision must not reach them, in either direction.
        var codex = WorkerProfileCatalog.Default().GetRequired("codex-spark");
        var configured = SubscriptionCliCompleter.SubstitutePlaceholders(
            codex.CommandTemplate,
            "codex-spark",
            "prompt.md",
            "gpt-5.3-codex-spark",
            "low",
            @"C:\work");
        var unset = SubscriptionCliCompleter.SubstitutePlaceholders(
            codex.CommandTemplate,
            "codex-spark",
            "prompt.md",
            "gpt-5.3-codex-spark",
            null,
            @"C:\work");

        Assert.Contains("-c model_reasoning_effort='low'", configured, StringComparison.Ordinal);
        // Unchanged legacy fallback for Codex: an unset effort still renders the complex default.
        Assert.Contains(
            $"-c model_reasoning_effort='{AgentCatalog.ComplexReasoningEffort}'",
            unset,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--effort", configured, StringComparison.Ordinal);
        Assert.DoesNotContain("--effort", unset, StringComparison.Ordinal);

        var codexDispatch = WorkerProfileDispatcher.BuildDispatchCommandTemplate(
            codex,
            ProviderKind.OpenAICodexSpark,
            new Dictionary<string, string?>
            {
                ["subscriptionModelName"] = "gpt-5.3-codex-spark",
                ["subscriptionReasoningEffort"] = null,
                ["permissionMode"] = "plan",
                ["sandboxMode"] = "read-only",
                ["workingDirectory"] = @"C:\work"
            });
        Assert.Contains("-c model_reasoning_effort=", codexDispatch, StringComparison.Ordinal);
        Assert.DoesNotContain("--effort", codexDispatch, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ClaudeEffort_change_adds_no_parallel_effort_key_and_no_timeout_change")]
    public void ChangeAddsNoParallelEffortKeyAndNoTimeoutChange()
    {
        var builtIn = WorkerProfileCatalog.Default().GetRequired("claude-cli");

        // One effort source of truth: the existing subscriptionReasoningEffort dispatch variable.
        Assert.Equal(1, CountOccurrences(builtIn.CommandTemplate, "{subscriptionReasoningEffort}"));
        Assert.Equal("{subscriptionReasoningEffort}", ClaudeCliEffortPolicy.EffortPlaceholder);
        Assert.Equal("--effort {subscriptionReasoningEffort}", ClaudeCliEffortPolicy.EffortSegment);
        Assert.Equal("high", AgentCatalog.ComplexReasoningEffort);
        Assert.Equal(TimeSpan.FromSeconds(180), SubscriptionCliCompleter.DefaultTimeout);
    }

    private static string BuildClaudeDispatchCommand(string? reasoningEffort) =>
        WorkerProfileDispatcher.BuildDispatchCommandTemplate(
            WorkerProfileCatalog.Default().GetRequired("claude-cli"),
            ProviderKind.AnthropicClaudeCli,
            ClaudeDispatchVariables(reasoningEffort));

    private static Dictionary<string, string?> ClaudeDispatchVariables(string? reasoningEffort) => new()
    {
        ["subscriptionModelName"] = "claude-opus-5",
        ["subscriptionReasoningEffort"] = reasoningEffort,
        ["permissionMode"] = "bypassPermissions",
        ["sandboxMode"] = "workspace-write",
        ["workingDirectory"] = @"C:\worker repo"
    };

    private static string SubstituteCompleterCommand(string template, string? reasoningEffort) =>
        SubscriptionCliCompleter.SubstitutePlaceholders(
            template,
            "claude-cli",
            "prompt.md",
            "claude-opus-5",
            reasoningEffort,
            @"C:\work");

    private static string PrepareLegacyCommand(
        string promptRoot,
        string template,
        string? reasoningEffort = null,
        Dictionary<string, string?>? variables = null) =>
        WorkerCommandTemplate.Prepare(
            new TaskBrief(
                new GoalId("goal123456789"),
                new TaskId("task123456789"),
                AgentRole.Developer,
                "Claude effort argument",
                "Effort prompt."),
            "claude-cli",
            template,
            promptRoot,
            variables ?? new Dictionary<string, string?>
            {
                ["subscriptionModelName"] = "claude-opus-5",
                ["subscriptionReasoningEffort"] = reasoningEffort,
                ["permissionMode"] = "bypassPermissions"
            },
            DateTimeOffset.Parse("2026-09-13T04:00:00Z")).Command;

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = value.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = value.IndexOf(token, index + token.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
