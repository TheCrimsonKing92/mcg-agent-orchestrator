using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ApparatusRedGateMessageFingerprintTests
{
    private const string Identity = "Sample.Tests.LaneGuardTests.NonParallelCollectionsCarryLaneName";

    [Fact]
    public void DifferentMessagesForSameGuardTestAreGenuine()
    {
        var result = ClassifyPair(
            "offending class OffendingCollectionXTests",
            "offending class OffendingCollectionYTests",
            "tests/Sample.Tests/OffendingCollectionYTests.cs");

        Assert.IsType<ApparatusRedDisposition.Genuine>(result);
    }

    [Fact]
    public void DifferentMessagesStayGenuineWithoutCandidateSymbolMatch()
    {
        var result = ClassifyPair("offending class X", "offending class Y", "src/Unrelated.cs");

        Assert.IsType<ApparatusRedDisposition.Genuine>(result);
    }

    [Fact]
    public void MessageNamingChangedClassIsGenuineEvenWhenFingerprintMatches()
    {
        var result = ClassifyPair(
            "offending class OffendingCollectionYTests",
            "offending class OffendingCollectionYTests",
            "tests/Sample.Tests/OffendingCollectionYTests.cs");

        Assert.IsType<ApparatusRedDisposition.Genuine>(result);
    }

    [Fact]
    public void MessageNamingChangedRepositoryPathIsGenuine()
    {
        var result = ClassifyPair(
            "offending source tests/Sample.Tests/A.cs",
            "offending source tests/Sample.Tests/A.cs",
            "tests/Sample.Tests/A.cs");

        Assert.IsType<ApparatusRedDisposition.Genuine>(result);
    }

    [Theory]
    [InlineData(@"C:\Users\ci\AppData\Local\Temp\run-one\out.log", "1532ms",
        @"D:\Temp\run-two\out.log", "2710ms")]
    [InlineData("/tmp/run-one/out.log", "2s", "/tmp/run-two/out.log", "7sec")]
    public void SameNormalizedMessageRegates(string firstPath, string firstDuration, string secondPath, string secondDuration)
    {
        var result = ClassifyPair(
            $"offending class SharedGuardFixture at {firstPath} after {firstDuration}",
            $"offending class SharedGuardFixture at {secondPath} after {secondDuration}",
            "src/Unrelated.cs");

        var regate = Assert.IsType<ApparatusRedDisposition.Regate>(result);
        Assert.Equal(ApparatusRedClassifier.CrossGoalEvidenceKind, regate.EvidenceKind);
        Assert.Equal(1, regate.RegateOrdinal);
    }

    [Fact]
    public void FailedCandidateRerunIsGenuineDespiteMatchingPriorFailure()
    {
        var result = ClassifyPair("shared guard failure", "shared guard failure", "src/Unrelated.cs", "Failed");

        var genuine = Assert.IsType<ApparatusRedDisposition.Genuine>(result);
        Assert.Contains("failed again on the candidate", genuine.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PassedCandidateRerunKeepsCrossGoalRegate()
    {
        var result = ClassifyPair("shared guard failure", "shared guard failure", "src/Unrelated.cs", "Passed");

        Assert.Equal(ApparatusRedClassifier.CrossGoalEvidenceKind,
            Assert.IsType<ApparatusRedDisposition.Regate>(result).EvidenceKind);
    }

    private static ApparatusRedDisposition ClassifyPair(
        string firstMessage,
        string secondMessage,
        string secondChangedPath,
        string? rerunOutcome = null)
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "tests", "Sample.Tests", "LaneGuardTests.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "namespace Sample.Tests; public sealed class LaneGuardTests {}");
            var index = new AcceptanceFailingTestIndex(Path.Combine(root, AcceptanceFailingTestIndex.FileName));
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z", null);
            var gate = new ApparatusRedGate(index, _ => root, () => now);
            var (_, firstGoal) = ConductorDriverTests.SimpleGoal();
            var (_, secondGoal) = ConductorDriverTests.SimpleGoal();
            gate.RecordGateCompletion(firstGoal, Summary(firstMessage),
                new Lazy<IReadOnlyList<string>>(() => ["tests/Sample.Tests/OffendingCollectionXTests.cs"]));
            var reading = gate.RecordGateCompletion(secondGoal, Summary(secondMessage, rerunOutcome),
                new Lazy<IReadOnlyList<string>>(() => [secondChangedPath]));
            var failingTest = Assert.Single(Assert.IsType<ApparatusRedGateReading>(reading).FailingTests);
            Assert.NotEmpty(failingTest.ResolvedSourcePaths);

            return gate.Classify(secondGoal, reading);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AcceptanceVerificationSummary Summary(string message, string? rerunOutcome = null)
    {
        var attribution = rerunOutcome is null
            ? null
            : new AcceptanceTestFailureAttribution(
                Identity,
                rerunOutcome == "Failed"
                    ? AcceptanceTestFailureOrigin.Introduced
                    : AcceptanceTestFailureOrigin.UnconfirmedIntroduced,
                "candidate rerun evidence",
                new CandidateFailureRerunEvidence(rerunOutcome, "candidate-rerun.trx"));
        var check = new AcceptanceCheckResult(
            "guard tests", false, 1, message,
            FailingTestIdentities: [Identity],
            TestProjectPath: "tests/Sample.Tests/Sample.Tests.csproj",
            FailingTestAttributions: attribution is null ? null : new[] { attribution });
        return new AcceptanceVerificationSummary(
            false, [check], FailedChecks: [check.Name], BranchHeadSha: "candidate-a", MainHeadSha: "main-a");
    }
}
