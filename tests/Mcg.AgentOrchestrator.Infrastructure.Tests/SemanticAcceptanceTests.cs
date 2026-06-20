using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SemanticAcceptanceTests
{
    private static SemanticAcceptanceInputs SampleInputs() => new(
        "Add a GetDiffExcerpt helper and feed it to the judge",
        ["Focused tests pass", "No forbidden paths changed"],
        ["src/A.cs", "tests/ATests.cs"],
        "diff --git a/src/A.cs b/src/A.cs\n+public static string GetDiffExcerpt() => ...;",
        "core tests: Passed!  - Failed: 0, Passed: 5");

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_parses_a_valid_fenced_verdict")]
    public void ParsesValidFencedVerdict()
    {
        var output = """
            Here is my assessment.
            ```json
            {"criteria_met": true, "confidence": "high", "reasons": ["adds GetDiffExcerpt", "wires judge"], "unmet_criteria": []}
            ```
            """;

        var verdict = SemanticAcceptancePlanner.Parse(output);

        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
        Assert.Equal("high", verdict.Confidence);
        Assert.Equal(2, verdict.Reasons.Count);
        Assert.Equal(0, verdict.UnmetCriteria.Count);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_parses_not_met_with_unmet_criteria")]
    public void ParsesNotMetWithUnmetCriteria()
    {
        var output = """
            ```json
            {"criteria_met": false, "confidence": "medium", "reasons": ["only touched docs"], "unmet_criteria": ["no code change implements X"]}
            ```
            """;

        var verdict = SemanticAcceptancePlanner.Parse(output);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.CriteriaMet);
        Assert.Equal(1, verdict.UnmetCriteria.Count);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_parses_a_bare_top_level_json_object")]
    public void ParsesBareTopLevelJsonObject()
    {
        var output = """{"criteria_met": true, "confidence": "medium", "reasons": ["bare json ok"], "unmet_criteria": []}""";

        var verdict = SemanticAcceptancePlanner.Parse(output);

        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
        Assert.Equal("medium", verdict.Confidence);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_parses_bare_json_with_preamble_text")]
    public void ParsesBareJsonWithPreambleText()
    {
        var output = """
            Here is my verdict.
            {"criteria_met": false, "confidence": "low", "reasons": ["incomplete"], "unmet_criteria": ["X missing"]}
            """;

        var verdict = SemanticAcceptancePlanner.Parse(output);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.CriteriaMet);
        Assert.Equal(1, verdict.UnmetCriteria.Count);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_rejects_output_without_fenced_json")]
    public void RejectsOutputWithoutFencedJson()
    {
        var verdict = SemanticAcceptancePlanner.Parse("The change looks fine to me, criteria met.");

        Assert.False(verdict.IsValid);
        Assert.True(verdict.ValidationErrors.Count > 0);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_rejects_verdict_missing_criteria_met")]
    public void RejectsVerdictMissingCriteriaMet()
    {
        var output = """
            ```json
            {"confidence": "high", "reasons": ["looks good"]}
            ```
            """;

        var verdict = SemanticAcceptancePlanner.Parse(output);

        Assert.False(verdict.IsValid);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_evidence_context_includes_all_sections")]
    public void EvidenceContextIncludesAllSections()
    {
        var context = SemanticAcceptancePlanner.BuildEvidenceContext(SampleInputs());

        Assert.True(context.Contains("Add a GetDiffExcerpt helper", StringComparison.Ordinal));
        Assert.True(context.Contains("Focused tests pass", StringComparison.Ordinal));
        Assert.True(context.Contains("src/A.cs", StringComparison.Ordinal));
        Assert.True(context.Contains("diff --git", StringComparison.Ordinal));
        Assert.True(context.Contains("core tests: Passed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_evidence_context_includes_per_file_diff_hunks")]
    public void EvidenceContextIncludesPerFileDiffHunks()
    {
        var inputs = new SemanticAcceptanceInputs(
            "Wire judge evidence to real diffs",
            ["judge sees implementation hunks"],
            ["src/Feature.cs", "tests/FeatureTests.cs"],
            "diff --git a/src/Feature.cs b/src/Feature.cs\n...(whole diff truncated at 80 chars)",
            "tests passed",
            [
                ("src/Feature.cs", "diff --git a/src/Feature.cs b/src/Feature.cs\n@@ -1,3 +1,4 @@\n+public sealed class Feature { }"),
                ("tests/FeatureTests.cs", "diff --git a/tests/FeatureTests.cs b/tests/FeatureTests.cs\n@@ -5,6 +5,7 @@\n+Assert.True(context.Contains(\"Feature\"));")
            ]);

        var context = SemanticAcceptancePlanner.BuildEvidenceContext(inputs);

        Assert.True(context.Contains("## Per-file unified diffs", StringComparison.Ordinal));
        Assert.True(context.Contains("### src/Feature.cs", StringComparison.Ordinal));
        Assert.True(context.Contains("@@ -1,3 +1,4 @@", StringComparison.Ordinal));
        Assert.True(context.Contains("+public sealed class Feature { }", StringComparison.Ordinal));
        Assert.True(context.Contains("### tests/FeatureTests.cs", StringComparison.Ordinal));
        Assert.True(context.Contains("+Assert.True(context.Contains(\"Feature\"));", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptancePlanner_evidence_context_includes_bounded_oversized_per_file_diff")]
    public void EvidenceContextIncludesBoundedOversizedPerFileDiff()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");

        Directory.CreateDirectory(Path.Combine(root, "src"));
        var path = Path.Combine(root, "src", "Large.cs");
        File.WriteAllText(path, "public sealed class Large\n{\n}\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");
        RunGit(root, "checkout", "-b", "goal/test");

        var body = string.Join(Environment.NewLine, Enumerable.Range(0, 100).Select(i => $"    public int Value{i} => {i};"));
        File.WriteAllText(path, $"public sealed class Large{Environment.NewLine}{{{Environment.NewLine}{body}{Environment.NewLine}}}{Environment.NewLine}");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "large change");

        var perFileDiffs = GoalAcceptanceEvidenceBundleBuilder.GetPerFileDiffs(root, perFileMaxChars: 220);
        var context = SemanticAcceptancePlanner.BuildEvidenceContext(new SemanticAcceptanceInputs(
            "Keep oversized changed-file evidence visible",
            [],
            GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(root),
            GoalAcceptanceEvidenceBundleBuilder.GetDiffExcerpt(root, maxChars: 80),
            null,
            perFileDiffs));

        var (_, diff) = Xunit.Assert.Single(perFileDiffs);
        Assert.True(diff.Length < 320);
        Assert.True(context.Contains("### src/Large.cs", StringComparison.Ordinal));
        Assert.True(context.Contains("@@", StringComparison.Ordinal));
        Assert.True(context.Contains("...(per-file diff truncated at 220 chars)", StringComparison.Ordinal));
        Assert.True(context.Contains("## Unified diff (may be truncated)", StringComparison.Ordinal));
        Assert.True(context.Contains("...(diff truncated at 80 chars)", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_aggregates_agreeing_judges_into_consensus")]
    public async Task EvaluatorAggregatesAgreeingJudges()
    {
        var judges = new ISemanticJudge[]
        {
            new FakeJudge("local", _ => new SemanticAcceptanceVerdict(true, "high", ["ok"], [], [])),
            new FakeJudge("paid", _ => new SemanticAcceptanceVerdict(true, "medium", ["also ok"], [], []))
        };

        var report = await SemanticAcceptanceEvaluator.EvaluateAsync(judges, SampleInputs(), TimeSpan.FromSeconds(5));

        Assert.Equal(2, report.Verdicts.Count);
        Assert.True(report.AllValidJudgesAgree);
        Assert.Equal(true, report.Consensus);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_no_consensus_when_valid_judges_disagree")]
    public async Task EvaluatorNoConsensusWhenJudgesDisagree()
    {
        var judges = new ISemanticJudge[]
        {
            new FakeJudge("local", _ => new SemanticAcceptanceVerdict(true, "high", [], [], [])),
            new FakeJudge("paid", _ => new SemanticAcceptanceVerdict(false, "high", [], ["X missing"], []))
        };

        var report = await SemanticAcceptanceEvaluator.EvaluateAsync(judges, SampleInputs(), TimeSpan.FromSeconds(5));

        Assert.True(report.Consensus is null);
        Assert.False(report.AllValidJudgesAgree);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_swallows_a_throwing_judge_as_invalid_verdict")]
    public async Task EvaluatorSwallowsThrowingJudge()
    {
        var judges = new ISemanticJudge[]
        {
            new FakeJudge("local", _ => new SemanticAcceptanceVerdict(true, "high", [], [], [])),
            new ThrowingJudge()
        };

        var report = await SemanticAcceptanceEvaluator.EvaluateAsync(judges, SampleInputs(), TimeSpan.FromSeconds(5));

        Assert.Equal(2, report.Verdicts.Count);
        Assert.Equal(1, report.ValidVerdicts.Count);
        // One valid verdict alone is the consensus; a throwing judge never breaks the gate.
        Assert.Equal(true, report.Consensus);
    }

    [Xunit.Fact(DisplayName = "ModelRegistrySemanticJudge_completes_through_provider_and_parses_verdict")]
    public async Task ModelRegistryJudgeCompletesAndParses()
    {
        var response = """
            ```json
            {"criteria_met": true, "confidence": "high", "reasons": ["diff implements the helper"], "unmet_criteria": []}
            ```
            """;
        var registry = new InMemoryModelProviderRegistry([new FakeJudgeProvider("Ollama", response)]);
        var judge = new ModelRegistrySemanticJudge(registry, "Ollama", "qwen3:8b");

        var verdict = await judge.JudgeAsync(SampleInputs(), default);

        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
        Assert.Equal("ollama:qwen3:8b", judge.Name);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_BuildJudges_resolves_acceptance_judge_bindings_deduped")]
    public void BuildJudgesResolvesAcceptanceJudgeBindingsDeduped()
    {
        var providers = new InMemoryModelProviderRegistry([]);
        var catalog = new ModelFunctionCatalog(
        [
            JudgeBinding(ModelLane.Local, "Ollama", "qwen3:8b"),
            JudgeBinding(ModelLane.CheapApi, "Anthropic", "claude-haiku-4-5"),
            JudgeBinding(ModelLane.Local, "Ollama", "qwen3:8b"),
            // A binding for a DIFFERENT purpose must be ignored by the acceptance-judge resolution.
            new ModelFunctionBinding("planner-sampler", ModelLane.CheapApi,
                new ModelProfile("Anthropic", "claude-haiku-4-5", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);

        var judges = SemanticAcceptanceEvaluator.BuildJudges(catalog, providers);

        // Two distinct acceptance-judge lanes; the duplicate Ollama/qwen3:8b is deduped; the
        // planner-sampler binding is ignored.
        Assert.Equal(2, judges.Count);
        Assert.True(judges.Any(judge => judge.Name == "ollama:qwen3:8b"));
        Assert.True(judges.Any(judge => judge.Name == "anthropic:claude-haiku-4-5"));
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_BuildJudges_empty_when_no_acceptance_judge_binding")]
    public void BuildJudgesEmptyWhenNoAcceptanceJudgeBinding()
    {
        var providers = new InMemoryModelProviderRegistry([]);
        var catalog = new ModelFunctionCatalog(
        [
            new ModelFunctionBinding("planner-sampler", ModelLane.CheapApi,
                new ModelProfile("Anthropic", "claude-haiku-4-5", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]);

        Assert.Equal(0, SemanticAcceptanceEvaluator.BuildJudges(catalog, providers).Count);
        Assert.Equal(0, SemanticAcceptanceEvaluator.BuildJudges(ModelFunctionCatalog.Empty, providers).Count);
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_one_not_met_makes_overall_not_met")]
    public async Task RecursiveJudgeAndAggregatesOneNotMetMakesOverallNotMet()
    {
        var leaf = new FakeJudge("leaf", inputs =>
        {
            var file = inputs.ChangedFiles.Count > 0 ? inputs.ChangedFiles[0] : string.Empty;
            return file == "src/A.cs"
                ? new SemanticAcceptanceVerdict(true, "high", ["A ok"], [], [])
                : new SemanticAcceptanceVerdict(false, "medium", ["B missing X"], ["X not implemented"], []);
        });

        var judge = new RecursivePerFileSemanticJudge(leaf);
        var inputs = new SemanticAcceptanceInputs(
            "objective",
            [],
            ["src/A.cs", "src/B.cs"],
            string.Empty,
            null,
            [("src/A.cs", "+int a = 1;"), ("src/B.cs", "+int b = 2;")]);

        var verdict = await judge.JudgeAsync(inputs, default);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.CriteriaMet);
        Assert.True(verdict.UnmetCriteria.Contains("X not implemented"));
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_all_met_makes_overall_met")]
    public async Task RecursiveJudgeAndAggregatesAllMetMakesOverallMet()
    {
        var leaf = new FakeJudge("leaf", inputs =>
            new SemanticAcceptanceVerdict(true, "high", [$"{inputs.ChangedFiles[0]} ok"], [], []));

        var judge = new RecursivePerFileSemanticJudge(leaf);
        var inputs = new SemanticAcceptanceInputs(
            "objective",
            [],
            ["src/A.cs", "src/B.cs"],
            string.Empty,
            null,
            [("src/A.cs", "+int a = 1;"), ("src/B.cs", "+int b = 2;")]);

        var verdict = await judge.JudgeAsync(inputs, default);

        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
        Assert.True(verdict.Reasons.Any(reason => reason.Contains("2 of 2 files judged", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_falls_back_to_whole_diff_when_no_per_file_diffs")]
    public async Task RecursiveJudgeFallsBackToWholeDiffWhenNoPerFileDiffs()
    {
        var leafCallCount = 0;
        string? capturedDiff = null;
        var leaf = new FakeJudge("leaf", inputs =>
        {
            leafCallCount++;
            capturedDiff = inputs.DiffExcerpt;
            return new SemanticAcceptanceVerdict(true, "high", ["whole diff ok"], [], []);
        });

        var judge = new RecursivePerFileSemanticJudge(leaf);
        var inputs = SampleInputs(); // PerFileDiffs is null — should delegate to leaf with whole diff

        var verdict = await judge.JudgeAsync(inputs, default);

        Assert.Equal(1, leafCallCount);
        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
        Assert.Equal(inputs.DiffExcerpt, capturedDiff);
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_name_wraps_leaf_name")]
    public void RecursiveJudgeNameWrapsLeafName()
    {
        var leaf = new FakeJudge("ollama:qwen3:8b", _ => SemanticAcceptanceVerdict.Invalid("unused"));
        var judge = new RecursivePerFileSemanticJudge(leaf);
        Assert.Equal("recursive(ollama:qwen3:8b)", judge.Name);
    }

    [Xunit.Fact(DisplayName = "SubscriptionCliSemanticJudge_parses_fenced_verdict_from_captured_stdout")]
    public async Task SubscriptionCliJudgeParsesVerdictFromCapturedStdout()
    {
        var stdout = """
            ```json
            {"criteria_met": true, "confidence": "high", "reasons": ["subscription judge approved"], "unmet_criteria": []}
            ```
            """;
        string? capturedCommand = null;
        Task<string> FakeRunner(string command, string workingDirectory, CancellationToken ct)
        {
            capturedCommand = command;
            return Task.FromResult(stdout);
        }

        var judge = new SubscriptionCliSemanticJudge(
            "claude --model {subscriptionModelName} --permission-mode {permissionMode} -p (Get-Content -Raw {promptPath})",
            "claude-cli",
            "claude-sonnet-4-6",
            null,
            FakeRunner);

        var verdict = await judge.JudgeAsync(SampleInputs(), default);

        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
        Assert.Equal("high", verdict.Confidence);
        Assert.Equal("sub:claude-cli:claude-sonnet-4-6", judge.Name);
        Assert.True(capturedCommand is not null);
        Assert.False(capturedCommand!.Contains("--permission-mode 'plan'", StringComparison.Ordinal));
        Assert.True(capturedCommand.Contains("--permission-mode 'default'", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "SubscriptionCliSemanticJudge_null_reasoning_effort_renders_high_in_command")]
    public async Task SubscriptionCliJudgeNullReasoningEffortRendersHigh()
    {
        string? capturedCommand = null;
        Task<string> CapturingRunner(string command, string workingDirectory, CancellationToken ct)
        {
            capturedCommand = command;
            return Task.FromResult("""
                ```json
                {"criteria_met": true, "confidence": "high", "reasons": ["ok"], "unmet_criteria": []}
                ```
                """);
        }

        var judge = new SubscriptionCliSemanticJudge(
            "codex --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} -p {promptPath}",
            "codex-cli",
            "gpt-5.3-codex-spark",
            null,
            CapturingRunner);

        var verdict = await judge.JudgeAsync(SampleInputs(), default);

        Assert.True(verdict.IsValid);
        Assert.True(capturedCommand is not null);
        Assert.True(capturedCommand!.Contains("model_reasoning_effort='high'", StringComparison.Ordinal));
        Assert.False(capturedCommand!.Contains("model_reasoning_effort=''", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "SubscriptionCliSemanticJudge_explicit_reasoning_effort_overrides_default")]
    public async Task SubscriptionCliJudgeExplicitReasoningEffortOverridesDefault()
    {
        string? capturedCommand = null;
        Task<string> CapturingRunner(string command, string workingDirectory, CancellationToken ct)
        {
            capturedCommand = command;
            return Task.FromResult("""
                ```json
                {"criteria_met": true, "confidence": "high", "reasons": ["ok"], "unmet_criteria": []}
                ```
                """);
        }

        var judge = new SubscriptionCliSemanticJudge(
            "codex --model {subscriptionModelName} -c model_reasoning_effort={subscriptionReasoningEffort} -p {promptPath}",
            "codex-cli",
            "gpt-5.3-codex-spark",
            "medium",
            CapturingRunner);

        var verdict = await judge.JudgeAsync(SampleInputs(), default);

        Assert.True(verdict.IsValid);
        Assert.True(capturedCommand is not null);
        Assert.True(capturedCommand!.Contains("model_reasoning_effort='medium'", StringComparison.Ordinal));
        Assert.False(capturedCommand!.Contains("model_reasoning_effort='high'", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "SubscriptionCliSemanticJudge_returns_invalid_when_runner_throws")]
    public async Task SubscriptionCliJudgeReturnsInvalidWhenRunnerThrows()
    {
        Task<string> ThrowingRunner(string command, string workingDirectory, CancellationToken ct) =>
            throw new InvalidOperationException("CLI not found");

        var judge = new SubscriptionCliSemanticJudge(
            "claude --model {subscriptionModelName} -p (Get-Content -Raw {promptPath})",
            "claude-cli",
            "claude-sonnet-4-6",
            null,
            ThrowingRunner);

        var verdict = await judge.JudgeAsync(SampleInputs(), default);

        Assert.False(verdict.IsValid);
        Assert.True(verdict.ValidationErrors.Count > 0);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_BuildJudges_picks_subscription_judge_when_binding_has_subscription")]
    public void BuildJudgesPicksSubscriptionJudgeWhenBindingHasSubscription()
    {
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        var catalog = new ModelFunctionCatalog(
        [
            new ModelFunctionBinding(
                ModelFunctionPurposes.AcceptanceJudge,
                ModelLane.Capable,
                new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey),
                Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6")),
            JudgeBinding(ModelLane.Local, "Ollama", "qwen3:8b")
        ]);

        var judges = SemanticAcceptanceEvaluator.BuildJudges(catalog, providers, profiles);

        Assert.Equal(2, judges.Count);
        Assert.True(judges.Any(j => j.Name == "sub:claude-cli:claude-sonnet-4-6"));
        Assert.True(judges.Any(j => j.Name == "ollama:qwen3:8b"));
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_BuildJudges_dedupes_subscription_judges_by_profile_and_alias")]
    public void BuildJudgesDedupsSubscriptionJudgesByProfileAndAlias()
    {
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        var catalog = new ModelFunctionCatalog(
        [
            new ModelFunctionBinding(
                ModelFunctionPurposes.AcceptanceJudge,
                ModelLane.Capable,
                new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey),
                Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6")),
            new ModelFunctionBinding(
                ModelFunctionPurposes.AcceptanceJudge,
                ModelLane.Capable,
                new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey),
                Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6"))
        ]);

        var judges = SemanticAcceptanceEvaluator.BuildJudges(catalog, providers, profiles);

        Assert.Equal(1, judges.Count);
        Assert.Equal("sub:claude-cli:claude-sonnet-4-6", judges[0].Name);
    }

    [Xunit.Fact(DisplayName = "SemanticAcceptanceEvaluator_BuildJudges_falls_back_to_api_judge_when_no_worker_profiles")]
    public void BuildJudgesFallsBackToApiJudgeWhenNoWorkerProfiles()
    {
        var providers = new InMemoryModelProviderRegistry([]);
        var catalog = new ModelFunctionCatalog(
        [
            new ModelFunctionBinding(
                ModelFunctionPurposes.AcceptanceJudge,
                ModelLane.Capable,
                new ModelProfile("Anthropic", "claude-sonnet-4-6", ModelCapability.Text, SubscriptionMode.ApiKey),
                Subscription: new SubscriptionLaunchProfile("claude-cli", "claude-sonnet-4-6"))
        ]);

        // workerProfiles=null: subscription is set but no catalog — fall back to API judge
        var judges = SemanticAcceptanceEvaluator.BuildJudges(catalog, providers);

        Assert.Equal(1, judges.Count);
        Assert.Equal("anthropic:claude-sonnet-4-6", judges[0].Name);
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_confidence_is_lowest_among_valid_verdicts")]
    public async Task RecursiveJudgeConfidenceIsLowest()
    {
        var verdicts = new[] { "high", "medium", "low" };
        var idx = 0;
        var leaf = new FakeJudge("leaf", _ =>
            new SemanticAcceptanceVerdict(true, verdicts[idx++], [], [], []));

        var judge = new RecursivePerFileSemanticJudge(leaf);
        var inputs = new SemanticAcceptanceInputs(
            "obj", [], ["a", "b", "c"], string.Empty, null,
            [("a", "+int a = 1;"), ("b", "+int b = 2;"), ("c", "+int c = 3;")]);

        var verdict = await judge.JudgeAsync(inputs, default);

        Assert.True(verdict.IsValid);
        Assert.Equal("low", verdict.Confidence);
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_subscription_cli_leaf_judges_whole_diff_in_single_call")]
    public async Task RecursiveJudgeSubscriptionCliLeafJudgesWholeDiffInSingleCall()
    {
        var callCount = 0;
        Task<string> CountingRunner(string command, string workingDirectory, CancellationToken ct)
        {
            callCount++;
            return Task.FromResult("""
                ```json
                {"criteria_met": true, "confidence": "high", "reasons": ["cli ok"], "unmet_criteria": []}
                ```
                """);
        }

        var cliLeaf = new SubscriptionCliSemanticJudge(
            "echo {promptPath}",
            "claude-cli",
            "claude-haiku-4-5",
            null,
            CountingRunner);

        var judge = new RecursivePerFileSemanticJudge(cliLeaf);
        var inputs = new SemanticAcceptanceInputs(
            "objective", [], ["src/A.cs", "src/B.cs", "src/C.cs"],
            "big diff",
            null,
            [("src/A.cs", "+int x = 1;"), ("src/B.cs", "+int y = 2;"), ("src/C.cs", "+int z = 3;")]);

        var verdict = await judge.JudgeAsync(inputs, default);

        // CLI leaf must NOT fan out per-file — N cold launches exceed the 180s budget.
        // The whole diff must be judged in exactly one call regardless of file count.
        Assert.Equal(1, callCount);
        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_judges_each_substantive_file_when_file_count_exceeds_cap")]
    public async Task RecursiveJudgeJudgesEachSubstantiveFileWhenFileCountExceedsCap()
    {
        var capturedFiles = new List<string>();
        var leaf = new FakeJudge("leaf", inputs =>
        {
            capturedFiles.Add(inputs.ChangedFiles[0]);
            return new SemanticAcceptanceVerdict(true, "high", [$"{inputs.ChangedFiles[0]} ok"], [], []);
        });

        var judge = new RecursivePerFileSemanticJudge(leaf);

        // Build MaxPerFileJudgeCalls+1 substantive files to exceed the cap.
        var fileCount = RecursivePerFileSemanticJudge.MaxPerFileJudgeCalls + 1;
        var perFileDiffs = Enumerable.Range(1, fileCount)
            .Select(i => ($"src/File{i}.cs", $"+int x{i} = {i};"))
            .ToList();
        var inputs = new SemanticAcceptanceInputs(
            "objective", [],
            perFileDiffs.Select(f => f.Item1).ToList(),
            "whole diff excerpt",
            null,
            perFileDiffs);

        var verdict = await judge.JudgeAsync(inputs, default);

        Assert.Equal(fileCount, capturedFiles.Count);
        Assert.True(perFileDiffs.All(file => capturedFiles.Contains(file.Item1)));
        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
        Assert.True(verdict.Reasons.Any(reason => reason.Contains($"{fileCount} of {fileCount} files judged", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_budgets_oversized_single_file_diff")]
    public async Task RecursiveJudgeBudgetsOversizedSingleFileDiff()
    {
        string? capturedDiff = null;
        var leaf = new FakeJudge("leaf", inputs =>
        {
            capturedDiff = inputs.DiffExcerpt;
            return new SemanticAcceptanceVerdict(true, "high", ["large file judged"], [], []);
        });

        var judge = new RecursivePerFileSemanticJudge(leaf);
        var oversizedDiff = "+" + new string('x', RecursivePerFileSemanticJudge.MaxPerFileDiffChars + 250);
        var inputs = new SemanticAcceptanceInputs(
            "objective", [],
            ["src/Large.cs"],
            "whole diff excerpt",
            null,
            [("src/Large.cs", oversizedDiff)]);

        var verdict = await judge.JudgeAsync(inputs, default);

        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
        Assert.True(capturedDiff is not null);
        Assert.True(capturedDiff!.Length < oversizedDiff.Length);
        Assert.True(capturedDiff.Contains("oversized single-file change still judged", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_reports_partial_coverage_when_one_file_has_no_verdict")]
    public async Task RecursiveJudgeReportsPartialCoverageWhenOneFileHasNoVerdict()
    {
        var leaf = new FakeJudge("leaf", inputs =>
        {
            var file = inputs.ChangedFiles[0];
            return file == "src/B.cs"
                ? SemanticAcceptanceVerdict.Invalid("Judge produced no output.")
                : new SemanticAcceptanceVerdict(true, "high", [$"{file} ok"], [], []);
        });

        var judge = new RecursivePerFileSemanticJudge(leaf);
        var inputs = new SemanticAcceptanceInputs(
            "objective", [],
            ["src/A.cs", "src/B.cs", "src/C.cs"],
            string.Empty,
            null,
            [("src/A.cs", "+int a = 1;"), ("src/B.cs", "+int b = 2;"), ("src/C.cs", "+int c = 3;")]);

        var verdict = await judge.JudgeAsync(inputs, default);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.CriteriaMet);
        Assert.Equal("low", verdict.Confidence);
        Assert.True(verdict.Reasons.Any(reason => reason.Contains("2 of 3 files judged", StringComparison.Ordinal)));
        Assert.True(verdict.Reasons.Any(reason => reason.Contains("src/B.cs: Judge produced no output.", StringComparison.Ordinal)));
        Assert.True(verdict.UnmetCriteria.Contains("src/B.cs: no valid per-file verdict"));
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_reports_partial_coverage_when_one_file_throws")]
    public async Task RecursiveJudgeReportsPartialCoverageWhenOneFileThrows()
    {
        var leaf = new FakeJudge("leaf", inputs =>
        {
            var file = inputs.ChangedFiles[0];
            if (file == "src/B.cs")
            {
                throw new InvalidOperationException("boom");
            }

            return new SemanticAcceptanceVerdict(true, "high", [$"{file} ok"], [], []);
        });

        var judge = new RecursivePerFileSemanticJudge(leaf);
        var inputs = new SemanticAcceptanceInputs(
            "objective", [],
            ["src/A.cs", "src/B.cs"],
            string.Empty,
            null,
            [("src/A.cs", "+int a = 1;"), ("src/B.cs", "+int b = 2;")]);

        var verdict = await judge.JudgeAsync(inputs, default);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.CriteriaMet);
        Assert.True(verdict.Reasons.Any(reason => reason.Contains("1 of 2 files judged", StringComparison.Ordinal)));
        Assert.True(verdict.Reasons.Any(reason => reason.Contains("Per-file judge failed for src/B.cs: boom", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_skips_whitespace_only_files_during_fan_out")]
    public async Task RecursiveJudgeSkipsWhitespaceOnlyFiles()
    {
        var capturedFiles = new List<string>();
        var leaf = new FakeJudge("leaf", inputs =>
        {
            capturedFiles.Add(inputs.ChangedFiles.Count > 0 ? inputs.ChangedFiles[0] : "?");
            return new SemanticAcceptanceVerdict(true, "high", ["ok"], [], []);
        });

        var judge = new RecursivePerFileSemanticJudge(leaf);
        var inputs = new SemanticAcceptanceInputs(
            "objective", [],
            ["src/A.cs", "src/B.cs", "src/C.cs"],
            string.Empty,
            null,
            [
                ("src/A.cs", "+int x = 1;"),       // substantive
                ("src/B.cs", "+   \n+\t"),          // whitespace-only lines
                ("src/C.cs", "+string s = \"hi\";") // substantive
            ]);

        var verdict = await judge.JudgeAsync(inputs, default);

        // Whitespace-only B must be skipped; A and C must be judged.
        Assert.Equal(2, capturedFiles.Count);
        Assert.True(capturedFiles.Contains("src/A.cs"));
        Assert.True(capturedFiles.Contains("src/C.cs"));
        Assert.False(capturedFiles.Contains("src/B.cs"));
        Assert.True(verdict.IsValid);
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_keeps_per_file_recursion_for_non_cli_leaf")]
    public async Task RecursiveJudgeKeepsPerFileRecursionForNonCliLeaf()
    {
        var capturedFiles = new List<string>();
        var leaf = new FakeJudge("local", inputs =>
        {
            capturedFiles.Add(inputs.ChangedFiles.Count > 0 ? inputs.ChangedFiles[0] : "?");
            return new SemanticAcceptanceVerdict(true, "high", ["ok"], [], []);
        });

        var judge = new RecursivePerFileSemanticJudge(leaf);
        var inputs = new SemanticAcceptanceInputs(
            "objective", [], ["src/A.cs", "src/B.cs"],
            "diff",
            null,
            [("src/A.cs", "+int a = 1;"), ("src/B.cs", "+int b = 2;")]);

        var verdict = await judge.JudgeAsync(inputs, default);

        // Local/API leaf must be called once per file.
        Assert.Equal(2, capturedFiles.Count);
        Assert.True(capturedFiles.Contains("src/A.cs"));
        Assert.True(capturedFiles.Contains("src/B.cs"));
        Assert.True(verdict.IsValid);
        Assert.True(verdict.CriteriaMet);
    }

    [Xunit.Fact(DisplayName = "SubscriptionCliSemanticJudge_BuildStartInfo_sets_RedirectStandardInput_true")]
    public void BuildStartInfoRedirectsStandardInput()
    {
        var startInfo = SubscriptionCliSemanticJudge.BuildStartInfo("echo hi", Path.GetTempPath());

        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Xunit.Fact(DisplayName = "SubscriptionCliSemanticJudge_has_180s_judge_timeout")]
    public void SubscriptionCliJudgeHas180sTimeout()
    {
        var judge = new SubscriptionCliSemanticJudge(
            "echo {promptPath}",
            "claude-cli",
            "claude-haiku-4-5",
            null,
            (_, _, _) => Task.FromResult(string.Empty));

        Assert.Equal(TimeSpan.FromSeconds(180), judge.JudgeTimeout);
    }

    [Xunit.Fact(DisplayName = "RecursivePerFileSemanticJudge_exposes_leaf_judge_timeout")]
    public void RecursiveJudgeExposesLeafJudgeTimeout()
    {
        var cliLeaf = new SubscriptionCliSemanticJudge(
            "echo {promptPath}",
            "claude-cli",
            "claude-haiku-4-5",
            null,
            (_, _, _) => Task.FromResult(string.Empty));

        var judge = new RecursivePerFileSemanticJudge(cliLeaf);

        Assert.Equal(TimeSpan.FromSeconds(180), judge.JudgeTimeout);
    }

    [Xunit.Fact(DisplayName = "RunCommandAsync_drain_timeout_unblocks_when_grandchild_holds_pipe_after_parent_exits")]
    public async Task RunCommandAsyncDrainTimeoutUnblocksWhenGrandchildHoldsPipe()
    {
        // Simulate the grandchild-holds-the-pipe bug: the parent PowerShell writes output
        // and exits, but a grandchild (started via Process.Start with UseShellExecute=false
        // so it inherits the stdout pipe handle) keeps the pipe open for 60s. Without the
        // fix, ReadToEndAsync hangs until the outer 20s CancellationToken fires. With the
        // fix, the 12s drain timeout kills the tree and returns well before 20s.
        var exe = WorkerShell.Executable;
        var command = $"Write-Output 'verdict'; $psi = [System.Diagnostics.ProcessStartInfo]::new('{exe}', '-NonInteractive -Command Start-Sleep 60'); $psi.UseShellExecute = $false; [System.Diagnostics.Process]::Start($psi) | Out-Null; exit 0";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await SubscriptionCliSemanticJudge.RunCommandAsync(command, Path.GetTempPath(), cts.Token);

        sw.Stop();

        // Drain timeout (12s) must fire before the outer 20s cancellation token.
        Assert.False(cts.IsCancellationRequested);
        Assert.True(sw.ElapsedMilliseconds < 18_000);
    }

    private static ModelFunctionBinding JudgeBinding(ModelLane lane, string provider, string model) =>
        new(ModelFunctionPurposes.AcceptanceJudge, lane,
            new ModelProfile(provider, model, ModelCapability.Text,
                lane == ModelLane.Local ? SubscriptionMode.LocalBridge : SubscriptionMode.ApiKey));

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var result = GitCli.Run(workingDirectory, args);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}");
        }
    }

    private sealed class FakeJudge(string name, Func<SemanticAcceptanceInputs, SemanticAcceptanceVerdict> verdict) : ISemanticJudge
    {
        public string Name { get; } = name;
        public TimeSpan? JudgeTimeout => null;

        public Task<SemanticAcceptanceVerdict> JudgeAsync(SemanticAcceptanceInputs inputs, CancellationToken cancellationToken)
            => Task.FromResult(verdict(inputs));
    }

    private sealed class ThrowingJudge : ISemanticJudge
    {
        public string Name => "throwing";
        public TimeSpan? JudgeTimeout => null;

        public Task<SemanticAcceptanceVerdict> JudgeAsync(SemanticAcceptanceInputs inputs, CancellationToken cancellationToken)
            => throw new InvalidOperationException("judge boom");
    }

    private sealed class FakeJudgeProvider(string providerName, string text) : IModelProvider
    {
        public string ProviderName { get; } = providerName;

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ModelResponse(text, null, "stop"));
    }
}
