using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsBeginTick : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsBeginTick(ITestOutputHelper output)
        : base(output)
    {
    }

    [Xunit.Fact(DisplayName = "BatchLoop_rearms_dispatch_remediation_before_goal_walk")]
    public void BatchLoopRearmsDispatchRemediationBeforeGoalWalk()
    {
        var preTickKernel = new AgentOrchestratorKernel();
        var preTickGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            preTickKernel,
            AgentCatalog.Default().Agents,
            "Pre-tick failure");
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            AgentCatalog.Default().Agents,
            "Loop failure");
        var shutdownCalls = 0;
        var driver = new ConductorDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningPaidWorkerCount: () => 0,
            createWorkspace: _ => "/tmp/workspace",
            dispatchAndStart: _ => DispatchStartOutcome.SpawnFailed("spawn failed"),
            startRecordedDispatches: null,
            buildServerShutdown: () => shutdownCalls++,
            runAcceptanceVerification: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: null,
            retryTask: null,
            recordTaskNote: null,
            recordCriterionRetryFeedback: null,
            clearCriterionRetryFeedback: null,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.AlreadyFastForwardable,
                "goal/test",
                "OK",
                [],
                null),
            land: (goal, _) => new LandingResult(
                goal.Id.Value,
                goal.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "Landed"),
            afterSuccessfulLanding: null,
            record: _ => { },
            cleanup: _ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null),
            writeEscalation: (_, _, _) => { },
            classifyChangeRisk: _ => null);

        driver.AdvanceOnce(preTickGoal, ConductorAutonomyPolicy.Conservative);
        Xunit.Assert.Equal(1, shutdownCalls);

        var summary = new ConductorBatchLoop().Run(
            kernel,
            driver,
            ConductorAutonomyPolicy.Conservative,
            Path.Combine(Path.GetTempPath(), $"conduct-stop-{Guid.NewGuid():N}.txt"),
            maxIterations: 1);

        Xunit.Assert.Equal(1, summary.Ticks);
        Xunit.Assert.Equal(2, shutdownCalls);
    }
}
