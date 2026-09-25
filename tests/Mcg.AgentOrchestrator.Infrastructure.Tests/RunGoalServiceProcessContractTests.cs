using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// The one real-process contract for RunGoalService: two echo tasks driven to completion through the
// real advance step and the local worker profile. RunGoalServiceTests owns every other control-flow
// fact through an injected in-process advance step, so this class stays at exactly one fact.
//
// The run is bounded by a cancellation failsafe rather than an xunit Timeout: a hang then reports the
// named failsafe instead of an opaque framework abort, and the worker exit files remain the only
// synchronisation signal (no wall-clock pacing).
[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class RunGoalServiceProcessContractTests
{
    private static readonly TimeSpan RunFailsafe = TimeSpan.FromMinutes(5);

    private static string CreateTempDirectory()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        _ = StateDbMigrations.EnsureUpToDate(OrchestratorWorkspace.ForDirectory(root).SqliteStatePath);
        return root;
    }

    private static RunGoalService.SleepFunc WaitForCurrentExitFile(Goal goal, string logDirectory)
    {
        return async (_, ct) =>
        {
            Directory.CreateDirectory(logDirectory);
            while (true)
            {
                var trackedExitPaths = goal.Tasks
                    .Select(task => task.LastProcess)
                    .Where(process => process is { IsRunning: true })
                    .Select(process => Path.GetFullPath(process!.ExitCodePath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (trackedExitPaths.Any(File.Exists))
                {
                    return;
                }

                var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var watcher = new FileSystemWatcher(logDirectory, "*.exit.txt")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler signal = (_, _) => changed.TrySetResult();
                RenamedEventHandler signalRename = (_, _) => changed.TrySetResult();
                ErrorEventHandler signalError = (_, args) => changed.TrySetException(args.GetException());
                watcher.Created += signal;
                watcher.Changed += signal;
                watcher.Renamed += signalRename;
                watcher.Error += signalError;

                // Exit artifacts are durable level signals until RunGoalService refreshes the owning task.
                // Re-check after subscribing to close the create-before-subscribe race without polling.
                if (trackedExitPaths.Any(File.Exists))
                {
                    return;
                }

                await changed.Task.WaitAsync(ct);
            }
        };
    }

    private static AgentDefinition EchoAgent() => new AgentDefinition(
        new AgentId("echo-planner"),
        "Echo Planner",
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local"));

    private static string PlannerSuccessCommand(string marker)
    {
        var plan = WorkerDispatchTestSupport.ResearcherContractFixture() + Environment.NewLine +
            WorkerDispatchTestSupport.PlannerContractPlanFixture().Replace(
            "`seed.txt`, ",
            string.Empty,
            StringComparison.Ordinal);
        var encodedPlan = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plan));
        return $"Write-Output ([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPlan}'))); Write-Output {marker}";
    }

    private static WorkerProfileCatalog EchoProfiles() => new WorkerProfileCatalog(
    [
        new WorkerProfile("local", $"Start-Sleep -Milliseconds 100; {PlannerSuccessCommand("{subscriptionModelName}")}")
    ]);

    private static Goal CreateRefinedGoal(AgentOrchestratorKernel kernel, string objective, IReadOnlyList<TaskSpec> tasks)
    {
        var goal = kernel.CreateGoal(objective, tasks);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            objective,
            ["RunGoalService fixture goal is already refined."],
            VerificationClass.TestVerifiable,
            [],
            []));
        return goal;
    }

    [Xunit.Fact(DisplayName = "RunGoalService_real_advance_step_completes_two_echo_tasks_sequentially")]
    public async Task RunGoalServiceRealAdvanceStepCompletesTwoEchoTasksSequentially()
    {
        var root = CreateTempDirectory();
        SeedLocalSkillCatalog(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task1 = new TaskSpec(TaskId.New(), "First echo task", AgentRole.Planner);
        var task2 = new TaskSpec(TaskId.New(), "Second echo task", AgentRole.Planner);
        var goal = CreateRefinedGoal(kernel, "Sequential echo run", [task1, task2]);
        var agent = EchoAgent();

        using var failsafe = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        failsafe.CancelAfter(RunFailsafe);

        RunGoalService.RunGoalResult result;
        try
        {
            result = await RunGoalService.RunAsync(
                kernel,
                [agent],
                EchoProfiles(),
                workspace,
                goal,
                allowLargePaidSubscriptionStart: false,
                pollInterval: TimeSpan.FromMilliseconds(50),
                sleep: WaitForCurrentExitFile(goal, workspace.LogDirectory),
                cancellationToken: failsafe.Token);
        }
        catch (OperationCanceledException) when (failsafe.IsCancellationRequested)
        {
            throw new Xunit.Sdk.XunitException(
                $"The real-process run-goal contract did not finish within the {RunFailsafe.TotalMinutes:0}-minute failsafe; " +
                $"task1={task1.Status}, task2={task2.Status}.");
        }

        Xunit.Assert.False(
            failsafe.IsCancellationRequested,
            $"The real-process run-goal contract did not finish within the {RunFailsafe.TotalMinutes:0}-minute failsafe; " +
            $"task1={task1.Status}, task2={task2.Status}, stopReason={result.StopReason}.");
        Assert.True(result.Executed);
        Assert.Equal(2, result.CompletedTasks.Count);
        Assert.True(result.CompletedTasks.All(t => t.Succeeded));
        Assert.True(result.ContinueAfter is null);
        Assert.True(result.StopEvidence is null);
        Assert.Equal(WorkTaskStatus.Completed, task1.Status);
        Assert.Equal(WorkTaskStatus.Completed, task2.Status);
    }
}
