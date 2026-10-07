using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the fact owns its root and event writer path.
public sealed class ConductorDriverTestsCohortRemoteLaneOptions : IDisposable
{
    private readonly string _root = InfrastructureTestSupport.CreateTempDirectory();

    [Fact(DisplayName = "Cohort gate options enable remote lanes and preserve member ownership")]
    public void CohortOptionsPreserveSinksAndCandidateBindings()
    {
        AcceptanceCohortMemberBinding[] bindings =
        [
            Binding("22222222222222222222222222222222", 'b'),
            Binding("11111111111111111111111111111111", 'a')
        ];
        var identity = AcceptanceCohortIdentity.Create(bindings, new string('c', 40),
            new string('d', 40), "manifest-v1");
        var writer = new ConductEventLogWriter(Path.Combine(_root, ConductEventLogWriter.CurrentFileName));

        var options = ConductorDriver.CreateCohortGateExecutionOptions(writer, identity, bindings);

        Assert.True(options.CohortRemoteLanes);
        Assert.NotNull(options.ProgressSink);
        Assert.NotNull(options.RemoteLaneEventSink);
        Assert.Equal(ConductorDriver.CohortGateRunIdentity(bindings.Select(binding => binding.GoalId.Value)),
            options.GateRunIdentity);
        Assert.Equal(bindings.Length, options.OwnerProtectedCohortMembers!.Count);
        foreach (var binding in bindings)
        {
            var member = Assert.Single(options.OwnerProtectedCohortMembers.Where(member => member.GoalId == binding.GoalId));
            Assert.Equal(binding.CandidateRevision, member.CandidateSha);
        }
        Assert.False(new AcceptanceRunExecutionOptions().CohortRemoteLanes);
    }

    private static AcceptanceCohortMemberBinding Binding(string goalValue, char revision) => new(
        new GoalId(goalValue), new string(revision, 40), new string(revision, 40),
        ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FirstTests.cs"], ["ownership:tests"],
        ChangeRiskTier.Behavior, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");

    public void Dispose() => Directory.Delete(_root, true);
}
