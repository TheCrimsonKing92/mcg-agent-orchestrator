using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class DependencyOrderingRegressionTests
{
    [Xunit.Fact(DisplayName = "Persistent_runner_dependency_backlog_commands_hydrate_goal_state")]
    public async Task PersistentRunnerDependencyBacklogCommandsHydrateGoalState()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var prerequisite = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Active goal prerequisite");
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        await repository.SaveAsync(kernel);
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["backlog-add", "Dependent item", "--depends-on", prerequisite.Id.Value[..8]],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
        var output = CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(
            ["backlog-list"],
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        var dependent = Xunit.Assert.Single(
            await new BacklogStore(workspace.BacklogStorePath).ListAsync(includeAll: true));
        Xunit.Assert.Equal(
            prerequisite.Id.Value,
            Xunit.Assert.Single(dependent.Dependencies).PrerequisiteId);
        Xunit.Assert.Contains(
            $"waiting on active prerequisite goal {prerequisite.Id.Value[..8]}",
            output);
    }

    [Xunit.Fact(DisplayName = "Completed_prior_dispatch_does_not_bypass_dependency_gate")]
    public void CompletedPriorDispatchDoesNotBypassDependencyGate()
    {
        var dependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var first = new TaskSpec(TaskId.New(), "First stage", AgentRole.Developer);
        var second = new TaskSpec(TaskId.New(), "Second stage", AgentRole.Tester);
        var active = kernel.CreateGoal("multi-stage dependent", [first, second]);
        kernel.ActivateGoal(active.Id, AgentCatalog.Default().Agents);
        var startedAt = DateTimeOffset.Parse("2026-07-30T00:00:00Z");
        StartAndCompleteProcess(kernel, active, first, startedAt);
        InjectDependency(kernel, active.Id, dependencyId, GoalStatus.Active.ToString());
        var workerStarts = 0;

        var summary = new ConductorBatchLoop().Run(
            kernel,
            MakeDriver(
                createWorkspace: _ => throw new Xunit.Sdk.XunitException(
                    "dependency-held goal must not create a workspace"),
                dispatchAndStart: _ =>
                {
                    workerStarts++;
                    return DispatchStartOutcome.Started();
                }),
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);

        Xunit.Assert.Equal(0, workerStarts);
        Xunit.Assert.Equal(1, summary.Held);
        Xunit.Assert.Contains(
            kernel.GetGoal(active.Id).Timeline,
            progress => progress.Message.Contains(
                $"waiting on dependency {dependencyId.Value[..8]}",
                StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "Held_dependency_creates_no_workspace_then_dispatches_after_landing")]
    public void HeldDependencyCreatesNoWorkspaceThenDispatchesAfterLanding()
    {
        var dependencyId = GoalId.New();
        var kernel = new AgentOrchestratorKernel();
        var active = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "dependent goal");
        InjectDependency(kernel, active.Id, dependencyId, GoalStatus.Active.ToString());
        var workspaceExists = false;
        var workspaceCreates = 0;
        var workerStarts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: workspaceExists),
            createWorkspace: _ =>
            {
                workspaceExists = true;
                workspaceCreates++;
                return "/tmp/dependency-ordering";
            },
            dispatchAndStart: _ =>
            {
                workerStarts++;
                return DispatchStartOutcome.Started();
            });

        var held = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 1);
        Xunit.Assert.Equal(1, held.Held);
        Xunit.Assert.Equal(0, workspaceCreates);
        Xunit.Assert.Equal(0, workerStarts);

        kernel.MarkKnownCompletedDependencyGoals([dependencyId]);
        var released = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            NoStopPath(),
            maxIterations: 2);

        Xunit.Assert.True(released.Advanced > 0);
        Xunit.Assert.Equal(1, workspaceCreates);
        Xunit.Assert.Equal(1, workerStarts);
    }

    private static void InjectDependency(
        AgentOrchestratorKernel kernel,
        GoalId dependentId,
        GoalId dependencyId,
        string dependencyStatus)
    {
        var snapshot = kernel.ExportSnapshot();
        kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(goal => goal.Id == dependentId.Value
                    ? goal with { DependsOn = [dependencyId.Value] }
                    : goal)
                .ToArray()
        });
        kernel.MarkKnownDependencyGoalStatuses([
            new KeyValuePair<GoalId, string>(dependencyId, dependencyStatus)
        ]);
    }

    private static void StartAndCompleteProcess(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        DateTimeOffset startedAt)
    {
        var root = CreateTempDirectory();
        var command = $"worker {task.RequiredRole}";
        kernel.RecordTaskDispatch(
            goal.Id,
            task.Id,
            new TaskDispatchRecord("test-worker", command, root, startedAt, BaseCommit: "base"));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(
                111,
                command,
                root,
                Path.Combine(root, "out.log"),
                Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"),
                startedAt,
                null,
                null,
                OwnedProcessIds: [111]));
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, "result");
        var process = kernel.GetTask(goal.Id, task.Id).LastProcess!;
        kernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            process with { CompletedAt = startedAt.AddMinutes(1), ExitCode = 0 },
            null);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                "manual",
                root,
                0,
                "ok",
                "",
                startedAt.AddMinutes(1)));
    }

    private static ConductorDriver MakeDriver(
        Func<Goal, GoalLifecycleFacts>? getFacts = null,
        Func<Goal, string>? createWorkspace = null,
        Func<Goal, DispatchStartOutcome>? dispatchAndStart = null) =>
        new(
            getFacts ?? (_ => GoalLifecycleFacts.None),
            () => 0,
            createWorkspace ?? (_ => "/tmp/workspace"),
            dispatchAndStart ?? (_ => DispatchStartOutcome.Started()),
            null,
            null,
            _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            null,
            null,
            null,
            null,
            null,
            _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                "goal/test",
                "OK",
                [],
                null),
            (goal, _) => new LandingResult(
                goal.Id.Value,
                goal.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "Landed"),
            null,
            _ => { },
            _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null),
            (_, _, _) => { },
            _ => null);

    private static string NoStopPath() =>
        Path.Combine(Path.GetTempPath(), $"conduct-stop-{Guid.NewGuid():N}.txt");
}
