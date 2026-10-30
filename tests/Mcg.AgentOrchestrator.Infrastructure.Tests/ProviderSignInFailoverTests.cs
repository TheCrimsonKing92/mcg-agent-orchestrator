using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Scenario = ProviderSignInFailoverScenarioTests.Scenario;

// Parallel-safe: fixed-clock in-memory kernels only, without worker processes or shared files.
public sealed class ProviderSignInFailoverTests
{
    [Theory]
    [InlineData("other-decision")]
    [InlineData("two-failed")]
    [InlineData("api-only")]
    [InlineData("wrong-role")]
    [InlineData("missing-assigned")]
    [InlineData("no-failed")]
    public void IneligibleRecoveryLeavesTimelineAndTaskUnchanged(string row)
    {
        var fixture = new Fixture(row == "two-failed" ? 2 : 1);
        var alternate = row switch
        {
            "api-only" => fixture.Alternate with { ExecutionPolicy = AgentExecutionPolicy.ApiOnly },
            "wrong-role" => fixture.Alternate with { Role = AgentRole.Developer },
            _ => fixture.Alternate
        };
        var decision = fixture.Decision(row == "other-decision" ? "real-worker-or-command-failure" : "failed-terminal-fallthrough");
        if (row == "no-failed") fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, fixture.Goal.Tasks.Single().Id, "Fixture reset.", RetryCause.ProviderInterruption);
        AgentDefinition[] agents = row == "missing-assigned" ? [alternate] : [fixture.Primary, alternate];
        var timeline = fixture.Goal.Timeline.ToArray();
        var taskStates = fixture.Goal.Tasks.Select(task => (task.Status, task.AssignedAgentId, task.LastDispatch, task.LastVerification)).ToArray();

        var result = new ProviderSignInFailover(fixture.Kernel, agents).TryFailover(fixture.Goal, decision, fixture.Retry);

        Assert.False(result);
        Assert.Equal(timeline, fixture.Goal.Timeline);
        Assert.Equal(taskStates, fixture.Goal.Tasks.Select(task => (task.Status, task.AssignedAgentId, task.LastDispatch, task.LastVerification)).ToArray());
        Assert.Empty(fixture.Causes);
    }

    [Fact]
    public void CatalogOrderChoosesFirstEligibleAgentAndRecordsRetryContract()
    {
        var fixture = new Fixture();
        var anthropic = ProviderHoldAgentSubstitutionTests.Scenario.Agent("anthropic-tester", "Anthropic", "claude-cli", "claude-opus-5-5");
        var failover = new ProviderSignInFailover(fixture.Kernel, [fixture.Primary, fixture.Alternate, anthropic]);

        Assert.True(failover.TryFailover(fixture.Goal, fixture.Decision(), fixture.Retry));

        var task = fixture.Goal.Tasks.Single();
        Assert.Equal(fixture.Alternate.Id, task.AssignedAgentId);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        var evt = Assert.Single(fixture.Goal.Timeline, item => item.Kind == ProgressKind.TaskRedelegated);
        const string message = "PROVIDER_SIGNIN_FAILOVER from=xai-tester from_provider=xAI to=openai-tester to_provider=OpenAI role=Tester";
        Assert.EndsWith(" Reason: " + message, evt.Message);
        Assert.Equal(message, fixture.RetryMessage);
        Assert.Null(fixture.RetryRound);
        Assert.Equal(new[] { RetryCause.ProviderInterruption }, fixture.Causes);
        Assert.False(OfflineAssignedAgentRedelegation.IsOperatorPin(fixture.Goal, task.Id));

        fixture.Kernel.ReassignTaskAgent(fixture.Goal.Id, task.Id, anthropic);

        Assert.True(OfflineAssignedAgentRedelegation.IsOperatorPin(fixture.Goal, task.Id));
    }

    [Theory]
    [InlineData("xai-tester", " ")]
    [InlineData("xai-tester", "")]
    [InlineData("xai-tester-2", "\t")]
    public void EarlierSourceExclusionUsesWholeTokens(string earlierSource, string suffix)
    {
        var fixture = new Fixture();
        var task = fixture.Goal.Tasks.Single();
        fixture.Kernel.ReassignTaskAgent(fixture.Goal.Id, task.Id, fixture.Alternate,
            $"{ProviderSignInFailover.Marker} from={earlierSource}{suffix}");
        fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, task.Id, "Fixture retry.", RetryCause.ProviderInterruption);
        fixture.Fail(task, fixture.Alternate);
        var prefixCollisionCandidate = fixture.Primary with { Id = new AgentId("xai-tester-2") };
        AgentDefinition[] agents = [fixture.Alternate, fixture.Primary, prefixCollisionCandidate];

        Assert.True(new ProviderSignInFailover(fixture.Kernel, agents).TryFailover(fixture.Goal, fixture.Decision(), fixture.Retry));

        Assert.Equal(earlierSource == "xai-tester" ? prefixCollisionCandidate.Id : fixture.Primary.Id, task.AssignedAgentId);
    }

    [Fact]
    public void WorkspaceFactoryRequiresKernelAndWorkspace()
    {
        var fixture = new Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(Path.Combine(Path.GetTempPath(), "signin-factory-unused"));

        Assert.Null(ProviderSignInFailover.ForWorkspace(null, workspace));
        Assert.Null(ProviderSignInFailover.ForWorkspace(fixture.Kernel, null));
    }

    private sealed class Fixture
    {
        private readonly ProviderSignInFailoverScenarioTests.FakeClock _clock = new();
        internal readonly AgentOrchestratorKernel Kernel;
        internal readonly Goal Goal;
        internal readonly AgentDefinition Primary = Scenario.PrimaryAgent();
        internal readonly AgentDefinition Alternate = Scenario.AlternateAgent();
        internal readonly List<RetryCause> Causes = [];
        internal string? RetryMessage;
        internal RetryRoundKind? RetryRound;

        internal Fixture(int count = 1)
        {
            Kernel = new(_clock);
            Goal = Kernel.CreateGoal("Verify sign-in recovery.", Enumerable.Range(0, count)
                .Select(index => new TaskSpec(TaskId.New(), $"Verify fixture check {index}.", AgentRole.Tester)).ToArray());
            Kernel.ActivateGoal(Goal.Id, [Primary, Alternate]);
            foreach (var task in Goal.Tasks) Fail(task, Primary);
        }

        internal void Fail(TaskSpec task, AgentDefinition agent)
        {
            Kernel.RecordTaskDispatch(Goal.Id, task.Id, new TaskDispatchRecord(
                agent.Subscription!.WorkerProfileName, "fixture worker", "C:\\fixture", _clock.UtcNow,
                ProviderName: agent.Model.ProviderName, GoalId: Goal.Id,
                WorkerProviderKind: WorkerProviderCatalog.Default().ResolveProfile(agent.Subscription.WorkerProfileName).Identity.Kind));
            Kernel.RecordTaskVerification(Goal.Id, task.Id, new TaskVerificationRecord("fixture worker", "C:\\fixture", 1,
                "", ProviderSignInFailoverScenarioTests.SignInError, _clock.UtcNow, DispatchStartedAt: _clock.UtcNow));
            Kernel.ReportTaskProgress(Goal.Id, task.Id, WorkTaskStatus.Failed, "Fixture sign-in failure.");
            Kernel.EscalateTaskFailure(Goal.Id, task.Id, "Fixture sign-in failure.");
            Assert.True(ProviderFailureSignals.HasRecoverableProviderAuthenticationFailure(task));
        }

        internal FailedGoalRecoveryDecision Decision(string evidence = "failed-terminal-fallthrough") => new(
            FailedGoalRecoveryAction.Escalate, new FailedGoalRecoveryIdentity(Goal.Id, null, null, "fixture"), 9, evidence, "Fixture decision.");

        internal TaskSpec Retry(GoalId goalId, TaskId taskId, string message, RetryRoundKind? round, RetryCause cause)
        {
            Assert.Equal(Goal.Id, goalId);
            Assert.Equal(Goal.Tasks.Single().Id, taskId);
            RetryMessage = message;
            RetryRound = round;
            Causes.Add(cause);
            return Kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: round);
        }
    }
}
