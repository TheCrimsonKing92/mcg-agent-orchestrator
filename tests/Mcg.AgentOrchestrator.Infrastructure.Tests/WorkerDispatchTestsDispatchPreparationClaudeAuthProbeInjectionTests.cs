using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Process-wide environment changes require the lane that owns EnvMutation.
[Collection(TestCollections.EnvMutation)]
public sealed class WorkerDispatchTestsDispatchPreparationClaudeAuthProbeInjectionTests : WorkerDispatchTestSupport
{
    private static readonly WorkerSandboxOptions EnabledSandbox = new(
        Enabled: true, WorkerSandboxOptions.DefaultAccount, WorkerSandboxOptions.DefaultCredentialTarget);

    [Fact]
    public void InjectedSignedInProbe_PreventsUnchangedRetryWithEmptyClaudeConfiguration()
    {
        using var environment = new SignedOutEnvironment();
        AssertSignedOutPrecondition();
        var fixture = PrepareUnchangedPaidRetry();
        var probeCalls = 0;
        var processStartCalls = 0;
        var processStartCheckpoints = 0;
        var result = new GoalDispatchOperations().StartSubscriptionReadyTasks(
            fixture.Kernel, fixture.Workspace, fixture.Goal, fixture.Agents, fixture.Profiles,
            checkpointBeforeWorkerStart: (kernel, _, _, phase) =>
            {
                if (phase == DispatchRecordCheckpointPhase.BeforeProcessStart) processStartCheckpoints++;
                fixture.Repository.SaveAsync(kernel).GetAwaiter().GetResult();
            },
            runner: new BackgroundDispatchRunner(disableProcessStart: true, startProcess: _ =>
            {
                processStartCalls++;
                throw new InvalidOperationException("Prevented retry reached the process launch seam.");
            }),
            sandboxOptions: EnabledSandbox,
            commandExists: DispatcherProviderProbeFakes.ProviderCommandsPresent,
            claudeAuthProbe: () =>
            {
                probeCalls++;
                return DispatcherProviderProbeFakes.SignedInClaudeCli();
            });

        Assert.True(probeCalls > 0, "Start preparation must consult the injected sign-in probe.");
        Assert.Empty(result.Processes.Tasks);
        Assert.Equal(0, processStartCheckpoints);
        Assert.Equal(0, processStartCalls);
        var stored = fixture.Repository.LoadAsync().GetAwaiter().GetResult().GetGoal(fixture.Goal.Id);
        var heldTask = Assert.Single(stored.Tasks);
        var prevention = Assert.Single(heldTask.RetryAdmissionHistory,
            receipt => receipt.Decision == RetryAdmissionDecision.Prevented);
        Assert.Equal(RetryCause.UnchangedContextRepeat, prevention.Cause);
        Assert.Null(heldTask.LastProcess);
        Assert.Single(stored.Timeline, entry =>
            entry.Kind == ProgressKind.NoProgressRedispatchPrevented && entry.TaskId == fixture.Planner.Id);
    }

    [Fact]
    public void OmittedProbe_ChangesRetryContextAndReachesDisabledStartWithEmptyClaudeConfiguration()
    {
        using var environment = new SignedOutEnvironment();
        AssertSignedOutPrecondition();
        var fixture = PrepareUnchangedPaidRetry();
        var processStartCalls = 0;
        var processStartCheckpoints = 0;
        var exception = Record.Exception(() => new GoalDispatchOperations().StartSubscriptionReadyTasks(
            fixture.Kernel, fixture.Workspace, fixture.Goal, fixture.Agents, fixture.Profiles,
            checkpointBeforeWorkerStart: (kernel, _, _, phase) =>
            {
                if (phase == DispatchRecordCheckpointPhase.BeforeProcessStart) processStartCheckpoints++;
                fixture.Repository.SaveAsync(kernel).GetAwaiter().GetResult();
            },
            runner: new BackgroundDispatchRunner(disableProcessStart: true, startProcess: _ =>
            {
                processStartCalls++;
                throw new InvalidOperationException("Disabled start reached the process launch seam.");
            }),
            sandboxOptions: EnabledSandbox,
            commandExists: DispatcherProviderProbeFakes.ProviderCommandsPresent));

        Assert.NotNull(exception);
        Assert.Contains(BackgroundDispatchRunner.DisableDispatchStartVariable, exception.Message);
        Assert.True(processStartCheckpoints > 0, "Changed retry context must reach the process-start checkpoint.");
        Assert.Equal(0, processStartCalls);
        var goal = fixture.Kernel.GetGoal(fixture.Goal.Id);
        Assert.DoesNotContain(fixture.Kernel.GetTask(goal.Id, fixture.Planner.Id).RetryAdmissionHistory,
            receipt => receipt.Decision == RetryAdmissionDecision.Prevented);
        Assert.DoesNotContain(goal.Timeline, entry =>
            entry.Kind == ProgressKind.NoProgressRedispatchPrevented && entry.TaskId == fixture.Planner.Id);
    }

    private static void AssertSignedOutPrecondition()
    {
        var realProbeState = ClaudeCliAuthProbe.ForOneDispatchPreflight()();
        Assert.False(realProbeState.HasCliCredentialArtifact);
        Assert.False(realProbeState.HasAnthropicApiKey);
    }

    private static PreparedRetry PrepareUnchangedPaidRetry()
    {
        var root = CreateTempDirectory();
        var workingDirectory = Path.Combine(root, "repo");
        Directory.CreateDirectory(workingDirectory);
        WriteSkill(workingDirectory, "orchestrator-dogfood");
        var workspace = OrchestratorWorkspace.ForDirectory(root, workingDirectory);
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
        var firstAt = DateTimeOffset.Parse("2026-07-07T12:00:00Z");
        var clock = new MutableClock(firstAt);
        var kernel = new AgentOrchestratorKernel(clock);
        var planner = new TaskSpec(TaskId.New(), "Plan the hermetic retry.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Prevent unchanged retry with fixture sign-in", [planner]);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Prevent unchanged retry with fixture sign-in", ["The unchanged retry is prevented once."],
            VerificationClass.TestVerifiable, [], []));
        var agents = new[] { SubscriptionPlannerAgent("planner", "Planner") };
        var profiles = DispatchTestProfiles();
        kernel.ActivateGoal(goal.Id, agents);
        const string retryMessage = "Retry after the unsuccessful paid attempt.";
        kernel.RetryTask(goal.Id, planner.Id, retryMessage, retryCause: RetryCause.ProviderInterruption);
        var first = WorkerProfileDispatcher.PrepareSubscriptionReadyBatch(
            kernel, goal, agents, profiles, workspace.PromptDirectory, workingDirectory, firstAt,
            commandExists: DispatcherProviderProbeFakes.ProviderCommandsPresent,
            sandboxOptions: EnabledSandbox,
            claudeAuthProbe: DispatcherProviderProbeFakes.SignedInClaudeCli);
        var dispatch = Assert.Single(first.Dispatches).Task.LastDispatch!;
        Assert.Equal(RetryAdmissionDecision.Allowed, kernel.RecordPreparedRetryAdmission(
            goal.Id, planner.Id, Assert.IsType<RetryContextFingerprint>(dispatch.RetryContextFingerprint),
            PaidRouteClassification.Paid, firstAt).Decision);
        kernel.RecordTaskProcessStarted(goal.Id, planner.Id, new TaskProcessRecord(
            4101, dispatch.Command, workingDirectory, Path.Combine(root, "first.out.log"),
            Path.Combine(root, "first.err.log"), Path.Combine(root, "first.exit"),
            firstAt, firstAt.AddSeconds(1), 1));
        kernel.RecordTaskVerification(goal.Id, planner.Id, new TaskVerificationRecord(
            "worker", workingDirectory, 1, "", "The first paid attempt did not complete.",
            firstAt, DispatchStartedAt: firstAt));
        kernel.ReportTaskProgress(goal.Id, planner.Id, WorkTaskStatus.Failed, "The first paid attempt did not complete.");
        clock.Advance();
        kernel.RetryTask(goal.Id, planner.Id, retryMessage, retryCause: RetryCause.ProviderInterruption);
        repository.SaveAsync(kernel).GetAwaiter().GetResult();
        return new PreparedRetry(kernel, workspace, goal, planner, agents, profiles, repository);
    }

    private sealed record PreparedRetry(
        AgentOrchestratorKernel Kernel,
        OrchestratorWorkspace Workspace,
        Goal Goal,
        TaskSpec Planner,
        IReadOnlyList<AgentDefinition> Agents,
        WorkerProfileCatalog Profiles,
        SqliteOrchestratorStateRepository Repository);

    private sealed class SignedOutEnvironment : IDisposable
    {
        private readonly string _emptyConfigDirectory = Path.Combine(
            Path.GetTempPath(), $"dispatch-start-empty-claude-{Guid.NewGuid():N}");
        private readonly Dictionary<string, string?> _previous = new()
        {
            ["CLAUDE_CONFIG_DIR"] = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            ["ANTHROPIC_API_KEY"] = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
            [BackgroundDispatchRunner.DisableDispatchStartVariable] =
                Environment.GetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable)
        };

        public SignedOutEnvironment()
        {
            Directory.CreateDirectory(_emptyConfigDirectory);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _emptyConfigDirectory);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable(BackgroundDispatchRunner.DisableDispatchStartVariable, "1");
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
            Directory.Delete(_emptyConfigDirectory, recursive: true);
        }
    }
}
