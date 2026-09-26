using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerCommandsConductLoopHydration : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_excludes_terminal_goals_from_hydration")]
    public void PersistentRunnerConductLoopExcludesTerminalGoalsFromHydration()
    {
        var root = CreateTempDirectory();
        var kernel = new AgentOrchestratorKernel();
        var terminalGoalIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var completed = kernel.CreateGoal($"Completed audit goal {i}", [new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)]);
            kernel.ActivateGoal(completed.Id, AgentCatalog.Default().Agents);
            kernel.RecordTaskVerification(completed.Id, completed.Tasks.Single().Id,
                new TaskVerificationRecord("manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            kernel = WithGoalStatus(kernel, completed.Id, GoalStatus.Completed);
            terminalGoalIds.Add(completed.Id.Value);
        }

        var cleanedUp = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Cleaned-up audit goal");
        var cancelled = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Cancelled audit goal");
        var superseded = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Superseded audit goal");
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active conductor goal");
        var failed = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Failed conductor goal");
        kernel.ReportTaskProgress(failed.Id, failed.Tasks.Single().Id, WorkTaskStatus.Failed, "Still needs conductor/operator attention");
        kernel = WithGoalStatus(kernel, cancelled.Id, GoalStatus.Cancelled);
        kernel = WithGoalStatus(kernel, superseded.Id, GoalStatus.Superseded);
        var repository = new InMemoryTransactionalStateRepository(kernel);
        repository.CleanedUpGoalIds.Add(cleanedUp.Id.Value);
        terminalGoalIds.AddRange([cleanedUp.Id.Value, cancelled.Id.Value, superseded.Id.Value]);

        var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository);

        Xunit.Assert.Equal(0, repository.LoadCount);
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.All(terminalGoalIds, id => Xunit.Assert.DoesNotContain(id, repository.LoadedGoalIds));
        Xunit.Assert.Contains(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Contains(failed.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.All(terminalGoalIds, id => Xunit.Assert.DoesNotContain(loaded.Goals, goal => goal.Id.Value == id));
        Xunit.Assert.Contains(loaded.Goals, goal => goal.Id == active.Id);
        Xunit.Assert.Contains(loaded.Goals, goal => goal.Id == failed.Id);
        Xunit.Assert.All(
            terminalGoalIds,
            id => Xunit.Assert.False(loaded.IsKnownCompletedDependencyGoal(new GoalId(id))));
        var expectedLoadedIds = new[] { active.Id.Value, failed.Id.Value }
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Xunit.Assert.Equal(
            expectedLoadedIds,
            repository.LoadGoalBatches.Single().OrderBy(id => id, StringComparer.Ordinal).ToArray());
        var terminalSnapshot = repository.LoadGoalAsync(new GoalId(terminalGoalIds[0])).GetAwaiter().GetResult();
        Xunit.Assert.NotNull(terminalSnapshot);
        Xunit.Assert.Equal(terminalGoalIds[0], terminalSnapshot.Id);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_keeps_parked_goals_metadata_only")]
    public void PersistentRunnerConductLoopKeepsParkedGoalsMetadataOnly()
    {
        const int parkedGoalCount = 53;
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Active conductor goal");
        var parkedGoalIds = new List<GoalId>();
        var largePayload = new string('x', 8192);
        for (var i = 0; i < parkedGoalCount; i++)
        {
            var parked = kernel.CreateGoal(
                $"Parked memory fixture {i}: {largePayload}",
                [new TaskSpec(TaskId.New(), $"Preserve parked metadata {i}", AgentRole.Researcher)]);
            kernel.ActivateGoal(parked.Id, AgentCatalog.Default().Agents);
            kernel.ParkGoal(parked.Id, "memory fixture");
            parkedGoalIds.Add(parked.Id);
        }

        var repository = new InMemoryTransactionalStateRepository(kernel);
        var preFixHydratedIds = kernel.Goals
            .Where(goal => goal.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Superseded))
            .Select(goal => goal.Id)
            .ToArray();
        var preFixBytes = repository.EstimateGoalSnapshotJsonBytes(preFixHydratedIds);
        var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;

        var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository);

        var workingSetAfter = Process.GetCurrentProcess().WorkingSet64;
        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.Contains(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.DoesNotContain(id.Value, repository.LoadedGoalIds));
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.DoesNotContain(loaded.Goals, goal => goal.Id == id));
        Xunit.Assert.All(parkedGoalIds, id =>
        {
            Xunit.Assert.True(loaded.TryGetKnownDependencyGoalStatus(id, out var status));
            Xunit.Assert.Equal(GoalStatus.Parked.ToString(), status);
        });
        var reduction = preFixBytes == 0
            ? 0
            : (double)(preFixBytes - repository.LoadedGoalSnapshotJsonBytes) / preFixBytes;
        var artifactPath = WriteParkedHydrationMeasurementArtifact(
            parkedGoalCount,
            preFixBytes,
            repository.LoadedGoalSnapshotJsonBytes,
            reduction,
            workingSetBefore,
            workingSetAfter);
        Console.WriteLine($"parked hydration measurement artifact: {artifactPath}");
        Xunit.Assert.True(File.Exists(artifactPath));
        Xunit.Assert.True(
            reduction >= 0.70,
            $"parked_count={parkedGoalCount}; pre_fix_goal_json_bytes={preFixBytes}; after_goal_json_bytes={repository.LoadedGoalSnapshotJsonBytes}; reduction={reduction:P1}; working_set_before={workingSetBefore}; working_set_after={workingSetAfter}");
    }

    private static string WriteParkedHydrationMeasurementArtifact(
        int parkedGoalCount,
        long preFixGoalJsonBytes,
        long afterGoalJsonBytes,
        double reduction,
        long workingSetBefore,
        long workingSetAfter)
    {
        var artifactPath = Path.Combine(Path.GetTempPath(), "mcg-conduct-loop-parked-hydration-measurement-latest.json");
        File.WriteAllText(
            artifactPath,
            JsonSerializer.Serialize(
                new
                {
                    fixture = "conduct-loop-parked-hydration",
                    parked_goal_count = parkedGoalCount,
                    safety_net_sweep_cadence_ticks = CliPersistentStateRunner.ParkedGoalSafetyNetSweepCadenceTicks,
                    pre_fix_goal_json_bytes = preFixGoalJsonBytes,
                    after_goal_json_bytes = afterGoalJsonBytes,
                    reduction,
                    working_set_before = workingSetBefore,
                    working_set_after = workingSetAfter
                },
                new JsonSerializerOptions { WriteIndented = true }));

        return artifactPath;
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_hydrates_unparked_goal_on_next_kernel_load")]
    public void PersistentRunnerConductLoopHydratesUnparkedGoalOnNextKernelLoad()
    {
        const int parkedGoalCount = 100;
        var kernel = new AgentOrchestratorKernel();
        var parkedGoalIds = new List<GoalId>();
        for (var i = 0; i < parkedGoalCount; i++)
        {
            var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                $"Parked fixture {i}");
            kernel.ParkGoal(parked.Id, "operator deferred");
            parkedGoalIds.Add(parked.Id);
        }

        var initiallyParkedRepository = new InMemoryTransactionalStateRepository(kernel);
        var initiallyLoaded = CliPersistentStateRunner.LoadConductLoopKernel(initiallyParkedRepository);
        Xunit.Assert.Empty(initiallyLoaded.Goals);
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.DoesNotContain(id.Value, initiallyParkedRepository.LoadedGoalIds));

        var unparkedGoalId = parkedGoalIds[42];
        kernel = WithGoalStatus(kernel, unparkedGoalId, GoalStatus.Active);
        var nextTickRepository = new InMemoryTransactionalStateRepository(kernel);
        var nextTickLoaded = CliPersistentStateRunner.LoadConductLoopKernel(nextTickRepository);

        Xunit.Assert.Contains(unparkedGoalId.Value, nextTickRepository.LoadedGoalIds);
        Xunit.Assert.Contains(nextTickLoaded.Goals, goal => goal.Id == unparkedGoalId);
        Xunit.Assert.All(
            parkedGoalIds.Where(id => id != unparkedGoalId),
            id => Xunit.Assert.DoesNotContain(id.Value, nextTickRepository.LoadedGoalIds));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_parked_safety_net_sweeps_every_four_ticks")]
    public void PersistentRunnerConductLoopParkedSafetyNetSweepsEveryFourTicks()
    {
        const int parkedGoalCount = 100;
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Active conductor goal");
        var parkedGoalIds = new List<GoalId>();
        for (var i = 0; i < parkedGoalCount; i++)
        {
            var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                $"Parked safety-net fixture {i}");
            kernel.ParkGoal(parked.Id, "operator deferred");
            parkedGoalIds.Add(parked.Id);
        }

        Xunit.Assert.Equal(4, CliPersistentStateRunner.ParkedGoalSafetyNetSweepCadenceTicks);
        Xunit.Assert.False(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(1));
        Xunit.Assert.False(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(2));
        Xunit.Assert.False(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(3));
        Xunit.Assert.True(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(4));
        Xunit.Assert.False(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(5));
        Xunit.Assert.True(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(8));

        var repository = new InMemoryTransactionalStateRepository(kernel);
        var sweepKernel = CliPersistentStateRunner.LoadConductLoopParkedGoalSafetyNetKernel(repository);

        Xunit.Assert.Equal(1, repository.LoadGoalsCount);
        Xunit.Assert.DoesNotContain(active.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.Contains(id.Value, repository.LoadedGoalIds));
        Xunit.Assert.Equal(parkedGoalCount, sweepKernel.Goals.Count);
        Xunit.Assert.All(sweepKernel.Goals, goal => Xunit.Assert.Equal(GoalStatus.Parked, goal.Status));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_safety_net_promotes_resolved_parked_wait_within_four_ticks")]
    public void PersistentRunnerConductLoopSafetyNetPromotesResolvedParkedWaitWithinFourTicks()
    {
        const int parkedGoalCount = 101;
        var kernel = new AgentOrchestratorKernel();
        var parkedGoalIds = new List<GoalId>();
        for (var i = 0; i < parkedGoalCount; i++)
        {
            var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                $"Parked wait fixture {i}");
            kernel.ParkGoal(parked.Id, "operator deferred");
            parkedGoalIds.Add(parked.Id);
        }

        var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Parked answered wait fixture");
        var targetTask = target.Tasks.Single();
        kernel.RecordTaskDispatch(target.Id, targetTask.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", DateTimeOffset.UtcNow));
        var wait = kernel.RequestHumanInput(target.Id, targetTask.Id, "Which option?");
        kernel.SubmitHumanInput(wait.Id, "Use option A.");
        var answeredSnapshot = kernel.ExportSnapshot();
        var answeredGoal = answeredSnapshot.Goals.Single(goal => goal.Id == target.Id.Value);
        var answeredAt = answeredGoal.Timeline
            .Last(evt => evt.Kind == ProgressKind.HumanInputReceived)
            .OccurredAt;
        var parkedAfterAnsweredSnapshot = answeredSnapshot with
        {
            Goals = answeredSnapshot.Goals
                .Select(goal => goal.Id == target.Id.Value
                    ? goal with
                    {
                        Status = GoalStatus.Parked,
                        Timeline = goal.Timeline
                            .Append(new ProgressEventSnapshot(
                                target.Id.Value,
                                null,
                                ProgressKind.GoalPolicyDecision,
                                "Goal parked: waiting for operator answer",
                                answeredAt.AddTicks(-1)))
                            .ToArray()
                    }
                    : goal)
                .ToArray()
        };
        kernel = AgentOrchestratorKernel.FromSnapshot(parkedAfterAnsweredSnapshot);

        var normalTickRepository = new InMemoryTransactionalStateRepository(kernel);
        for (var tick = 1; tick < CliPersistentStateRunner.ParkedGoalSafetyNetSweepCadenceTicks; tick++)
        {
            var normalTickKernel = CliPersistentStateRunner.LoadConductLoopKernel(normalTickRepository);
            Xunit.Assert.DoesNotContain(target.Id, normalTickKernel.Goals.Select(goal => goal.Id));
            Xunit.Assert.False(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(tick));
        }

        var safetyNetRepository = new InMemoryTransactionalStateRepository(kernel);
        var currentTickKernel = AgentOrchestratorKernel.FromSnapshot(parkedAfterAnsweredSnapshot with
        {
            Goals = [parkedAfterAnsweredSnapshot.Goals.Single(goal => goal.Id == target.Id.Value)],
            HumanInputRequests = parkedAfterAnsweredSnapshot.HumanInputRequests
                .Where(request => request.GoalId == target.Id.Value)
                .ToArray()
        });
        var safetyNetKernel = CliPersistentStateRunner.LoadConductLoopParkedGoalSafetyNetKernel(safetyNetRepository);
        var promoted = safetyNetKernel.RefreshParkedGoalsWithResolvedHumanWaits();
        var promotedSnapshots = safetyNetKernel.ExportSnapshot().Goals
            .Where(goal => goal.Status != GoalStatus.Parked)
            .ToArray();
        safetyNetRepository.SaveGoalSnapshotsAsync(promotedSnapshots).GetAwaiter().GetResult();
        var nextPrewalkKernel = CliPersistentStateRunner.LoadConductLoopKernel(safetyNetRepository);

        Xunit.Assert.Equal(4, CliPersistentStateRunner.ParkedGoalSafetyNetSweepCadenceTicks);
        Xunit.Assert.True(CliPersistentStateRunner.IsParkedGoalSafetyNetSweepTick(4));
        Xunit.Assert.Equal(1, promoted);
        Xunit.Assert.Contains(target.Id.Value, safetyNetRepository.LoadedGoalIds);
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.Contains(id.Value, safetyNetRepository.LoadedGoalIds));
        Xunit.Assert.Contains(currentTickKernel.Goals, goal => goal.Id == target.Id && goal.Status == GoalStatus.Parked);
        Xunit.Assert.Contains(nextPrewalkKernel.Goals, goal => goal.Id == target.Id && goal.Status == GoalStatus.Active);
        Xunit.Assert.DoesNotContain(nextPrewalkKernel.Goals, goal => parkedGoalIds.Contains(goal.Id));
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_targeted_query_promotes_resolved_parked_wait_on_next_load")]
    public void PersistentRunnerConductLoopTargetedQueryPromotesResolvedParkedWaitOnNextLoad()
    {
        const int parkedGoalCount = 100;
        var kernel = new AgentOrchestratorKernel();
        var parkedGoalIds = new List<GoalId>();
        for (var i = 0; i < parkedGoalCount; i++)
        {
            var parked = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                AgentCatalog.Default().Agents,
                $"Parked targeted fixture {i}");
            kernel.ParkGoal(parked.Id, "operator deferred");
            parkedGoalIds.Add(parked.Id);
        }

        var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Parked targeted answered wait fixture");
        var targetTask = target.Tasks.Single();
        kernel.RecordTaskDispatch(target.Id, targetTask.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", DateTimeOffset.UtcNow));
        var wait = kernel.RequestHumanInput(target.Id, targetTask.Id, "Which option?");
        kernel.SubmitHumanInput(wait.Id, "Use option A.");
        var answeredSnapshot = kernel.ExportSnapshot();
        var answeredAt = answeredSnapshot.Goals
            .Single(goal => goal.Id == target.Id.Value)
            .Timeline
            .Last(evt => evt.Kind == ProgressKind.HumanInputReceived)
            .OccurredAt;
        kernel = AgentOrchestratorKernel.FromSnapshot(answeredSnapshot with
        {
            Goals = answeredSnapshot.Goals
                .Select(goal => goal.Id == target.Id.Value
                    ? goal with
                    {
                        Status = GoalStatus.Parked,
                        Timeline = goal.Timeline
                            .Append(new ProgressEventSnapshot(
                                target.Id.Value,
                                null,
                                ProgressKind.GoalPolicyDecision,
                                "Goal parked: waiting for operator answer",
                                answeredAt.AddTicks(-1)))
                            .ToArray()
                    }
                    : goal)
                .ToArray()
        });

        var repository = new InMemoryTransactionalStateRepository(kernel);
        var currentTickKernel = AgentOrchestratorKernel.FromSnapshot(answeredSnapshot with
        {
            Goals = answeredSnapshot.Goals
                .Where(goal => goal.Id == target.Id.Value)
                .Select(goal => goal with
                {
                    Status = GoalStatus.Parked,
                    Timeline = goal.Timeline
                        .Append(new ProgressEventSnapshot(
                            target.Id.Value,
                            null,
                            ProgressKind.GoalPolicyDecision,
                            "Goal parked: waiting for operator answer",
                            answeredAt.AddTicks(-1)))
                        .ToArray()
                })
                .ToArray(),
            HumanInputRequests = answeredSnapshot.HumanInputRequests
                .Where(request => request.GoalId == target.Id.Value)
                .ToArray()
        });
        var targetedKernel = CliPersistentStateRunner.LoadConductLoopResolvedParkedHumanWaitKernel(repository);
        var promoted = targetedKernel.RefreshParkedGoalsWithResolvedHumanWaits();
        var promotedSnapshots = targetedKernel.ExportSnapshot().Goals
            .Where(goal => goal.Status != GoalStatus.Parked)
            .ToArray();
        repository.SaveGoalSnapshotsAsync(promotedSnapshots).GetAwaiter().GetResult();
        var nextTickKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);

        Xunit.Assert.Equal(1, repository.CompletedHumanInputQueryCount);
        Xunit.Assert.Contains(target.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.All(parkedGoalIds, id => Xunit.Assert.DoesNotContain(id.Value, targetedKernel.Goals.Select(goal => goal.Id.Value)));
        Xunit.Assert.Equal(1, promoted);
        Xunit.Assert.Contains(currentTickKernel.Goals, goal => goal.Id == target.Id && goal.Status == GoalStatus.Parked);
        Xunit.Assert.Contains(nextTickKernel.Goals, goal => goal.Id == target.Id && goal.Status == GoalStatus.Active);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_unpark_persist_failure_is_surfaced")]
    public void PersistentRunnerConductLoopUnparkPersistFailureIsSurfaced()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Parked targeted persist failure fixture");
        var targetTask = target.Tasks.Single();
        kernel.RecordTaskDispatch(target.Id, targetTask.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", DateTimeOffset.UtcNow));
        var wait = kernel.RequestHumanInput(target.Id, targetTask.Id, "Which option?");
        kernel.SubmitHumanInput(wait.Id, "Use option A.");
        var answeredSnapshot = kernel.ExportSnapshot();
        var answeredAt = answeredSnapshot.Goals
            .Single(goal => goal.Id == target.Id.Value)
            .Timeline
            .Last(evt => evt.Kind == ProgressKind.HumanInputReceived)
            .OccurredAt;
        kernel = AgentOrchestratorKernel.FromSnapshot(answeredSnapshot with
        {
            Goals = answeredSnapshot.Goals
                .Select(goal => goal.Id == target.Id.Value
                    ? goal with
                    {
                        Status = GoalStatus.Parked,
                        Timeline = goal.Timeline
                            .Append(new ProgressEventSnapshot(
                                target.Id.Value,
                                null,
                                ProgressKind.GoalPolicyDecision,
                                "Goal parked: waiting for operator answer",
                                answeredAt.AddTicks(-1)))
                            .ToArray()
                    }
                    : goal)
                .ToArray()
        });
        var repository = new InMemoryTransactionalStateRepository(kernel);
        var loopKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        var persistAttempts = 0;
        var context = new CliExecutionContext(
            loopKernel,
            workspace,
            providers,
            agents,
            profiles,
            currentGoal: null,
            reloadKernel: () => CliPersistentStateRunner.LoadConductLoopKernel(repository),
            persistKernel: _ => { },
            persistGoalKernel: (_, changedGoalIds) =>
            {
                if (changedGoalIds.Contains(target.Id))
                {
                    persistAttempts++;
                    throw new InvalidOperationException("resolved parked promotion write failed");
                }
            },
            reloadResolvedParkedHumanWaitKernel: () => CliPersistentStateRunner.LoadConductLoopResolvedParkedHumanWaitKernel(repository),
            reloadParkedGoalSafetyNetKernel: () => new AgentOrchestratorKernel());

        var output = CaptureConsole(() => CliCommandHandlers.Execute(["conduct", "--loop", "--max-iterations", "1"], context));

        var eventText = File.ReadAllText(workspace.ConductEventsLogPath);
        Xunit.Assert.Equal(1, persistAttempts);
        Xunit.Assert.Contains("PARKED_UNPARK_PERSISTENCE_FAILED", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("resolved parked promotion write failed", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("LOOP_JANITORIAL_FAILED", output, StringComparison.Ordinal);
        Xunit.Assert.Contains("PARKED_UNPARK_PERSISTENCE_FAILED", eventText, StringComparison.Ordinal);
        Xunit.Assert.Contains("loop-janitorial-failure", eventText, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(GoalStatus.Cancelled, false)]
    [Xunit.InlineData(GoalStatus.Cancelled, true)]
    [Xunit.InlineData(GoalStatus.Superseded, false)]
    [Xunit.InlineData(GoalStatus.Failed, false)]
    public async Task ConductLoop_ExternalTerminalStatus_EvictsBeforePrewalk(
        GoalStatus storedStatus,
        bool enqueueIntent)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var completedTask = new TaskSpec(TaskId.New(), "Completed predecessor.", AgentRole.Developer);
            var failedTask = new TaskSpec(TaskId.New(), "Failed successor.", AgentRole.Tester);
            var goal = kernel.CreateGoal("Externally terminalized conductor goal", [completedTask, failedTask]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, completedTask.Id, WorkTaskStatus.Completed, "done");
            kernel.ReportTaskProgress(goal.Id, failedTask.Id, WorkTaskStatus.Failed, "failed");
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var loopKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
            var staleLoopGoal = loopKernel.GetGoal(goal.Id);
            var terminalKernel = WithGoalStatus(kernel, goal.Id, storedStatus);
            await repository.SaveGoalSnapshotsAsync(terminalKernel.ExportSnapshot().Goals);
            var nonTargetedReload = CliPersistentStateRunner.LoadConductLoopKernel(repository);
            Xunit.Assert.Equal(GoalStatus.Active, staleLoopGoal.Status);
            Xunit.Assert.DoesNotContain(nonTargetedReload.Goals, candidate => candidate.Id == goal.Id);

            var intentStore = SqliteOperatorIntentStore.ForDirectories(
                workspace.OrchestratorDirectory,
                workspace.LogDirectory);
            var intent = new OperatorIntentRecord(
                $"terminal-eviction-{storedStatus}",
                $"terminal-eviction-key-{storedStatus}",
                OperatorIntentVerbs.Progress,
                goal.Id.Value,
                failedTask.Id.Value,
                JsonSerializer.Serialize(
                    new ProgressOperatorIntentPayload(WorkTaskStatus.Running, "must not resurrect"),
                    OperatorIntentJson.Options),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            if (enqueueIntent)
                await intentStore.EnqueueAsync(intent);

            var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            var context = new CliExecutionContext(
                loopKernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                currentGoal: null,
                reloadKernel: () => CliPersistentStateRunner.LoadConductLoopKernel(repository),
                persistKernel: _ => { },
                persistGoalKernel: (_, _) => { },
                reloadResolvedParkedHumanWaitKernel: () => new AgentOrchestratorKernel(),
                reloadParkedGoalSafetyNetKernel: () => new AgentOrchestratorKernel(),
                reloadKernelForGoals: trackedIds =>
                    CliPersistentStateRunner.LoadConductLoopKernel(repository, trackedIds))
            {
                EventWriter = eventWriter
            };

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--max-iterations", "1"],
                context));

            var eventsPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
            var events = File.ReadAllLines(eventsPath)
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                var escalations = events.Where(document =>
                    document.RootElement.GetProperty("eventType").GetString() == "GoalEscalated").ToArray();
                Xunit.Assert.All(escalations, escalation => Xunit.Assert.Equal(
                    "Goal is in Failed state; operator action required",
                    escalation.RootElement.GetProperty("reason").GetString()));
                Xunit.Assert.Empty(escalations);
                var eviction = Xunit.Assert.Single(events.Where(document =>
                    document.RootElement.GetProperty("eventType").GetString() == "GoalEvictedFromConductor"));
                Xunit.Assert.Equal(storedStatus.ToString(), eviction.RootElement.GetProperty("status").GetString());
                Xunit.Assert.Equal(
                    enqueueIntent ? "operator-intent-forced-reload" : "scheduled-reload",
                    eviction.RootElement.GetProperty("trigger").GetString());
            }
            finally
            {
                foreach (var document in events)
                    document.Dispose();
            }

            Xunit.Assert.Empty(loopKernel.Goals);
            Xunit.Assert.All(staleLoopGoal.Tasks, task => Xunit.Assert.Null(task.LastDispatch));
            Xunit.Assert.True(loopKernel.TryGetKnownDependencyGoalStatus(goal.Id, out var reconciledStatus));
            Xunit.Assert.Equal(storedStatus.ToString(), reconciledStatus);
            Xunit.Assert.Contains(repository.LoadGoalBatches, batch => batch.Contains(goal.Id.Value));

            if (enqueueIntent)
            {
                var intentOutcome = await intentStore.GetAsync(intent.Id);
                Xunit.Assert.Equal(OperatorIntentStatus.Rejected, intentOutcome!.Status);
                Xunit.Assert.Contains("reasonCode=goal-terminal-evicted", intentOutcome.Outcome, StringComparison.Ordinal);
                Xunit.Assert.Contains($"stored status is {storedStatus}", intentOutcome.Outcome, StringComparison.Ordinal);
            }

            var storedKernel = await repository.LoadAsync();
            var storedGoal = storedKernel.GetGoal(goal.Id);
            var stopPlan = GoalAbandonPlanner.Build(storedKernel, storedGoal, workspace, "operator stop", new());
            Xunit.Assert.Equal(storedStatus, stopPlan.GoalStatus);
            Xunit.Assert.Contains(stopPlan.Steps, step =>
                step.Kind == GoalAbandonStepKind.GoalStatus &&
                step.Detail == $"Goal is already {storedStatus}.");

            IReadOnlyList<AgentDefinition> readerAgents = AgentCatalog.Default().Agents;
            var readerProfiles = WorkerProfileCatalog.Default();
            Goal? readerCurrentGoal = null;
            var goalsOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["goals"],
                repository,
                workspace,
                ref readerAgents,
                new InMemoryModelProviderRegistry([]),
                ref readerProfiles,
                ref readerCurrentGoal));
            var statusOutput = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["status", goal.Id.Value[..8]],
                repository,
                workspace,
                ref readerAgents,
                new InMemoryModelProviderRegistry([]),
                ref readerProfiles,
                ref readerCurrentGoal));
            Xunit.Assert.Contains($"{goal.Id.Value[..8]} {storedStatus}", goalsOutput, StringComparison.Ordinal);
            Xunit.Assert.Contains(storedStatus.ToString(), statusOutput, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public void ConductLoop_LiveGoal_ReloadsAndWalks()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var completedTask = new TaskSpec(TaskId.New(), "Completed predecessor.", AgentRole.Developer);
            var failedTask = new TaskSpec(TaskId.New(), "Failed successor.", AgentRole.Tester);
            var goal = kernel.CreateGoal("Live conductor goal", [completedTask, failedTask]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            kernel.ReportTaskProgress(goal.Id, completedTask.Id, WorkTaskStatus.Completed, "done");
            kernel.ReportTaskProgress(goal.Id, failedTask.Id, WorkTaskStatus.Failed, "failed");
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var loopKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
            var eventWriter = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            var context = new CliExecutionContext(
                loopKernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                currentGoal: null,
                reloadKernel: () => CliPersistentStateRunner.LoadConductLoopKernel(repository),
                persistKernel: _ => { },
                persistGoalKernel: (_, _) => { },
                reloadResolvedParkedHumanWaitKernel: () => new AgentOrchestratorKernel(),
                reloadParkedGoalSafetyNetKernel: () => new AgentOrchestratorKernel(),
                reloadKernelForGoals: trackedIds =>
                    CliPersistentStateRunner.LoadConductLoopKernel(repository, trackedIds))
            {
                EventWriter = eventWriter
            };

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--max-iterations", "1"],
                context));

            var trackedGoal = Xunit.Assert.Single(loopKernel.Goals);
            Xunit.Assert.Equal(goal.Id, trackedGoal.Id);
            Xunit.Assert.Equal(GoalStatus.Active, trackedGoal.Status);
            Xunit.Assert.Contains(repository.LoadGoalBatches, batch => batch.Contains(goal.Id.Value));
            var eventsPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
            var escalationLine = Xunit.Assert.Single(File.ReadLines(eventsPath).Where(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("eventType").GetString() == "GoalEscalated";
            }));
            using var escalation = JsonDocument.Parse(escalationLine);
            Xunit.Assert.Equal("GoalEscalated", escalation.RootElement.GetProperty("eventType").GetString());
            Xunit.Assert.Equal(GoalStatus.Active.ToString(), escalation.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact]
    public async Task ConductLoop_EvictedGoal_LaterIntentKeepsReason()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Cancelled work.", AgentRole.Developer);
            var goal = kernel.CreateGoal("Externally cancelled conductor goal", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var repository = new InMemoryTransactionalStateRepository(kernel);
            var loopKernel = CliPersistentStateRunner.LoadConductLoopKernel(repository);
            await repository.SaveGoalSnapshotsAsync(
                WithGoalStatus(kernel, goal.Id, GoalStatus.Cancelled).ExportSnapshot().Goals);
            var context = new CliExecutionContext(
                loopKernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents,
                WorkerProfileCatalog.Default(),
                currentGoal: null,
                reloadKernel: () => CliPersistentStateRunner.LoadConductLoopKernel(repository),
                persistKernel: _ => { },
                persistGoalKernel: (_, _) => { },
                reloadResolvedParkedHumanWaitKernel: () => new AgentOrchestratorKernel(),
                reloadParkedGoalSafetyNetKernel: () => new AgentOrchestratorKernel(),
                reloadKernelForGoals: trackedIds =>
                    CliPersistentStateRunner.LoadConductLoopKernel(repository, trackedIds));

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--max-iterations", "1"],
                context));
            Xunit.Assert.Empty(loopKernel.Goals);

            var intentStore = SqliteOperatorIntentStore.ForDirectories(
                workspace.OrchestratorDirectory,
                workspace.LogDirectory);
            var intent = new OperatorIntentRecord(
                "later-terminal-eviction",
                "later-terminal-eviction-key",
                OperatorIntentVerbs.Progress,
                goal.Id.Value,
                task.Id.Value,
                JsonSerializer.Serialize(
                    new ProgressOperatorIntentPayload(WorkTaskStatus.Running, "must remain cancelled"),
                    OperatorIntentJson.Options),
                [],
                "operator",
                "test",
                "test",
                DateTimeOffset.UtcNow);
            await intentStore.EnqueueAsync(intent);

            _ = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--max-iterations", "1"],
                context));

            var outcome = await intentStore.GetAsync(intent.Id);
            Xunit.Assert.Equal(OperatorIntentStatus.Rejected, outcome!.Status);
            Xunit.Assert.Contains("reasonCode=goal-terminal-evicted", outcome.Outcome, StringComparison.Ordinal);
            Xunit.Assert.Contains("stored status is Cancelled", outcome.Outcome, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_targeted_query_ignores_synthetic_parked_wait_completion")]
    public void PersistentRunnerConductLoopTargetedQueryIgnoresSyntheticParkedWaitCompletion()
    {
        var kernel = new AgentOrchestratorKernel();
        var target = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Parked synthetic wait fixture");
        var targetTask = target.Tasks.Single();
        kernel.RecordTaskDispatch(target.Id, targetTask.Id, new TaskDispatchRecord("local", "agent run", "C:\\repo", DateTimeOffset.UtcNow));
        kernel.RequestHumanInput(target.Id, targetTask.Id, "Need operator decision.");
        kernel.ParkGoal(target.Id, "waiting for operator answer");

        var repository = new InMemoryTransactionalStateRepository(kernel);
        var targetedKernel = CliPersistentStateRunner.LoadConductLoopResolvedParkedHumanWaitKernel(repository);
        var promoted = targetedKernel.RefreshParkedGoalsWithResolvedHumanWaits();

        Xunit.Assert.Equal(1, repository.CompletedHumanInputQueryCount);
        Xunit.Assert.DoesNotContain(target.Id.Value, repository.LoadedGoalIds);
        Xunit.Assert.Equal(0, promoted);
        Xunit.Assert.Empty(targetedKernel.Goals);
    }

    [Xunit.Fact(DisplayName = "CliPersistentStateRunner_conduct_loop_sweeps_terminal_candidates_loaded_on_demand")]
    public void PersistentRunnerConductLoopSweepsTerminalCandidatesLoadedOnDemand()
    {
        var root = CreateShortAcceptanceRepository();
        GoalId? cleanupGoalId = null;
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement terminal cleanup", AgentRole.Developer);
            var goal = kernel.CreateGoal("Terminal cleanup candidate", [task]);
            cleanupGoalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "Done.");
            kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
                "manual", root, 0, "passed", string.Empty, DateTimeOffset.UtcNow));
            CommitGoalWork(root, goal.Id, "src/terminal-cleanup.txt", "goal work");
            kernel = WithGoalStatus(kernel, goal.Id, GoalStatus.Completed);
            var repository = new InMemoryTransactionalStateRepository(kernel);

            var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
                ["conduct", "--loop", "--max-iterations", "0"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));

            var restored = repository.LoadGoalAsync(goal.Id).GetAwaiter().GetResult()!;
            Xunit.Assert.Equal(0, repository.LoadCount);
            Xunit.Assert.True(repository.LoadGoalsCount >= 2);
            Xunit.Assert.Contains(goal.Id.Value, repository.LoadedGoalIds);
            Xunit.Assert.Equal(GoalStatus.Completed, restored.Status);
            Xunit.Assert.Contains("completed-branch-unmerged", output, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("completed-branch-normalized", output, StringComparison.Ordinal);
        }
        finally
        {
            CleanupAcceptanceRepository(root, cleanupGoalId);
        }
    }
}
