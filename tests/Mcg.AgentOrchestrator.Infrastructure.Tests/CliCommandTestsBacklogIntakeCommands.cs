using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text.Json;

public sealed class CliCommandTestsBacklogIntakeCommands : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "Cli_backlog_add_keeps_body_text_grouped_before_file_flag")]
    public void CliBacklogAddKeepsBodyTextGroupedBeforeFileFlag()
    {
        var parts = CliArgumentParser.SplitCommand(
            "backlog-add Parser regression Keep multi word backlog body text --body-file body.md");

        Xunit.Assert.Equal(
            ["backlog-add", "Parser", "regression Keep multi word backlog body text", "--body-file", "body.md"],
            parts);
    }

    [Xunit.Fact]
    public void CliBacklogAddGroupsFlaggedMultiWordTitleForBothParserEntryPoints()
    {
        var interactive = CliArgumentParser.SplitCommand(
            "backlog-add --title Parser regression title --text-file body.md --depends-on abc12345");
        var oneShot = CliArgumentParser.NormalizeArgs(
            ["backlog-add", "--title", "Parser", "regression", "title", "--text-file", "body.md", "--depends-on", "abc12345"]);

        Xunit.Assert.Equal(
            ["backlog-add", "--title", "Parser regression title", "--text-file", "body.md", "--depends-on", "abc12345"],
            interactive);
        Xunit.Assert.Equal(interactive, oneShot);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_list_splits_limit_status_and_text_flags")]
    public void CliBacklogListSplitsLimitStatusAndTextFlags()
    {
        var interactive = CliArgumentParser.SplitCommand("backlog-list --limit 5 --status open --text Foo");
        var oneShot = CliArgumentParser.NormalizeArgs(["backlog-list", "--limit", "5", "--status", "open", "--text", "Foo"]);

        Xunit.Assert.Equal(["backlog-list", "--limit", "5", "--status", "open", "--text", "Foo"], interactive);
        Xunit.Assert.Equal(interactive, oneShot);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_update_groups_multi_word_field_values")]
    public void CliBacklogUpdateGroupsMultiWordFieldValues()
    {
        var interactive = CliArgumentParser.SplitCommand("backlog-update abc123 --title New title --description Better current truth --priority high");
        var oneShot = CliArgumentParser.NormalizeArgs(["backlog-update", "abc123", "--title", "New", "title", "--description", "Better", "current", "truth", "--priority", "high"]);

        Xunit.Assert.Equal(["backlog-update", "abc123", "--title", "New title", "--description", "Better current truth", "--priority", "high"], interactive);
        Xunit.Assert.Equal(interactive, oneShot);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_list_limit_filter_caps_results")]
    public void CliBacklogListLimitFilterCapsResults()
    {
        var items = new[]
        {
            BacklogItemFor("One"),
            BacklogItemFor("Two"),
            BacklogItemFor("Three")
        };

        var filtered = CliCommandHandlers.ApplyBacklogListFilters(items, status: null, text: null, limit: 2);

        Xunit.Assert.Equal(["One", "Two"], filtered.Select(item => item.Title));
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_list_status_filter_matches_case_insensitively")]
    public void CliBacklogListStatusFilterMatchesCaseInsensitively()
    {
        var items = new[]
        {
            BacklogItemFor("Open item", status: BacklogItemStatus.Open),
            BacklogItemFor("Done item", status: BacklogItemStatus.Done)
        };

        var filtered = CliCommandHandlers.ApplyBacklogListFilters(items, status: "done", text: null, limit: null);

        var item = Xunit.Assert.Single(filtered);
        Xunit.Assert.Equal("Done item", item.Title);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_list_text_filter_matches_title_and_body_case_insensitively")]
    public void CliBacklogListTextFilterMatchesTitleAndBodyCaseInsensitively()
    {
        var items = new[]
        {
            BacklogItemFor("Dashboard polish", body: "No matching detail"),
            BacklogItemFor("Worker output", body: "Needs Foo evidence"),
            BacklogItemFor("Other", body: "No match")
        };

        var filtered = CliCommandHandlers.ApplyBacklogListFilters(items, status: null, text: "foo", limit: null);

        var item = Xunit.Assert.Single(filtered);
        Xunit.Assert.Equal("Worker output", item.Title);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_list_combines_limit_status_and_text_filters")]
    public void CliBacklogListCombinesLimitStatusAndTextFilters()
    {
        var items = new[]
        {
            BacklogItemFor("First Foo", status: BacklogItemStatus.Open),
            BacklogItemFor("Done Foo", status: BacklogItemStatus.Done),
            BacklogItemFor("Second Foo", status: BacklogItemStatus.Open),
            BacklogItemFor("No match", status: BacklogItemStatus.Open)
        };

        var filtered = CliCommandHandlers.ApplyBacklogListFilters(items, status: "open", text: "foo", limit: 1);

        var item = Xunit.Assert.Single(filtered);
        Xunit.Assert.Equal("First Foo", item.Title);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_list_renders_supersede_duplicate_and_related_annotations")]
    public void CliBacklogListRendersSupersedeDuplicateAndRelatedAnnotations()
    {
        var now = DateTimeOffset.UtcNow;
        var superseded = new BacklogItem("old-item", "Old", "", BacklogItemStatus.Superseded, now, now, null, SupersededBy: "replacement-item");
        var duplicate = new BacklogItem("duplicate-item", "Dup", "", BacklogItemStatus.Open, now, now, null)
        {
            Links = [new BacklogLink("canonical-item", "duplicate-item", BacklogLinkKind.Duplicate, "canonical-item", now)]
        };
        var related = new BacklogItem("related-one", "Related", "", BacklogItemStatus.Open, now, now, null)
        {
            Links = [new BacklogLink("related-one", "related-two", BacklogLinkKind.Related, null, now)]
        };

        Xunit.Assert.Equal("[Superseded→replacem]", CliCommandHandlers.RenderBacklogListTag(superseded));
        Xunit.Assert.Equal("[Dup→canonica]", CliCommandHandlers.RenderBacklogListTag(duplicate));
        Xunit.Assert.Equal(" [Related→related-]", CliCommandHandlers.RenderBacklogListSuffix(related));
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_triage_filters_stale_open_items")]
    public void CliBacklogTriageFiltersStaleOpenItems()
    {
        var now = DateTimeOffset.Parse("2026-07-03T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var items = new[]
        {
            BacklogItemFor("Very stale open", updatedAt: now.AddDays(-45)),
            BacklogItemFor("Fresh open", updatedAt: now.AddDays(-2)),
            BacklogItemFor("Closed stale", status: BacklogItemStatus.Done, updatedAt: now.AddDays(-60))
        };

        var output = CliCommandHandlers.RenderBacklogTriage(items, [], limit: 5, staleDays: 30, now);

        Xunit.Assert.Contains("Stale open (>30d):", output);
        Xunit.Assert.Contains("Very stale open", output);
        Xunit.Assert.DoesNotContain("Fresh open", output);
        Xunit.Assert.DoesNotContain("Closed stale", output);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_triage_links_active_goals")]
    public void CliBacklogTriageLinksActiveGoals()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var activeItem = store.AddAsync("Active linked backlog").GetAwaiter().GetResult();
        var closedGoalItem = store.AddAsync("Terminal linked backlog").GetAwaiter().GetResult();
        var kernel = new AgentOrchestratorKernel();
        var activeGoal = kernel.CreateGoal("Active linked objective");
        var completedGoal = kernel.CreateGoal("Completed linked objective");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        kernel.ActivateGoal(activeGoal.Id, agents);
        kernel.ActivateGoal(completedGoal.Id, agents);
        kernel.ReportTaskProgress(completedGoal.Id, completedGoal.Tasks[0].Id, WorkTaskStatus.Completed, "Done");
        kernel.SetGoalSourceBacklogItemId(activeGoal.Id, activeItem.Id);
        kernel.SetGoalSourceBacklogItemId(completedGoal.Id, closedGoalItem.Id);

        var output = ExecuteCliAndCapture(["backlog-triage", "--limit", "5"], kernel, workspace);

        Xunit.Assert.Contains("Active-linked open:", output);
        Xunit.Assert.Contains($"goal={activeGoal.Id.Value[..8]}:Active Active linked backlog", output);
        Xunit.Assert.DoesNotContain($"goal={completedGoal.Id.Value[..8]}:Completed", output);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_intake_prints_goal_slice_without_mutating_state")]
    public void CliBacklogIntakePrintsGoalSliceWithoutMutatingState()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, """
        # Backlog

        ## Decision record (durable context, not work items)

        Not work.

        ## Add dashboard operator inbox

        Add dashboard subscription-backed operator inbox for failed preflights and acceptance gates. Done when dashboard evidence is visible.
        """);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        bool changed = false;
        var output = CaptureConsole(() =>
        {
            changed = CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", "dashboard operator inbox"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });
        Xunit.Assert.False(changed);
        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.Contains("Backlog intake: 1 item", output);
        Xunit.Assert.Contains("## Add dashboard operator inbox", output);
        Xunit.Assert.Contains("Roles:", output);
        Xunit.Assert.Contains("Risks: subscription-cost, operator-ux", output);
        Xunit.Assert.Contains("Target files/scopes:", output);
        Xunit.Assert.Contains("Scope confidence: unknown", output);
        Xunit.Assert.Contains("Includes:", output);
        Xunit.Assert.DoesNotContain("src/Mcg.AgentOrchestrator.App/Dashboard", output);
        Xunit.Assert.Contains("Create commands:", output);
        Xunit.Assert.Contains("--create-goal", output);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_intake_create_simple_goal_requires_explicit_flag")]
    public void CliBacklogIntakeCreateSimpleGoalRequiresExplicitFlag()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, """
        # Backlog

        ## Add deterministic build/test broker

        Add deterministic build and test broker for CS2012 and isolated artifacts. Done when focused tests prove isolation.
        """);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        bool changed = false;
        var output = CaptureConsole(() =>
        {
            changed = CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", "build/test broker", "--create-simple-goal", "--backlog-coverage", "full"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });

        Xunit.Assert.True(changed);
        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.Single(currentGoal!.Tasks);
        Xunit.Assert.Contains("Backlog slice: Add deterministic build/test broker", currentGoal.Objective);
        Xunit.Assert.Contains("Scope confidence: unknown", currentGoal.Objective);
        Xunit.Assert.Contains("Includes:", currentGoal.Objective);
        Xunit.Assert.DoesNotContain("scripts/Invoke-IsolatedDotnet.ps1", currentGoal.Objective);
        Xunit.Assert.Contains("Created simple goal from backlog slice.", output);
    }

    [Xunit.Fact]
    public void CliBacklogIntakeReportsAutomaticScoutPipeline()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, """
        # Backlog

        ## Update CLI help label

        Update one label in src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs. Done when a focused assertion passes.
        """);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() =>
            CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", "Update CLI help label", "--create-goal", "--backlog-coverage", "full"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Equal(
            [AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            currentGoal!.Tasks.Select(task => task.RequiredRole));
        Xunit.Assert.Contains("Created goal from backlog slice.", output, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Created five-role", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"workflow\":\"scout\"", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"selectionSource\":\"automatic\"", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"reasons\":[", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("require an explicit --pipeline value", output, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("five-role")]
    [Xunit.InlineData("scout")]
    public void CliBacklogIntakeForcedPipelineUsesExactPersistedOrder(string pipeline)
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, """
        # Backlog

        ## Update forced CLI help label

        Update one label in src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs. Done when a focused assertion passes.
        """);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() =>
            CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", "Update forced CLI help label", "--create-goal", "--pipeline", pipeline, "--backlog-coverage", "full"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Equal(
            pipeline == "scout"
                ? (AgentRole[])[AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer]
                : (AgentRole[])[AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            currentGoal!.Tasks.Select(task => task.RequiredRole));
        Xunit.Assert.Contains($"\"workflow\":\"{pipeline}\"", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"selectionSource\":\"explicitly-required\"", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CliBacklogIntakeUnsatisfiedForcedBatchFailsBeforeAnyReservation()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, """
        # Backlog

        ## Update first forced label

        Update one label in src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs. Done when a focused assertion passes.

        ## Update second forced label

        Update one label in src/Mcg.AgentOrchestrator.App/Program.cs. Done when a focused assertion passes.
        """);
        var workspace = CreateRefinedWorkspace(root);
        var intake = BacklogIntakePlanner.Build(workspace.BacklogStorePath, headingFilter: null, maxItems: 5);
        var items = intake.Items.OrderBy(item => item.Heading, StringComparer.Ordinal).ToArray();
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents
            .Where(agent => agent.Role != AgentRole.Reviewer)
            .ToArray();
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() =>
            CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", items[0].Heading, items[1].Heading, "--create-goal", "--pipeline", "five-role", "--backlog-coverage", "full"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Contains("missing available agent role(s): Reviewer", exception.Message, StringComparison.Ordinal);
        var intakeRecords = new BacklogIntakeRecordStore(workspace.SqliteStatePath);
        Xunit.Assert.All(items, item => Xunit.Assert.Null(intakeRecords.Get(item.Id)));
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        Xunit.Assert.All(items, item =>
            Xunit.Assert.Equal(
                BacklogItemStatus.Open,
                backlog.GetByExactIdAsync(item.Id).GetAwaiter().GetResult()!.Status));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void CliBacklogIntakeForcedFiveRoleRejectsMismatchedReuse(bool reuseByIntakeRecord)
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, """
        # Backlog

        ## Update security token reuse guard

        Update security token rollback behavior in src/Mcg.AgentOrchestrator.App/AuthPolicy.cs with focused tests.
        """);
        var workspace = CreateRefinedWorkspace(root);
        var intake = BacklogIntakePlanner.Build(workspace.BacklogStorePath, "Update security token reuse guard", maxItems: 1);
        var item = Xunit.Assert.Single(intake.Items);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var existingGoal = GoalLifecycleCommands.CreateAndActivateGoal(kernel, agents, item.SuggestedObjective);
        Xunit.Assert.Equal(
            [AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            existingGoal.Tasks.Select(task => task.RequiredRole));
        if (reuseByIntakeRecord)
        {
            var records = new BacklogIntakeRecordStore(workspace.SqliteStatePath);
            _ = records.Reserve(item.Id, item.Heading);
            records.MarkGoalCreated(item.Id, existingGoal.Id.Value);
        }
        else
        {
            kernel.SetGoalSourceBacklogItemId(existingGoal.Id, item.Id);
        }

        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var exception = Xunit.Assert.Throws<InvalidOperationException>(() =>
            CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", item.Heading, "--create-goal", "--pipeline", "five-role", "--backlog-coverage", "full"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Contains("does not match the explicitly requested intake pipeline", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Requested workflow='five-role'", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("selectionSource='explicitly-required'", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("persisted workflow='scout'", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("selectionSource='automatic'", exception.Message, StringComparison.Ordinal);
        Xunit.Assert.Contains("Existing goal was not reused", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CliBacklogIntakeForcedFiveRoleRendersMatchingReuseDecision()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, """
        # Backlog

        ## Update matching reuse label

        Update one label in src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs. Done when a focused assertion passes.
        """);
        var workspace = CreateRefinedWorkspace(root);
        var intake = BacklogIntakePlanner.Build(workspace.BacklogStorePath, "Update matching reuse label", maxItems: 1);
        var item = Xunit.Assert.Single(intake.Items);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var forcedPlan = GoalObjectivePlanner.Build(item.SuggestedObjective, GoalIntakePipeline.FiveRole);
        var existingGoal = GoalLifecycleCommands.CreateAndActivateGoal(kernel, agents, forcedPlan);
        kernel.SetGoalSourceBacklogItemId(existingGoal.Id, item.Id);
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var changed = true;

        var output = CaptureConsole(() =>
            changed = CliCommandDispatcher.ExecuteCommand(
                ["backlog-intake", item.Heading, "--create-goal", "--pipeline", "five-role", "--backlog-coverage", "full"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.False(changed);
        Xunit.Assert.Equal(existingGoal.Id, currentGoal!.Id);
        Xunit.Assert.Contains($"already has goal {existingGoal.Id.Value[..8]}", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"workflow\":\"five-role\"", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"selectionSource\":\"explicitly-required\"", output, StringComparison.Ordinal);
    }


    [Xunit.Fact(DisplayName = "Cli_goal_plan_prints_dependency_graph_without_mutating_state")]
    public void CliGoalPlanPrintsDependencyGraphWithoutMutatingState()
    {
        var root = CreateTempDirectory();
        WritePlanningBacklog(root);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        bool changed = false;
        var output = CaptureConsole(() =>
        {
            changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-plan"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });
        Xunit.Assert.False(changed);
        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.Contains("Goal plan: 3 node(s)", output);
        Xunit.Assert.Contains("Compiled graph:", output);
        Xunit.Assert.Contains("Rollback boundary:", output);
        Xunit.Assert.Contains("Capabilities:", output);
        Xunit.Assert.Contains("Verification contracts:", output);
        Xunit.Assert.Contains("0 dependency edge(s)", output);
        Xunit.Assert.Contains("Parallel batches:", output);
        Xunit.Assert.Contains("Create commands:", output);
        Xunit.Assert.Contains("heading=\"Add feature A planner\"", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("heading=\"Add feature B planner\"", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("heading=\"Add feature C planner\"", output, StringComparison.Ordinal);
        Xunit.Assert.Equal(
            3,
            output.Split("\"intakeItemId\":", StringSplitOptions.None).Length - 1);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_plan_excludes_the_goal_created_from_the_same_backlog_item")]
    public void CliGoalPlanExcludesTheGoalCreatedFromTheSameBacklogItem()
    {
        var root = CreateTempDirectory();
        WritePlanningBacklog(root);
        var workspace = CreateRefinedWorkspace(root);
        var intake = BacklogIntakePlanner.Build(workspace.BacklogStorePath, "Add feature A planner", maxItems: 1);
        var item = Xunit.Assert.Single(intake.Items);
        var kernel = new AgentOrchestratorKernel();
        var linkedGoal = kernel.CreateGoal(
            item.SuggestedObjective,
            [new TaskSpec(TaskId.New(), "Implement planned work.", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(linkedGoal.Id, item.Id);
        var differentlyLinkedGoal = kernel.CreateGoal(
            item.SuggestedObjective,
            [new TaskSpec(TaskId.New(), "Implement other planned work.", AgentRole.Developer)]);
        kernel.SetGoalSourceBacklogItemId(differentlyLinkedGoal.Id, "different-backlog-item");
        var unlinkedGoal = kernel.CreateGoal(
            item.SuggestedObjective,
            [new TaskSpec(TaskId.New(), "Implement unlinked planned work.", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() =>
            CliCommandDispatcher.ExecuteCommand(
                ["goal-plan", "Add feature A planner"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.Contains($"item={item.Id}", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("comparedGoals=2", output, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain(linkedGoal.Id.Value[..8], output, StringComparison.Ordinal);
        Xunit.Assert.Contains(differentlyLinkedGoal.Id.Value[..8], output, StringComparison.Ordinal);
        Xunit.Assert.Contains(unlinkedGoal.Id.Value[..8], output, StringComparison.Ordinal);
        Xunit.Assert.Equal(3, kernel.Goals.Count);
    }


    [Xunit.Fact(DisplayName = "GoalObjectivePlanner_classifies_low_medium_and_high_risk_objectives")]
    public void GoalObjectivePlannerClassifiesLowMediumAndHighRiskObjectives()
    {
        var docs = GoalObjectivePlanner.Build("Inspect docs/usage.md and summarize current behavior", simple: false);
        var code = GoalObjectivePlanner.Build("Implement src/Mcg.AgentOrchestrator.App/GoalObjectivePlanner.cs with tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CliCommandTests.cs coverage", simple: true);
        var high = GoalObjectivePlanner.Build("Update auth token rollback policy across src/Mcg.AgentOrchestrator.App/AuthPolicy.cs tests/Mcg.AgentOrchestrator.Infrastructure.Tests/AuthPolicyTests.cs scripts/RepairAuth.ps1 config/auth.json", simple: false);

        Xunit.Assert.True(docs.CanCreateGoal);
        Xunit.Assert.Contains("docs/usage.md", docs.FileScopes);
        Xunit.Assert.Contains(docs.RequiredVerification, item => item.Contains("documentation diff", StringComparison.Ordinal));
        Xunit.Assert.True(code.CanCreateGoal);
        Xunit.Assert.Contains(code.RequiredTools, item => item.Contains("Invoke-TestSummary", StringComparison.Ordinal));
        Xunit.Assert.Single(code.TaskBoundaries);
        Xunit.Assert.Equal(AgentRole.Developer, code.TaskBoundaries[0].Role);
        Xunit.Assert.True(high.CanCreateGoal);
        Xunit.Assert.Contains("high-risk", high.RiskLabels);
        Xunit.Assert.Contains("multi-scope", high.RiskLabels);
        Xunit.Assert.Contains("security-risk", high.RiskLabels);
        Xunit.Assert.Equal(GoalIntakePipeline.Scout, docs.PipelineDecision.Pipeline);
        Xunit.Assert.False(docs.PipelineDecision.IsOverride);
        Xunit.Assert.Equal(GoalIntakePipeline.DeveloperOnly, code.PipelineDecision.Pipeline);
        Xunit.Assert.True(code.PipelineDecision.IsOverride);
        Xunit.Assert.Equal(GoalIntakePipeline.Scout, high.PipelineDecision.Pipeline);
        Xunit.Assert.False(high.PipelineDecision.IsOverride);
        Xunit.Assert.Equal(
            [AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            high.TaskBoundaries.Select(boundary => boundary.Role));
        Xunit.Assert.Contains(high.PipelineDecision.Reasons, reason => reason.Contains("security-risk", StringComparison.Ordinal));
    }


    [Xunit.Fact(DisplayName = "GoalObjectivePlanner_routes_open_ended_objectives_to_scout_pipeline")]
    public void GoalObjectivePlannerRoutesOpenEndedObjectivesToScoutPipeline()
    {
        var plan = GoalObjectivePlanner.Build("Build a dashboard supervision workflow for unattended goals", simple: false);

        Xunit.Assert.Contains("scope-implicit", plan.RiskLabels);
        Xunit.Assert.Equal(GoalIntakePipeline.Scout, plan.PipelineDecision.Pipeline);
        Xunit.Assert.Equal([AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer], plan.TaskBoundaries.Select(boundary => boundary.Role));
    }


    [Xunit.Fact(DisplayName = "Cli_intent_template_prints_feature_objective_without_mutating_state")]
    public void CliIntentTemplatePrintsFeatureObjectiveWithoutMutatingState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        bool changed = false;
        var output = CaptureConsole(() =>
        {
            changed = CliCommandDispatcher.ExecuteCommand(
                ["intent-template", "feature", "Add dashboard action recommendations"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });
        Xunit.Assert.False(changed);
        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.Contains("Intent template: feature", output);
        Xunit.Assert.Contains("Request: Add dashboard action recommendations", output);
        Xunit.Assert.Contains("Decomposition rules:", output);
        Xunit.Assert.Contains("Required evidence:", output);
        Xunit.Assert.Contains("Verification policy:", output);
        Xunit.Assert.Contains("Deterministic workflows:", output);
        Xunit.Assert.Contains("--create-goal", output);
    }


    [Xunit.Fact(DisplayName = "Cli_intent_template_creates_simple_goal_from_test_hardening_template")]
    public void CliIntentTemplateCreatesSimpleGoalFromTestHardeningTemplate()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        bool changed = false;
        var output = CaptureConsole(() =>
        {
            changed = CliCommandDispatcher.ExecuteCommand(
                ["intent-template", "test-hardening", "Expose failure triage in the goal page", "--create-simple-goal"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });

        Xunit.Assert.True(changed);
        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.Single(currentGoal!.Tasks);
        Xunit.Assert.Contains("Intent template: test-hardening", currentGoal.Objective);
        Xunit.Assert.Contains("Request: Expose failure triage in the goal page", currentGoal.Objective);
        Xunit.Assert.Contains("Prefer deterministic fixtures over sleeps or broad process cleanup.", currentGoal.Objective);
        Xunit.Assert.Contains("Run the focused test repeatedly when fixing flake.", currentGoal.Objective);
        Xunit.Assert.Contains("Created simple goal from intent template.", output);
    }


    [Xunit.Fact(DisplayName = "GoalDependencyPlanner_compiles_stable_graph_and_batches_independent_scopes")]
    public void GoalDependencyPlannerCompilesStableGraphAndBatchesIndependentScopes()
    {
        var root = CreateTempDirectory();
        WriteCompilationBacklog(root);

        var first = GoalDependencyPlanner.Build(BacklogIntakePlanner.Build(BacklogStorePathFor(root), maxItems: 2));
        var second = GoalDependencyPlanner.Build(BacklogIntakePlanner.Build(BacklogStorePathFor(root), maxItems: 2));

        Xunit.Assert.True(first.CompiledGraph.IsRunnable);
        Xunit.Assert.Equal(first.CompiledGraph.GraphId, second.CompiledGraph.GraphId);
        Xunit.Assert.Equal(2, first.CompiledGraph.Nodes.Count);
        Xunit.Assert.All(first.CompiledGraph.Nodes, node => Xunit.Assert.True(node.CanCreateGoal));
        Xunit.Assert.Contains(first.CompiledGraph.Nodes[0].RequiredCapabilities, item => item == "workspace-write");
        Xunit.Assert.Contains(first.CompiledGraph.Nodes[0].VerificationContracts, item => item.Contains("Focused unit tests", StringComparison.Ordinal));
        Xunit.Assert.Equal(2, first.ParallelPlan.Batches.Count);
        Xunit.Assert.Equal(["g1"], first.ParallelPlan.Batches[0].IntentIds);
        Xunit.Assert.Equal(["g2"], first.ParallelPlan.Batches[1].IntentIds);
    }


    [Xunit.Fact(DisplayName = "GoalDependencyPlanner_serializes_conflicting_compiled_file_scopes")]
    public void GoalDependencyPlannerSerializesConflictingCompiledFileScopes()
    {
        var root = CreateTempDirectory();
        WriteConflictingCompilationBacklog(root);

        var plan = GoalDependencyPlanner.Build(BacklogIntakePlanner.Build(BacklogStorePathFor(root), maxItems: 2));

        Xunit.Assert.True(plan.CompiledGraph.IsRunnable);
        Xunit.Assert.Equal(2, plan.ParallelPlan.Batches.Count);
        Xunit.Assert.Equal(["g1"], plan.ParallelPlan.Batches[0].IntentIds);
        Xunit.Assert.Equal(["g2"], plan.ParallelPlan.Batches[1].IntentIds);
        var second = plan.CompiledGraph.Nodes.Single(node => node.Id == "g2");
        Xunit.Assert.Equal(2, second.ParallelBatch);
        Xunit.Assert.Equal(ParallelExecutionDisposition.Serialized, second.ParallelDisposition);
    }



    [Xunit.Fact(DisplayName = "Cli_goal_plan_create_simple_goals_embeds_explicit_dependencies")]
    public void CliGoalPlanCreateSimpleGoalsEmbedsExplicitDependencies()
    {
        var root = CreateTempDirectory();
        WritePlanningBacklog(root);
        var workspace = CreateRefinedWorkspace(root);
        var intake = BacklogIntakePlanner.Build(workspace.BacklogStorePath, headingFilter: null, maxItems: 10);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        bool changed = false;
        var output = CaptureConsole(() =>
        {
            changed = CliCommandDispatcher.ExecuteCommand(
                ["goal-plan", "--create-simple-goals", "--backlog-coverage", "full"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
        });

        Xunit.Assert.True(changed);
        Xunit.Assert.Equal(3, kernel.Goals.Count);
        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.DoesNotContain(kernel.Goals, goal => goal.Objective.Contains("Explicit dependencies:", StringComparison.Ordinal));
        Xunit.Assert.All(kernel.Goals, goal => Xunit.Assert.Single(goal.Tasks));
        Xunit.Assert.Equal(
            intake.Items.Select(item => item.Id).Order(StringComparer.Ordinal),
            kernel.Goals.Select(goal => goal.SourceBacklogItemId).Order(StringComparer.Ordinal));
        Xunit.Assert.Contains("Created simple goal", output);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_intake_splits_heading_before_create_flags")]
    public void CliBacklogIntakeSplitsHeadingBeforeCreateFlags()
    {
        var parts = CliArgumentParser.SplitCommand(
            "backlog-intake Add dashboard operator inbox --create-goal --backlog-coverage full");

        Xunit.Assert.Equal(
            ["backlog-intake", "Add dashboard operator inbox", "--create-goal", "--backlog-coverage", "full"],
            parts);
    }


    [Xunit.Fact(DisplayName = "Cli_goal_plan_splits_filter_before_create_flags")]
    public void CliGoalPlanSplitsFilterBeforeCreateFlags()
    {
        var parts = CliArgumentParser.SplitCommand(
            "goal-plan unattended supervisor --create-goals --backlog-coverage full");

        Xunit.Assert.Equal(
            ["goal-plan", "unattended supervisor", "--create-goals", "--backlog-coverage", "full"],
            parts);
    }


    [Xunit.Fact(DisplayName = "Cli_intent_template_splits_request_before_create_flags")]
    public void CliIntentTemplateSplitsRequestBeforeCreateFlags()
    {
        var parts = CliArgumentParser.SplitCommand(
            "intent-template feature add dashboard recommendations --create-goal");

        Xunit.Assert.Equal(
            ["intent-template", "feature add dashboard recommendations", "--create-goal"],
            parts);
    }


    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_skips_kernel_state_for_operator_and_backlog_store_only_commands")]
    public void RunnerSkipsKernelStateForOperatorAndBacklogStoreOnlyCommands()
    {
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["operator-listen"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["OPERATOR-LISTEN"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["operator-channel"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["operator-channel", "test", "--spine"]));

        // Backlog commands that inspect neither dependencies nor similarity skip state and stay concurrent.
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["backlog-add", "title"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["BACKLOG-ADD", "title"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["backlog-add", "title", "--no-similar"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["backlog-add", "title", "--depends-on", "abc"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["backlog-list"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["backlog-similar", "query"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["backlog-update", "abc", "--title", "new"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["backlog-annotate", "abc", "receipt"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["backlog-close", "abc"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["backlog-supersede", "abc", "def"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["backlog-unsupersede", "abc"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["backlog-link", "abc", "def"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["conduct", "--help"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["conduct", "--loop", "--help"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["workspace", "--help"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["workspace", "create", "-h"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["gate-status"]));

        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["goals"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["backlog-show", "abc"]));
        Xunit.Assert.True(CliPersistentStateRunner.RequiresKernelBacklogState(["backlog-list"]));
        Xunit.Assert.True(CliPersistentStateRunner.RequiresKernelBacklogState(["backlog-similar", "query"]));
        Xunit.Assert.True(CliPersistentStateRunner.RequiresKernelBacklogState(["backlog-show", "abc"]));
        Xunit.Assert.True(CliPersistentStateRunner.RequiresKernelBacklogState(["backlog-depends", "abc", "--on", "def"]));
        Xunit.Assert.True(CliPersistentStateRunner.RequiresKernelBacklogState(["backlog-add", "title", "--depends-on", "abc"]));
        Xunit.Assert.True(CliPersistentStateRunner.RequiresKernelBacklogState(["backlog-add", "title"]));
        Xunit.Assert.False(CliPersistentStateRunner.RequiresKernelBacklogState(["backlog-add", "title", "--no-similar"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["run", "1"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["conduct", "abc123"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["acceptance"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["serve-dashboard"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState([]));
        Xunit.Assert.Equal(
            CliCommandCapability.QueryOnly,
            CliCommandCapabilities.Classify(["backlog-similar", "query"]));
    }

    [Xunit.Fact(DisplayName = "Cli_gate_status_lists_stable_slot_heartbeats")]
    public void CliGateStatusListsStableSlotHeartbeats()
    {
        var root = CreateTempDirectory();
        var storageRoot = new DotnetBuildStorageRoot(Path.Combine(root, ".orchestrator", "test-dotnet"));
        try
        {
            GateHeartbeatArtifacts.Write(
                GateHeartbeatArtifacts.GetStableSlotPath(1, storageRoot),
                new GateHeartbeatSnapshot(
                    "abcdef12abcdef12abcdef12abcdef12",
                    "verification-check",
                    "WorkerDispatchTests",
                    1,
                    111,
                    222,
                    "running",
                    DateTimeOffset.UtcNow.AddSeconds(-20),
                    DateTimeOffset.UtcNow.AddSeconds(-1),
                    DateTimeOffset.UtcNow.AddSeconds(-10),
                    12,
                    3,
                    15,
                    "dotnet test"));
            _ = CreateRefinedWorkspace(root);
            var result = RunAppCommand(root, "gate-status");
            Xunit.Assert.True(result.ExitCode == 0, result.Stderr);
            var output = result.Stdout;

            Xunit.Assert.Contains("Gate status:", output);
            Xunit.Assert.Contains("slot-1: goal=abcdef12 phase=verification-check state=running", output);
            Xunit.Assert.Contains("target=WorkerDispatchTests", output);
            Xunit.Assert.Contains("child_pid=222", output);
            Xunit.Assert.Contains("output_bytes=15", output);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_add_body_file_creates_item_with_file_content")]
    public async Task CliBacklogAddBodyFileCreatesItemWithFileContent()
    {
        var root = CreateTempDirectory();
        var bodyContent = "Add file-backed backlog body support.\n\nMulti-line item body that would overflow an inline CLI argument.";
        var bodyPath = Path.Combine(root, "body.md");
        File.WriteAllText(bodyPath, bodyContent, System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-add", "File-backed item", "--body-file", bodyPath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = Xunit.Assert.Single(await store.ListAsync(includeAll: true));
        Xunit.Assert.Equal("File-backed item", item.Title);
        Xunit.Assert.Equal(bodyContent, item.Body);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_dependency_commands_add_show_remove_and_clear")]
    public async Task CliBacklogDependencyCommandsAddShowRemoveAndClear()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var prerequisite = await store.AddAsync("Backlog prerequisite");
        var kernel = new AgentOrchestratorKernel();
        var goalPrerequisite = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Goal prerequisite");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            [
                "backlog-add", "Dependent item",
                "--depends-on", prerequisite.Id[..8],
                "--depends-on", goalPrerequisite.Id.Value[..8]
            ],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        var dependent = (await store.ListAsync(includeAll: true))
            .Single(item => item.Title == "Dependent item");
        Xunit.Assert.Equal(
            [prerequisite.Id, goalPrerequisite.Id.Value],
            dependent.Dependencies.Select(edge => edge.PrerequisiteId));

        var shown = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-show", dependent.Id[..8]],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));
        Xunit.Assert.Contains("Dependencies:", shown);
        Xunit.Assert.Contains(prerequisite.Id, shown);
        Xunit.Assert.Contains(goalPrerequisite.Id.Value, shown);

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-depends", dependent.Id[..8], "--remove", prerequisite.Id[..8]],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));
        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-depends", dependent.Id[..8], "--clear"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));
        Xunit.Assert.Empty((await store.GetByExactIdAsync(dependent.Id))!.Dependencies);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_add_text_file_alias_creates_item_with_file_content")]
    public async Task CliBacklogAddTextFileAliasCreatesItemWithFileContent()
    {
        var root = CreateTempDirectory();
        var bodyContent = "Add file-backed backlog body support via --text-file.\n\nMulti-line item body.";
        var bodyPath = Path.Combine(root, "body.md");
        File.WriteAllText(bodyPath, bodyContent, System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-add", "File-backed item", "--text-file", bodyPath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = Xunit.Assert.Single(await store.ListAsync(includeAll: true));
        Xunit.Assert.Equal("File-backed item", item.Title);
        Xunit.Assert.Equal(bodyContent, item.Body);
    }

    [Xunit.Fact]
    public async Task CliBacklogAddFlaggedAndPositionalTitlesPersistEquivalentRecords()
    {
        var root = CreateTempDirectory();
        var bodyContent = "Equivalent backlog body.\n\nPreserve all text.";
        var bodyPath = Path.Combine(root, "body.md");
        File.WriteAllText(bodyPath, bodyContent, System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();

        ExecuteCliAndCapture(
            ["backlog-add", "Equivalent title", "Equivalent backlog body.\n\nPreserve all text."],
            kernel,
            workspace);
        ExecuteCliAndCapture(
            CliArgumentParser.NormalizeArgs(
                ["backlog-add", "--title", "Equivalent", "title", "--text-file", bodyPath]),
            kernel,
            workspace);

        var items = await new BacklogStore(workspace.BacklogStorePath).ListAsync(includeAll: true);
        Xunit.Assert.Equal(2, items.Count);
        Xunit.Assert.All(items, item => Xunit.Assert.Equal("Equivalent title", item.Title));
        Xunit.Assert.All(items, item => Xunit.Assert.Equal(bodyContent, item.Body));
    }

    [Xunit.Fact]
    public async Task CliBacklogAddRejectsInvalidTitleShapesWithoutPersistence()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<IReadOnlyList<string>> invalidCommands =
        [
            ["backlog-add"],
            ["backlog-add", "--title", ""],
            ["backlog-add", "--title", "First", "--title", "Second"],
            ["backlog-add", "Positional title", "--title", "Flagged title"]
        ];

        foreach (var command in invalidCommands)
        {
            _ = Xunit.Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(command, kernel, workspace));
        }

        Xunit.Assert.Empty(await new BacklogStore(workspace.BacklogStorePath).ListAsync(includeAll: true));
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_add_text_file_rejects_inline_body")]
    public async Task CliBacklogAddTextFileRejectsInlineBody()
    {
        var root = CreateTempDirectory();
        var bodyPath = Path.Combine(root, "body.md");
        File.WriteAllText(bodyPath, "File body.", System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.Throws<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"backlog-add File-backed-item Inline body --text-file {bodyPath}"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("either inline text or --text-file", ex.Message);
        var store = new BacklogStore(workspace.BacklogStorePath);
        Xunit.Assert.Empty(await store.ListAsync(includeAll: true));
    }


    [Xunit.Fact(DisplayName = "Cli_status_prints_source_backlog_title_as_goal_label")]
    public async Task CliStatusPrintsSourceBacklogTitleAsGoalLabel()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Friendly backlog title");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Objective preview remains");
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["status", goal.Id.Value[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains($"Goal {goal.Id.Value} (Friendly backlog title)", output);
        Xunit.Assert.Contains("Objective: Objective preview remains", output);
        Xunit.Assert.Contains("Status: Draft", output);
        Xunit.Assert.Contains("Tasks:", output);
    }


    [Xunit.Fact(DisplayName = "Cli_status_omits_goal_label_without_source_backlog_title")]
    public void CliStatusOmitsGoalLabelWithoutSourceBacklogTitle()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("No linked backlog");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["status", goal.Id.Value[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains($"Goal {goal.Id.Value}{Environment.NewLine}", output);
        Xunit.Assert.DoesNotContain("()", output);
        Xunit.Assert.Contains("Objective: No linked backlog", output);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_add_body_file_missing_gives_clear_error")]
    public async Task CliBacklogAddBodyFileMissingGivesClearError()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var missingPath = Path.Combine(root, "does-not-exist.md");

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-add", "File-backed item", "--body-file", missingPath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("--body-file not found", ex.Message);
        Xunit.Assert.Contains(missingPath, ex.Message);
        var store = new BacklogStore(workspace.BacklogStorePath);
        Xunit.Assert.Empty(await store.ListAsync(includeAll: true));
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_close_text_file_rejects_inline_reason")]
    public async Task CliBacklogCloseTextFileRejectsInlineReason()
    {
        var root = CreateTempDirectory();
        var reasonPath = Path.Combine(root, "reason.md");
        File.WriteAllText(reasonPath, "File reason.", System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Close duplicate reason");
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var ex = Xunit.Assert.Throws<ArgumentException>(() => CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"backlog-close {item.Id[..8]} Inline reason --text-file {reasonPath}"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("either inline text or --text-file", ex.Message);
        var open = await store.GetByExactIdAsync(item.Id);
        Xunit.Assert.NotNull(open);
        Xunit.Assert.Equal(BacklogItemStatus.Open, open!.Status);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_close_text_file_missing_gives_clear_error")]
    public async Task CliBacklogCloseTextFileMissingGivesClearError()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Close missing reason");
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var missingPath = Path.Combine(root, "does-not-exist.md");

        var ex = Xunit.Assert.ThrowsAny<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-close", item.Id[..8], "--text-file", missingPath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("--text-file not found", ex.Message);
        Xunit.Assert.Contains(missingPath, ex.Message);
        var open = await store.GetByExactIdAsync(item.Id);
        Xunit.Assert.NotNull(open);
        Xunit.Assert.Equal(BacklogItemStatus.Open, open!.Status);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_close_reason_file_closes_item_with_file_content")]
    public async Task CliBacklogCloseReasonFileClosesItemWithFileContent()
    {
        var root = CreateTempDirectory();
        var reasonContent = "Resolved by the file-backed backlog close path.\n\nIncludes detail that would overflow inline command text.";
        var reasonPath = Path.Combine(root, "reason.md");
        File.WriteAllText(reasonPath, reasonContent, System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Close from file", "Original body");
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-close", item.Id[..8], "--reason-file", reasonPath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var closed = await store.GetByExactIdAsync(item.Id);
        Xunit.Assert.NotNull(closed);
        Xunit.Assert.Equal(BacklogItemStatus.Done, closed!.Status);
        Xunit.Assert.Contains("Original body", closed.Body);
        Xunit.Assert.Contains(reasonContent, closed.Body);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_close_text_file_alias_closes_item_with_file_content")]
    public async Task CliBacklogCloseTextFileAliasClosesItemWithFileContent()
    {
        var root = CreateTempDirectory();
        var reasonContent = "Resolved by the uniform --text-file backlog close path.\n\nIncludes the long operator receipt.";
        var reasonPath = Path.Combine(root, "reason.md");
        File.WriteAllText(reasonPath, reasonContent, System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Close from text file", "Original body");
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-close", item.Id[..8], "--text-file", reasonPath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var closed = await store.GetByExactIdAsync(item.Id);
        Xunit.Assert.NotNull(closed);
        Xunit.Assert.Equal(BacklogItemStatus.Done, closed!.Status);
        Xunit.Assert.Contains("Original body", closed.Body);
        Xunit.Assert.Contains(reasonContent, closed.Body);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_annotate_open_item_show_renders_ordered_notes")]
    public async Task CliBacklogAnnotateOpenItemShowRendersOrderedNotes()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Annotate open item", "Original body");
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"backlog-annotate {item.Id[..8]} First discovered receipt"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            CliArgumentParser.SplitCommand($"backlog-annotate {item.Id[..8]} Second discovered receipt"),
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-show", item.Id[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("Notes:", output);
        Xunit.Assert.Contains("First discovered receipt", output);
        Xunit.Assert.Contains("Second discovered receipt", output);
        Xunit.Assert.True(
            output.IndexOf("First discovered receipt", StringComparison.Ordinal) <
            output.IndexOf("Second discovered receipt", StringComparison.Ordinal));
        var annotated = await store.GetByExactIdAsync(item.Id);
        Xunit.Assert.Equal("Original body", annotated!.Body);
        Xunit.Assert.Equal(BacklogItemStatus.Open, annotated.Status);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_annotate_text_file_preserves_multiline_note")]
    public async Task CliBacklogAnnotateTextFilePreservesMultilineNote()
    {
        var root = CreateTempDirectory();
        var noteContent = "Root cause receipt line 1\n\nRoot cause receipt line 3";
        var notePath = Path.Combine(root, "note.md");
        File.WriteAllText(notePath, noteContent, System.Text.Encoding.UTF8);
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Annotate from file");
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-annotate", item.Id[..8], "--text-file", notePath],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var show = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-show", item.Id[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var annotated = await store.GetByExactIdAsync(item.Id);

        Xunit.Assert.Equal(noteContent, Xunit.Assert.Single(annotated!.Notes).Text);
        Xunit.Assert.Contains(noteContent, show);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_annotate_unknown_and_ambiguous_prefix_fail_without_mutation")]
    public async Task CliBacklogAnnotateUnknownAndAmbiguousPrefixFailWithoutMutation()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var now = DateTimeOffset.UtcNow;
        await store.UpsertAsync(new BacklogItem("ambiguous-alpha", "Ambiguous alpha", "Alpha body", BacklogItemStatus.Open, now, now, null));
        await store.UpsertAsync(new BacklogItem("ambiguous-beta", "Ambiguous beta", "Beta body", BacklogItemStatus.Open, now, now, null));
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var missing = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-annotate", "missing", "Should not land"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var ambiguous = Xunit.Assert.Throws<InvalidOperationException>(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-annotate", "ambiguous", "Should not land"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains("No backlog item found with id prefix 'missing'", missing.Message);
        Xunit.Assert.Contains("Ambiguous id prefix 'ambiguous'", ambiguous.Message);
        var alpha = await store.GetByExactIdAsync("ambiguous-alpha");
        var beta = await store.GetByExactIdAsync("ambiguous-beta");
        Xunit.Assert.Equal("Alpha body", alpha!.Body);
        Xunit.Assert.Empty(alpha.Notes);
        Xunit.Assert.Equal("Beta body", beta!.Body);
        Xunit.Assert.Empty(beta.Notes);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_annotate_closed_item_succeeds_and_show_renders_note")]
    public async Task CliBacklogAnnotateClosedItemSucceedsAndShowRendersNote()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var item = await store.AddAsync("Closed receipt item", "Closed body");
        await store.CloseAsync(item.Id);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-annotate", item.Id[..8], "Receipt after closure"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["backlog-show", item.Id[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var annotated = await store.GetByExactIdAsync(item.Id);

        Xunit.Assert.Equal(BacklogItemStatus.Done, annotated!.Status);
        Xunit.Assert.Contains("Receipt after closure", output);
        Xunit.Assert.Contains("Status:  Done", output);
    }


    [Xunit.Fact(DisplayName = "Cli_backlog_update_supersede_and_link_mutate_store_and_list_annotations")]
    public async Task CliBacklogUpdateSupersedeAndLinkMutateStoreAndListAnnotations()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var old = await store.AddAsync("Old item", "Old body");
        var replacement = await store.AddAsync("Replacement item");
        var duplicate = await store.AddAsync("Duplicate item");
        var related = await store.AddAsync("Related item");
        var kernel = new AgentOrchestratorKernel();

        ExecuteCliAndCapture(["backlog-update", old.Id[..8], "--title", "Updated old", "--description", "Updated body", "--priority", "high"], kernel, workspace);
        ExecuteCliAndCapture(["backlog-supersede", old.Id[..8], replacement.Id[..8]], kernel, workspace);
        ExecuteCliAndCapture(["backlog-link", replacement.Id[..8], duplicate.Id[..8]], kernel, workspace);
        ExecuteCliAndCapture(["backlog-link", replacement.Id[..8], related.Id[..8], "--related"], kernel, workspace);
        var list = ExecuteCliAndCapture(["backlog-list"], kernel, workspace);
        var updated = await store.GetByExactIdAsync(old.Id);

        Xunit.Assert.Equal("Updated old", updated!.Title);
        Xunit.Assert.Equal("Updated body", updated.Body);
        Xunit.Assert.Equal("high", updated.Priority);
        Xunit.Assert.Contains(old.Id, list);
        Xunit.Assert.Contains(duplicate.Id, list);
        Xunit.Assert.Contains(replacement.Id, list);
        Xunit.Assert.Contains($"[Superseded→{replacement.Id[..8]}]", list);
        Xunit.Assert.Contains($"[Dup→{replacement.Id[..8]}]", list);
        Xunit.Assert.Contains($"[Related→{related.Id[..8]}]", list);
    }


    [Xunit.Fact]
    public async Task BacklogList_AllItems_ShowsTimestampsStatusGoalAndExactTitleReadOnly()
    {
        // Parallel-safe: the SQLite store and kernel are scoped to this test's unique temp root.
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var historicalItem = new BacklogItem(
            Guid.NewGuid().ToString("n"),
            "Filed defect 0: exact title",
            "Body 0",
            BacklogItemStatus.Open,
            new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
            new DateTimeOffset(2025, 6, 7, 8, 9, 10, TimeSpan.Zero),
            SourceGoalId: null);
        await store.UpsertAsync(historicalItem);
        var items = new List<BacklogItem> { historicalItem };
        for (var index = 1; index < 7; index++)
        {
            items.Add(await store.AddAsync($"Filed defect {index}: exact title", $"Body {index}"));
        }

        await store.CloseAsync(items[6].Id);
        var kernel = new AgentOrchestratorKernel();
        ExecuteCliAndCapture(
            ["backlog-intake", items[2].Title, "--create-simple-goal", "--backlog-coverage", "full"],
            kernel,
            workspace);
        var linkedGoal = Xunit.Assert.Single(kernel.Goals);
        var goalCountBeforeList = kernel.Goals.Count;

        var first = ExecuteCliAndCapture(["backlog-list"], kernel, workspace);
        var second = ExecuteCliAndCapture(["backlog-list"], kernel, workspace);
        var open = ExecuteCliAndCapture(["backlog-list", "--status", "open"], kernel, workspace);
        var rows = first.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(" | status=", StringComparison.Ordinal))
            .ToArray();

        Xunit.Assert.Equal(7, rows.Length);
        Xunit.Assert.Contains("Backlog list: 7 item(s) from backlog store", first);
        Xunit.Assert.Equal(first, second);
        Xunit.Assert.Equal(goalCountBeforeList, kernel.Goals.Count);
        Xunit.Assert.Contains(rows, row =>
            row.Contains("[created=2025-01-02] [updated=2025-06-07]", StringComparison.Ordinal) &&
            row.Contains($"{items[0].Id} | status=open | goal=- | title={items[0].Title}", StringComparison.Ordinal));
        Xunit.Assert.Contains(rows, row => row.Contains($"{items[2].Id} | status=claimed | goal={linkedGoal.Id.Value} | title={items[2].Title}", StringComparison.Ordinal));
        Xunit.Assert.Contains(rows, row => row.Contains($"{items[6].Id} | status=closed | goal=- | title={items[6].Title}", StringComparison.Ordinal));
        Xunit.Assert.Contains($"{items[0].Id} | status=open | goal=- | title={items[0].Title}", open);
        Xunit.Assert.Contains($"{items[2].Id} | status=claimed | goal={linkedGoal.Id.Value} | title={items[2].Title}", open);
        Xunit.Assert.DoesNotContain(items[6].Id, open);

        var claimedRow = Xunit.Assert.Single(rows.Where(row => row.Contains(items[2].Id, StringComparison.Ordinal)));
        var emittedTitle = claimedRow[(claimedRow.IndexOf(" | title=", StringComparison.Ordinal) + " | title=".Length)..];
        var roundTrip = ExecuteCliAndCapture(["backlog-intake", emittedTitle], kernel, workspace);
        Xunit.Assert.Contains($"## {items[2].Title}", roundTrip);
        Xunit.Assert.Contains("Backlog intake: 1 item(s) from backlog store", roundTrip);
    }


    [Xunit.Fact]
    public void BacklogList_EmptyStore_ReportsZero()
    {
        // Parallel-safe: the SQLite store and kernel are scoped to this test's unique temp root.
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        BacklogStore.Setup(workspace.BacklogStorePath);

        var output = ExecuteCliAndCapture(["backlog-list"], new AgentOrchestratorKernel(), workspace);

        Xunit.Assert.Contains("Backlog list: 0 item(s) from backlog store", output);
    }


    [Xunit.Fact]
    public async Task BacklogIntake_Unfiltered_DisclosesSelectionAndWithheldCount()
    {
        // Parallel-safe: the SQLite store and kernel are scoped to this test's unique temp root.
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var eligibleItems = new List<BacklogItem>();
        for (var index = 0; index < 7; index++)
        {
            eligibleItems.Add(await store.AddAsync($"Intake candidate {index}", $"Body {index}"));
        }
        var closed = await store.AddAsync("Closed intake candidate", "Closed body");
        await store.CloseAsync(closed.Id);
        var superseded = await store.AddAsync("Superseded intake candidate", "Superseded body");
        await store.SupersedeAsync(superseded.Id, eligibleItems[0].Id);

        var kernel = new AgentOrchestratorKernel();
        var output = ExecuteCliAndCapture(["backlog-intake"], kernel, workspace);

        Xunit.Assert.Contains("Backlog intake: 5 of 7 item(s) from backlog store", output);
        Xunit.Assert.Contains("filter: first 5 open, non-superseded, canonical non-decision-record items in filing order", output);
        Xunit.Assert.Contains("2 withheld", output);
        Xunit.Assert.Contains("Use `backlog-list` to see all items.", output);
        Xunit.Assert.Empty(kernel.Goals);
    }


    [Xunit.Fact(DisplayName = "Backlog_planner_mapper_use_default_list_that_hides_superseded_and_duplicates")]
    public async Task BacklogPlannerMapperUseDefaultListThatHidesSupersededAndDuplicates()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var old = await store.AddAsync("Old hidden");
        var replacement = await store.AddAsync("Current visible");
        var duplicate = await store.AddAsync("Duplicate hidden");
        await store.SupersedeAsync(old.Id, replacement.Id);
        await store.LinkAsync(replacement.Id, duplicate.Id);

        var intake = BacklogIntakePlanner.Build(workspace.BacklogStorePath, maxItems: 5);
        var plan = GoalDependencyPlanner.Build(intake);

        Xunit.Assert.Equal(["Current visible"], intake.Items.Select(item => item.Heading));
        Xunit.Assert.Equal(1, plan.Nodes.Count);
    }


    [Xunit.Fact]
    public void CliDogfoodLogListMissingDatabasePrintsNoEntriesWithoutCreatingFile()
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        Assert.False(File.Exists(workspace.DogfoodLogStorePath));

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["dogfood-log", "list"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Assert.Equal($"No dogfood log entries in {workspace.DogfoodLogStorePath}.{Environment.NewLine}", output);
        Assert.False(File.Exists(workspace.DogfoodLogStorePath));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void CliDogfoodLogListIncompatibleDatabaseUsesReadableSetupErrorWithoutMutation(bool older)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        RunEventStoreTests.CreateLegacyDogfoodDatabase(workspace.DogfoodLogStorePath);
        if (older)
        {
            DogfoodLogStore.Setup(workspace.DogfoodLogStorePath);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = workspace.DogfoodLogStorePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE store_schema_versions SET version = 0";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var bytes = File.ReadAllBytes(workspace.DogfoodLogStorePath);
        var schema = RunEventStoreTests.DogfoodSchemaSnapshot(workspace.DogfoodLogStorePath);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var error = Assert.Throws<InvalidOperationException>(() => CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["dogfood-log", "list"], kernel, workspace, ref agents, providers, ref profiles, ref currentGoal)));

        var expected = $"Dogfood log store '{workspace.DogfoodLogStorePath}' schema is {(older ? "Older" : "Missing")} (expected version 1); run setup.";
        Assert.Equal(expected, error.Message);
        Assert.Equal($"InvalidOperationException: {expected}", ProgramStartupErrorFormatter.Format(error));
        Assert.Equal(bytes, File.ReadAllBytes(workspace.DogfoodLogStorePath));
        Assert.Equal(schema, RunEventStoreTests.DogfoodSchemaSnapshot(workspace.DogfoodLogStorePath));
    }

    [Xunit.Fact(DisplayName = "Cli_dogfood_log_add_and_list_use_sqlite_store")]
    public async Task CliDogfoodLogAddAndListUseSqliteStore()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Dogfood command objective");
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        var workspace = CreateRefinedWorkspace(root);

        var addOutput = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["dogfood-log", "add", goal.Id.Value[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var listOutput = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["dogfood-log", "list", "--limit", "5"],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var record = await new DogfoodLogStore(workspace.DogfoodLogStorePath)
            .GetByGoalIdAsync(goal.Id.Value);
        Xunit.Assert.NotNull(record);
        Xunit.Assert.Contains("Recorded to", addOutput);
        Xunit.Assert.Contains("Dogfood command objective", listOutput);
        Xunit.Assert.Contains("Acceptance status unknown (no durable acceptance receipt).", record!.RenderedMarkdown);
        Xunit.Assert.DoesNotContain("Acceptance passed", record.RenderedMarkdown, StringComparison.Ordinal);

        var originalMarkdown = record.RenderedMarkdown;
        GoalOperationJournal.AcceptancePassed(
            root,
            goal,
            "acceptance",
            "branch-head",
            "main-head",
            "Acceptance passed after the entry was recorded.");
        var secondGoal = kernel.CreateGoal("Second dogfood command objective");
        currentGoal = secondGoal;
        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["dogfood-log", "add", secondGoal.Id.Value[..8]],
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var unchanged = await new DogfoodLogStore(workspace.DogfoodLogStorePath)
            .GetByGoalIdAsync(goal.Id.Value);
        Xunit.Assert.Equal(originalMarkdown, unchanged!.RenderedMarkdown);
    }

    [Xunit.Fact]
    public async Task CliBacklogSimilarReturnsRankedPointersAcrossStatusesAndCompletedGoals()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var open = await store.AddAsync("Open pointer", "crossstatusuniqueword");
        var done = await store.AddAsync("Done pointer", "crossstatusuniqueword");
        await store.CloseAsync(done.Id);
        var superseded = await store.AddAsync("Superseded pointer", "crossstatusuniqueword");
        await store.UpdateAsync(superseded.Id, new BacklogItemUpdate(Status: BacklogItemStatus.Superseded));
        var noteOnly = await store.AddAsync("Note-only pointer");
        await store.AppendNoteAsync(noteOnly.Id, "noteonlyuniqueword");
        var kernel = new AgentOrchestratorKernel();
        var completedGoal = kernel.CreateGoal(
            "crossstatusuniqueword completed goal",
            [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
        kernel = WithGoalStatus(kernel, completedGoal.Id, GoalStatus.Completed);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var statusOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["backlog-similar", "crossstatusuniqueword"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var noteOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["backlog-similar", "noteonlyuniqueword"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.Contains($"kind=backlog id={open.Id[..8]} status=Open title=Open pointer updated=", statusOutput);
        Xunit.Assert.Contains($"kind=backlog id={done.Id[..8]} status=Done title=Done pointer updated=", statusOutput);
        Xunit.Assert.Contains($"kind=backlog id={superseded.Id[..8]} status=Superseded title=Superseded pointer updated=", statusOutput);
        Xunit.Assert.Contains($"kind=goal id={completedGoal.Id.Value[..8]} status=Completed title=crossstatusuniqueword completed goal updated=", statusOutput);
        var firstPointer = noteOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0];
        Xunit.Assert.Contains($"kind=backlog id={noteOnly.Id[..8]}", firstPointer);
        Xunit.Assert.DoesNotContain(" excerpt=", statusOutput);
    }

    [Xunit.Fact]
    public async Task CliBacklogSimilarIdExcludesSelfAndHonorsOutputOptions()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        var source = await store.AddAsync("Identity source", "identityuniqueword");
        await store.AppendNoteAsync(source.Id, "noteidentityword");
        var related = await store.AddAsync("Identity related", "identityuniqueword noteidentityword " + new string('x', 400));
        var unrelated = await store.AddAsync("Open unrelated", "identityuniqueword");
        await store.CloseAsync(related.Id);

        var output = ExecuteCliAndCapture(
            ["backlog-similar", "--id", source.Id[..8], "--status", "Done", "--limit", "1", "--excerpt"],
            new AgentOrchestratorKernel(),
            workspace);

        Xunit.Assert.DoesNotContain(source.Id[..8], output);
        Xunit.Assert.Contains($"kind=backlog id={related.Id[..8]} status=Done", output);
        Xunit.Assert.DoesNotContain(unrelated.Id[..8], output);
        var pointer = Xunit.Assert.Single(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("- kind=", StringComparison.Ordinal)));
        var excerpt = pointer[(pointer.IndexOf(" excerpt=", StringComparison.Ordinal) + " excerpt=".Length)..];
        Xunit.Assert.InRange(excerpt.Length, 1, 200);
    }

    [Xunit.Theory]
    [Xunit.InlineData("\"")]
    [Xunit.InlineData("(")]
    [Xunit.InlineData(")")]
    [Xunit.InlineData("*")]
    [Xunit.InlineData(":")]
    [Xunit.InlineData("AND")]
    [Xunit.InlineData("OR")]
    [Xunit.InlineData("NOT")]
    [Xunit.InlineData("NEAR")]
    public async Task CliBacklogSimilarTreatsFtsSyntaxAsPlainText(string query)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        await new BacklogStore(workspace.BacklogStorePath).AddAsync("Syntax source", "AND OR NOT NEAR");

        var exception = Record.Exception(() => ExecuteCliAndCapture(
            ["backlog-similar", query],
            new AgentOrchestratorKernel(),
            workspace));

        Xunit.Assert.Null(exception);
    }

    [Xunit.Fact]
    public async Task CliBacklogAddSimilarityIsAdvisorySuppressibleAndFailureSafe()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        for (var index = 0; index < 5; index++)
            await store.AddAsync($"Prior advisory {index}", "advisoryuniqueword");

        var advisory = ExecuteCliAndCapture(
            ["backlog-add", "New advisory", "advisoryuniqueword"],
            new AgentOrchestratorKernel(),
            workspace);
        var advisoryLines = advisory.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var similarIndex = Array.IndexOf(advisoryLines, "Similar:");
        Xunit.Assert.StartsWith("Added:", advisoryLines[0], StringComparison.Ordinal);
        Xunit.Assert.True(similarIndex > 0);
        Xunit.Assert.InRange(advisoryLines.Count(line => line.StartsWith("- kind=", StringComparison.Ordinal)), 1, 3);

        var kernel = new AgentOrchestratorKernel();
        var completedGoal = kernel.CreateGoal(
            "goaladvisoryuniqueword completed goal",
            [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
        kernel = WithGoalStatus(kernel, completedGoal.Id, GoalStatus.Completed);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var goalAdvisory = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["backlog-add", "Completed goal advisory", "goaladvisoryuniqueword"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        Xunit.Assert.Contains("Similar:", goalAdvisory);
        Xunit.Assert.Contains($"kind=goal id={completedGoal.Id.Value[..8]} status=Completed", goalAdvisory);

        var suppressed = ExecuteCliAndCapture(
            ["backlog-add", "Suppressed advisory", "advisoryuniqueword", "--no-similar"],
            new AgentOrchestratorKernel(),
            workspace);
        Xunit.Assert.Single(suppressed.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Xunit.Assert.DoesNotContain("Similar:", suppressed);

        BacklogSimilaritySearch.ForcedFailureMessage = "forced test failure";
        string failedSearch;
        try
        {
            failedSearch = ExecuteCliAndCapture(
                ["backlog-add", "Failure-safe advisory", "advisoryuniqueword"],
                new AgentOrchestratorKernel(),
                workspace);
        }
        finally
        {
            BacklogSimilaritySearch.ForcedFailureMessage = null;
        }

        var failedLines = failedSearch.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Xunit.Assert.Equal(2, failedLines.Length);
        Xunit.Assert.StartsWith("Added:", failedLines[0], StringComparison.Ordinal);
        Xunit.Assert.Equal("Warning: similarity search unavailable; item was added.", failedLines[1]);
        Xunit.Assert.Contains(
            await store.ListAsync(includeAll: true),
            item => item.Title == "Failure-safe advisory");
    }

    [Xunit.Fact]
    public async Task CliBacklogSimilarDoesNotChangePersistentTablesOrFiles()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new BacklogStore(workspace.BacklogStorePath);
        await store.AddAsync("Stable similarity source", "stableuniqueword");
        var beforeTables = ReadBacklogTableNames(workspace.BacklogStorePath);
        var beforeFiles = SnapshotFiles(root);

        _ = ExecuteCliAndCapture(
            ["backlog-similar", "stableuniqueword"],
            new AgentOrchestratorKernel(),
            workspace);

        Xunit.Assert.Equal(beforeTables, ReadBacklogTableNames(workspace.BacklogStorePath));
        Xunit.Assert.Equal(beforeFiles, SnapshotFiles(root));
    }

    [Xunit.Fact]
    public void CliBacklogSimilarMissingStoreDoesNotCreateBacklogDatabase()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var beforeFiles = SnapshotFiles(root);

        var output = ExecuteCliAndCapture(
            ["backlog-similar", "absentuniqueword"],
            new AgentOrchestratorKernel(),
            workspace);

        Xunit.Assert.Contains("Backlog similar: 0 result(s)", output);
        Xunit.Assert.False(File.Exists(workspace.BacklogStorePath));
        Xunit.Assert.Equal(beforeFiles, SnapshotFiles(root));
    }

    private static string[] ReadBacklogTableNames(string databasePath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False;");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }


}
