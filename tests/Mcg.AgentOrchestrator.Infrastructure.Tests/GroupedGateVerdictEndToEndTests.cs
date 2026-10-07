using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class GroupedGateVerdictEndToEndTests
{
    [Theory(DisplayName = "The real host result round trips into the child completion event")]
    [InlineData("cohort", "passed")]
    [InlineData("train", "passed")]
    [InlineData("cohort", "failed")]
    [InlineData("train", "failed")]
    [InlineData("cohort", null)]
    [InlineData("train", null)]
    public void HostResultEmitsRecordedVerdictAndReceipt(string kind, string? verdict)
    {
        using var fixture = new AttemptFixture(kind);
        ConductorGroupedGateOutcome? outcome = verdict is null ? null : new(verdict, "r1");
        var exitCode = ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath,
            _ => outcome, (_, _) => new NoopRestoration());
        Assert.Equal(0, exitCode);

        // Reload the host's owner claim before driving the real reader with deterministic liveness.
        var claimed = ConductorGroupedGateAttemptCoordinator.Read(fixture.Attempt.MetadataPath);
        var events = new List<string>();
        var reconciledAt = DateTimeOffset.UnixEpoch.AddHours(1);
        var coordinator = new ConductorGroupedGateAttemptCoordinator(fixture.Root,
            isProcessAlive: _ => false, utcNow: () => reconciledAt, eventSink: events.Add);
        var reconciled = coordinator.TryReconcileDead(claimed, "child-result-published");

        Assert.Equal("Reconciled", reconciled.Outcome);
        Assert.Equal(reconciledAt, reconciled.ReconciledAt);
        var line = Assert.Single(events);
        Assert.StartsWith($"ACCEPTANCE_COHORT_CHILD_COMPLETED kind={kind} ", line);
        if (verdict is null)
        {
            Assert.Contains("result=completed verdict=unknown", line);
            Assert.DoesNotContain("receipt=", line);
        }
        else
        {
            Assert.Contains($"result=completed verdict={verdict} receipt=r1", line);
        }
    }

    private sealed class NoopRestoration : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class AttemptFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"grouped-verdict-e2e-{Guid.NewGuid():N}");
        internal ConductorGroupedGateAttempt Attempt { get; }

        internal AttemptFixture(string kind)
        {
            Attempt = new ConductorGroupedGateAttempt("verdict-e2e-attempt", kind,
                [Member("first-member"), Member("second-member")], "main", "tree", "manifest",
                "gate-identity", DateTimeOffset.UnixEpoch, 0, 91001,
                Path.Combine(Root, "attempt.json"), Path.Combine(Root, "result.json"),
                Path.Combine(Root, "exit"), Path.Combine(Root, "out.log"),
                Path.Combine(Root, "err.log"), Root, ConductorAutonomyPolicy.Conservative.ToJson());
            ConductorGroupedGateAttemptCoordinator.Save(Attempt);
        }

        private static ConductorGroupedGateMember Member(string id) =>
            new(id, "branch", "candidate", [], [], ChangeRiskTier.Behavior);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
