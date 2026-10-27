using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure classifier inputs, or a per-test source root and index with a fixed clock.
public sealed class ApparatusRedClassifierBaselineNotExecutedTests
{
    private const string Identity = "Sample.Tests.UnchangedTests.FailsOnce";
    private const string TestPath = "tests/Sample.Tests/UnchangedTests.cs";
    private const string ChangedPath = "src/Changed.cs";
    private const string Failure = "System.TimeoutException : App CLI did not exit within 30 seconds.";
    private const string SlotsBusy = "focused baseline execution failed: DotnetBuildSlotsBusyException";
    private const string EvidenceKind = "baseline-not-executed";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T05:00:52Z");

    [Theory]
    [InlineData(SlotsBusy)]
    [InlineData("focused baseline execution failed: BuildLockBlockedException")]
    [InlineData("focused baseline execution failed: AcceptanceInfrastructureDeferredException")]
    public void RecognizedBaselineFault_RegatesWithBaselineEvidence(string baselineEvidence)
    {
        var failure = new ApparatusRedFailingTest(
            "focused CLI infrastructure tests", Identity, null, [TestPath], false, false,
            FailureMessage: Failure,
            BaselineAttribution: new AcceptanceTestFailureAttribution(
                Identity, AcceptanceTestFailureOrigin.Unattributed, baselineEvidence));

        var regate = Assert.IsType<ApparatusRedDisposition.Regate>(ApparatusRedClassifier.Classify(
            new ApparatusRedEvidence([ChangedPath], [failure], true, 0, 2)));

        Assert.Equal(EvidenceKind, regate.EvidenceKind);
        Assert.Equal(new[] { Identity }, regate.TestIdentities);
        Assert.Equal(1, regate.RegateOrdinal);
        Assert.Equal(2, regate.RegateCap);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void SlotsBusyBaseline_AtOrBeyondCap_ExhaustsWithBaselineEvidence(int regateCount)
    {
        var failure = new ApparatusRedFailingTest(
            "focused CLI infrastructure tests", Identity, null, [TestPath], false, false,
            FailureMessage: Failure,
            BaselineAttribution: new AcceptanceTestFailureAttribution(
                Identity, AcceptanceTestFailureOrigin.Unattributed, SlotsBusy));

        var exhausted = Assert.IsType<ApparatusRedDisposition.BoundExhausted>(ApparatusRedClassifier.Classify(
            new ApparatusRedEvidence([ChangedPath], [failure], true, regateCount, 2)));

        Assert.Equal(EvidenceKind, exhausted.EvidenceKind);
        Assert.Equal(new[] { Identity }, exhausted.TestIdentities);
        Assert.Equal(regateCount, exhausted.RegateCount);
        Assert.Equal(2, exhausted.RegateCap);
    }

    [Fact]
    public void SlotsBusyBaseline_RecordedByGate_RegatesWithoutOtherApparatusEvidence()
    {
        using var fixture = new Fixture();
        var reading = fixture.Record();

        var failure = Assert.Single(reading.FailingTests);
        Assert.Null(failure.ExceptionSignature);
        Assert.False(failure.HasCrossGoalOccurrence);
        Assert.False(failure.InsideChangedPaths);
        Assert.False(failure.CandidateRerunPassed);
        Assert.False(failure.CandidateRerunFailed);
        Assert.False(failure.CandidateRerunNotExecuted);
        Assert.Null(failure.CandidateRerunInfrastructureSignature);
        Assert.Equal(new[] { TestPath }, failure.ResolvedSourcePaths);
        Assert.Empty(failure.ReferencedChangedTypes!);
        Assert.NotNull(failure.BaselineAttribution);
        Assert.Equal(AcceptanceTestFailureOrigin.Unattributed, failure.BaselineAttribution.Origin);
        Assert.Equal(SlotsBusy, failure.BaselineAttribution.Evidence);
        Assert.Null(failure.BaselineAttribution.CandidateRerun);

        var regate = Assert.IsType<ApparatusRedDisposition.Regate>(fixture.Gate.Classify(fixture.Goal, reading));

        Assert.Equal(EvidenceKind, regate.EvidenceKind);
        Assert.Equal(new[] { Identity }, regate.TestIdentities);
        Assert.Equal(1, regate.RegateOrdinal);
        Assert.Equal(2, regate.RegateCap);
    }

    [Theory]
    [InlineData("changed-test", "lives inside the candidate's changed paths")]
    [InlineData("plain-baseline", "neither a recorded infrastructure signature")]
    [InlineData("failed-rerun", "failed again on the candidate")]
    [InlineData("changed-type", "references candidate-changed type Changed")]
    public void IneligibleBaselineFault_ReturnsGenuine(string scenario, string expectedReason)
    {
        var failure = new ApparatusRedFailingTest(
            "focused CLI infrastructure tests", Identity, null, [TestPath], false, false,
            FailureMessage: Failure,
            BaselineAttribution: new AcceptanceTestFailureAttribution(
                Identity, AcceptanceTestFailureOrigin.Unattributed, SlotsBusy));
        failure = scenario switch
        {
            "changed-test" => failure with { InsideChangedPaths = true },
            "plain-baseline" => failure with
            {
                BaselineAttribution = failure.BaselineAttribution! with { Evidence = "focused baseline test failed" }
            },
            "failed-rerun" => failure with { CandidateRerunFailed = true },
            "changed-type" => failure with { ReferencedChangedTypes = ["Changed"] },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var genuine = Assert.IsType<ApparatusRedDisposition.Genuine>(ApparatusRedClassifier.Classify(
            new ApparatusRedEvidence([ChangedPath], [failure], true, 0, 2)));

        Assert.Contains(expectedReason, genuine.Reason, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal Goal Goal { get; }
        internal ApparatusRedGate Gate { get; }

        internal Fixture()
        {
            var fullPath = Path.Combine(Root, TestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "namespace Sample.Tests; public sealed class UnchangedTests { }");
            (_, Goal) = ConductorDriverTests.SimpleGoal();
            var index = new AcceptanceFailingTestIndex(Path.Combine(Root, AcceptanceFailingTestIndex.FileName));
            Gate = new ApparatusRedGate(index, _ => Root, () => Now);
        }

        internal ApparatusRedGateReading Record()
        {
            var check = new AcceptanceCheckResult(
                "focused CLI infrastructure tests", false, 2, Failure,
                FailingTestIdentities: [Identity], TestProjectPath: "tests/Sample.Tests/Sample.Tests.csproj",
                FailingTestAttributions:
                [new AcceptanceTestFailureAttribution(
                    Identity, AcceptanceTestFailureOrigin.Unattributed, SlotsBusy)]);

            return Assert.IsType<ApparatusRedGateReading>(Gate.RecordGateCompletion(
                Goal,
                new AcceptanceVerificationSummary(false, [check], BranchHeadSha: "candidate-a"),
                new Lazy<IReadOnlyList<string>>(() => [ChangedPath])));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
