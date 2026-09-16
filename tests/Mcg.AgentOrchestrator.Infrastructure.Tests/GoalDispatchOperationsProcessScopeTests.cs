using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Negative control for the scoped process probes. Dispatch readiness used to consult two mutable
/// statics, so any test or concurrent operation could change what every other one observed. These
/// tests interleave two instances over the same recorded process id and require each to keep its own
/// answer, in both construction orders.
/// </summary>
public sealed class GoalDispatchOperationsProcessScopeTests
{
    private const int ParentProcessId = 28516;
    private const int ChildProcessId = 28517;

    [Xunit.Fact(DisplayName = "Interleaved_GoalDispatchOperations_instances_keep_their_own_process_liveness")]
    public void InterleavedGoalDispatchOperationsInstancesKeepTheirOwnProcessLiveness()
    {
        var live = CreateWorkspaceWithRecordedProcess();
        var dead = CreateWorkspaceWithRecordedProcess();

        var observesLive = CreateOperations(seesChildAsLive: true);
        var observesDead = CreateOperations(seesChildAsLive: false);

        // A-B-A-B over the same recorded process id. If the probes were shared, the second
        // construction would decide both answers and one of these assertions would flip.
        RunReadyBatch(observesLive, live);
        RunReadyBatch(observesDead, dead);
        RunReadyBatch(observesLive, live);
        RunReadyBatch(observesDead, dead);

        AssertProcessHeldLive(live);
        AssertProcessReconciledAsExited(dead);
    }

    [Xunit.Fact(DisplayName = "GoalDispatchOperations_process_liveness_is_independent_of_construction_order")]
    public void GoalDispatchOperationsProcessLivenessIsIndependentOfConstructionOrder()
    {
        var live = CreateWorkspaceWithRecordedProcess();
        var dead = CreateWorkspaceWithRecordedProcess();

        // Reversed construction order relative to the first test.
        var observesDead = CreateOperations(seesChildAsLive: false);
        var observesLive = CreateOperations(seesChildAsLive: true);

        RunReadyBatch(observesDead, dead);
        RunReadyBatch(observesLive, live);
        RunReadyBatch(observesDead, dead);
        RunReadyBatch(observesLive, live);

        AssertProcessHeldLive(live);
        AssertProcessReconciledAsExited(dead);
    }

    [Xunit.Fact(DisplayName = "GoalDispatchOperations_exposes_no_static_mutable_process_probe")]
    public void GoalDispatchOperationsExposesNoStaticMutableProcessProbe()
    {
        var settableStatics = typeof(GoalDispatchOperations)
            .GetProperties(System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic)
            .Where(property => property.CanWrite)
            .Select(property => property.Name)
            .Concat(typeof(GoalDispatchOperations)
                .GetFields(System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic)
                .Where(field => !field.IsInitOnly && !field.IsLiteral)
                .Select(field => field.Name))
            .ToList();

        Assert.True(
            settableStatics.Count == 0,
            $"Scoped probes must not be replaced by process-wide state: {string.Join(", ", settableStatics)}");
    }

    private static GoalDispatchOperations CreateOperations(bool seesChildAsLive)
    {
        var childIdentity = new SpawnProcessIdentity(
            ChildProcessId,
            DateTimeOffset.Parse("2026-07-11T01:52:20Z"),
            @"C:\workers\child.exe");
        return new GoalDispatchOperations(
            isProcessRunning: processId => seesChildAsLive && processId == ChildProcessId,
            readProcessIdentity: processId => seesChildAsLive && processId == ChildProcessId ? childIdentity : null);
    }

    private static void RunReadyBatch(GoalDispatchOperations operations, DispatchFixture fixture)
    {
        var batch = operations.SubscriptionDispatchReadyBatch(
            fixture.Kernel,
            fixture.Workspace,
            fixture.Kernel.GetGoal(fixture.GoalId),
            [fixture.Agent],
            fixture.Profiles);

        // Neither instance may prepare work while the recorded process is unresolved; this keeps the
        // assertions below about observed process identity, not about dispatch preparation.
        Assert.Empty(batch.Dispatches);
    }

    private static void AssertProcessHeldLive(DispatchFixture fixture)
    {
        var task = fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId);
        Assert.NotNull(task.LastProcess);
        Assert.True(
            task.LastProcess!.IsRunning,
            "The instance whose probe reports the child alive must still observe a running process.");
    }

    private static void AssertProcessReconciledAsExited(DispatchFixture fixture)
    {
        var task = fixture.Kernel.GetTask(fixture.GoalId, fixture.TaskId);
        Assert.NotNull(task.LastProcess);
        Assert.False(
            task.LastProcess!.IsRunning,
            "The instance whose probe reports the child dead must reconcile the process as exited.");
    }

    private sealed record DispatchFixture(
        AgentOrchestratorKernel Kernel,
        OrchestratorWorkspace Workspace,
        GoalId GoalId,
        TaskId TaskId,
        AgentDefinition Agent,
        WorkerProfileCatalog Profiles);

    private static DispatchFixture CreateWorkspaceWithRecordedProcess()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        SeedLocalSkillCatalog(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);

        var clock = DateTimeOffset.Parse("2026-07-11T01:52:19Z");
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Inspect live process state", AgentRole.Planner, "Record explicit verification.");
        var goal = kernel.CreateGoal("Scope process probes per dispatch operation instance", [task]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Process-scope fixture goal is already refined.",
            ["Process-scope fixture goal is already refined."],
            VerificationClass.TestVerifiable,
            [],
            []));
        var agent = new AgentDefinition(
            new AgentId("subscription-planner"),
            "Subscription planner",
            AgentRole.Planner,
            new ModelProfile("OpenAI", "gpt-5.4-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.PreferSubscription,
            Subscription: new SubscriptionLaunchProfile("codex-cli"));
        kernel.ActivateGoal(goal.Id, [agent]);

        var stdout = Path.Combine(root, "out.log");
        var stderr = Path.Combine(root, "err.log");
        var exit = Path.Combine(root, "exit.txt");
        File.WriteAllText(stdout, "done");
        File.WriteAllText(stderr, string.Empty);
        DispatchExitArtifacts.Write(exit, DispatchExitArtifacts.Native(0, "worker exited", clock));
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "recorded dispatch", root, clock));
        var processRecord = new TaskProcessRecord(
            ParentProcessId, "recorded dispatch", root, stdout, stderr, exit, clock, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, processRecord);
        File.WriteAllText(
            BackgroundDispatchRunner.GetHeartbeatPath(processRecord),
            "{\"pid\":28516,\"childPid\":28517,\"ownedPids\":[28517],\"ownedProcessIdentities\":[{\"processId\":28517," +
            "\"startedAt\":\"2026-07-11T01:52:20Z\",\"imagePath\":\"C:\\\\workers\\\\child.exe\"}],\"state\":\"running\"," +
            "\"lastObservedAt\":\"2026-07-11T01:52:19Z\",\"lastProgressAt\":\"2026-07-11T01:52:19Z\"," +
            "\"stdoutBytes\":4,\"stderrBytes\":0,\"ownedCpuMs\":1}");

        // RecordTaskProcessStarted leaves the task Running, and process reconciliation only considers
        // Assigned tasks. Without this stale-Assigned rewrite the injected probes are never consulted and
        // both instances would trivially report a live process, making the negative control vacuous.
        kernel = RewriteSingleTaskAsStaleAssigned(kernel);

        return new DispatchFixture(
            kernel,
            workspace,
            goal.Id,
            task.Id,
            agent,
            new WorkerProfileCatalog([new WorkerProfile("codex-cli", "Write-Output {promptPath}")]));
    }

    private static AgentOrchestratorKernel RewriteSingleTaskAsStaleAssigned(AgentOrchestratorKernel kernel)
    {
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = snapshot.Goals.Single();
        var staleAssignedSnapshot = goalSnapshot.Tasks.Single() with { Status = WorkTaskStatus.Assigned };
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with { Tasks = [staleAssignedSnapshot] }]
        });
    }
}
