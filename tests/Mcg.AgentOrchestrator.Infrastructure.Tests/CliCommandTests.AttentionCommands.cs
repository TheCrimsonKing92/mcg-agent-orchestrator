using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

public sealed class CliCommandTestsAttentionCommands : CliCommandTestBase
{
    [Xunit.Fact]
    public void Cli_pending_renders_open_and_total_request_counts()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Show request counts",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        var answered = kernel.RequestHumanInput(goal.Id, task.Id, "Which branch?");
        kernel.SubmitHumanInput(answered.Id, "Use main.");
        kernel.RequestHumanInput(goal.Id, task.Id, "Which deployment target?");

        var output = ExecuteCliAndCapture(["pending"], kernel, workspace);

        Xunit.Assert.Contains("requests=1 open / 2 total", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_attention_normalizes_one_shot_subcommands")]
    public void CliAttentionNormalizesOneShotSubcommands()
    {
        var show = CliArgumentParser.NormalizeArgs(["attention", "show", "abc123ef"]);
        var dismiss = CliArgumentParser.NormalizeArgs(["attention", "dismiss", "abc123ef"]);
        var answer = CliArgumentParser.NormalizeArgs(
            ["attention", "answer", "abc123ef", "391ce87f", "Use", "a", "static", "helper."]);

        Xunit.Assert.Equal(["attention", "show", "abc123ef"], show);
        Xunit.Assert.Equal(["attention", "dismiss", "abc123ef"], dismiss);
        Xunit.Assert.Equal(["attention", "answer", "abc123ef", "391ce87f", "Use a static helper."], answer);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_splits_interactive_subcommands")]
    public void CliAttentionSplitsInteractiveSubcommands()
    {
        var show = CliArgumentParser.SplitCommand("attention show abc123ef");
        var dismiss = CliArgumentParser.SplitCommand("attention dismiss abc123ef");
        var answer = CliArgumentParser.SplitCommand("attention answer abc123ef 391ce87f Use a static helper.");

        Xunit.Assert.Equal(["attention", "show", "abc123ef"], show);
        Xunit.Assert.Equal(["attention", "dismiss", "abc123ef"], dismiss);
        Xunit.Assert.Equal(["attention", "answer", "abc123ef", "391ce87f", "Use a static helper."], answer);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_show_goal_prefix_filters_unrelated_items")]
    public async Task CliAttentionShowGoalPrefixFiltersUnrelatedItems()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal(new GoalId("aaaaaaaa111111111111111111111111"), "Target goal");
        var other = kernel.CreateGoal(new GoalId("bbbbbbbb222222222222222222222222"), "Other goal");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, target.Id.Value, "Target clarification", "Target body", $"spec-clarification:{target.Id.Value}:scope:11111111");
        for (var index = 0; index < 12; index++)
        {
            _ = await store.RaiseAsync(CollaborationItemType.Clarification, other.Id.Value, $"Other clarification {index}", "Other body", $"spec-clarification:{other.Id.Value}:scope:{index:00000000}");
        }

        var output = ExecuteCliAndCapture(["attention", "show", "aaaaaaaa"], kernel, workspace);

        Xunit.Assert.Contains("[11111111] Target clarification", output);
        Xunit.Assert.Contains("Target body", output);
        Xunit.Assert.DoesNotContain("Other clarification", output);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_show_goal_prefix_prints_empty_state")]
    public async Task CliAttentionShowGoalPrefixPrintsEmptyState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var empty = kernel.CreateGoal(new GoalId("cccccccc333333333333333333333333"), "Empty goal");
        var other = kernel.CreateGoal(new GoalId("dddddddd444444444444444444444444"), "Other goal");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, other.Id.Value, "Other clarification", "Other body", $"spec-clarification:{other.Id.Value}:scope:22222222");

        var output = ExecuteCliAndCapture(["attention", "show", "cccccccc"], kernel, workspace);

        Xunit.Assert.Contains($"No open attention items for goal {empty.Id.Value}.", output);
        Xunit.Assert.DoesNotContain("Other clarification", output);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_answer_goal_prefix_confirms_single_item")]
    public async Task CliAttentionAnswerGoalPrefixConfirmsSingleItem()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal(new GoalId("eeeeeeee555555555555555555555555"), "Target goal");
        var other = kernel.CreateGoal(new GoalId("ffffffff666666666666666666666666"), "Other goal");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, target.Id.Value, "Target clarification", "Target body", $"spec-clarification:{target.Id.Value}:scope:33333333");
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, other.Id.Value, "Other clarification", "Other body", $"spec-clarification:{other.Id.Value}:scope:44444444");

        var output = ExecuteCliAndCapture(["attention", "answer", "eeeeeeee", "33333333", "Use the target answer."], kernel, workspace);
        var queue = await store.GetAttentionQueueAsync();

        Xunit.Assert.Equal($"Answered clarification '33333333' for goal '{target.Id.Value[..8]}'.{Environment.NewLine}", output);
        Xunit.Assert.DoesNotContain("Other clarification", output);
        Xunit.Assert.DoesNotContain(queue, item => item.CorrelationKey == $"spec-clarification:{target.Id.Value}:scope:33333333");
        Xunit.Assert.Contains(queue, item => item.CorrelationKey == $"spec-clarification:{other.Id.Value}:scope:44444444");
    }

    [Xunit.Fact(DisplayName = "Cli_attention_answer_rejects_invalid_feasibility_disposition")]
    public async Task CliAttentionAnswerRejectsInvalidFeasibilityDisposition()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal(new GoalId("feedface555555555555555555555555"), "Target goal");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var correlationKey = $"spec-clarification:{target.Id.Value}:feasibility:33333333";
        _ = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            target.Id.Value,
            "Spec feasibility clarification needed",
            "Fork kind: feasibility",
            correlationKey);

        var output = ExecuteCliAndCapture(
            ["attention", "answer", target.Id.Value[..8], "33333333", "Measure it with two conductor gates."],
            kernel,
            workspace);
        var item = Xunit.Assert.Single(await store.ListAsync(target.Id.Value));

        Xunit.Assert.Equal(
            $"Failed to resolve clarification '33333333' for goal '{target.Id.Value[..8]}'.{Environment.NewLine}",
            output);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised, item.Status);
        Xunit.Assert.Null(item.Resolution);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_answer_goal_prefix_accepts_text_file")]
    public async Task CliAttentionAnswerGoalPrefixAcceptsTextFile()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal(new GoalId("abc10000aaaaaaaaaaaaaaaaaaaaaaaa"), "Target goal");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, target.Id.Value, "Target clarification", "Target body", $"spec-clarification:{target.Id.Value}:scope:12345678");
        var answer = "Use the file-backed answer.\n\nPreserve the complete response.";
        var answerPath = Path.Combine(root, "attention-answer.md");
        File.WriteAllText(answerPath, answer, System.Text.Encoding.UTF8);

        var output = ExecuteCliAndCapture(
            CliArgumentParser.SplitCommand($"attention answer {target.Id.Value[..8]} 12345678 --text-file {answerPath}"),
            kernel,
            workspace);
        var resolved = (await store.ListAsync()).Single(item => item.CorrelationKey == $"spec-clarification:{target.Id.Value}:scope:12345678");

        Xunit.Assert.Equal($"Answered clarification '12345678' for goal '{target.Id.Value[..8]}'.{Environment.NewLine}", output);
        Xunit.Assert.Equal(answer, resolved.Resolution);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_answer_rejects_inline_text_and_file")]
    public void CliAttentionAnswerRejectsInlineTextAndFile()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal(new GoalId("abc20000aaaaaaaaaaaaaaaaaaaaaaaa"), "Target goal");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = store.RaiseAsync(CollaborationItemType.Clarification, target.Id.Value, "Target clarification", "Target body", $"spec-clarification:{target.Id.Value}:scope:12345678").GetAwaiter().GetResult();
        var answerPath = Path.Combine(root, "attention-answer.md");
        File.WriteAllText(answerPath, "File answer.", System.Text.Encoding.UTF8);

        var ex = Xunit.Assert.ThrowsAny<ArgumentException>(() => ExecuteCliAndCapture(
            CliArgumentParser.SplitCommand($"attention answer {target.Id.Value[..8]} 12345678 Inline answer --text-file {answerPath}"),
            kernel,
            workspace));

        Xunit.Assert.Contains("either inline text or --text-file", ex.Message);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_answer_goal_prefix_rejects_wrong_clarification_id")]
    public async Task CliAttentionAnswerGoalPrefixRejectsWrongClarificationId()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal(new GoalId("12345678aaaaaaaaaaaaaaaaaaaaaaaa"), "Target goal");
        var other = kernel.CreateGoal(new GoalId("87654321bbbbbbbbbbbbbbbbbbbbbbbb"), "Other goal");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, other.Id.Value, "Other clarification", "Other body", $"spec-clarification:{other.Id.Value}:scope:55555555");

        var ex = Xunit.Assert.ThrowsAny<ArgumentException>(() =>
            ExecuteCliAndCapture(["attention", "answer", "12345678", "55555555", "Do not cross streams."], kernel, workspace));

        Xunit.Assert.Contains($"does not belong to goal '{target.Id.Value}'", ex.Message);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_answer_unknown_goal_prefix_errors")]
    public async Task CliAttentionAnswerUnknownGoalPrefixErrors()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var known = kernel.CreateGoal(new GoalId("abcdef12aaaaaaaaaaaaaaaaaaaaaaaa"), "Known goal");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, known.Id.Value, "Known clarification", "Known body", $"spec-clarification:{known.Id.Value}:scope:77777777");

        var ex = Xunit.Assert.ThrowsAny<KeyNotFoundException>(() =>
            ExecuteCliAndCapture(["attention", "answer", "99999999", "77777777", "Use the known answer."], kernel, workspace));
        var queue = await store.GetAttentionQueueAsync();

        Xunit.Assert.Contains("No goal found matching prefix '99999999'", ex.Message);
        Xunit.Assert.Contains(queue, item => item.CorrelationKey == $"spec-clarification:{known.Id.Value}:scope:77777777");
    }


    [Xunit.Fact(DisplayName = "Cli_attention_answer_unknown_goal_prefix_errors_even_when_clarification_id_is_unknown")]
    public void CliAttentionAnswerUnknownGoalPrefixErrorsEvenWhenClarificationIdIsUnknown()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        _ = kernel.CreateGoal(new GoalId("abcdef12aaaaaaaaaaaaaaaaaaaaaaaa"), "Known goal");

        var ex = Xunit.Assert.ThrowsAny<KeyNotFoundException>(() =>
            ExecuteCliAndCapture(["attention", "answer", "99999999", "88888888", "Use the answer."], kernel, workspace));

        Xunit.Assert.Contains("No goal found matching prefix '99999999'", ex.Message);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_answer_global_multi_word_answer_preserves_compatibility")]
    public async Task CliAttentionAnswerGlobalMultiWordAnswerPreservesCompatibility()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goalA = kernel.CreateGoal(new GoalId("a1a1a1a1111111111111111111111111"), "A");
        var goalB = kernel.CreateGoal(new GoalId("b2b2b2b2222222222222222222222222"), "B");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, goalA.Id.Value, "Target global clarification", "Target body", $"spec-clarification:{goalA.Id.Value}:global:66666666");
        for (var index = 0; index < 10; index++)
        {
            _ = await store.RaiseAsync(CollaborationItemType.Clarification, goalB.Id.Value, $"Unrelated clarification {index}", "Other body", $"spec-clarification:{goalB.Id.Value}:global:{index:00000000}");
        }

        var output = ExecuteCliAndCapture(
            ["attention", "answer", "66666666", "Use", "the", "global", "answer."],
            kernel,
            workspace);
        var queue = await store.GetAttentionQueueAsync();

        Xunit.Assert.Equal($"Answered clarification '66666666'.{Environment.NewLine}", output);
        Xunit.Assert.DoesNotContain("Unrelated clarification", output);
        Xunit.Assert.DoesNotContain(queue, item => item.CorrelationKey == $"spec-clarification:{goalA.Id.Value}:global:66666666");
        Xunit.Assert.Equal(10, queue.Count);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_answer_global_accepts_hex_like_first_answer_token")]
    public async Task CliAttentionAnswerGlobalAcceptsHexLikeFirstAnswerToken()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goalA = kernel.CreateGoal(new GoalId("c1c1c1c1111111111111111111111111"), "A");
        var goalB = kernel.CreateGoal(new GoalId("d2d2d2d2222222222222222222222222"), "B");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, goalA.Id.Value, "Target global clarification", "Target body", $"spec-clarification:{goalA.Id.Value}:global:66666666");
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, goalB.Id.Value, "Unrelated clarification", "Other body", $"spec-clarification:{goalB.Id.Value}:global:deadbeef");

        var output = ExecuteCliAndCapture(
            ["attention", "answer", "66666666", "deadbeef", "continue"],
            kernel,
            workspace);
        var items = await store.ListAsync();
        var resolved = items.Single(item => item.CorrelationKey == $"spec-clarification:{goalA.Id.Value}:global:66666666");
        var queue = await store.GetAttentionQueueAsync();

        Xunit.Assert.Equal($"Answered clarification '66666666'.{Environment.NewLine}", output);
        Xunit.Assert.Equal("deadbeef continue", resolved.Resolution);
        Xunit.Assert.DoesNotContain(queue, item => item.CorrelationKey == $"spec-clarification:{goalA.Id.Value}:global:66666666");
        Xunit.Assert.Contains(queue, item => item.CorrelationKey == $"spec-clarification:{goalB.Id.Value}:global:deadbeef");
    }


    [Xunit.Fact(DisplayName = "Cli_attention_answer_global_id_wins_when_it_matches_goal_prefix")]
    public async Task CliAttentionAnswerGlobalIdWinsWhenItMatchesGoalPrefix()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var collidingGoal = kernel.CreateGoal(new GoalId("66666666aaaaaaaaaaaaaaaaaaaaaaaa"), "Colliding goal");
        var owner = kernel.CreateGoal(new GoalId("eeeeeeeebbbbbbbbbbbbbbbbbbbbbbbb"), "Owner goal");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, owner.Id.Value, "Target global clarification", "Target body", $"spec-clarification:{owner.Id.Value}:global:66666666");
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, collidingGoal.Id.Value, "Scoped clarification", "Scoped body", $"spec-clarification:{collidingGoal.Id.Value}:scope:deadbeef");

        var output = ExecuteCliAndCapture(
            ["attention", "answer", "66666666", "deadbeef", "continue"],
            kernel,
            workspace);
        var items = await store.ListAsync();
        var resolved = items.Single(item => item.CorrelationKey == $"spec-clarification:{owner.Id.Value}:global:66666666");
        var queue = await store.GetAttentionQueueAsync();

        Xunit.Assert.Equal($"Answered clarification '66666666'.{Environment.NewLine}", output);
        Xunit.Assert.Equal("deadbeef continue", resolved.Resolution);
        Xunit.Assert.DoesNotContain(queue, item => item.CorrelationKey == $"spec-clarification:{owner.Id.Value}:global:66666666");
        Xunit.Assert.Contains(queue, item => item.CorrelationKey == $"spec-clarification:{collidingGoal.Id.Value}:scope:deadbeef");
    }


    [Xunit.Fact(DisplayName = "Cli_attention_show_global_lists_full_queue")]
    public async Task CliAttentionShowGlobalListsFullQueue()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goalA = kernel.CreateGoal(new GoalId("abc00000111111111111111111111111"), "A");
        var goalB = kernel.CreateGoal(new GoalId("def00000222222222222222222222222"), "B");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        for (var index = 0; index < 10; index++)
        {
            var goal = index % 2 == 0 ? goalA : goalB;
            _ = await store.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value, $"Global clarification {index}", "Body", $"spec-clarification:{goal.Id.Value}:global:{index:00000000}");
        }

        var output = ExecuteCliAndCapture(["attention", "show"], kernel, workspace);

        Xunit.Assert.Contains("Attention queue: 10 open reach-up item(s)", output);
        Xunit.Assert.Contains("Global clarification 0", output);
        Xunit.Assert.Contains("Global clarification 9", output);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_show_excludes_parked_goal_waits_by_default_and_all_includes_history")]
    public void CliAttentionShowExcludesParkedGoalWaitsByDefaultAndAllIncludesHistory()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var live = kernel.CreateGoal(new GoalId("abc10000111111111111111111111111"), "Live wait");
        var parked = kernel.CreateGoal(new GoalId("abc20000222222222222222222222222"), "Parked wait");
        _ = kernel.RequestHumanInput(live.Id, null, "Need live choice.", HumanWaitKind.RiskReview);
        _ = kernel.RequestHumanInput(parked.Id, null, "Need parked choice.", HumanWaitKind.RiskReview);
        kernel.ParkGoal(parked.Id, "deferred");

        var defaultOutput = ExecuteCliAndCapture(["attention", "show"], kernel, workspace);
        var allOutput = ExecuteCliAndCapture(["attention", "show", "--all"], kernel, workspace);

        Xunit.Assert.Contains("Need live choice.", defaultOutput);
        Xunit.Assert.DoesNotContain("Need parked choice.", defaultOutput);
        Xunit.Assert.Contains("Need parked choice.", allOutput);
        Xunit.Assert.Contains("resolution: Goal parked: deferred", allOutput);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_show_migrates_legacy_goal_parked_waits")]
    public async Task CliAttentionShowMigratesLegacyGoalParkedWaits()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var live = kernel.CreateGoal(new GoalId("abc30000333333333333333333333333"), "Live wait");
        var parked = kernel.CreateGoal(new GoalId("abc40000444444444444444444444444"), "Legacy parked wait");
        _ = kernel.RequestHumanInput(live.Id, null, "Need live answer.", HumanWaitKind.RiskReview);
        var parkedWait = kernel.RequestHumanInput(parked.Id, null, "Goal parked: stale_resurrected_sweep");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            parked.Id.Value,
            "Parked clarification",
            "Parked body",
            $"spec-clarification:{parked.Id.Value}:scope:99999999");

        var defaultOutput = ExecuteCliAndCapture(["attention", "show"], kernel, workspace);
        var allOutput = ExecuteCliAndCapture(["attention", "show", "--all"], kernel, workspace);
        var items = await store.ListAsync(parked.Id.Value);

        Xunit.Assert.Equal(GoalStatus.Parked, parked.Status);
        Xunit.Assert.True(parkedWait.IsCompleted);
        Xunit.Assert.Equal("Goal parked: stale_resurrected_sweep", parkedWait.Answer);
        Xunit.Assert.DoesNotContain("stale_resurrected_sweep", defaultOutput);
        Xunit.Assert.DoesNotContain("Parked clarification", defaultOutput);
        Xunit.Assert.Contains("Need live answer.", defaultOutput);
        Xunit.Assert.Contains("stale_resurrected_sweep", allOutput);
        Xunit.Assert.Contains("Parked clarification", allOutput);
        Xunit.Assert.All(items, item =>
        {
            Xunit.Assert.Equal(CollaborationItemStatus.Resolved, item.Status);
            Xunit.Assert.Equal("Goal parked: stale_resurrected_sweep", item.Resolution);
        });
    }


    [Xunit.Fact(DisplayName = "Cli_attention_show_lists_typed_human_waits_with_goal_filter")]
    public void CliAttentionShowListsTypedHumanWaitsWithGoalFilter()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal(new GoalId("abc10000111111111111111111111111"), "Target wait");
        var other = kernel.CreateGoal(new GoalId("def20000222222222222222222222222"), "Other wait");
        _ = kernel.RequestHumanInput(
            target.Id,
            null,
            "Need provider login.",
            HumanWaitKind.ProviderAuth,
            resumeCommand: "provider login resume");
        _ = kernel.RequestHumanInput(
            other.Id,
            null,
            "Approve risk.",
            HumanWaitKind.RiskReview,
            resumeCommand: "risk resume");

        var globalOutput = ExecuteCliAndCapture(["attention", "show"], kernel, workspace);
        var scopedOutput = ExecuteCliAndCapture(["attention", "show", "--goal", "abc10000"], kernel, workspace);

        Xunit.Assert.Contains("ProviderAuth", globalOutput);
        Xunit.Assert.Contains("RiskReview", globalOutput);
        Xunit.Assert.Contains("resume: provider login resume", scopedOutput);
        Xunit.Assert.Contains("goal=abc10000", scopedOutput);
        Xunit.Assert.DoesNotContain("RiskReview", scopedOutput);
        Xunit.Assert.DoesNotContain("risk resume", scopedOutput);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_show_unknown_goal_prefix_errors")]
    public void CliAttentionShowUnknownGoalPrefixErrors()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        _ = kernel.CreateGoal(new GoalId("11111111aaaaaaaaaaaaaaaaaaaaaaaa"), "Known goal");

        var ex = Xunit.Assert.ThrowsAny<KeyNotFoundException>(() =>
            ExecuteCliAndCapture(["attention", "show", "99999999"], kernel, workspace));

        Xunit.Assert.Contains("No goal found matching prefix '99999999'", ex.Message);
    }


    [Xunit.Fact(DisplayName = "Cli_park_goal_requires_confirmation_and_resolves_attention_waits")]
    public void CliParkGoalRequiresConfirmationAndResolvesAttentionWaits()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Park interrupted work", [task]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);
        RecordRunningProcess(kernel, goal, task, root);
        var goalPrefix = goal.Id.Value[..8];

        var dryRunOutput = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                CliArgumentParser.SplitCommand($"park-goal {goalPrefix} Operator paused for review."),
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
        Xunit.Assert.True(task.LastProcess!.IsRunning);
        Xunit.Assert.Empty(kernel.HumanInputRequests);
        Xunit.Assert.Contains("Goal park dry run", dryRunOutput);
        Xunit.Assert.Contains("attention waits: resolve with park reason", dryRunOutput);

        var applyOutput = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                CliArgumentParser.SplitCommand($"park-goal {goalPrefix} Operator paused for review. --confirm-goal-park"),
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Equal(GoalStatus.Parked, goal.Status);
        Xunit.Assert.False(task.LastProcess!.IsRunning);
        Xunit.Assert.Empty(kernel.HumanInputRequests);
        Xunit.Assert.Contains("Resolved human waits: 0", applyOutput);
        Xunit.Assert.Contains("Resolved attention items: 0", applyOutput);
    }


}
