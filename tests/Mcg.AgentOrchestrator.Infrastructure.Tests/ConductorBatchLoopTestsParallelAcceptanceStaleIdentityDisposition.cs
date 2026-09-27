using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsParallelAcceptanceStaleIdentityDisposition(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Fact]
    public void ChangedIdentityHasOwnDispositionWhileOtherFaultsRemainFaults()
    {
        var (_, goal) = SimpleGoal();
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Disposition.cs"]);

        Assert.Equal("identity-stale", ConductorBatchLoop.AcceptanceRunDisposition(
            ConductorParallelAcceptanceRunResult.Fault(candidate,
                new AcceptanceExecutionIdentityChangedException("changed", isChangedIdentity: true))));
        Assert.Equal("fault", ConductorBatchLoop.AcceptanceRunDisposition(
            ConductorParallelAcceptanceRunResult.Fault(candidate,
                new AcceptanceExecutionIdentityChangedException("unresolved", isChangedIdentity: false))));
        Assert.Equal("fault", ConductorBatchLoop.AcceptanceRunDisposition(
            ConductorParallelAcceptanceRunResult.Fault(candidate,
                new InvalidOperationException("unrelated"))));
    }
}
