using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorGroupedGateChildExitEventTests
{
    [Theory(DisplayName = "A completed child reports its recorded gate verdict after its owner exits")]
    [InlineData("passed", "passed")]
    [InlineData("failed", "failed")]
    [InlineData("PASSED", "passed")]
    [InlineData("FAILED", "failed")]
    public void CompletedChildEmitsRecordedVerdict(string recordedVerdict, string expectedVerdict)
    {
        using var fixture = new AttemptFixture();
        File.WriteAllText(fixture.Attempt.ResultPath, JsonSerializer.Serialize(new
        {
            fixture.Attempt.IdentityValue, Status = "completed", Verdict = recordedVerdict,
            Error = "first error line\r\nsecond error line"
        }));

        var line = fixture.Reconcile("child-result-published");

        Assert.Equal(fixture.ExpectedLine("ACCEPTANCE_COHORT_CHILD_COMPLETED",
            "child-result-published", $"result=completed error=first error line verdict={expectedVerdict}"), line);
    }

    [Fact(DisplayName = "An exited owner without a published child result keeps the dead event")]
    public void MissingChildResultKeepsDeadEvent()
    {
        using var fixture = new AttemptFixture();

        var line = fixture.Reconcile("owner-dead");

        Assert.Equal(fixture.ExpectedLine("ACCEPTANCE_COHORT_RECONCILED_DEAD",
            "owner-dead", "result=none"), line);
    }

    [Theory(DisplayName = "A completed child without a recognized verdict reports unknown")]
    [InlineData("{}")]
    [InlineData("{\"Verdict\":null}")]
    [InlineData("{\"Verdict\":1}")]
    [InlineData("{\"Verdict\":\"skipped\"}")]
    [InlineData("{\"Verdict\":\"\"}")]
    public void CompletedChildWithoutKnownVerdictEmitsUnknown(string verdictJson)
    {
        using var fixture = new AttemptFixture();
        using var verdictDocument = JsonDocument.Parse(verdictJson);
        var result = new Dictionary<string, object> { ["Status"] = "completed" };
        if (verdictDocument.RootElement.TryGetProperty("Verdict", out var verdict))
            result["Verdict"] = verdict;
        File.WriteAllText(fixture.Attempt.ResultPath, JsonSerializer.Serialize(result));

        var line = fixture.Reconcile("child-result-published");

        Assert.Equal(fixture.ExpectedLine("ACCEPTANCE_COHORT_CHILD_COMPLETED",
            "child-result-published", "result=completed verdict=unknown"), line);
    }

    [Theory(DisplayName = "Other child statuses keep the dead event even when a verdict is present")]
    [InlineData("failed")]
    [InlineData("Completed")]
    [InlineData("completed\nextra")]
    public void OtherChildStatusesKeepDeadEvent(string status)
    {
        using var fixture = new AttemptFixture();
        File.WriteAllText(fixture.Attempt.ResultPath, JsonSerializer.Serialize(new
        {
            Status = status, Verdict = "passed"
        }));

        var line = fixture.Reconcile("child-result-published");

        Assert.Equal(fixture.ExpectedLine("ACCEPTANCE_COHORT_RECONCILED_DEAD",
            "child-result-published", $"result={status.Split('\n')[0]}"), line);
    }

    private sealed class AttemptFixture : IDisposable
    {
        private static readonly DateTimeOffset ReconciledAt = DateTimeOffset.UnixEpoch.AddHours(1);
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"grouped-child-exit-{Guid.NewGuid():N}");
        internal ConductorGroupedGateAttempt Attempt { get; }

        internal AttemptFixture()
        {
            Attempt = new ConductorGroupedGateAttempt("child-exit-attempt", "merge-train",
                [Member("first-member"), Member("second-member")], "main", "tree", "manifest",
                "child-exit-identity", DateTimeOffset.UnixEpoch, 4242, 91001,
                Path.Combine(_root, "attempt.json"), Path.Combine(_root, "result.json"),
                Path.Combine(_root, "exit"), Path.Combine(_root, "out.log"),
                Path.Combine(_root, "err.log"), _root, ConductorAutonomyPolicy.Conservative.ToJson());
            ConductorGroupedGateAttemptCoordinator.Save(Attempt);
        }

        internal string Reconcile(string detail)
        {
            var events = new List<string>();
            var coordinator = new ConductorGroupedGateAttemptCoordinator(_root,
                isProcessAlive: _ => false, utcNow: () => ReconciledAt, eventSink: events.Add);

            var reconciled = coordinator.TryReconcileDead(Attempt, detail);

            Assert.Equal("Reconciled", reconciled.Outcome);
            Assert.Equal(detail, reconciled.Detail);
            Assert.Equal(ReconciledAt, reconciled.ReconciledAt);
            var persisted = ConductorGroupedGateAttemptCoordinator.Read(Attempt.MetadataPath);
            Assert.Equal("Reconciled", persisted.Outcome);
            Assert.Equal(detail, persisted.Detail);
            Assert.Equal(ReconciledAt, persisted.ReconciledAt);
            coordinator.TryReconcileDead(Attempt, detail);
            return Assert.Single(events);
        }

        internal string ExpectedLine(string eventName, string detail, string suffix) =>
            $"{eventName} kind={Attempt.Kind} attempt={Attempt.AttemptId} " +
            $"members={string.Join('+', Attempt.Members.Select(member => member.GoalId))} " +
            $"identity={Attempt.IdentityValue} owner={Attempt.OwnerProcessId} reason={detail} {suffix}";

        private static ConductorGroupedGateMember Member(string id) =>
            new(id, "branch", "candidate", [], [], ChangeRiskTier.Behavior);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
