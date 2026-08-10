using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
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

    [Xunit.Fact(DisplayName = "Cli_backlog_intake_reports_actual_automatic_reduced_pipeline")]
    public void CliBacklogIntakeReportsActualAutomaticReducedPipeline()
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
        Xunit.Assert.InRange(currentGoal!.Tasks.Count, 1, 2);
        Xunit.Assert.Equal(AgentRole.Developer, currentGoal.Tasks[0].RequiredRole);
        Xunit.Assert.DoesNotContain(currentGoal.Tasks, task => task.RequiredRole is AgentRole.Researcher or AgentRole.Planner or AgentRole.Tester);
        var expectedWorkflow = currentGoal.Tasks.Count == 1 ? "developer-only" : "developer-reviewer";
        Xunit.Assert.Contains("Created goal from backlog slice.", output, StringComparison.Ordinal);
        Xunit.Assert.DoesNotContain("Created five-role", output, StringComparison.Ordinal);
        Xunit.Assert.Contains($"\"workflow\":\"{expectedWorkflow}\"", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"selectionSource\":\"automatic\"", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"reasons\":[", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_intake_forced_five_role_uses_exact_persisted_order")]
    public void CliBacklogIntakeForcedFiveRoleUsesExactPersistedOrder()
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
                ["backlog-intake", "Update forced CLI help label", "--create-goal", "--pipeline", "five-role", "--backlog-coverage", "full"],
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Equal(
            [AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            currentGoal!.Tasks.Select(task => task.RequiredRole));
        Xunit.Assert.Contains("\"workflow\":\"five-role\"", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("\"selectionSource\":\"explicitly-required\"", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Cli_backlog_intake_unsatisfied_forced_batch_fails_before_any_reservation")]
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
        Xunit.Assert.Contains(code.RequiredTools, item => item.Contains("Invoke-IsolatedDotnet", StringComparison.Ordinal));
        Xunit.Assert.Single(code.TaskBoundaries);
        Xunit.Assert.Equal(AgentRole.Developer, code.TaskBoundaries[0].Role);
        Xunit.Assert.True(high.CanCreateGoal);
        Xunit.Assert.Contains("high-risk", high.RiskLabels);
        Xunit.Assert.Contains("multi-scope", high.RiskLabels);
        Xunit.Assert.Contains("security-risk", high.RiskLabels);
        Xunit.Assert.Equal(GoalIntakePipeline.DeveloperOnly, docs.PipelineDecision.Pipeline);
        Xunit.Assert.False(docs.PipelineDecision.IsOverride);
        Xunit.Assert.Equal(GoalIntakePipeline.DeveloperOnly, code.PipelineDecision.Pipeline);
        Xunit.Assert.True(code.PipelineDecision.IsOverride);
        Xunit.Assert.Equal(GoalIntakePipeline.DeveloperReviewer, high.PipelineDecision.Pipeline);
        Xunit.Assert.Equal([AgentRole.Developer, AgentRole.Reviewer], high.TaskBoundaries.Select(boundary => boundary.Role));
        Xunit.Assert.Contains(high.PipelineDecision.Reasons, reason => reason.Contains("security-risk", StringComparison.Ordinal));
    }


    [Xunit.Fact(DisplayName = "GoalObjectivePlanner_routes_open_ended_objectives_to_five_role_pipeline")]
    public void GoalObjectivePlannerRoutesOpenEndedObjectivesToFiveRolePipeline()
    {
        var plan = GoalObjectivePlanner.Build("Build a dashboard supervision workflow for unattended goals", simple: false);

        Xunit.Assert.Contains("scope-implicit", plan.RiskLabels);
        Xunit.Assert.Equal(GoalIntakePipeline.FiveRole, plan.PipelineDecision.Pipeline);
        Xunit.Assert.Equal([AgentRole.Researcher, AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer], plan.TaskBoundaries.Select(boundary => boundary.Role));
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


    [Xunit.Fact(DisplayName = "Cli_intent_template_creates_simple_goal_from_dashboard_template")]
    public void CliIntentTemplateCreatesSimpleGoalFromDashboardTemplate()
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
                ["intent-template", "dashboard", "Expose failure triage in the goal page", "--create-simple-goal"],
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
        Xunit.Assert.Contains("Intent template: dashboard", currentGoal.Objective);
        Xunit.Assert.Contains("Request: Expose failure triage in the goal page", currentGoal.Objective);
        Xunit.Assert.Contains("Expose the same state through API DTOs before relying on rendered HTML.", currentGoal.Objective);
        Xunit.Assert.Contains("Run focused dashboard rendering/API tests.", currentGoal.Objective);
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


    [Xunit.Fact(DisplayName = "Dashboard_goal_plan_dto_exposes_compiled_graph_before_goal_creation")]
    public void DashboardGoalPlanDtoExposesCompiledGraphBeforeGoalCreation()
    {
        var root = CreateTempDirectory();
        WriteCompilationBacklog(root);
        var intake = BacklogIntakePlanner.Build(BacklogStorePathFor(root), maxItems: 2);
        var plan = GoalDependencyPlanner.Build(intake);

        var dto = DashboardResponseMapper.ToBacklogGoalPlanDto(intake, plan);

        Xunit.Assert.Equal(2, dto.NodeCount);
        Xunit.Assert.Equal(plan.CompiledGraph.GraphId, dto.CompiledGraph.GraphId);
        Xunit.Assert.True(dto.CompiledGraph.IsRunnable);
        Xunit.Assert.Equal(2, dto.CompiledGraph.Nodes.Count);
        Xunit.Assert.Contains(dto.CompiledGraph.Nodes[0].FileScopes, scope => scope.Contains("src/FeatureA", StringComparison.Ordinal));
        Xunit.Assert.Equal(2, dto.ParallelPlan.Batches.Count);
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

        // Backlog commands that do not inspect dependencies skip state and stay concurrent with a conductor.
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["backlog-add", "title"]));
        Xunit.Assert.True(CliPersistentStateRunner.SkipsKernelState(["BACKLOG-ADD", "title"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["backlog-add", "title", "--depends-on", "abc"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["backlog-list"]));
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
        Xunit.Assert.True(CliPersistentStateRunner.RequiresKernelBacklogState(["backlog-show", "abc"]));
        Xunit.Assert.True(CliPersistentStateRunner.RequiresKernelBacklogState(["backlog-depends", "abc", "--on", "def"]));
        Xunit.Assert.True(CliPersistentStateRunner.RequiresKernelBacklogState(["backlog-add", "title", "--depends-on", "abc"]));
        Xunit.Assert.False(CliPersistentStateRunner.RequiresKernelBacklogState(["backlog-add", "title"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["run", "1"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["conduct", "abc123"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["acceptance"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState(["serve-dashboard"]));
        Xunit.Assert.False(CliPersistentStateRunner.SkipsKernelState([]));
    }

    [Xunit.Fact(DisplayName = "Cli_gate_status_lists_stable_slot_heartbeats")]
    public void CliGateStatusListsStableSlotHeartbeats()
    {
        var root = CreateTempDirectory();
        var isolatedRoot = Path.Combine(root, "isolated-dotnet");
        var previousRoot = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, isolatedRoot);
            GateHeartbeatArtifacts.Write(
                GateHeartbeatArtifacts.GetStableSlotPath(1),
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
            var workspace = CreateRefinedWorkspace(root);
            var output = ExecuteCliAndCapture(["gate-status"], new AgentOrchestratorKernel(), workspace);

            Xunit.Assert.Contains("Gate status:", output);
            Xunit.Assert.Contains("slot-1: goal=abcdef12 phase=verification-check state=running", output);
            Xunit.Assert.Contains("target=WorkerDispatchTests", output);
            Xunit.Assert.Contains("child_pid=222", output);
            Xunit.Assert.Contains("output_bytes=15", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, previousRoot);
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
        var dto = DashboardResponseMapper.ToBacklogGoalPlanDto(intake, plan);

        Xunit.Assert.Equal(["Current visible"], intake.Items.Select(item => item.Heading));
        Xunit.Assert.Equal(1, dto.NodeCount);
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


}
