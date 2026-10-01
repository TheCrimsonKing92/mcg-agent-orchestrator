using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class AcceptanceCohortWorkflowTestsGroupedGateReconciliationEvents
{
    [Fact(DisplayName = "Dead owner reconciliation publishes one persisted event without a child result")]
    public void DeadOwnerWithoutResultEmitsOneEvent()
    {
        using var fixture = new AttemptFixture();
        var events = new List<string>();
        var coordinator = fixture.Coordinator(ownerAlive: false, line =>
        {
            var persisted = ConductorGroupedGateAttemptCoordinator.Read(fixture.Attempt.MetadataPath);
            Assert.Equal("Reconciled", persisted.Outcome);
            Assert.Equal("owner-dead", persisted.Detail);
            events.Add(line);
        });

        var reconciled = coordinator.TryReconcileDead(fixture.Attempt, "owner-dead");

        var line = Assert.Single(events);
        AssertEventIdentity(line, fixture.Attempt);
        Assert.Contains("reason=owner-dead result=none", line);
        Assert.Equal("owner-dead", reconciled.Detail);
        Assert.Equal("Reconciled", reconciled.Outcome);
        Assert.Equal(AttemptFixture.ReconciledAt, reconciled.ReconciledAt);
    }

    [Fact(DisplayName = "A published failed child result contributes its status and first error line")]
    public void PublishedFailureEmitsStatusAndFirstErrorLine()
    {
        using var fixture = new AttemptFixture();
        File.WriteAllText(fixture.Attempt.ResultPath, JsonSerializer.Serialize(new
        {
            fixture.Attempt.IdentityValue, Status = "failed", Error = "first error line\r\nsecond error line"
        }));
        var events = new List<string>();
        var coordinator = fixture.Coordinator(ownerAlive: false, events.Add);

        coordinator.TryReconcileDead(fixture.Attempt, "child-result-published");

        var line = Assert.Single(events);
        AssertEventIdentity(line, fixture.Attempt);
        Assert.Contains("reason=child-result-published result=failed error=first error line", line);
        Assert.DoesNotContain("second error line", line);
        Assert.DoesNotContain("\r", line);
        Assert.DoesNotContain("\n", line);
        var persisted = ConductorGroupedGateAttemptCoordinator.Read(fixture.Attempt.MetadataPath);
        Assert.Equal("Reconciled", persisted.Outcome);
        Assert.Equal("child-result-published", persisted.Detail);
    }

    [Fact(DisplayName = "A live owner leaves its record unchanged and emits no reconciliation event")]
    public void LiveOwnerRemainsUnchangedWithoutEvent()
    {
        using var fixture = new AttemptFixture();
        File.WriteAllText(fixture.Attempt.ResultPath, "unreadable result must not be inspected");
        var original = File.ReadAllText(fixture.Attempt.MetadataPath);
        var events = new List<string>();
        var coordinator = fixture.Coordinator(ownerAlive: true, events.Add);

        var unchanged = coordinator.TryReconcileDead(fixture.Attempt, "child-result-published");

        Assert.Empty(events);
        Assert.Equal(original, File.ReadAllText(fixture.Attempt.MetadataPath));
        Assert.Null(unchanged.ReconciledAt);
        Assert.Null(unchanged.Detail);
        Assert.Equal("Running", unchanged.Outcome);
    }

    [Theory(DisplayName = "Unreadable child results remain observable without breaking reconciliation")]
    [InlineData("passed")]
    [InlineData("{}")]
    [InlineData("{\"Status\":1}")]
    [InlineData("[]")]
    public void UnreadableResultEmitsDiagnostic(string result)
    {
        using var fixture = new AttemptFixture();
        File.WriteAllText(fixture.Attempt.ResultPath, result);
        var events = new List<string>();
        var coordinator = fixture.Coordinator(ownerAlive: false, events.Add);

        var reconciled = coordinator.TryReconcileDead(fixture.Attempt, "child-result-published");

        var line = Assert.Single(events);
        Assert.Contains("result=unreadable error=", line);
        Assert.DoesNotContain("\r", line);
        Assert.DoesNotContain("\n", line);
        Assert.Equal("Reconciled", reconciled.Outcome);
    }

    [Fact(DisplayName = "Retrying reconciliation with a stale record does not publish another event")]
    public void AlreadyReconciledRecordDoesNotEmitAgain()
    {
        using var fixture = new AttemptFixture();
        var events = new List<string>();
        var coordinator = fixture.Coordinator(ownerAlive: false, events.Add);
        coordinator.TryReconcileDead(fixture.Attempt, "owner-dead");
        var original = File.ReadAllText(fixture.Attempt.MetadataPath);

        coordinator.TryReconcileDead(fixture.Attempt, "child-result-published");

        Assert.Single(events);
        Assert.Equal(original, File.ReadAllText(fixture.Attempt.MetadataPath));
    }

    [Fact(DisplayName = "An unavailable event sink does not interrupt persisted reconciliation")]
    public void SinkIoFailureDoesNotInterruptReconciliation()
    {
        using var fixture = new AttemptFixture();
        var calls = 0;
        var coordinator = fixture.Coordinator(ownerAlive: false, _ =>
        {
            calls++;
            throw new IOException("event sink unavailable");
        });

        var reconciled = coordinator.TryReconcileDead(fixture.Attempt, "owner-dead");

        Assert.Equal(1, calls);
        Assert.Equal("Reconciled", reconciled.Outcome);
        Assert.Equal("owner-dead", ConductorGroupedGateAttemptCoordinator.Read(
            fixture.Attempt.MetadataPath).Detail);
    }

    private static void AssertEventIdentity(string line, ConductorGroupedGateAttempt attempt)
    {
        Assert.StartsWith("ACCEPTANCE_COHORT_RECONCILED_DEAD ", line);
        Assert.Contains($"kind={attempt.Kind}", line);
        Assert.Contains($"attempt={attempt.AttemptId}", line);
        Assert.Contains($"identity={attempt.IdentityValue}", line);
        Assert.Contains($"owner={attempt.OwnerProcessId}", line);
        Assert.Contains($"members={string.Join('+', attempt.Members.Select(member => member.GoalId))}", line);
    }

    private sealed class AttemptFixture : IDisposable
    {
        internal static readonly DateTimeOffset ReconciledAt = DateTimeOffset.UnixEpoch.AddHours(1);
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"grouped-reconcile-{Guid.NewGuid():N}");
        internal ConductorGroupedGateAttempt Attempt { get; }

        internal AttemptFixture()
        {
            Attempt = new ConductorGroupedGateAttempt("reconcile-attempt", "merge-train",
                [Member("first-member"), Member("second-member")], "main", "tree", "manifest",
                "reconciliation-identity", DateTimeOffset.UnixEpoch, 4242, 91001,
                Path.Combine(_root, "attempt.json"), Path.Combine(_root, "result.json"),
                Path.Combine(_root, "exit"), Path.Combine(_root, "out.log"),
                Path.Combine(_root, "err.log"), _root, ConductorAutonomyPolicy.Conservative.ToJson());
            ConductorGroupedGateAttemptCoordinator.Save(Attempt);
        }

        internal ConductorGroupedGateAttemptCoordinator Coordinator(bool ownerAlive, Action<string> sink) =>
            new(_root, isProcessAlive: _ => ownerAlive, utcNow: () => ReconciledAt, eventSink: sink);

        private static ConductorGroupedGateMember Member(string id) =>
            new(id, "branch", "candidate", [], [], ChangeRiskTier.Behavior);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
