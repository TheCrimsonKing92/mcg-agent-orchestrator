using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsStartupAndMetadata : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_routes_single_goal_conduct_outside_command_transaction")]
    public void RunnerRoutesSingleGoalConductOutsideCommandTransaction()
    {
        Xunit.Assert.True(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "abc123"]));
        Xunit.Assert.True(CliPersistentStateRunner.IsSingleGoalConductCommand(["CONDUCT", "abc123", "--policy", "Permissive"]));

        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "--loop"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "abc123", "--watch"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct", "--help"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["conduct"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand(["next"]));
        Xunit.Assert.False(CliPersistentStateRunner.IsSingleGoalConductCommand([]));
    }


    [Xunit.Theory(DisplayName = "Cli_dispatcher_help_prints_command_specific_usage")]
    [Xunit.InlineData(new[] { "conduct", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "-h" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "--loop", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "goals", "subscribe", "--help" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "help", "goals", "subscribe" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "reassign-agent", "--help" }, CliCommandHelp.ReassignAgentUsage)]
    [Xunit.InlineData(new[] { "workspace", "--help" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "-h" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "create", "-h" }, CliCommandHelp.WorkspaceCreateUsage)]
    public void CliDispatcherHelpPrintsCommandSpecificUsage(string[] args, string expectedUsage)
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                args,
                kernel,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Xunit.Assert.False(changed);
        });

        Xunit.Assert.Contains(expectedUsage, output);
    }


    [Xunit.Theory(DisplayName = "Cli_startup_help_exits_before_state_repository_creation")]
    [Xunit.InlineData(new[] { "conduct", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "-h" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "conduct", "--loop", "--help" }, CliCommandHelp.ConductUsage)]
    [Xunit.InlineData(new[] { "goals", "subscribe", "--help" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "help", "goals", "subscribe" }, GoalMonitoringSubscriptionCommand.GoalsSubscribeUsage)]
    [Xunit.InlineData(new[] { "reassign-agent", "--help" }, CliCommandHelp.ReassignAgentUsage)]
    [Xunit.InlineData(new[] { "workspace", "--help" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "-h" }, CliCommandHelp.WorkspaceUsage)]
    [Xunit.InlineData(new[] { "workspace", "create", "-h" }, CliCommandHelp.WorkspaceCreateUsage)]
    public void CliStartupHelpExitsBeforeStateRepositoryCreation(string[] args, string expectedUsage)
    {
        var root = CreateTempDirectory();
        var result = RunAppCli(root, args);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.Contains(expectedUsage, result.StandardOutput);
        Xunit.Assert.True(string.IsNullOrWhiteSpace(result.StandardError), result.StandardError);
        Xunit.Assert.False(File.Exists(Path.Combine(root, ".orchestrator", "state.db")));
        Xunit.Assert.False(Directory.Exists(Path.Combine(root, ".orchestrator")));
    }


    [Xunit.Fact(DisplayName = "Cli_startup_short_read_command_bootstraps_fresh_state_and_releases_it")]
    public async Task CliStartupShortReadCommandExitsWithinTwoSecondsAndReleasesState()
    {
        var root = CreateTempDirectory();

        var result = await RunAppCliWithExitTimeout(root, ["next", "--full"], TimeSpan.FromSeconds(60));

        Xunit.Assert.True(
            result.ExitedWithinTimeout,
            $"CLI did not exit within the 60 second hang guard. stdout: {result.StandardOutput} stderr: {result.StandardError}");
        Xunit.Assert.Equal(1, result.ExitCode);
        Xunit.Assert.Contains("Create a goal first", result.StandardError);

        var statePath = Path.Combine(root, ".orchestrator", "state.db");
        Xunit.Assert.True(File.Exists(statePath));
        Xunit.Assert.True(StateDbMigrations.IsUpToDate(statePath));
        using var stateLockProbe = File.Open(statePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Xunit.Assert.True(stateLockProbe.CanWrite);
    }

    [Xunit.Fact(DisplayName = "Cli_startup_read_command_bootstraps_unmigrated_non_wal_state")]
    public void CliStartupReadCommandBootstrapsUnmigratedNonWalState()
    {
        var root = CreateTempDirectory();
        var orchestratorDirectory = Path.Combine(root, ".orchestrator");
        var statePath = Path.Combine(orchestratorDirectory, "state.db");
        Directory.CreateDirectory(orchestratorDirectory);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={statePath};Pooling=False"))
        {
            connection.Open();
        }
        Xunit.Assert.False(StateDbMigrations.IsUpToDate(statePath));

        var result = RunAppCli(root, ["goals"]);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.True(StateDbMigrations.IsUpToDate(statePath));
        using var migrated = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={statePath};Mode=ReadOnly;Pooling=False");
        migrated.Open();
        using var journalMode = migrated.CreateCommand();
        journalMode.CommandText = "PRAGMA journal_mode";
        Xunit.Assert.Equal(
            "wal",
            Convert.ToString(journalMode.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Xunit.Fact]
    public void CliStartupReadCommandMigratesPublishedVersion7StateBeforeOpeningRepository()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        CreateVersion7StateOutboxFixture(workspace.SqliteStatePath);

        var result = RunAppCli(root, ["goals"]);

        Xunit.Assert.Equal(0, result.ExitCode);
        Xunit.Assert.True(StateDbMigrations.IsUpToDate(workspace.SqliteStatePath));
        using (var unchanged = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={workspace.SqliteStatePath};Mode=ReadOnly;Pooling=False"))
        {
            unchanged.Open();
            using var columns = unchanged.CreateCommand();
            columns.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_table_info('state_outbox') ORDER BY cid)";
            Xunit.Assert.Equal(
                "id,kind,payload_json,created_at,quarantined_at,quarantine_reason,processing_token,processing_started_at",
                Convert.ToString(columns.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            using var migrationCount = unchanged.CreateCommand();
            migrationCount.CommandText = "SELECT COUNT(*) FROM schema_migrations";
            Xunit.Assert.Equal(13L, migrationCount.ExecuteScalar());
            using var claimTable = unchanged.CreateCommand();
            claimTable.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'source_backlog_claims'";
            Xunit.Assert.Equal(1L, claimTable.ExecuteScalar());
        }
    }


    [Xunit.Fact(DisplayName = "ConsoleViews_PrintGoals_renders_metadata_summaries")]
    public void PrintGoalsRendersMetadataSummaries()
    {
        IReadOnlyList<GoalSummary> summaries =
        [
            new GoalSummary("0123456789abcdef0123456789abcdef", "Active", "Build the widget", "2026-06-15T00:00:00.0000000+00:00"),
            new GoalSummary("fedcba98", "Completed", "Ship the gadget", "2026-06-14T00:00:00.0000000+00:00"),
            new GoalSummary(
                "badc0ffe",
                "Active",
                "Recover failed work",
                "2026-06-13T00:00:00.0000000+00:00",
                Condition: GoalLifecycle.ActiveWithFailedTaskCondition)
        ];

        var output = CaptureConsole(() => ConsoleViews.PrintGoals(summaries));

        Xunit.Assert.Contains("01234567 Active: Build the widget", output);
        Xunit.Assert.Contains("fedcba98 Completed: Ship the gadget", output);
        Xunit.Assert.Contains("badc0ffe Active [active-with-failed-task]: Recover failed work", output);
    }

    [Xunit.Fact(DisplayName = "ConsoleViews_PrintGoal_renders_effective_acceptance_criteria_corrections")]
    public void PrintGoalRendersEffectiveAcceptanceCriteriaCorrections()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Render correction overlay");
        var task = goal.Tasks.First(task => task.RequiredRole == AgentRole.Developer);
        kernel.RecordOperatorTaskNote(
            goal.Id,
            task.Id,
            "CRITERIA CORRECTION: supersedes=\"full suite required\"; correction=\"focused build-check accepted\"");

        var output = CaptureConsole(() => ConsoleViews.PrintGoal(goal));

        Xunit.Assert.Contains("Effective acceptance criteria corrections:", output);
        Xunit.Assert.Contains("supersedes: full suite required", output);
        Xunit.Assert.Contains("correction: focused build-check accepted", output);
        Xunit.Assert.Contains("provenance: operator", output);
    }

    [Xunit.Fact]
    public async Task GoalAmendWaivePersistsBriefAndAuditEvent()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement the slice", AgentRole.Developer);
        var reviewer = new TaskSpec(TaskId.New(), "Review the slice", AgentRole.Reviewer);
        var goal = kernel.CreateGoal("Recover acceptance scope", [developer, reviewer]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Ship recoverable acceptance scope",
            ["focused tests pass", "  measure unavailable makespan  "],
            VerificationClass.TestVerifiable,
            [],
            []));
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        await repository.SaveAsync(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var reasonPath = Path.Combine(root, "waiver-reason.txt");
        await File.WriteAllTextAsync(reasonPath, "requires conductor evidence:\r\nno worker substitute is acceptable");

        var output = CaptureConsole(() =>
        {
            var changed = CliPersistentStateRunner.ExecuteCommand(
                CliArgumentParser.SplitCommand(
                    $"goal-amend {goal.Id.Value[..8]} --waive 2 --reason-file {reasonPath} --actor miles"),
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Xunit.Assert.True(changed);
        });

        var restored = await repository.LoadAsync();
        var restoredGoal = restored.GetGoal(goal.Id);
        var waiver = Xunit.Assert.Single(restoredGoal.EffectiveAcceptanceCriteriaCorrections);
        var brief = restored.BuildTaskBrief(goal.Id, reviewer.Id).Content;
        var eventPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
        var auditLine = File.ReadLines(eventPath).Single(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("eventType").GetString() == "AcceptanceCriterionWaived";
        });
        using var auditEvent = JsonDocument.Parse(auditLine);

        Xunit.Assert.Contains("Acceptance criterion waived", output);
        Xunit.Assert.Contains("criterion=2", output);
        Xunit.Assert.Equal("measure unavailable makespan", waiver.SupersededCriterion);
        Xunit.Assert.Equal("requires conductor evidence: no worker substitute is acceptable", waiver.WaiverReason);
        Xunit.Assert.Contains("- [WAIVED] measure unavailable makespan", brief);
        Xunit.Assert.Contains("Reason: requires conductor evidence: no worker substitute is acceptable", brief);
        Xunit.Assert.Equal("measure unavailable makespan", auditEvent.RootElement.GetProperty("criterion").GetString());
        Xunit.Assert.Equal("miles", auditEvent.RootElement.GetProperty("actor").GetString());
        Xunit.Assert.Equal("requires conductor evidence: no worker substitute is acceptable", auditEvent.RootElement.GetProperty("reason").GetString());
        Xunit.Assert.True(auditEvent.RootElement.TryGetProperty("recordedAt", out _));
        Xunit.Assert.Equal(waiver.CapturedAcceptanceCriteriaHash, auditEvent.RootElement.GetProperty("capturedAcceptanceCriteriaHash").GetString());
        Xunit.Assert.All(restoredGoal.Tasks, task => Xunit.Assert.Null(task.LastDispatch));
    }

}
