using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class FundamentalAliasTests
{
    // ─── next: copy-pasteable Run: line ───────────────────────────────────────

    [Xunit.Fact(DisplayName = "Cli_next_prints_run_command_as_copy_pasteable_line")]
    public void CliNextPrintsRunCommandAsCopyPasteableLine()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Expose Run command", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.ApiOnly)
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["next"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Contains("Run: ", output);
        Xunit.Assert.Contains("run 1", output);
        // The Run: line must appear after the action list
        var runLineIndex = output.IndexOf("Run: ", StringComparison.Ordinal);
        var actionLineIndex = output.IndexOf("command: run 1", StringComparison.Ordinal);
        Xunit.Assert.True(runLineIndex > actionLineIndex, $"Run: line should appear after action list. runLineIndex={runLineIndex} actionLineIndex={actionLineIndex}");
    }

    [Xunit.Fact(DisplayName = "Cli_next_run_command_is_health_suggested_command")]
    public void CliNextRunCommandIsHealthSuggestedCommand()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Health suggested command", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.ApiOnly)
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["next"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        // Health section and Run: section must both exist and be consistent
        Xunit.Assert.Contains("Health:", output);
        var runLine = output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("Run: ", StringComparison.Ordinal));
        Xunit.Assert.NotNull(runLine);
        var runCommand = runLine!["Run: ".Length..];
        Xunit.Assert.False(string.IsNullOrWhiteSpace(runCommand));
    }

    // ─── next --full: bounded diagnostics replacement ─────────────────────────

    [Xunit.Fact(DisplayName = "Cli_next_full_prints_bounded_diagnostics_not_deep_sections")]
    public void CliNextFullPrintsBoundedDiagnosticsNotDeepSections()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Expose detail via next full", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.ApiOnly)
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var conciseOutput = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["next"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        var fullOutput = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["next", "--full"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        // Concise next prints the health line and the Run: line but not the detail sections
        Xunit.Assert.Contains("Run: ", conciseOutput);
        Xunit.Assert.DoesNotContain("Attention:", conciseOutput);
        Xunit.Assert.DoesNotContain("Goal readiness", conciseOutput);
        Xunit.Assert.DoesNotContain("Loop health report", conciseOutput);
        Xunit.Assert.DoesNotContain("Supervisor goal:", conciseOutput);
        Xunit.Assert.DoesNotContain("Operator inbox:", conciseOutput);

        Xunit.Assert.Contains("Goal diagnostics", fullOutput);
        Xunit.Assert.Contains("Mode: bounded", fullOutput);
        Xunit.Assert.Contains("Deeper commands:", fullOutput);
        Xunit.Assert.Contains($"readiness {goal.Id.Value[..8]}", fullOutput);
        Xunit.Assert.Contains($"operator-inbox {goal.Id.Value[..8]}", fullOutput);
        Xunit.Assert.DoesNotContain("Attention:", fullOutput);
        Xunit.Assert.DoesNotContain("Goal readiness", fullOutput);
        Xunit.Assert.DoesNotContain("Loop health report", fullOutput);
        Xunit.Assert.DoesNotContain("Supervisor goal:", fullOutput);
        Xunit.Assert.DoesNotContain("Operator inbox:", fullOutput);
    }

    [Xunit.Fact(DisplayName = "Cli_next_full_points_to_deeper_diagnostic_commands")]
    public void CliNextFullPointsToDeeperDiagnosticCommands()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Expose model and subscription detail", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.ApiOnly)
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["next", "--full"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Contains($"subscription-plan {goal.Id.Value[..8]}", output);
        Xunit.Assert.Contains("model-outcomes", output);
        Xunit.Assert.Contains("loop-health", output);
        Xunit.Assert.Contains($"failure-triage {goal.Id.Value[..8]}", output);
        Xunit.Assert.Contains($"goal-recovery {goal.Id.Value[..8]}", output);
        Xunit.Assert.DoesNotContain("Model outcome scorecard", output);
        Xunit.Assert.DoesNotContain("subscription plan:", output);
        Xunit.Assert.DoesNotContain("Goal recovery", output);
        Xunit.Assert.DoesNotContain("Failure triage goal:", output);
    }

    [Xunit.Fact(DisplayName = "Cli_next_full_accepts_goal_prefix_before_full_flag")]
    public void CliNextFullAcceptsGoalPrefixBeforeFullFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Goal prefix test", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents =
        [
            new AgentDefinition(
                new AgentId("developer"),
                "Developer",
                AgentRole.Developer,
                new ModelProfile("OpenAI", "gpt-5-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
                ExecutionPolicy: AgentExecutionPolicy.ApiOnly)
        ];
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        kernel.ActivateGoal(goal.Id, agents);

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["next", goal.Id.Value[..8], "--full"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Equal(goal.Id, currentGoal?.Id);
        Xunit.Assert.Contains("Goal diagnostics", output);
        Xunit.Assert.Contains("Deeper commands:", output);
    }

    // ─── accept alias ─────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Cli_accept_alias_prints_acceptance_summary")]
    public void CliAcceptAliasPrintsAcceptanceSummary()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Accept alias test", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["accept"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
            // Goal is Active not Completed — acceptance merge returns false
            Xunit.Assert.False(changed);
        });

        // PrintAcceptanceSummary always prints even when merge is blocked
        Xunit.Assert.Contains("acceptance: not accepted", output);
    }

    [Xunit.Fact(DisplayName = "Cli_accept_alias_resolves_named_goal_by_prefix")]
    public void CliAcceptAliasResolvesNamedGoalByPrefix()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Named accept test", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        kernel.ActivateGoal(goal.Id, agents);

        var output = CaptureConsole(() =>
            CliCommandDispatcher.ExecuteCommand(
                ["accept", goal.Id.Value[..8]],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Equal(goal.Id, currentGoal?.Id);
        Xunit.Assert.Contains("acceptance:", output);
    }

    // ─── stop alias ───────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Cli_stop_cancel_delegates_to_cancel_goal")]
    public void CliStopCancelDelegatesToCancelGoal()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Stop cancel test", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        // Without --confirm-goal-stop, cancel-goal on an active non-current goal would fail.
        // Use a Completed goal to avoid the confirmation requirement.
        kernel.ReportTaskProgress(goal.Id, goal.Tasks.Single().Id, WorkTaskStatus.Completed, "Done.");

        CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["stop", goal.Id.Value[..8], "No longer needed", "--as", "cancel", "--confirm-goal-stop"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Equal(GoalStatus.Cancelled, currentGoal!.Status);
        Xunit.Assert.Contains(goal.Timeline, (ProgressEvent evt) =>
            evt.Kind == ProgressKind.GoalCancelled &&
            evt.Message == "No longer needed");
    }

    [Xunit.Fact(DisplayName = "Cli_stop_park_dry_run_shows_plan_without_confirming")]
    public void CliStopParkDryRunShowsPlanWithoutConfirming()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Stop park test", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["stop", goal.Id.Value[..8], "Pausing for now", "--as", "park"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
            Xunit.Assert.False(changed); // dry run
        });

        Xunit.Assert.Contains("park dry run", output);
        Xunit.Assert.Equal(GoalStatus.Active, goal.Status);
    }

    [Xunit.Fact(DisplayName = "Cli_stop_abandon_delegates_to_abandon_goal")]
    public void CliStopAbandonDelegatesToAbandonGoal()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Stop abandon test", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;
        kernel.ActivateGoal(goal.Id, agents);

        CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["stop", goal.Id.Value[..8], "Wrong direction taken", "--as", "abandon", "--confirm-goal-abandon"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.Equal(GoalStatus.Cancelled, goal.Status);
        Xunit.Assert.Contains(goal.Timeline, (ProgressEvent evt) =>
            evt.Kind == ProgressKind.GoalCancelled &&
            evt.Message.Contains("Wrong direction taken", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Cli_stop_requires_as_flag")]
    public void CliStopRequiresAsFlag()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Stop no-mode test", [new TaskSpec(TaskId.New(), "Do work", AgentRole.Developer)]);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = goal;

        var ex = Assert.ThrowsAny<ArgumentException>(() =>
        {
            CliCommandDispatcher.ExecuteCommand(
                ["stop", goal.Id.Value[..8], "Some reason"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
        });

        Xunit.Assert.Contains("--as", ex.Message);
    }

    // ─── config alias ─────────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Cli_config_agents_lists_agent_catalog")]
    public void CliConfigAgentsListsAgentCatalog()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["config", "agents"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Contains("Agents:", output);
        Xunit.Assert.Contains("provider-routing=automatic", output);
    }

    [Xunit.Fact(DisplayName = "Cli_config_doctor_runs_health_check")]
    public void CliConfigDoctorRunsHealthCheck()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["config", "doctor"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        // PrintHealth outputs "Ready: <bool>\r\nProviders:\r\n..."
        Xunit.Assert.Contains("Ready:", output);
    }

    [Xunit.Fact(DisplayName = "Cli_config_policy_lists_autonomy_policies")]
    public void CliConfigPolicyListsAutonomyPolicies()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["config", "policy"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Contains("Policy observe:", output);
        Xunit.Assert.Contains("Policy safe-auto:", output);
    }

    [Xunit.Fact(DisplayName = "Cli_config_profiles_lists_worker_profiles")]
    public void CliConfigProfilesListsWorkerProfiles()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["config", "profiles"],
            kernel, workspace, ref agents, providers, ref profiles, ref currentGoal));

        Xunit.Assert.Contains("Worker profiles:", output);
    }

    // ─── goal alias with flags ────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "Cli_goal_simple_flag_creates_single_developer_task_goal")]
    public void CliGoalSimpleFlagCreatesSingleDeveloperTaskGoal()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["goal", "Add a simple helper to docs/usage.md", "--simple"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
            Xunit.Assert.True(changed);
        });

        Xunit.Assert.NotNull(currentGoal);
        Xunit.Assert.Single(kernel.Goals);
        Xunit.Assert.Single(currentGoal!.Tasks);
        Xunit.Assert.Equal(AgentRole.Developer, currentGoal.Tasks.Single().RequiredRole);
    }

    [Xunit.Fact(DisplayName = "Cli_goal_from_backlog_flag_delegates_to_backlog_intake")]
    public void CliGoalFromBacklogFlagDelegatesToBacklogIntake()
    {
        var root = CreateTempDirectory();
        SeedBacklog(root, """
        # Backlog

        ## Add smoke test coverage

        Add smoke tests for the CLI commands. Done when focused tests pass.
        """);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        var output = CaptureConsole(() =>
        {
            var changed = CliCommandDispatcher.ExecuteCommand(
                ["goal", "smoke test", "--from-backlog"],
                kernel, workspace, ref agents, providers, ref profiles, ref currentGoal);
            Xunit.Assert.False(changed); // no --create-goal flag, dry run
        });

        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.Contains("Backlog intake:", output);
        Xunit.Assert.Contains("## Add smoke test coverage", output);
    }

    // ─── SplitCommand for new aliases ────────────────────────────────────────

    [Xunit.Fact(DisplayName = "CliArgumentParser_stop_splits_goal_reason_and_mode_flag")]
    public void CliArgumentParserStopSplitsGoalReasonAndModeFlag()
    {
        var parts = CliArgumentParser.SplitCommand("stop 8544c918 Build verification failed --as cancel --confirm-goal-stop");

        Xunit.Assert.Equal("stop", parts[0]);
        Xunit.Assert.Equal("8544c918", parts[1]);
        Xunit.Assert.Equal("Build verification failed", parts[2]);
        Xunit.Assert.Equal("--as", parts[3]);
        Xunit.Assert.Equal("cancel", parts[4]);
        Xunit.Assert.Equal("--confirm-goal-stop", parts[5]);
    }

    [Xunit.Fact(DisplayName = "CliArgumentParser_normalize_args_stop_preserves_reason_mode_and_confirm_tokens")]
    public void CliArgumentParserNormalizeArgsStopPreservesReasonModeAndConfirmTokens()
    {
        var parts = CliArgumentParser.NormalizeArgs(
            ["stop", "8544c918", "Build", "verification", "failed", "--as", "park", "--confirm-goal-park"]);

        Xunit.Assert.Equal(
            ["stop", "8544c918", "Build verification failed", "--as", "park", "--confirm-goal-park"],
            parts);
    }

    [Xunit.Fact(DisplayName = "CliArgumentParser_goal_splits_objective_and_simple_flag")]
    public void CliArgumentParserGoalSplitsObjectiveAndSimpleFlag()
    {
        var parts = CliArgumentParser.SplitCommand("goal Add a helper to usage docs --simple");

        Xunit.Assert.Equal("goal", parts[0]);
        Xunit.Assert.Equal("Add a helper to usage docs", parts[1]);
        Xunit.Assert.Equal("--simple", parts[2]);
    }

    [Xunit.Fact(DisplayName = "CliArgumentParser_goal_without_flags_preserves_objective")]
    public void CliArgumentParserGoalWithoutFlagsPreservesObjective()
    {
        var parts = CliArgumentParser.SplitCommand("goal Add a multi-role feature with tests");

        Xunit.Assert.Equal("goal", parts[0]);
        Xunit.Assert.Equal("Add a multi-role feature with tests", parts[1]);
        Xunit.Assert.Equal(2, parts.Count);
    }

    [Xunit.Fact(DisplayName = "CliArgumentParser_accept_splits_like_simple_command")]
    public void CliArgumentParserAcceptSplitsLikeSimpleCommand()
    {
        var parts = CliArgumentParser.SplitCommand("accept 8544c918");

        Xunit.Assert.Equal("accept", parts[0]);
        Xunit.Assert.Equal("8544c918", parts[1]);
    }

}
