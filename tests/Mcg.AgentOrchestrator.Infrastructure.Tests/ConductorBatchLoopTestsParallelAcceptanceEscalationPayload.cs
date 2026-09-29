using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class ConductorBatchLoopTestsParallelAcceptance
{
    private static void AssertProtectedBoundaryEscalation(string faultMessage, string escalation)
    {
        Assert.Equal(WorkerProcessJobs.BuildProtectedBoundaryRegistrationFailure(
            4001, new ProtectedProcessIdentity(4001, 100)), faultMessage);
        Assert.Equal(WorkerRegistrationFaultDisposition.Terminal,
            ConductorParallelAcceptanceAttemptCoordinator.ClassifyWorkerRegistrationFault(faultMessage));
        Assert.Contains("pid=4001", escalation);
        Assert.Contains("protected=4001@100", escalation);
    }

    [Xunit.Fact]
    public void ExhaustedBackgroundBuildLockPersistsTypedEscalation()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Update src/BuildLockPayload.cs");
        var attemptRoot = CreateTempDirectory("mcg-conductor-build-lock-payload");
        var attempts = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) =>
            {
                attempts++;
                throw new BuildLockBlockedException(new BuildLockAttribution(
                    @"C:\mcg-dotnet-isolated\goals\deadbeef\artifacts\bin\Mcg.AgentOrchestrator.Core.dll",
                    [new BuildLockHolder(null, "unknown-probe-timeout", null, false)],
                    "handle64-timeout", "acceptance-output", "classify-build-lock"));
            },
            getLandingFileScopes: _ => ["src/BuildLockPayload.cs"],
            parallelAcceptanceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true));

        try
        {
            var summary = new ConductorBatchLoop().Run(kernel, driver, ConductorAutonomyPolicy.Conservative,
                NoStopPath(), maxIterations: ConductorBatchLoop.ParallelAcceptanceTransientFailureCap,
                watchInterval: TimeSpan.FromMilliseconds(1), sleepFunc: _ => false);

            Assert.Equal(ConductorBatchLoop.ParallelAcceptanceTransientFailureCap, attempts);
            Assert.Equal(1, summary.Escalated);
            Assert.Contains(goal.Timeline, evt =>
                evt.TickOutcome?.EscalationKind == nameof(ConductorEscalationKind.BackgroundAcceptanceFailed));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }
}
