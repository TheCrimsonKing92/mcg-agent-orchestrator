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
        var globalAnswer = CliArgumentParser.NormalizeArgs(
            ["attention", "answer", "391ce87f", "Use", "the", "global", "request", "id."]);
        var fullRequestAnswer = CliArgumentParser.NormalizeArgs(
            ["attention", "answer", "abc123ef", "391ce87f391ce87f391ce87f391ce87f", "Use", "the", "full", "request", "id."]);
        var topicAnswer = CliArgumentParser.NormalizeArgs(
            ["attention", "answer", "abc123ef", "stranded-edits-disposition", "Use", "the", "topic", "id."]);
        var correlationAnswer = CliArgumentParser.NormalizeArgs(
            ["attention", "answer", "abc123ef", "spec-clarification:abc123ef:scope:duplicate-topic", "Use", "the", "full", "id."]);

        Xunit.Assert.Equal(["attention", "show", "abc123ef"], show);
        Xunit.Assert.Equal(["attention", "dismiss", "abc123ef"], dismiss);
        Xunit.Assert.Equal(["attention", "answer", "abc123ef", "391ce87f", "Use a static helper."], answer);
        Xunit.Assert.Equal(["attention", "answer", "391ce87f", "Use the global request id."], globalAnswer);
        Xunit.Assert.Equal(["attention", "answer", "abc123ef", "391ce87f391ce87f391ce87f391ce87f", "Use the full request id."], fullRequestAnswer);
        Xunit.Assert.Equal(["attention", "answer", "abc123ef", "stranded-edits-disposition", "Use the topic id."], topicAnswer);
        Xunit.Assert.Equal(["attention", "answer", "abc123ef", "spec-clarification:abc123ef:scope:duplicate-topic", "Use the full id."], correlationAnswer);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_splits_interactive_subcommands")]
    public void CliAttentionSplitsInteractiveSubcommands()
    {
        var show = CliArgumentParser.SplitCommand("attention show abc123ef");
        var dismiss = CliArgumentParser.SplitCommand("attention dismiss abc123ef");
        var answer = CliArgumentParser.SplitCommand("attention answer abc123ef 391ce87f Use a static helper.");
        var globalAnswer = CliArgumentParser.SplitCommand("attention answer 391ce87f Use the global request id.");
        var fullRequestAnswer = CliArgumentParser.SplitCommand(
            "attention answer abc123ef 391ce87f391ce87f391ce87f391ce87f Use the full request id.");
        var topicAnswer = CliArgumentParser.SplitCommand(
            "attention answer abc123ef stranded-edits-disposition Use the topic id.");
        var correlationAnswer = CliArgumentParser.SplitCommand(
            "attention answer abc123ef spec-clarification:abc123ef:scope:duplicate-topic Use the full id.");

        Xunit.Assert.Equal(["attention", "show", "abc123ef"], show);
        Xunit.Assert.Equal(["attention", "dismiss", "abc123ef"], dismiss);
        Xunit.Assert.Equal(["attention", "answer", "abc123ef", "391ce87f", "Use a static helper."], answer);
        Xunit.Assert.Equal(["attention", "answer", "391ce87f", "Use the global request id."], globalAnswer);
        Xunit.Assert.Equal(["attention", "answer", "abc123ef", "391ce87f391ce87f391ce87f391ce87f", "Use the full request id."], fullRequestAnswer);
        Xunit.Assert.Equal(["attention", "answer", "abc123ef", "stranded-edits-disposition", "Use the topic id."], topicAnswer);
        Xunit.Assert.Equal(["attention", "answer", "abc123ef", "spec-clarification:abc123ef:scope:duplicate-topic", "Use the full id."], correlationAnswer);
    }


    [Xunit.Fact(DisplayName = "Cli_attention_item_dismissal_handles_cross_goal_and_owned_items_selectively")]
    public async Task CliAttentionItemDismissalHandlesCrossGoalAndOwnedItemsSelectively()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("aaaaaaaa111111111111111111111111"), "Owned attention");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var crossGoal = await store.RaiseAsync(
            CollaborationItemType.Verify,
            null,
            "Historical canary",
            "Already handled");
        var ownedStale = await store.RaiseAsync(
            CollaborationItemType.Verify,
            goal.Id.Value,
            "Owned stale item",
            "Clear only this item");
        var ownedLive = await store.RaiseAsync(
            CollaborationItemType.Decision,
            goal.Id.Value,
            "Owned live item",
            "Keep this item");

        var listing = ExecuteCliAndCapture(["attention", "show"], kernel, workspace);
        Xunit.Assert.Contains("goal=cross-goal", listing, StringComparison.Ordinal);
        Xunit.Assert.Contains($"dismiss: attention dismiss --item {crossGoal.Id}", listing, StringComparison.Ordinal);
        Xunit.Assert.Contains($"dismiss: attention dismiss --item {ownedStale.Id}", listing, StringComparison.Ordinal);

        var crossGoalResult = ExecuteCliAndCapture(
            ["attention", "dismiss", "--item", crossGoal.Id[..8]],
            kernel,
            workspace);
        var ownedResult = ExecuteCliAndCapture(
            ["attention", "dismiss", "--item", ownedStale.Id],
            kernel,
            workspace);

        Xunit.Assert.Contains($"Dismissed attention item '{crossGoal.Id}'.", crossGoalResult, StringComparison.Ordinal);
        Xunit.Assert.Contains($"Dismissed attention item '{ownedStale.Id}'.", ownedResult, StringComparison.Ordinal);
        var remaining = await store.GetAttentionQueueAsync();
        Xunit.Assert.Equal(ownedLive.Id, Xunit.Assert.Single(remaining).Id);
    }

    [Xunit.Fact(DisplayName = "Cli_attention_item_dismissal_distinguishes_unknown_from_ineligible")]
    public async Task CliAttentionItemDismissalDistinguishesUnknownFromIneligible()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var resolved = await store.RaiseAsync(
            CollaborationItemType.Verify,
            null,
            "Resolved item",
            "Historical",
            "resolved-attention-item");
        Xunit.Assert.True(await store.TryResolveAsync(resolved.CorrelationKey!, "already handled"));

        var unknown = Xunit.Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            ["attention", "dismiss", "--item", "ffffffff"],
            kernel,
            workspace));
        var ineligible = Xunit.Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(
            ["attention", "dismiss", "--item", resolved.Id],
            kernel,
            workspace));
        var displayArtifact = Xunit.Assert.Throws<KeyNotFoundException>(() => ExecuteCliAndCapture(
            ["attention", "dismiss", "cross-goal"],
            kernel,
            workspace));

        Xunit.Assert.Equal("No attention item matches id 'ffffffff'.", unknown.Message);
        Xunit.Assert.Contains("matched, but status is 'Resolved'; nothing was dismissed", ineligible.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("No goal found matching prefix 'cross-goal'", displayArtifact.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_attention_goal_dismissal_resolves_all_owned_items_and_is_idempotent")]
    public async Task CliAttentionGoalDismissalResolvesAllOwnedItemsAndIsIdempotent()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = kernel.CreateGoal(new GoalId("abcddcba111111111111111111111111"), "Target attention");
        var other = kernel.CreateGoal(new GoalId("dcbaabcd222222222222222222222222"), "Other attention");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var targetVerify = await store.RaiseAsync(
            CollaborationItemType.Verify,
            target.Id.Value,
            "Target verification",
            "Dismiss this item");
        var targetDecision = await store.RaiseAsync(
            CollaborationItemType.Decision,
            target.Id.Value,
            "Target decision",
            "Dismiss this item too");
        var otherItem = await store.RaiseAsync(
            CollaborationItemType.Verify,
            other.Id.Value,
            "Other verification",
            "Keep this item");

        var dismissed = ExecuteCliAndCapture(
            ["attention", "dismiss", target.Id.Value[..8]],
            kernel,
            workspace);
        var repeated = ExecuteCliAndCapture(
            ["attention", "dismiss", target.Id.Value[..8]],
            kernel,
            workspace);

        Xunit.Assert.Equal(
            $"Dismissed 2 of 2 open attention item(s) for goal '{target.Id.Value}'.{Environment.NewLine}",
            dismissed);
        Xunit.Assert.Equal(
            $"Dismissed 0 of 0 open attention item(s) for goal '{target.Id.Value}'.{Environment.NewLine}",
            repeated);
        var items = await store.ListAsync();
        Xunit.Assert.Equal(
            CollaborationItemStatus.Resolved,
            items.Single(item => item.Id == targetVerify.Id).Status);
        Xunit.Assert.Equal(
            CollaborationItemStatus.Resolved,
            items.Single(item => item.Id == targetDecision.Id).Status);
        Xunit.Assert.Equal(
            CollaborationItemStatus.Raised,
            items.Single(item => item.Id == otherItem.Id).Status);
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

    [Xunit.Fact]
    public async Task CliAttentionShowGoalPrefixPrintsOpenDecision()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("cccccccc111111111111111111111111"), "Decision goal");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var decision = await store.RaiseAsync(
            CollaborationItemType.Decision,
            goal.Id.Value,
            "Need a call",
            "Decide the remaining slice.");

        var output = ExecuteCliAndCapture(["attention", "show", "cccccccc"], kernel, workspace);

        Xunit.Assert.Contains($"[{decision.Id[..8]}] (Decision) Need a call", output, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("No open attention items", output, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Answer with:", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task CliAttentionShowGoalPrefixPrintsWaitAndDecision()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Wait and decision");
        var task = goal.Tasks.Single();
        kernel.RequestHumanInput(goal.Id, task.Id, "Which branch?");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var decision = await store.RaiseAsync(
            CollaborationItemType.Decision,
            goal.Id.Value,
            "Need a call",
            "Decide after the wait.");

        var output = ExecuteCliAndCapture(["attention", "show", goal.Id.Value[..8]], kernel, workspace);

        Xunit.Assert.Contains("Human waits:", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("Which branch?", output, StringComparison.Ordinal);
        Xunit.Assert.Contains($"[{decision.Id[..8]}] (Decision) Need a call", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task CliAttentionListingItemIdRoundTripsThroughScopedAnswer()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("aa11bb22cccccccccccccccccccccccc"), "Listed identifier");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var raised = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Printed identifier",
            "Answer the identifier printed by bare attention.",
            $"spec-clarification:{goal.Id.Value}:scope:printed-id");

        var listing = ExecuteCliAndCapture(["attention"], kernel, workspace);
        var listingLine = listing.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("[Clarification]", StringComparison.Ordinal));
        var printedId = listingLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        var output = ExecuteCliAndCapture(
            ["attention", "answer", goal.Id.Value[..8], printedId, "Use the printed identifier."],
            kernel,
            workspace);
        ApplyQueuedAnswer(kernel, workspace, goal);
        var resolved = (await store.ListAsync()).Single(item => item.Id == raised.Id);

        Xunit.Assert.Equal(raised.Id[..8], printedId);
        Xunit.Assert.Contains("verb=answer", output, StringComparison.Ordinal);
        Xunit.Assert.Equal("Use the printed identifier.", resolved.Resolution);
    }

    [Xunit.Fact]
    public async Task CliAttentionListingItemIdAlsoResolvesGlobally()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("bb22cc33dddddddddddddddddddddddd"), "Global listed identifier");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var raised = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Global printed identifier",
            "Answer globally.",
            $"spec-clarification:{goal.Id.Value}:scope:global-printed-id");

        var output = ExecuteCliAndCapture(
            ["attention", "answer", raised.Id[..8], "Use the global printed identifier."],
            kernel,
            workspace);
        ApplyQueuedAnswer(kernel, workspace, goal);
        var resolved = (await store.ListAsync()).Single(item => item.Id == raised.Id);

        Xunit.Assert.Contains("verb=answer", output, StringComparison.Ordinal);
        Xunit.Assert.Equal("Use the global printed identifier.", resolved.Resolution);
    }

    [Xunit.Fact]
    public async Task CliAttentionCollidingPrefixesRoundTripShownIds()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("aabbccdd111111111111111111111111"), "Colliding topics");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var disposition = $"spec-clarification:{goal.Id.Value}:stranded-edits-disposition";
        var preservation = $"spec-clarification:{goal.Id.Value}:stranded-edits-preservation-mechanism";
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value, "Disposition", "First body", disposition);
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value, "Preservation", "Second body", preservation);

        var shown = ExecuteCliAndCapture(["attention", "show", goal.Id.Value[..8]], kernel, workspace);
        var firstAnswer = ExecuteCliAndCapture(
            CliArgumentParser.NormalizeArgs(
                ["attention", "answer", goal.Id.Value[..8], "stranded-edits-disposition", "Apply", "disposition."]),
            kernel,
            workspace);
        var secondAnswer = ExecuteCliAndCapture(
            CliArgumentParser.SplitCommand(
                $"attention answer {goal.Id.Value[..8]} stranded-edits-preservation-mechanism Preserve edits."),
            kernel,
            workspace);
        ApplyQueuedAnswer(kernel, workspace, goal);
        ApplyQueuedAnswer(kernel, workspace, goal);
        var items = await store.ListAsync();

        Xunit.Assert.Contains("[stranded-edits-disposition] Disposition", shown, StringComparison.Ordinal);
        Xunit.Assert.Contains("[stranded-edits-preservation-mechanism] Preservation", shown, StringComparison.Ordinal);
        Xunit.Assert.Contains("verb=answer", firstAnswer, StringComparison.Ordinal);
        Xunit.Assert.Contains("verb=answer", secondAnswer, StringComparison.Ordinal);
        Xunit.Assert.Equal("Apply disposition.", items.Single(item => item.CorrelationKey == disposition).Resolution);
        Xunit.Assert.Equal("Preserve edits.", items.Single(item => item.CorrelationKey == preservation).Resolution);
    }

    [Xunit.Fact]
    public async Task CliAttentionAnswerKeepsPeerIdStable()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("bbccddee222222222222222222222222"), "Stable topic ids");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        _ = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Disposition",
            "First body",
            $"spec-clarification:{goal.Id.Value}:stranded-edits-disposition");
        _ = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Preservation",
            "Second body",
            $"spec-clarification:{goal.Id.Value}:stranded-edits-preservation-mechanism");

        var before = ExecuteCliAndCapture(["attention", "show", goal.Id.Value[..8]], kernel, workspace);
        _ = ExecuteCliAndCapture(
            ["attention", "answer", goal.Id.Value[..8], "stranded-edits-disposition", "Apply disposition."],
            kernel,
            workspace);
        ApplyQueuedAnswer(kernel, workspace, goal);
        var after = ExecuteCliAndCapture(["attention", "show", goal.Id.Value[..8]], kernel, workspace);

        const string stableLine = "[stranded-edits-preservation-mechanism] Preservation";
        Xunit.Assert.Contains(stableLine, before, StringComparison.Ordinal);
        Xunit.Assert.Contains(stableLine, after, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task CliAttentionFullCorrelationKeyEscapesTrueCollision()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("ccddeeaa333333333333333333333333"), "Identical topic ids");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var firstKey = $"spec-clarification:{goal.Id.Value}:scope-a:duplicate-topic";
        var secondKey = $"spec-clarification:{goal.Id.Value}:scope-b:duplicate-topic";
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value, "First", "First body", firstKey);
        _ = await store.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value, "Second", "Second body", secondKey);

        var shown = ExecuteCliAndCapture(["attention", "show", goal.Id.Value[..8]], kernel, workspace);
        var ambiguous = Xunit.Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            ["attention", "answer", goal.Id.Value[..8], "duplicate-topic", "Ambiguous answer."],
            kernel,
            workspace));
        var answered = ExecuteCliAndCapture(
            CliArgumentParser.SplitCommand(
                $"attention answer {goal.Id.Value[..8]} {secondKey} Second answer."),
            kernel,
            workspace);
        ApplyQueuedAnswer(kernel, workspace, goal);
        var resolved = (await store.ListAsync()).Single(item => item.CorrelationKey == secondKey);

        Xunit.Assert.Contains($"[{firstKey}] First", shown, StringComparison.Ordinal);
        Xunit.Assert.Contains($"[{secondKey}] Second", shown, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("more characters", ambiguous.Message, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("full correlation key", ambiguous.Message, StringComparison.OrdinalIgnoreCase);
        Xunit.Assert.Contains("verb=answer", answered, StringComparison.Ordinal);
        Xunit.Assert.Equal("Second answer.", resolved.Resolution);
    }

    [Xunit.Fact]
    public async Task CliAttentionSingleTokenTopicShowsRoundTripCorrelationKey()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("ddeeffaa444444444444444444444444"), "Single-token topic");
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var correlationKey = $"spec-clarification:{goal.Id.Value}:scope";
        _ = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Scope",
            "Scope body",
            correlationKey);

        var shown = ExecuteCliAndCapture(["attention", "show", goal.Id.Value[..8]], kernel, workspace);
        var answerParts = CliArgumentParser.NormalizeArgs(
            ["attention", "answer", goal.Id.Value[..8], correlationKey, "Use", "the", "narrow", "scope."]);
        var answered = ExecuteCliAndCapture(answerParts, kernel, workspace);
        ApplyQueuedAnswer(kernel, workspace, goal);

        Xunit.Assert.Contains($"[{correlationKey}] Scope", shown, StringComparison.Ordinal);
        Xunit.Assert.Contains("verb=answer", answered, StringComparison.Ordinal);
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
        ApplyQueuedAnswer(kernel, workspace, target);
        var queue = await store.GetAttentionQueueAsync();

        Xunit.Assert.Contains("verb=answer", output);
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
        var coordinator = OperatorIntentCoordinator.CreateDefault(workspace);
        coordinator.ExecutePending(kernel, target);
        var item = Xunit.Assert.Single(await store.ListAsync(target.Id.Value));

        Xunit.Assert.Contains("verb=answer", output);
        var rejected = Xunit.Assert.Single(await SqliteOperatorIntentStore.ForDirectories(
            workspace.OrchestratorDirectory, workspace.LogDirectory).ListForGoalAsync(target.Id.Value));
        Xunit.Assert.Equal(OperatorIntentStatus.Rejected, rejected.Status);
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
        ApplyQueuedAnswer(kernel, workspace, target);
        var resolved = (await store.ListAsync()).Single(item => item.CorrelationKey == $"spec-clarification:{target.Id.Value}:scope:12345678");

        Xunit.Assert.Contains("verb=answer", output);
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

        Xunit.Assert.Contains($"Clarification id '55555555' was not found for goal '{target.Id.Value}'", ex.Message);
        Xunit.Assert.Contains($"attention show {target.Id.Value[..8]}", ex.Message);
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
        ApplyQueuedAnswer(kernel, workspace, goalA);
        var queue = await store.GetAttentionQueueAsync();

        Xunit.Assert.Contains("verb=answer", output);
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
        ApplyQueuedAnswer(kernel, workspace, goalA);
        var items = await store.ListAsync();
        var resolved = items.Single(item => item.CorrelationKey == $"spec-clarification:{goalA.Id.Value}:global:66666666");
        var queue = await store.GetAttentionQueueAsync();

        Xunit.Assert.Contains("verb=answer", output);
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
        ApplyQueuedAnswer(kernel, workspace, owner);
        var items = await store.ListAsync();
        var resolved = items.Single(item => item.CorrelationKey == $"spec-clarification:{owner.Id.Value}:global:66666666");
        var queue = await store.GetAttentionQueueAsync();

        Xunit.Assert.Contains("verb=answer", output);
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


    [Xunit.Fact]
    public void SpecClarification_AdvertisedCommand_ClosesWaitAndResumesGoal()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement after clarification.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Answer the advertised clarification.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = kernel.RequestHumanInput(
            goal.Id,
            task.Id,
            "Which implementation should be used?",
            HumanWaitKind.SpecClarification);

        Xunit.Assert.Equal(GoalStatus.WaitingForHuman, goal.Status);
        var shown = ExecuteCliAndCapture(["attention", "show", goal.Id.Value[..8]], kernel, workspace);
        var advertised = shown.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Single(line => line.StartsWith("resume: ", StringComparison.Ordinal))["resume: ".Length..];
        Xunit.Assert.StartsWith($"attention answer {goal.Id.Value[..8]} ", advertised, StringComparison.Ordinal);
        var command = CliArgumentParser.SplitCommand(advertised.Replace("<answer>", "approved", StringComparison.Ordinal));

        var result = ExecuteCliAndCaptureResult(command, kernel, workspace);

        Xunit.Assert.False(result.Changed);
        Xunit.Assert.False(request.IsCompleted);
        ApplyQueuedAnswer(kernel, workspace, goal);
        Xunit.Assert.True(request.IsCompleted);
        Xunit.Assert.Equal("approved", request.Answer);
        Xunit.Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
    }


    [Xunit.Fact]
    public void SpecClarification_UniqueDisplayedRequestId_WorksWithoutGoalPrefix()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement after clarification.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Answer by globally unique request id.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = kernel.RequestHumanInput(
            goal.Id,
            task.Id,
            "Which implementation should be used?",
            HumanWaitKind.SpecClarification);
        var shown = ExecuteCliAndCapture(["attention", "show", goal.Id.Value[..8]], kernel, workspace);
        var advertised = shown.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Single(line => line.StartsWith("resume: ", StringComparison.Ordinal))["resume: ".Length..];
        var scopedCommand = CliArgumentParser.SplitCommand(
            advertised.Replace("<answer>", "globally-approved", StringComparison.Ordinal));
        var globalCommand = CliArgumentParser.SplitCommand(
            $"attention answer {scopedCommand[3]} globally-approved");

        var result = ExecuteCliAndCaptureResult(globalCommand, kernel, workspace);

        Xunit.Assert.False(result.Changed);
        Xunit.Assert.False(request.IsCompleted);
        ApplyQueuedAnswer(kernel, workspace, goal);
        Xunit.Assert.True(request.IsCompleted);
        Xunit.Assert.Equal("globally-approved", request.Answer);
        Xunit.Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
    }


    [Xunit.Fact]
    public void SpecClarification_FullRequestId_ResolvesWithinGoalScope()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement after clarification.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Answer by full request id.", [task]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = kernel.RequestHumanInput(
            goal.Id,
            task.Id,
            "Which implementation should be used?",
            HumanWaitKind.SpecClarification);
        var command = CliArgumentParser.NormalizeArgs(
            ["attention", "answer", goal.Id.Value[..8], request.Id.Value, "full-id-approved"]);

        var result = ExecuteCliAndCaptureResult(command, kernel, workspace);

        Xunit.Assert.False(result.Changed);
        Xunit.Assert.False(request.IsCompleted);
        ApplyQueuedAnswer(kernel, workspace, goal);
        Xunit.Assert.True(request.IsCompleted);
        Xunit.Assert.Equal("full-id-approved", request.Answer);
        Xunit.Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
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
        var prospective = kernel.RequestHumanInput(
            goal.Id,
            task.Id,
            "Observe the candidate after implementation.",
            HumanWaitKind.ProspectiveAcceptanceEvidence);
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
        Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id));
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
        Xunit.Assert.False(prospective.IsCompleted);
        Xunit.Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Xunit.Assert.Contains("Resolved human waits: 0", applyOutput);
        Xunit.Assert.Contains("Resolved attention items: 0", applyOutput);
    }


    private static void ApplyQueuedAnswer(
        AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace, Goal goal)
    {
        var coordinator = OperatorIntentCoordinator.CreateDefault(workspace);
        Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
        coordinator.CompletePersisted([goal.Id]);
    }
}
