using Mcg.AgentOrchestrator.Core;

public sealed class KernelIngestNewGoalsTests
{
    [Xunit.Fact(DisplayName = "IngestNewGoals_adds_unknown_goals_and_leaves_in-flight_goals_untouched")]
    public void IngestNewGoalsAddsUnknownAndPreservesExisting()
    {
        var clock = new FakeClock();

        // Live kernel (e.g. the daemon loop's): goal A is activated and carries in-flight task state.
        var live = new AgentOrchestratorKernel(clock);
        var goalA = live.CreateGoal("Goal A");
        live.ActivateGoal(goalA.Id, DefaultAgents());
        var aTaskCountBefore = live.Goals.First(g => g.Id == goalA.Id).Tasks.Count;
        Xunit.Assert.True(aTaskCountBefore > 0);

        // Store snapshot: a STALE copy of A (never activated, no tasks) plus a brand-new goal B.
        var store = new AgentOrchestratorKernel(clock);
        store.CreateGoal(goalA.Id, "Goal A");
        var goalB = store.CreateGoal("Goal B");
        var snapshot = store.ExportSnapshot();

        var ingested = live.IngestNewGoals(snapshot);

        Xunit.Assert.Equal(1, ingested);
        Xunit.Assert.Contains(live.Goals, g => g.Id == goalB.Id);
        // A is left exactly as the live kernel had it — NOT overwritten by the stale snapshot copy.
        Xunit.Assert.Equal(aTaskCountBefore, live.Goals.First(g => g.Id == goalA.Id).Tasks.Count);
    }

    [Xunit.Fact(DisplayName = "IngestNewGoals_returns_zero_when_no_new_goals")]
    public void IngestNewGoalsReturnsZeroWhenNothingNew()
    {
        var clock = new FakeClock();
        var live = new AgentOrchestratorKernel(clock);
        var goalA = live.CreateGoal("Goal A");

        var store = new AgentOrchestratorKernel(clock);
        store.CreateGoal(goalA.Id, "Goal A");

        Xunit.Assert.Equal(0, live.IngestNewGoals(store.ExportSnapshot()));
        Xunit.Assert.Single(live.Goals);
    }

    [Xunit.Fact(DisplayName = "RefreshTrackedGoals_replaces_existing_goal_from_persisted_snapshot")]
    public void RefreshTrackedGoalsReplacesExistingGoalFromPersistedSnapshot()
    {
        var developerId = TaskId.New().Value;
        var testerId = TaskId.New().Value;
        var live = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-refresh",
                    "Refresh role handoff",
                    GoalStatus.Active,
                    [
                        new TaskSnapshot(developerId, "Implement.", AgentRole.Developer, WorkTaskStatus.Assigned, null, null, null, [], null, null),
                        new TaskSnapshot(testerId, "Test.", AgentRole.Tester, WorkTaskStatus.Assigned, null, null, null, [], null, null)
                    ],
                    [])
            ],
            []));
        var store = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-refresh",
                    "Refresh role handoff",
                    GoalStatus.Active,
                    [
                        new TaskSnapshot(developerId, "Implement.", AgentRole.Developer, WorkTaskStatus.Completed, null, null, null, [], null, null),
                        new TaskSnapshot(testerId, "Test.", AgentRole.Tester, WorkTaskStatus.Assigned, null, null, null, [], null, null)
                    ],
                    [])
            ],
            []));

        var refreshed = live.RefreshTrackedGoals(store.ExportSnapshot());

        var goal = live.Goals.Single();
        Xunit.Assert.Equal(1, refreshed);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, goal.Tasks.Single(task => task.Id.Value == developerId).Status);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, goal.Tasks.Single(task => task.Id.Value == testerId).Status);
    }

    [Xunit.Fact(DisplayName = "EvictTerminalGoalAggregates_removes_full_terminal_goal_from_active_dictionary")]
    public void EvictTerminalGoalAggregatesRemovesFullTerminalGoalFromActiveDictionary()
    {
        var goalId = GoalId.New();
        var taskId = TaskId.New();
        var createdAt = DateTimeOffset.Parse("2026-07-22T23:59:00Z");
        var terminatedAt = DateTimeOffset.Parse("2026-07-23T00:00:00Z");
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    goalId.Value,
                    "Terminal title\nFULL_OBJECTIVE_SENTINEL",
                    GoalStatus.Completed,
                    [
                        new TaskSnapshot(
                            taskId.Value,
                            "TASK_SENTINEL",
                            AgentRole.Developer,
                            WorkTaskStatus.Completed,
                            null,
                            null,
                            null,
                            [],
                            new TaskDispatchSnapshot(
                                "worker",
                                "cmd",
                                "C:\\repo",
                                createdAt,
                                ResultCommit: "result-sha"),
                            null)
                    ],
                    [
                        new ProgressEventSnapshot(goalId.Value, null, ProgressKind.GoalCreated, "CREATED_SENTINEL", createdAt),
                        new ProgressEventSnapshot(goalId.Value, taskId.Value, ProgressKind.TaskCompleted, "TIMELINE_SENTINEL", terminatedAt)
                    ],
                    RefinedSpec: new RefinedSpecSnapshot(
                        "REFINED_SPEC_SENTINEL",
                        ["ACCEPTANCE_SENTINEL"],
                        VerificationClass.TestVerifiable.ToString(),
                        [],
                        []))
            ],
            [
                new HumanInputRequestSnapshot(
                    "request-sentinel",
                    goalId.Value,
                    taskId.Value,
                    "HUMAN_INPUT_SENTINEL",
                    createdAt)
            ]));

        var evicted = kernel.EvictTerminalGoalAggregates([goalId]);

        Xunit.Assert.Equal(1, evicted);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.Empty(kernel.HumanInputRequests);
        Xunit.Assert.Empty(kernel.ExportSnapshot().Goals);
        Xunit.Assert.True(kernel.IsKnownCompletedDependencyGoal(goalId));
        Xunit.Assert.True(kernel.TryGetKnownDependencyGoalStatus(goalId, out var status));
        Xunit.Assert.Equal(GoalStatus.Completed.ToString(), status);
    }

    [Xunit.Fact(DisplayName = "IngestNewGoals_tracks_terminal_metadata_without_adding_schedulable_goal")]
    public void IngestNewGoalsTracksTerminalMetadataWithoutAddingSchedulableGoal()
    {
        var terminalId = GoalId.New();
        var snapshot = new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    terminalId.Value,
                    "Terminal title\nFULL_OBJECTIVE_SENTINEL",
                    GoalStatus.Completed,
                    [new TaskSnapshot(TaskId.New().Value, "TASK_SENTINEL", AgentRole.Developer, WorkTaskStatus.Completed, null, null, null, [], null, null)],
                    [new ProgressEventSnapshot(terminalId.Value, null, ProgressKind.TaskCompleted, "TIMELINE_SENTINEL", DateTimeOffset.Parse("2026-07-23T00:00:00Z"))])
            ],
            []);
        var kernel = new AgentOrchestratorKernel();

        var ingested = kernel.IngestNewGoals(snapshot);

        Xunit.Assert.Equal(0, ingested);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.True(kernel.IsKnownCompletedDependencyGoal(terminalId));
        Xunit.Assert.True(kernel.TryGetKnownDependencyGoalStatus(terminalId, out var status));
        Xunit.Assert.Equal(GoalStatus.Completed.ToString(), status);
    }

    [Xunit.Fact(DisplayName = "IngestNewGoals_tracks_failed_terminal_metadata_without_satisfying_dependency")]
    public void IngestNewGoalsTracksFailedTerminalMetadataWithoutSatisfyingDependency()
    {
        var terminalId = GoalId.New();
        var snapshot = new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    terminalId.Value,
                    "Failed terminal title\nFULL_OBJECTIVE_SENTINEL",
                    GoalStatus.Failed,
                    [new TaskSnapshot(TaskId.New().Value, "TASK_SENTINEL", AgentRole.Developer, WorkTaskStatus.Failed, null, null, null, [], null, null)],
                    [new ProgressEventSnapshot(terminalId.Value, null, ProgressKind.TaskFailed, "TIMELINE_SENTINEL", DateTimeOffset.Parse("2026-07-23T00:00:00Z"))])
            ],
            []);
        var kernel = new AgentOrchestratorKernel();

        var ingested = kernel.IngestNewGoals(snapshot);

        Xunit.Assert.Equal(0, ingested);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.False(kernel.IsKnownCompletedDependencyGoal(terminalId));
        Xunit.Assert.True(kernel.TryGetKnownDependencyGoalStatus(terminalId, out var status));
        Xunit.Assert.Equal(GoalStatus.Failed.ToString(), status);
    }

    [Xunit.Fact(DisplayName = "RefreshTrackedGoals_evicts_tracked_goal_that_became_terminal")]
    public void RefreshTrackedGoalsEvictsTrackedGoalThatBecameTerminal()
    {
        var goalId = GoalId.New();
        var taskId = TaskId.New();
        var live = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    goalId.Value,
                    "Runtime terminalizing goal",
                    GoalStatus.Active,
                    [new TaskSnapshot(taskId.Value, "Implement.", AgentRole.Developer, WorkTaskStatus.Running, null, null, null, [], null, null)],
                    [])
            ],
            []));
        var store = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    goalId.Value,
                    "Runtime terminalizing goal\nFULL_OBJECTIVE_SENTINEL",
                    GoalStatus.Completed,
                    [new TaskSnapshot(taskId.Value, "Implement.", AgentRole.Developer, WorkTaskStatus.Completed, null, null, null, [], null, null)],
                    [new ProgressEventSnapshot(goalId.Value, taskId.Value, ProgressKind.TaskCompleted, "TIMELINE_SENTINEL", DateTimeOffset.Parse("2026-07-23T00:00:00Z"))])
            ],
            []));

        var refreshed = live.RefreshTrackedGoals(store.ExportSnapshot());

        Xunit.Assert.Equal(1, refreshed);
        Xunit.Assert.Empty(live.Goals);
        Xunit.Assert.Empty(live.ExportSnapshot().Goals);
        Xunit.Assert.True(live.IsKnownCompletedDependencyGoal(goalId));
        Xunit.Assert.True(live.TryGetKnownDependencyGoalStatus(goalId, out var status));
        Xunit.Assert.Equal(GoalStatus.Completed.ToString(), status);
    }

    [Xunit.Fact(DisplayName = "RefreshTrackedGoals_evicts_failed_terminal_without_satisfying_dependency")]
    public void RefreshTrackedGoalsEvictsFailedTerminalWithoutSatisfyingDependency()
    {
        var goalId = GoalId.New();
        var taskId = TaskId.New();
        var live = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    goalId.Value,
                    "Runtime failing goal",
                    GoalStatus.Active,
                    [new TaskSnapshot(taskId.Value, "Implement.", AgentRole.Developer, WorkTaskStatus.Running, null, null, null, [], null, null)],
                    [])
            ],
            []));
        var store = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    goalId.Value,
                    "Runtime failing goal\nFULL_OBJECTIVE_SENTINEL",
                    GoalStatus.Failed,
                    [new TaskSnapshot(taskId.Value, "Implement.", AgentRole.Developer, WorkTaskStatus.Failed, null, null, null, [], null, null)],
                    [new ProgressEventSnapshot(goalId.Value, taskId.Value, ProgressKind.TaskFailed, "TIMELINE_SENTINEL", DateTimeOffset.Parse("2026-07-23T00:00:00Z"))])
            ],
            []));

        var refreshed = live.RefreshTrackedGoals(store.ExportSnapshot());

        Xunit.Assert.Equal(1, refreshed);
        Xunit.Assert.Empty(live.Goals);
        Xunit.Assert.Empty(live.ExportSnapshot().Goals);
        Xunit.Assert.False(live.IsKnownCompletedDependencyGoal(goalId));
        Xunit.Assert.True(live.TryGetKnownDependencyGoalStatus(goalId, out var status));
        Xunit.Assert.Equal(GoalStatus.Failed.ToString(), status);
    }

    [Xunit.Fact(DisplayName = "FromSnapshot_metadata_only_terminal_stub_carries_required_metadata")]
    public void FromSnapshotMetadataOnlyTerminalStubCarriesRequiredMetadata()
    {
        var goalId = GoalId.New();
        var createdAt = DateTimeOffset.Parse("2026-07-22T23:59:00Z");
        var terminatedAt = DateTimeOffset.Parse("2026-07-23T00:00:00Z");
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    goalId.Value,
                    new string('x', 300) + "\nFULL_OBJECTIVE_SENTINEL",
                    GoalStatus.Completed,
                    [],
                    [],
                    IsMetadataOnly: true,
                    ResultCommit: " result-sha ",
                    CreatedAt: createdAt,
                    TerminatedAt: terminatedAt)
            ],
            []));

        var goal = kernel.Goals.Single();

        Xunit.Assert.True(goal.IsMetadataOnly);
        Xunit.Assert.Equal(240, goal.Objective.Length);
        Xunit.Assert.DoesNotContain("FULL_OBJECTIVE_SENTINEL", goal.Objective, StringComparison.Ordinal);
        Xunit.Assert.Equal("result-sha", goal.MetadataResultCommit);
        Xunit.Assert.Equal(createdAt, goal.MetadataCreatedAt);
        Xunit.Assert.Equal(terminatedAt, goal.MetadataTerminatedAt);
    }
}
