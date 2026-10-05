using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its source root and index; every gate uses the same fixed clock.
public sealed class ApparatusRedClassifierRerunNotExecutedTests
{
    private const string Identity = "Sample.Tests.UnchangedTests.FailsOnce";
    private const string TestPath = "tests/Sample.Tests/UnchangedTests.cs";
    private const string ChangedPath = "src/Changed.cs";
    private const string Failure = "System.TimeoutException : App CLI did not exit within 30 seconds.";
    private const string SlotsBusy = "DotnetBuildSlotsBusyException: Stable dotnet build slots busy for goal-x.";
    private const string EvidenceKind = "candidate-rerun-not-executed";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T05:00:00Z");

    [Theory]
    [InlineData(SlotsBusy)]
    [InlineData("BuildLockBlockedException: Build artifact lock blocked progress")]
    [InlineData("AcceptanceInfrastructureDeferredException: Acceptance infrastructure deferred")]
    public void RecognizedRerunFaultRegatesAndIndexesOriginalFailure(string error)
    {
        using var fixture = new Fixture();
        var reading = fixture.Record(fixture.Check(error));

        var failure = Assert.Single(reading.FailingTests);
        Assert.False(failure.CandidateRerunPassed);
        Assert.False(failure.CandidateRerunFailed);
        Assert.Null(failure.ExceptionSignature);
        Assert.Equal(new[] { TestPath }, failure.ResolvedSourcePaths);
        var regate = Assert.IsType<ApparatusRedDisposition.Regate>(fixture.Gate.Classify(fixture.Goal, reading));
        Assert.Equal(EvidenceKind, regate.EvidenceKind);
        Assert.Equal(new[] { Identity }, regate.TestIdentities);
        Assert.Equal(1, regate.RegateOrdinal);
        Assert.Equal(2, regate.RegateCap);
        var record = Assert.Single(fixture.Index.Read());
        Assert.Equal(EvidenceKind, record.EvidenceKind);
        Assert.Equal(AcceptanceFailingTestIndex.ComputeMessageFingerprint(Failure), record.MessageFingerprint);
    }

    [Theory]
    [InlineData("candidate rerunner unavailable")]
    [InlineData("harness timeout")]
    [InlineData(null)]
    [InlineData("")]
    public void UnmatchedOrAbsentRerunErrorRemainsGenuine(string? error)
    {
        using var fixture = new Fixture();

        var reading = fixture.Record(fixture.Check(error));

        Assert.IsType<ApparatusRedDisposition.Genuine>(fixture.Gate.Classify(fixture.Goal, reading));
        Assert.Null(Assert.Single(fixture.Index.Read()).EvidenceKind);
    }

    [Theory]
    [InlineData("Failed", AcceptanceTestFailureOrigin.Introduced)]
    [InlineData("Passed", AcceptanceTestFailureOrigin.Introduced)]
    [InlineData("NotExecuted", AcceptanceTestFailureOrigin.Inherited)]
    public void OnlyIntroducedNotExecutedAttributionQualifies(string outcome, AcceptanceTestFailureOrigin origin)
    {
        using var fixture = new Fixture();
        var check = fixture.Check(SlotsBusy) with
        {
            FailingTestAttributions:
            [new AcceptanceTestFailureAttribution(
                Identity, origin, "baseline evidence", new CandidateFailureRerunEvidence(outcome, null, SlotsBusy))]
        };

        var reading = fixture.Record(check);

        Assert.IsType<ApparatusRedDisposition.Genuine>(fixture.Gate.Classify(fixture.Goal, reading));
        Assert.Null(Assert.Single(fixture.Index.Read()).EvidenceKind);
    }

    [Fact]
    public void RerunErrorIsMatchedOnlyForItsOwnIdentity()
    {
        using var fixture = new Fixture();
        var check = fixture.Check("candidate rerunner unavailable");
        check = check with
        {
            FailingTestAttributions = check.FailingTestAttributions!.Concat(
                [new AcceptanceTestFailureAttribution(
                    "Sample.Tests.OtherTests.FailsOnce", AcceptanceTestFailureOrigin.Introduced,
                    "main green", new CandidateFailureRerunEvidence("NotExecuted", null, SlotsBusy))]).ToArray()
        };

        Assert.IsType<ApparatusRedDisposition.Genuine>(fixture.Gate.Classify(fixture.Goal, fixture.Record(check)));
        Assert.Null(Assert.Single(fixture.Index.Read()).EvidenceKind);
    }

    [Fact]
    public void AnyMatchingNotExecutedAttributionSuppliesEvidence()
    {
        using var fixture = new Fixture();
        var check = fixture.Check("candidate rerunner unavailable");
        check = check with
        {
            FailingTestAttributions = check.FailingTestAttributions!.Concat(
                fixture.Check(SlotsBusy).FailingTestAttributions!).ToArray()
        };

        var regate = Assert.IsType<ApparatusRedDisposition.Regate>(fixture.Gate.Classify(fixture.Goal, fixture.Record(check)));
        Assert.Equal(EvidenceKind, regate.EvidenceKind);
    }

    [Theory]
    [InlineData("unknown-paths", "candidate changed paths are unknown")]
    [InlineData("missing-identity", "a failing check carries no test identity")]
    [InlineData("unresolved-source", "could not be placed in a source file")]
    [InlineData("changed-source", "lives inside the candidate's changed paths")]
    [InlineData("candidate-path-message", "names a candidate changed path or type")]
    [InlineData("rerun-failed", "failed again on the candidate")]
    [InlineData("changed-type", "references candidate-changed type Changed")]
    public void EarlierGenuineRulesStillWin(string scenario, string reason)
    {
        using var fixture = new Fixture();
        var check = fixture.Check(SlotsBusy);
        IReadOnlyList<string> changedPaths = [ChangedPath];
        switch (scenario)
        {
            case "unknown-paths":
                changedPaths = [];
                break;
            case "missing-identity":
                check = check with { FailingTestIdentities = [] };
                break;
            case "unresolved-source":
                File.Delete(Path.Combine(fixture.Root, TestPath));
                break;
            case "changed-source":
                changedPaths = [TestPath];
                break;
            case "candidate-path-message":
                check = check with { OutputTail = "failure mentions src/Changed.cs" };
                break;
            case "rerun-failed":
                check = check with
                {
                    FailingTestAttributions = check.FailingTestAttributions!.Concat(
                        [new AcceptanceTestFailureAttribution(
                            Identity, AcceptanceTestFailureOrigin.Introduced, "main green",
                            new CandidateFailureRerunEvidence("Failed", "rerun.trx"))]).ToArray()
                };
                break;
            case "changed-type":
                fixture.Write(ChangedPath, "namespace Sample; public sealed class Changed { }");
                fixture.Write(TestPath, "namespace Sample.Tests; public sealed class UnchangedTests { private Changed subject; }");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var genuine = Assert.IsType<ApparatusRedDisposition.Genuine>(
            fixture.Gate.Classify(fixture.Goal, fixture.Record(check, changedPaths)));

        Assert.Contains(reason, genuine.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SiblingWithNoApparatusEvidenceKeepsWholeGateGenuine()
    {
        using var fixture = new Fixture();
        const string sibling = "Sample.Tests.OtherTests.FailsOnce";
        fixture.Write("tests/Sample.Tests/OtherTests.cs", "namespace Sample.Tests; public sealed class OtherTests { }");
        var check = fixture.Check(SlotsBusy) with { FailingTestIdentities = [Identity, sibling] };

        var genuine = Assert.IsType<ApparatusRedDisposition.Genuine>(
            fixture.Gate.Classify(fixture.Goal, fixture.Record(check)));

        Assert.Contains(sibling, genuine.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IndexedBlockedRerunCountsForLaterCrossGoalOccurrence()
    {
        using var fixture = new Fixture();
        fixture.Record(fixture.Check(SlotsBusy));
        var prior = Assert.Single(fixture.Index.Read());
        Assert.Equal(EvidenceKind, prior.EvidenceKind);
        var (_, laterGoal) = ConductorDriverTests.SimpleGoal();
        var reading = fixture.Record(fixture.Check("candidate rerunner unavailable"), goal: laterGoal);

        var regate = Assert.IsType<ApparatusRedDisposition.Regate>(fixture.Gate.Classify(laterGoal, reading));

        Assert.Equal("cross-goal-flake", regate.EvidenceKind);
        Assert.Equal(1, regate.RegateOrdinal);
    }

    [Fact]
    public void OwnInfrastructureSignatureKeepsItsExistingPrecedence()
    {
        using var fixture = new Fixture();
        var check = fixture.Check(SlotsBusy) with { OutputTail = "Build artifact lock blocked progress" };

        var regate = Assert.IsType<ApparatusRedDisposition.Regate>(fixture.Gate.Classify(fixture.Goal, fixture.Record(check)));

        Assert.Equal("infrastructure-exception", regate.EvidenceKind);
        Assert.Equal(EvidenceKind, Assert.Single(fixture.Index.Read()).EvidenceKind);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal Goal Goal { get; }
        internal AcceptanceFailingTestIndex Index { get; }
        internal ApparatusRedGate Gate { get; }

        internal Fixture()
        {
            Write(TestPath, "namespace Sample.Tests; public sealed class UnchangedTests { }");
            (_, Goal) = ConductorDriverTests.SimpleGoal();
            Index = new AcceptanceFailingTestIndex(Path.Combine(Root, AcceptanceFailingTestIndex.FileName));
            Gate = new ApparatusRedGate(Index, _ => Root, () => Now);
        }

        internal AcceptanceCheckResult Check(string? error) => new(
            "focused CLI infrastructure tests", false, 2, Failure,
            FailingTestIdentities: [Identity], TestProjectPath: "tests/Sample.Tests/Sample.Tests.csproj",
            FailingTestAttributions:
            [new AcceptanceTestFailureAttribution(
                Identity, AcceptanceTestFailureOrigin.Introduced, "focused identity was green at merge-base main-a",
                new CandidateFailureRerunEvidence("NotExecuted", null, error))]);

        internal ApparatusRedGateReading Record(
            AcceptanceCheckResult check, IReadOnlyList<string>? changedPaths = null, Goal? goal = null) =>
            Assert.IsType<ApparatusRedGateReading>(Gate.RecordGateCompletion(
                goal ?? Goal,
                new AcceptanceVerificationSummary(false, [check], BranchHeadSha: "candidate-a"),
                new Lazy<IReadOnlyList<string>>(() => changedPaths ?? [ChangedPath])));

        internal void Write(string path, string source)
        {
            var fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, source);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
