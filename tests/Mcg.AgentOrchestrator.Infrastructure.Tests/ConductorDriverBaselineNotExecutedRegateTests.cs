using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: each case owns its source root, TRX receipts, index and journal; time is fixed.
public sealed class ConductorDriverBaselineNotExecutedRegateTests
{
    private const string Identity = "Sample.Tests.UnchangedTests.FailsOnce";
    private const string TestPath = "tests/Sample.Tests/UnchangedTests.cs";
    private const string ChangedPath = "src/Changed.cs";
    private const string CheckName = "focused CLI infrastructure tests";
    private const string Failure = "System.TimeoutException : App CLI did not exit within 30 seconds.";
    private const string SlotsBusy = "focused baseline execution failed: DotnetBuildSlotsBusyException";
    private const string EvidenceKind = "baseline-not-executed";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T05:00:52Z");

    [Fact]
    public void SlotsBusyBaseline_RestoresVerifiedWithoutReopeningWorker()
    {
        using var fixture = new Fixture();

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);

        Assert.Equal(1, fixture.AcceptanceRuns);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Contains(EvidenceKind, held.Reason, StringComparison.Ordinal);
        Assert.Contains("1/2", held.Reason, StringComparison.Ordinal);
        Assert.NotNull(held.Decision);
        Assert.Equal(EvidenceKind, held.Decision.DiscriminatingEvidence);
        AssertNoWorkerReopened(fixture);
        Assert.Equal(1, AcceptanceFailingTestIndex.CountRegates(fixture.Index.Read(), fixture.Goal.Id.Value));
        var census = Assert.Single(fixture.Index.Read().Where(record =>
            record.Kind == AcceptanceFailingTestIndexKinds.GateFailure));
        Assert.Equal(Identity, census.TestIdentity);
        // Gate-failure census precedes classification; the regate record owns its evidence kind.
        Assert.Null(census.EvidenceKind);
        var regate = Assert.Single(fixture.Index.Read().Where(record =>
            record.Kind == AcceptanceFailingTestIndexKinds.ApparatusRegate));
        Assert.Equal(EvidenceKind, regate.EvidenceKind);
        var journal = Assert.Single(GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries.Where(entry =>
            entry.Operation == GoalOperationJournal.AcceptanceApparatusRegateOperation));
        Assert.Contains("evidence=" + EvidenceKind, journal.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries, entry =>
            entry.Operation == GoalOperationJournal.AcceptanceApparatusGenuineOperation);
    }

    [Theory]
    [InlineData("changed-test", "lives inside the candidate's changed paths")]
    [InlineData("plain-baseline", "neither a recorded infrastructure signature")]
    [InlineData("failed-rerun", "failed again on the candidate")]
    [InlineData("changed-type", "references candidate-changed type Changed")]
    public void IneligibleBaselineFault_ReopensDeveloper(string scenario, string expectedReason)
    {
        using var fixture = new Fixture(scenario);
        var reading = Assert.IsType<ApparatusRedGateReading>(fixture.Gate.RecordGateCompletion(
            fixture.Goal, fixture.Summary, new(() => fixture.ChangedPaths)));
        var failure = Assert.Single(reading.FailingTests);
        Assert.Equal(AcceptanceTestFailureOrigin.Unattributed, failure.BaselineAttribution!.Origin);
        Assert.Equal(scenario == "plain-baseline" ? "focused baseline test failed" : SlotsBusy,
            failure.BaselineAttribution.Evidence);
        Assert.Null(failure.ExceptionSignature);
        Assert.False(failure.HasCrossGoalOccurrence);
        Assert.Equal(scenario == "changed-test", failure.InsideChangedPaths);
        Assert.Equal(scenario == "failed-rerun", failure.CandidateRerunFailed);
        Assert.Equal(scenario == "changed-type" ? new[] { "Changed" } : Array.Empty<string>(),
            failure.ReferencedChangedTypes);
        var genuine = Assert.IsType<ApparatusRedDisposition.Genuine>(fixture.Gate.Classify(fixture.Goal, reading));
        Assert.Contains(expectedReason, genuine.Reason, StringComparison.Ordinal);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Advance().Outcome);

        Assert.Equal(1, fixture.AcceptanceRuns);
        Assert.Equal(1, fixture.RetryCalls);
        var task = Assert.Single(fixture.Goal.Tasks);
        Assert.Equal(AgentRole.Developer, task.RequiredRole);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Equal(0, AcceptanceFailingTestIndex.CountRegates(fixture.Index.Read(), fixture.Goal.Id.Value));
        var journal = Assert.Single(GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries.Where(entry =>
            entry.Operation == GoalOperationJournal.AcceptanceApparatusGenuineOperation));
        Assert.Contains(expectedReason, journal.Detail, StringComparison.Ordinal);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void RepeatedSlotsBusyBaseline_ExhaustsCapAndEscalatesWithoutRetry()
    {
        using var fixture = new Fixture();
        for (var ordinal = 1; ordinal <= 2; ordinal++)
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Contains($"{ordinal}/2", held.Reason, StringComparison.Ordinal);
            Assert.Contains(EvidenceKind, held.Reason, StringComparison.Ordinal);
            Assert.Equal(ordinal, AcceptanceFailingTestIndex.CountRegates(fixture.Index.Read(), fixture.Goal.Id.Value));
            AssertNoWorkerReopened(fixture);
        }

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(fixture.Advance().Outcome);

        Assert.Equal(3, fixture.AcceptanceRuns);
        Assert.Equal(GoalLifecycleState.Verified, escalated.State);
        Assert.Contains(ApparatusRedClassifier.BoundExhaustedToken, escalated.Reason, StringComparison.Ordinal);
        Assert.Contains(EvidenceKind, escalated.Reason, StringComparison.Ordinal);
        AssertNoWorkerReopened(fixture);
        Assert.Equal(2, AcceptanceFailingTestIndex.CountRegates(fixture.Index.Read(), fixture.Goal.Id.Value));
        Assert.Equal(3, fixture.Index.Read().Count(record => record.Kind == AcceptanceFailingTestIndexKinds.GateFailure));
    }

    private static void AssertNoWorkerReopened(Fixture fixture)
    {
        Assert.Equal(0, fixture.RetryCalls);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        var task = Assert.Single(fixture.Goal.Tasks);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(0, task.CriterionRetryCount);
    }

    private sealed class Fixture : IDisposable
    {
        private const string Attempt = "baseline-not-executed-attempt";
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal AcceptanceFailingTestIndex Index { get; }
        internal ApparatusRedGate Gate { get; }
        internal AcceptanceVerificationSummary Summary { get; }
        internal IReadOnlyList<string> ChangedPaths { get; }
        internal int RetryCalls { get; private set; }
        internal int AcceptanceRuns { get; private set; }

        internal Fixture(string? scenario = null)
        {
            var source = Path.Combine(Root, TestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, scenario == "changed-type"
                ? "namespace Sample.Tests; public sealed class UnchangedTests { private Changed? subject; }"
                : "namespace Sample.Tests; public sealed class UnchangedTests { }");
            var changedSource = Path.Combine(Root, ChangedPath);
            Directory.CreateDirectory(Path.GetDirectoryName(changedSource)!);
            File.WriteAllText(changedSource, "namespace Sample; public sealed class Changed { }");
            ChangedPaths = scenario == "changed-test" ? [TestPath] : [ChangedPath];
            (Kernel, Goal) = SimpleGoal();
            PassVerification(Kernel, Goal, Goal.Tasks.Single());
            Index = new AcceptanceFailingTestIndex(Path.Combine(Root, AcceptanceFailingTestIndex.FileName));
            Gate = new ApparatusRedGate(Index, _ => Root, () => Now);
            var check = new AcceptanceCheckResult(
                CheckName, false, 2, Failure,
                FailingTestIdentities: [Identity], TestProjectPath: "tests/Sample.Tests/Sample.Tests.csproj",
                FailingTestAttributions:
                [new AcceptanceTestFailureAttribution(Identity, AcceptanceTestFailureOrigin.Unattributed,
                    scenario == "plain-baseline" ? "focused baseline test failed" : SlotsBusy)]);
            if (scenario == "failed-rerun")
            {
                // A bound lane rerun proves candidate failure while preserving the Unattributed baseline.
                var first = WriteFailedTrx("first.trx");
                var rerun = WriteFailedTrx("rerun.trx");
                var invocation = Attempt + ":" + AcceptanceArtifactFileNames.Slug(CheckName) + ":";
                check = check with
                {
                    TestResultPaths = [first, rerun], TestResultAttemptId = Attempt, TestResultRunOrdinal = 1,
                    ExecutedTestCount = 1, CompletionDecision = new(false, "failing-trx", false, 1, 1, 1, "Failed"),
                    LaneRerun = new(invocation + "0", [first], [Identity], "failing-trx",
                        invocation + "1", [rerun], [Identity], "failing-trx", AcceptanceLaneRerunEvidence.ConfirmedFailure)
                };
                check = AcceptanceLaneRerunAttributionReader.Attach(check, Goal.Id.Value, "candidate-a", "main-a");
                Assert.NotNull(check.LaneRerun!.Attribution);
            }
            Summary = new AcceptanceVerificationSummary(
                false, [check], FailedChecks: [check.Name], BranchHeadSha: "candidate-a", MainHeadSha: "main-a",
                AttemptId: Attempt,
                CheckAttributions:
                [new AcceptanceCheckAttribution(check.Name, AcceptanceFailureOrigin.Unattributed, SlotsBusy)]);
        }

        private string WriteFailedTrx(string name)
        {
            var path = Path.Combine(Root, name);
            new XDocument(new XElement("TestRun", new XAttribute("id", Guid.NewGuid()),
                new XElement("Results", new XElement("UnitTestResult",
                    new XAttribute("testName", Identity), new XAttribute("outcome", "Failed"),
                    new XElement("Output", new XElement("ErrorInfo", new XElement("Message", Failure))))))).Save(path);
            return path;
        }

        internal ConductorAdvanceResult Advance() => MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => { AcceptanceRuns++; return Summary; },
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                RetryCalls++;
                return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
            },
            recordCriterionRetryFeedback: Kernel.RecordCriterionRetryFeedback,
            getLandingFileScopes: _ => ChangedPaths,
            executionDirectory: Root,
            apparatusRedGate: Gate)
            .AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
