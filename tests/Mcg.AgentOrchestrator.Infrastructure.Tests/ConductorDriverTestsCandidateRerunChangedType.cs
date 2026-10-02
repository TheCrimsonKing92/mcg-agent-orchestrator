using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

// Parallel-safe: each case owns its source root, index and journal, with an injected fixed clock.
public sealed class ConductorDriverTestsCandidateRerunChangedType
{
    private const string Identity = "Sample.Tests.UnchangedTests.FailsOnce";
    private const string ChangedPath = "src/Sample.App/CliCommandHandlers.cs";
    private const string TestPath = "tests/Sample.Tests/UnchangedTests.cs";
    private const string Failure =
        "System.IO.IOException : The process cannot access the file 'run-events.db' because it is being used by another process.";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T12:00:00Z");

    [Fact]
    public void ChangedTypeWithOnlyPassingRerunReopensWorkerAndNamesType()
    {
        using var fixture = new Fixture();

        var result = fixture.Advance();

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.True(fixture.RetryCalled);
        var task = fixture.Goal.Tasks.Single();
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.DoesNotContain(fixture.Index.Read(), record =>
            record.Kind == AcceptanceFailingTestIndexKinds.ApparatusRegate);
        var entry = Assert.Single(GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries.Where(candidate =>
            candidate.Operation == GoalOperationJournal.AcceptanceApparatusGenuineOperation));
        Assert.Contains(Identity, entry.Detail, StringComparison.Ordinal);
        Assert.Contains("references candidate-changed type CliCommandHandlers", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("one passing candidate rerun", entry.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "cross-goal-flake")]
    [InlineData(true, "infrastructure-exception")]
    public void IndependentApparatusEvidenceStillRegatesWithChangedType(bool signature, string expectedKind)
    {
        using var fixture = new Fixture();
        if (signature)
        {
            fixture.Message = "Stable dotnet build slots busy";
        }
        else
        {
            fixture.Index.Append(
                [new AcceptanceFailingTestIndexRecord(
                    AcceptanceFailingTestIndex.ContractVersion,
                    AcceptanceFailingTestIndexKinds.GateFailure,
                    "prior-goal",
                    Now.AddHours(-1),
                    TestIdentity: Identity,
                    InsideChangedPaths: false,
                    MessageFingerprint: AcceptanceFailingTestIndex.ComputeMessageFingerprint(Failure))],
                Now);
        }

        AssertRegate(fixture, expectedKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingChangedFileOrUnreferencedTypeKeepsPassingRerunRegate(bool unreferenced)
    {
        using var fixture = new Fixture();
        if (unreferenced)
        {
            fixture.Write(ChangedPath, "namespace Sample; internal class RunEventStore { }");
        }
        else
        {
            File.Delete(Path.Combine(fixture.Root, ChangedPath));
        }

        AssertRegate(fixture, ApparatusRedClassifier.CandidateRerunEvidenceKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GateRecordsMatchesAndLockedChangedSourceKeepsExistingDisposition(bool locked)
    {
        using var fixture = new Fixture();
        using var fileLock = locked
            ? new FileStream(Path.Combine(fixture.Root, ChangedPath), FileMode.Open, FileAccess.Read, FileShare.None)
            : null;

        var reading = Assert.IsType<ApparatusRedGateReading>(fixture.Gate.RecordGateCompletion(
            fixture.Goal, fixture.Summary(), new Lazy<IReadOnlyList<string>>(() => [ChangedPath])));

        var failure = Assert.Single(reading.FailingTests);
        Assert.Equal(new[] { TestPath }, failure.ResolvedSourcePaths);
        Assert.True(failure.CandidateRerunPassed);
        Assert.False(failure.InsideChangedPaths);
        if (locked)
        {
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(failure.ReferencedChangedTypes));
            Assert.Equal(ApparatusRedClassifier.CandidateRerunEvidenceKind,
                Assert.IsType<ApparatusRedDisposition.Regate>(fixture.Gate.Classify(fixture.Goal, reading)).EvidenceKind);
        }
        else
        {
            Assert.Equal(new[] { "CliCommandHandlers" }, failure.ReferencedChangedTypes);
            Assert.Contains("CliCommandHandlers",
                Assert.IsType<ApparatusRedDisposition.Genuine>(fixture.Gate.Classify(fixture.Goal, reading)).Reason,
                StringComparison.Ordinal);
        }
    }

    private static void AssertRegate(Fixture fixture, string expectedKind)
    {
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Contains(expectedKind, held.Reason, StringComparison.Ordinal);
        Assert.False(fixture.RetryCalled);
        Assert.Equal(0, fixture.Goal.Tasks.Single().CriterionRetryCount);
        var record = Assert.Single(fixture.Index.Read().Where(candidate =>
            candidate.Kind == AcceptanceFailingTestIndexKinds.ApparatusRegate));
        Assert.Equal(expectedKind, record.EvidenceKind);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();
        internal AgentOrchestratorKernel Kernel { get; }
        internal Goal Goal { get; }
        internal AcceptanceFailingTestIndex Index { get; }
        internal ApparatusRedGate Gate { get; }
        internal string Message { get; set; } = Failure;
        internal bool RetryCalled { get; private set; }

        internal Fixture()
        {
            Write(TestPath,
                "namespace Sample.Tests; public sealed class UnchangedTests { private CliCommandHandlers? subject; }");
            Write(ChangedPath, "namespace Sample; internal static partial class CliCommandHandlers { }");
            (Kernel, Goal) = SimpleGoal();
            PassVerification(Kernel, Goal, Goal.Tasks.Single());
            Index = new AcceptanceFailingTestIndex(Path.Combine(Root, AcceptanceFailingTestIndex.FileName));
            Gate = new ApparatusRedGate(Index, _ => Root, () => Now);
        }

        internal void Write(string path, string source)
        {
            var fullPath = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, source);
        }

        internal AcceptanceVerificationSummary Summary()
        {
            var check = new AcceptanceCheckResult(
                Identity, false, 1, Message,
                FailingTestIdentities: [Identity],
                TestProjectPath: "tests/Sample.Tests/Sample.Tests.csproj",
                FailingTestAttributions:
                [new AcceptanceTestFailureAttribution(
                    Identity, AcceptanceTestFailureOrigin.UnconfirmedIntroduced, "main green",
                    new CandidateFailureRerunEvidence("Passed", "candidate-rerun.trx"))]);
            return new AcceptanceVerificationSummary(
                false, [check], FailedChecks: [check.Name], BranchHeadSha: "candidate-a", MainHeadSha: "main-a",
                CheckAttributions:
                [new AcceptanceCheckAttribution(check.Name, AcceptanceFailureOrigin.Introduced, "main green")]);
        }

        internal ConductorAdvanceResult Advance() => MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => Summary(),
            retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
            {
                RetryCalled = true;
                return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
            },
            recordCriterionRetryFeedback: Kernel.RecordCriterionRetryFeedback,
            getLandingFileScopes: _ => [ChangedPath],
            executionDirectory: Root,
            apparatusRedGate: Gate).AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
