using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: all evidence, task transitions and clock state belong to each fixture.
public sealed class InconclusiveTesterRedRouteTests
{
    private const string Candidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string NextCandidate = "cccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void ReaderPreservesReceiptAndFailuresIncludingTheoryArguments()
    {
        var fixture = new Fixture();
        string[] tests = ["Namespace.AlphaTests.Fails", "AlphaTests.Method(x: 1.5)"];
        fixture.Record(tests: tests, selections: ["Infrastructure.Tests:Namespace.aLpHaTeStS"]);

        var failure = Assert.IsType<AttributedFailure>(fixture.Read());

        Assert.Equal("pre-tester-receipt", failure.Source);
        Assert.Equal("red-receipt", failure.ReceiptId);
        Assert.Equal(Candidate, failure.CandidateSha);
        Assert.Equal(tests, failure.FailingTests);
    }

    [Theory]
    [InlineData("unusable")]
    [InlineData("green")]
    [InlineData("started")]
    [InlineData("actionable-red")]
    public void ReaderDeclinesOtherOutcomes(string outcome)
    {
        var fixture = new Fixture();
        fixture.Record(outcome: outcome);
        Assert.Null(fixture.Read());
    }

    [Theory]
    [InlineData("ExternalTests.Fails")]
    [InlineData("MethodWithoutClass")]
    public void ReaderDeclinesFailuresOutsideExecutedClasses(string identity)
    {
        var fixture = new Fixture();
        fixture.Record(tests: [identity]);
        Assert.Null(fixture.Read());
    }

    [Theory]
    [InlineData("AlphaTests")]
    [InlineData("Infrastructure.Tests:AlphaTests")]
    public void ReaderDeclinesNotRunClasses(string notRun)
    {
        var fixture = new Fixture();
        fixture.Record(notRun: [notRun]);
        Assert.Null(fixture.Read());
    }

    [Fact]
    public void ReaderRequiresFailuresCurrentCandidateAndFailedInconclusiveTester()
    {
        var fixture = new Fixture();
        Assert.Null(fixture.Read());
        fixture.Record(tests: []);
        Assert.Null(fixture.Read());
        fixture.Record();
        Assert.Null(InconclusiveTesterRedRoute.ReadPreTesterRed(fixture.Goal, fixture.Tester, null));
        Assert.Null(InconclusiveTesterRedRoute.ReadPreTesterRed(fixture.Goal, fixture.Tester, ""));
        Assert.Null(InconclusiveTesterRedRoute.ReadPreTesterRed(fixture.Goal, fixture.Tester, NextCandidate));
        Assert.Null(InconclusiveTesterRedRoute.ReadPreTesterRed(fixture.Goal, fixture.Developer, Candidate));

        var failed = new Fixture("fail");
        failed.Record();
        Assert.NotEqual(DispatchOutcomeKind.VerificationInconclusive,
            DispatchFailureClassifier.Classify(failed.Tester, failed.Tester.LastVerification!).Kind);
        Assert.Null(failed.Read());

        fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, fixture.Tester.Id, "Retry", RetryCause.Unknown);
        Assert.Null(fixture.Read());
    }

    [Fact]
    public void NewestCurrentCandidateEntryControlsTheReader()
    {
        var fixture = new Fixture();
        fixture.Record();
        fixture.Record(outcome: "started");
        Assert.Null(fixture.Read());
    }

    [Theory]
    [InlineData("failed-terminal-fallthrough", false)]
    [InlineData("verification-inconclusive-unchanged-inputs", true)]
    public void DecisionRequiresInconclusiveEvidenceAndTheNamedTester(string evidence, bool otherTask)
    {
        var fixture = new Fixture();
        fixture.Record();
        var decision = fixture.Decision(evidence) with
        {
            Identity = new(fixture.Goal.Id, otherTask ? fixture.Developer.Id : fixture.Tester.Id, null, "fixed")
        };
        Assert.False(InconclusiveTesterRedRoute.Decide(fixture.Goal, decision, Candidate).IsRoute);
        Assert.False(InconclusiveTesterRedRoute.Decide(fixture.Goal,
            fixture.Decision() with { Identity = new(fixture.Goal.Id, TaskId.New(), null, "fixed") }, Candidate).IsRoute);
    }

    [Theory]
    [InlineData("verification-inconclusive")]
    [InlineData("verification-inconclusive-unchanged-inputs")]
    [InlineData("verification-inconclusive-budget-exhausted")]
    public void DecisionReusesCandidateAndSignatureRepeatBound(string evidence)
    {
        var fixture = new Fixture();
        fixture.Record();
        var first = InconclusiveTesterRedRoute.Decide(fixture.Goal, fixture.Decision(evidence), Candidate);
        Assert.True(first.IsRoute);
        Assert.Equal(fixture.Developer.Id, first.DeveloperTaskId);
        fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, fixture.Developer.Id, first.Feedback!, RetryCause.NewTestFinding);
        fixture.CompleteDeveloper();
        fixture.FailTester("inconclusive");
        Assert.Single(AttributedFailureDeveloperRouteTests.MarkerRetries(fixture.Goal));

        var repeated = InconclusiveTesterRedRoute.Decide(fixture.Goal, fixture.Decision(evidence), Candidate);
        Assert.Equal("already-routed", repeated.DeclineReason);
        fixture.Record(candidate: NextCandidate);
        Assert.True(InconclusiveTesterRedRoute.Decide(fixture.Goal, fixture.Decision(evidence), NextCandidate).IsRoute);
    }

    private sealed class Fixture
    {
        private readonly FixtureClock _clock = new();
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; } = new(TaskId.New(), "Implement", AgentRole.Developer);
        public TaskSpec Tester { get; } = new(TaskId.New(), "Verify", AgentRole.Tester);

        public Fixture(string status = "inconclusive")
        {
            Kernel = new(_clock);
            Goal = Kernel.CreateGoal("Route receipt evidence", [Developer, Tester]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            CompleteDeveloper();
            FailTester(status);
        }

        public void CompleteDeveloper()
        {
            Kernel.RecordTaskDispatch(Goal.Id, Developer.Id, new("fixture", "implement", Path.GetTempPath(), _clock.Next()));
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id,
                new("implement", Path.GetTempPath(), 0, "ok", "", _clock.Next(), HasCommittedChanges: true));
            Assert.Equal(WorkTaskStatus.Completed, Developer.Status);
        }

        public void FailTester(string status)
        {
            Kernel.RecordTaskDispatch(Goal.Id, Tester.Id, new("fixture", "verify", Path.GetTempPath(), _clock.Next()));
            Kernel.RecordDispatchExecutionResult(Goal.Id, Tester.Id,
                new("verify", Path.GetTempPath(), 0,
                    $"WORKER_RESULT:\nfiles: none\ntests: {status} - retained evidence\nblockers: none\nEND_WORKER_RESULT",
                    "", _clock.Next(), WorkerResultPresent: true, DispatchStartedAt: Tester.LastDispatch!.DispatchedAt));
            Assert.Equal(WorkTaskStatus.Failed, Tester.Status);
        }

        public void Record(string outcome = "red", string candidate = Candidate, string[]? tests = null,
            string[]? selections = null, string[]? notRun = null) =>
            Kernel.RecordFindingEvidenceRun(Goal.Id, Tester.Id, PreTesterEvidenceIndexLines.FormatMarker(
                new(outcome, candidate, "red-receipt", selections ?? ["Infrastructure.Tests:AlphaTests"],
                    notRun ?? [], null, tests ?? ["AlphaTests.Fails"])));

        public AttributedFailure? Read() => InconclusiveTesterRedRoute.ReadPreTesterRed(Goal, Tester, Candidate);

        public FailedGoalRecoveryDecision Decision(string evidence = "verification-inconclusive") =>
            new(FailedGoalRecoveryAction.RetryTransient, new(Goal.Id, Tester.Id, null, "fixed"), 3,
                evidence, "Tester inconclusive", RetryCause.EnvironmentApparatusFailure);
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Next() => UtcNow = UtcNow.AddMinutes(1);
    }
}
