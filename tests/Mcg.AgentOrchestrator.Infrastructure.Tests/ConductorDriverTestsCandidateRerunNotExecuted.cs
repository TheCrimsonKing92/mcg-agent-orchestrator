using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: every case owns its source root, index and journal and uses a fixed gate clock.
public sealed class ConductorDriverTestsCandidateRerunNotExecuted
{
    private const string Identity = "Sample.Tests.UnchangedTests.FailsOnce";
    private const string TestPath = "tests/Sample.Tests/UnchangedTests.cs";
    private const string ChangedPath = "src/Changed.cs";
    private const string Failure = "System.TimeoutException : App CLI did not exit within 30 seconds.";
    private const string SlotsBusy = "DotnetBuildSlotsBusyException: Stable dotnet build slots busy for goal-x.";
    private const string EvidenceKind = "candidate-rerun-not-executed";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-05T05:00:00Z");

    [Fact]
    public void SlotsBusyRerunRestoresVerifiedWithoutReopeningWorker()
    {
        using var fixture = new Fixture();

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);

        Assert.Equal(1, fixture.AcceptanceRuns);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Contains(EvidenceKind, held.Reason, StringComparison.Ordinal);
        Assert.Contains("1/2", held.Reason, StringComparison.Ordinal);
        AssertNoWorkerReopened(fixture);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(1, AcceptanceFailingTestIndex.CountRegates(fixture.Index.Read(), fixture.Goal.Id.Value));
        var census = Assert.Single(fixture.Index.Read().Where(record =>
            record.Kind == AcceptanceFailingTestIndexKinds.GateFailure));
        Assert.Equal(EvidenceKind, census.EvidenceKind);
        Assert.Equal(Identity, census.TestIdentity);
        Assert.Equal(AcceptanceFailingTestIndex.ComputeMessageFingerprint(Failure), census.MessageFingerprint);
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
    [InlineData("candidate rerunner unavailable")]
    [InlineData("harness timeout")]
    public void UnrecognizedRerunErrorKeepsGenuineDeveloperRetry(string error)
    {
        using var fixture = new Fixture { Error = error };

        Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Advance().Outcome);

        AssertDeveloperReopened(fixture);
        var journal = Assert.Single(GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries.Where(entry =>
            entry.Operation == GoalOperationJournal.AcceptanceApparatusGenuineOperation));
        Assert.Contains("neither a recorded infrastructure signature", journal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedTestSourceWinsOverSlotsBusyRerun()
    {
        using var fixture = new Fixture { ChangedPaths = [TestPath] };

        Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Advance().Outcome);

        AssertDeveloperReopened(fixture);
        var journal = Assert.Single(GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries.Where(entry =>
            entry.Operation == GoalOperationJournal.AcceptanceApparatusGenuineOperation));
        Assert.Contains("lives inside the candidate's changed paths", journal.Detail, StringComparison.Ordinal);
    }

    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void ThirdSlotsBusyFailureExhaustsSharedRegateCap()
    {
        using var fixture = new Fixture();
        for (var ordinal = 1; ordinal <= 2; ordinal++)
        {
            // A fresh driver and gate each time read the count from the same durable index.
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
        Assert.Equal(2, GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries.Count(entry =>
            entry.Operation == GoalOperationJournal.AcceptanceApparatusRegateOperation));
    }

    private static void AssertNoWorkerReopened(Fixture fixture)
    {
        Assert.False(fixture.RetryCalled);
        Assert.All(fixture.Goal.Tasks, task =>
        {
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Equal(0, task.CriterionRetryCount);
        });
    }

    private static void AssertDeveloperReopened(Fixture fixture)
    {
        Assert.Equal(1, fixture.AcceptanceRuns);
        Assert.True(fixture.RetryCalled);
        var task = Assert.Single(fixture.Goal.Tasks);
        Assert.Equal(AgentRole.Developer, task.RequiredRole);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Equal(0, AcceptanceFailingTestIndex.CountRegates(fixture.Index.Read(), fixture.Goal.Id.Value));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal AcceptanceFailingTestIndex Index { get; }
        internal string Error { get; set; } = SlotsBusy;
        internal IReadOnlyList<string> ChangedPaths { get; set; } = [ChangedPath];
        internal bool RetryCalled { get; private set; }
        internal int AcceptanceRuns { get; private set; }

        internal Fixture()
        {
            var source = Path.Combine(Root, TestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "namespace Sample.Tests; public sealed class UnchangedTests { }");
            (Kernel, Goal) = SimpleGoal();
            PassVerification(Kernel, Goal, Goal.Tasks.Single());
            Index = new AcceptanceFailingTestIndex(Path.Combine(Root, AcceptanceFailingTestIndex.FileName));
        }

        internal ConductorAdvanceResult Advance() => MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ =>
            {
                AcceptanceRuns++;
                var check = new AcceptanceCheckResult(
                    "focused CLI infrastructure tests", false, 2, Failure,
                    FailingTestIdentities: [Identity],
                    TestProjectPath: "tests/Sample.Tests/Sample.Tests.csproj",
                    FailingTestAttributions:
                    [new AcceptanceTestFailureAttribution(
                        Identity, AcceptanceTestFailureOrigin.Introduced,
                        "focused identity was green at merge-base main-a",
                        new CandidateFailureRerunEvidence("NotExecuted", null, Error))]);
                return new AcceptanceVerificationSummary(
                    false, [check], FailedChecks: [check.Name], BranchHeadSha: "candidate-a", MainHeadSha: "main-a",
                    CheckAttributions:
                    [new AcceptanceCheckAttribution(check.Name, AcceptanceFailureOrigin.Introduced, "main green")]);
            },
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                RetryCalled = true;
                return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
            },
            recordCriterionRetryFeedback: Kernel.RecordCriterionRetryFeedback,
            getLandingFileScopes: _ => ChangedPaths,
            executionDirectory: Root,
            apparatusRedGate: new ApparatusRedGate(Index, _ => Root, () => Now))
            .AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
