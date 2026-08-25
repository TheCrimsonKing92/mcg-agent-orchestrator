using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsPortfolioCommands : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "Cli_epic_and_project_commands_assign_members_and_print_rollups")]
    public void EpicAndProjectCommandsAssignMembersAndPrintRollups()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var active = kernel.CreateGoal(new GoalId("aaaaaaaa111111111111111111111111"), "Implement board model");
        var landed = kernel.CreateGoal(new GoalId("bbbbbbbb222222222222222222222222"), "Land board model");
        kernel.ActivateGoal(active.Id, AgentCatalog.Default().Agents);
        kernel.ActivateGoal(landed.Id, AgentCatalog.Default().Agents);
        kernel = WithGoalStatus(kernel, landed.Id, GoalStatus.Completed);
        var backlog = new BacklogStore(workspace.BacklogStorePath)
            .AddAsync("Portfolio backlog member")
            .GetAwaiter()
            .GetResult();

        ExecuteCliAndCapture(["project-add", "Control Plane"], kernel, workspace);
        ExecuteCliAndCapture(["epic-add", "Portfolio Board"], kernel, workspace);
        ExecuteCliAndCapture(["project-assign", "Portfolio Board", "Control Plane"], kernel, workspace);
        ExecuteCliAndCapture(["epic-assign", active.Id.Value[..8], "Portfolio Board"], kernel, workspace);
        ExecuteCliAndCapture(["epic-assign", backlog.Id[..8], "Portfolio Board"], kernel, workspace);
        ExecuteCliAndCapture(["epic-assign", landed.Id.Value[..8], "Portfolio Board"], kernel, workspace);

        var output = ExecuteCliAndCapture(["epic-list"], kernel, workspace);

        Xunit.Assert.Contains("Portfolio Board", output);
        Xunit.Assert.Contains("project=Control Plane", output);
        Xunit.Assert.Contains("goals=2", output);
        Xunit.Assert.Contains("backlog=1", output);
        Xunit.Assert.Contains("active=1", output);
        Xunit.Assert.Contains("landed=1", output);
    }

    [Xunit.Fact(DisplayName = "Cli_portfolio_view_renders_project_epic_goal_hierarchy")]
    public void PortfolioViewRendersProjectEpicGoalHierarchy()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("cccccccc333333333333333333333333"), "Render hierarchy goal");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var project = store.AddProjectAsync("Control Plane").GetAwaiter().GetResult();
        var epic = store.AddEpicAsync("Portfolio Board", project.Id).GetAwaiter().GetResult();
        store.AssignGoalToEpicAsync(goal.Id.Value, epic.Id).GetAwaiter().GetResult();

        var output = ExecuteCliAndCapture(["portfolio"], kernel, workspace);

        Xunit.Assert.Contains("Project: Control Plane", output);
        Xunit.Assert.Contains("Epic: Portfolio Board", output);
        Xunit.Assert.Contains("cccccccc [Active]", output);
        Xunit.Assert.Contains("Render hierarchy goal", output);
    }

    [Xunit.Fact(DisplayName = "Cli_status_prints_epic_and_project_membership")]
    public void StatusPrintsEpicAndProjectMembership()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("dddddddd444444444444444444444444"), "Status membership goal");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var project = store.AddProjectAsync("Control Plane").GetAwaiter().GetResult();
        var epic = store.AddEpicAsync("Portfolio Board", project.Id).GetAwaiter().GetResult();
        store.AssignGoalToEpicAsync(goal.Id.Value, epic.Id).GetAwaiter().GetResult();

        var output = ExecuteCliAndCapture(["status", goal.Id.Value[..8]], kernel, workspace);

        Xunit.Assert.Contains($"Goal {goal.Id.Value}", output);
        Xunit.Assert.Contains("Objective: Status membership goal", output);
        Xunit.Assert.Contains("Status: Active", output);
        Xunit.Assert.Contains("Portfolio: epic=Portfolio Board", output);
        Xunit.Assert.Contains("project=Control Plane", output);
    }

    [Xunit.Fact(DisplayName = "Cli_status_tasks_only_prints_identifier_status_and_every_task_without_objective")]
    public void StatusTasksOnlyPrintsIdentifierStatusAndEveryTaskWithoutObjective()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        const string objectiveToken = "OBJECTIVE_TOKEN_NOT_IN_TASKS";
        TaskSpec[] tasks =
        [
            new(TaskId.New(), "Inspect the status renderer", AgentRole.Researcher),
            new(TaskId.New(), "Implement the status flag", AgentRole.Developer),
            new(TaskId.New(), "Verify the status output", AgentRole.Tester)
        ];
        var goal = kernel.CreateGoal(new GoalId("abababab111111111111111111111111"), $"Suppress {objectiveToken}", tasks);

        var output = ExecuteCliAndCapture(["status", goal.Id.Value[..8], "--tasks-only"], kernel, workspace);

        Xunit.Assert.Contains($"Goal {goal.Id.Value}", output);
        Xunit.Assert.Contains("Status: Draft", output);
        Xunit.Assert.Contains("Tasks:", output);
        foreach (var task in tasks)
        {
            Xunit.Assert.Contains(task.Description, output);
        }

        Xunit.Assert.DoesNotContain("Objective:", output);
        Xunit.Assert.DoesNotContain(objectiveToken, output);
    }

    [Xunit.Fact(DisplayName = "Cli_status_default_output_keeps_existing_byte_shape")]
    public void StatusDefaultOutputKeepsExistingByteShape()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        TaskSpec[] tasks =
        [
            new(TaskId.New(), "Inspect default status", AgentRole.Researcher),
            new(TaskId.New(), "Implement default status", AgentRole.Developer),
            new(TaskId.New(), "Verify default status", AgentRole.Tester)
        ];
        var goal = kernel.CreateGoal(new GoalId("cdcdcdcd222222222222222222222222"), "Preserve default status objective", tasks);

        var output = ExecuteCliAndCapture(["status", goal.Id.Value[..8]], kernel, workspace);

        var expected = string.Join(Environment.NewLine,
        [
            string.Empty,
            $"Goal {goal.Id.Value}",
            "Objective: Preserve default status objective",
            "Status: Draft",
            "Tasks:",
            "  1. [Pending] Researcher: Inspect default status (unassigned)",
            "  2. [Pending] Developer: Implement default status (unassigned)",
            "  3. [Pending] Tester: Verify default status (unassigned)",
            string.Empty
        ]) + Environment.NewLine;
        Xunit.Assert.Equal(expected, output);
    }

    [Xunit.Fact(DisplayName = "Cli_epic_members_lists_terminal_goal_and_backlog_members")]
    public void EpicMembersListsTerminalGoalAndBacklogMembers()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("eeeeeeee555555555555555555555555"), "Cancelled member");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Cancelled);
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var backlog = backlogStore.AddAsync("Done backlog member").GetAwaiter().GetResult();
        backlogStore.TryCloseByIdAsync(backlog.Id).GetAwaiter().GetResult();
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        var epic = store.AddEpicAsync("Terminal members").GetAwaiter().GetResult();
        store.AssignGoalToEpicAsync(goal.Id.Value, epic.Id).GetAwaiter().GetResult();
        store.AssignBacklogItemToEpicAsync(backlog.Id, epic.Id).GetAwaiter().GetResult();

        var output = ExecuteCliAndCapture(["epic-members", epic.Id[..8]], kernel, workspace);

        Xunit.Assert.Contains($"Goal: {goal.Id.Value}", output);
        Xunit.Assert.Contains($"BacklogItem: {backlog.Id}", output);
    }

    [Xunit.Fact(DisplayName = "Cli_epic_suggest_records_all_signals_without_assigning_members")]
    public void EpicSuggestRecordsAllSignalsWithoutAssigningMembers()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var scopeA = kernel.CreateGoal(new GoalId("eeeeeeee555555555555555555555555"), "Update src/Feature/Board.cs");
        var scopeB = kernel.CreateGoal(new GoalId("ffffffff666666666666666666666666"), "Test src/Feature/Board.cs");
        var sourceA = kernel.CreateGoal(new GoalId("11111111aaaaaaaaaaaaaaaaaaaaaaaa"), "Source incident goal");
        var sourceB = kernel.CreateGoal(new GoalId("22222222bbbbbbbbbbbbbbbbbbbbbbbb"), "Source incident follow-up");
        kernel.SetGoalSourceBacklogItemId(sourceA.Id, "incident000000000000000000000000");
        kernel.SetGoalSourceBacklogItemId(sourceB.Id, "incident000000000000000000000000");

        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var parent = backlogStore.AddAsync("Parent backlog").GetAwaiter().GetResult();
        var dependent = backlogStore.AddAsync("Dependent backlog", $"Depends on backlog {parent.Id[..8]}.").GetAwaiter().GetResult();
        var lineageA = backlogStore.AddAsync("Lineage A", "Parent incident alpha123.", sourceGoalId: "lineage-source").GetAwaiter().GetResult();
        var lineageB = backlogStore.AddAsync("Lineage B", "Parent incident alpha123.", sourceGoalId: "lineage-source").GetAwaiter().GetResult();

        var output = ExecuteCliAndCapture(["epic-suggest"], kernel, workspace);
        var suggestions = new PortfolioStore(workspace.PortfolioStorePath).ListSuggestionsAsync().GetAwaiter().GetResult();

        Xunit.Assert.Contains("signal=backlog-dependency", output);
        Xunit.Assert.Contains("signal=shared-file-scope", output);
        Xunit.Assert.Contains("signal=intake-lineage", output);
        Xunit.Assert.Contains(suggestions, suggestion => suggestion.Signal == "backlog-dependency" && suggestion.BacklogItemIds.Contains(parent.Id) && suggestion.BacklogItemIds.Contains(dependent.Id));
        Xunit.Assert.Contains(suggestions, suggestion => suggestion.Signal == "shared-file-scope" && suggestion.GoalIds.Contains(scopeA.Id.Value) && suggestion.GoalIds.Contains(scopeB.Id.Value));
        Xunit.Assert.Contains(suggestions, suggestion => suggestion.Signal == "intake-lineage" && suggestion.BacklogItemIds.Contains(lineageA.Id) && suggestion.BacklogItemIds.Contains(lineageB.Id));
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        Xunit.Assert.Null(store.GetGoalMembershipAsync(scopeA.Id.Value).GetAwaiter().GetResult());
        Xunit.Assert.Null(store.GetBacklogMembershipAsync(parent.Id).GetAwaiter().GetResult());
    }

    [Xunit.Fact(DisplayName = "Cli_epic_suggestions_lists_persisted_evidence")]
    public void EpicSuggestionsListsPersistedEvidence()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var store = new PortfolioStore(workspace.PortfolioStorePath);
        store.ReplaceSuggestionsAsync([
            new PortfolioClusterSuggestion(
                "abc1234567890000",
                "shared-file-scope",
                "Shared file scope: src/A.cs",
                "2 goals reference src/A.cs",
                ["aaaaaaaa111111111111111111111111", "bbbbbbbb222222222222222222222222"],
                [],
                DateTimeOffset.UtcNow)
        ]).GetAwaiter().GetResult();

        var output = ExecuteCliAndCapture(["epic-suggestions"], new AgentOrchestratorKernel(), workspace);

        Xunit.Assert.Contains("signal=shared-file-scope", output);
        Xunit.Assert.Contains("evidence: 2 goals reference src/A.cs", output);
    }
}
