using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorGroupedGateAttemptHostVerdictTests
{
    [Theory(DisplayName = "Completed results carry the returned verdict and receipt or omit both")]
    [InlineData("passed")]
    [InlineData("failed")]
    [InlineData(null)]
    public void ReturnedOutcomeControlsCompletedFields(string? verdict)
    {
        AssertPublishedOutcome(verdict is null ? null : new(verdict, "r1"), verdict);
    }

    [Theory(DisplayName = "Cohort receipts publish only recorded binary gate verdicts")]
    [InlineData(AcceptanceCohortGateOutcome.Passed, "passed")]
    [InlineData(AcceptanceCohortGateOutcome.Failed, "failed")]
    [InlineData(AcceptanceCohortGateOutcome.InfrastructureFailure, null)]
    [InlineData(AcceptanceCohortGateOutcome.Invalidated, null)]
    public void CohortReceiptMapsToPublishedOutcome(AcceptanceCohortGateOutcome outcome, string? verdict)
    {
        AssertPublishedOutcome(ConductorDriver.ToGroupedGateOutcome(outcome, "r1"), verdict);
    }

    [Theory(DisplayName = "Merge train receipts publish only recorded binary gate verdicts")]
    [InlineData(MergeTrainGateOutcome.Passed, "passed")]
    [InlineData(MergeTrainGateOutcome.Failed, "failed")]
    [InlineData(MergeTrainGateOutcome.InfrastructureFailure, null)]
    [InlineData(MergeTrainGateOutcome.Invalidated, null)]
    public void TrainReceiptMapsToPublishedOutcome(MergeTrainGateOutcome outcome, string? verdict)
    {
        AssertPublishedOutcome(ConductorDriver.ToGroupedGateOutcome(outcome, "r1"), verdict);
    }

    private static void AssertPublishedOutcome(ConductorGroupedGateOutcome? outcome, string? verdict)
    {
        using var fixture = new AttemptFixture();
        var exitCode = ConductorGroupedGateAttemptHost.Run(fixture.Attempt.MetadataPath,
            _ => outcome, (_, _) => new NoopRestoration());

        Assert.Equal(0, exitCode);
        Assert.Equal("0", File.ReadAllText(fixture.Attempt.ExitCodePath));
        using var result = JsonDocument.Parse(File.ReadAllText(fixture.Attempt.ResultPath));
        var root = result.RootElement;
        Assert.Equal(fixture.Attempt.IdentityValue, root.GetProperty("IdentityValue").GetString());
        Assert.Equal("completed", root.GetProperty("Status").GetString());
        if (verdict is null)
        {
            Assert.Null(outcome);
            Assert.False(root.TryGetProperty("Verdict", out _));
            Assert.False(root.TryGetProperty("ReceiptId", out _));
        }
        else
        {
            Assert.Equal(verdict, root.GetProperty("Verdict").GetString());
            Assert.Equal("r1", root.GetProperty("ReceiptId").GetString());
        }
    }

    private sealed class NoopRestoration : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class AttemptFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"grouped-host-verdict-{Guid.NewGuid():N}");
        internal ConductorGroupedGateAttempt Attempt { get; }

        internal AttemptFixture()
        {
            Attempt = new ConductorGroupedGateAttempt("host-verdict-attempt", "cohort", [], "main", "tree",
                "manifest", "host-identity", DateTimeOffset.UnixEpoch, 0, 91001,
                Path.Combine(_root, "attempt.json"), Path.Combine(_root, "result.json"),
                Path.Combine(_root, "exit"), Path.Combine(_root, "out.log"),
                Path.Combine(_root, "err.log"), _root, ConductorAutonomyPolicy.Conservative.ToJson());
            ConductorGroupedGateAttemptCoordinator.Save(Attempt);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
