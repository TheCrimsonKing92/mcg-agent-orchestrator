using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AttributedFailureDeveloperRouteTests;
using static AttributedFailureAutoRouteScenarioTests;

// Parallel-safe: each case owns its kernel, TRX directory and SQLite database.
public sealed class AttributedCohortFailureMessageRouteTests
{
    private const string DeveloperName =
        "WorkerTargetHomeBrokerContextTests.NonHomeRemovesOnlyTheEvidenceBroker(role: Developer)";
    private const string PlannerName =
        "WorkerTargetHomeBrokerContextTests.NonHomeRemovesOnlyTheEvidenceBroker(role: Planner)";
    private const string IncidentPartition =
        "cohort-lane-scoped-partition-v1-c63074b3075b7a4ec80ee928d394ee52364fc8c26ecab1f8973117b456d941d8";
    // Exact attribution text from eec2502c's 06:14Z lifecycle decision; its reason adds a prose prefix.
    private const string IncidentEvidence =
        "cohort-attribution=cohort-v2-ec07d31ef78adbe4215590be0e4e0e983b2729f66eb91d70d6808d07da099377 " +
        "cohort-partition=" + IncidentPartition + " reproduced=" + DeveloperName + "," + PlannerName;
    private static readonly string[] IncidentNames = [DeveloperName, PlannerName];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Eec2502c_SpacedRoleCases_RouteDeveloperInsteadOfEscalating(bool literalRecord)
    {
        using var files = new FixtureFiles();
        var (kernel, first, second) = CohortGoals();
        if (literalRecord)
        {
            RecordCohortFailure(kernel, second, evidence: IncidentEvidence);
            Assert.All(second.LatestAcceptanceFailure!.CheckAttributions!,
                attribution => Assert.Equal(IncidentEvidence, attribution.Evidence));
        }
        else
        {
            var trx = files.WriteTrx();
            var verdict = ApplyMember(kernel, first, second, [trx]);
            Assert.True(verdict.ShouldRecord);
            Assert.Equal(IncidentPartition, verdict.PartitionReceiptId);
            Assert.Equal("cohort-attribution=" + CreateIdentity(first, second).Value +
                " cohort-partition=" + IncidentPartition + " reproduced=" + string.Join(',', IncidentNames),
                verdict.Detail.Split('\n')[0]);
            Assert.Null(first.LatestAcceptanceFailure);
            Assert.True(kernel.RouteRecordedAcceptanceFailure(second.Id, "Attributed cohort failure recorded."));
        }

        Assert.Equal(GoalStatus.AcceptanceFailed, second.Status);
        var failure = Assert.IsType<AttributedFailure>(
            AttributedFailureDeveloperRoute.ReadAcceptanceFailure(second.LatestAcceptanceFailure));
        Assert.Equal(IncidentNames, failure.FailingTests);
        Assert.Equal(Candidate, failure.CandidateSha);
        Assert.Empty(MarkerRetries(second));
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 1);

        var result = driver.AdvanceOnce(second, ConductorAutonomyPolicy.Permissive);

        Assert.False(result.WasEscalated);
        AssertRoute(second, second.Tasks.Single(), harness, IncidentPartition, Candidate, IncidentNames);
        Assert.Empty(harness.Escalations);
        Assert.Empty(MarkerRetries(first));
    }

    [Fact]
    public void UnreproducedMember_SkipsRecordingAndEmptyRecordEscalates()
    {
        var (kernel, first, second) = CohortGoals();
        var verdict = ApplyMember(kernel, first, second, [], reproduced: false);
        Assert.False(verdict.ShouldRecord);
        Assert.Equal("not-attributed", verdict.Reason);
        Assert.Null(second.LatestAcceptanceFailure);
        RecordCohortFailure(kernel, second, evidence: IncidentEvidence[..IncidentEvidence.IndexOf("reproduced=", StringComparison.Ordinal)] +
            "reproduced=");
        var failure = Assert.IsType<AttributedFailure>(
            AttributedFailureDeveloperRoute.ReadAcceptanceFailure(second.LatestAcceptanceFailure));
        Assert.Empty(failure.FailingTests);
        AssertDeclinedWithoutRetry(kernel, second);
    }

    [Fact]
    public void Apparatus_WithSpacedNamesAndMessages_EscalatesWithoutRetry()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        RecordCohortFailure(kernel, goal, evidence: IncidentEvidence + Detail("apparatus message"),
            cause: AcceptanceFailureCause.EnvironmentalApparatus);
        var failure = Assert.IsType<AttributedFailure>(
            AttributedFailureDeveloperRoute.ReadAcceptanceFailure(goal.LatestAcceptanceFailure));
        Assert.Equal(IncidentNames, failure.FailingTests);
        Assert.Contains("apparatus message", failure.ReproductionDetail);
        Assert.Equal("classified-acceptance-failure",
            AttributedFailureDeveloperRoute.Decide(goal, failure, Candidate).DeclineReason);
        AssertDeclinedWithoutRetry(kernel, goal);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void SameCandidateAndNames_ChangedMessages_DoNotResetRetryCap()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        RecordCohortFailure(kernel, goal, evidence: IncidentEvidence + Detail("first message"));
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 1);
        Assert.False(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).WasEscalated);
        AssertRoute(goal, goal.Tasks.Single(), harness, IncidentPartition, Candidate, IncidentNames);
        Assert.Contains("first message", Assert.Single(MarkerRetries(goal)).Message);

        var developer = goal.Tasks.Single();
        CompleteRecordedProcess(kernel, goal, developer);
        kernel.RecordTaskVerification(goal.Id, developer.Id,
            new("fixture", Path.GetTempPath(), 0, "No new commit", "", DateTimeOffset.UtcNow, HasCommittedChanges: false));
        RecordCohortFailure(kernel, goal, evidence: IncidentEvidence + Detail("different message"));
        var failure = Assert.IsType<AttributedFailure>(
            AttributedFailureDeveloperRoute.ReadAcceptanceFailure(goal.LatestAcceptanceFailure));
        Assert.Contains("different message", failure.ReproductionDetail);
        Assert.Equal("already-routed", AttributedFailureDeveloperRoute.Decide(goal, failure, Candidate).DeclineReason);
        driver.BeginTick(kernel, 2);

        AssertUnhandled(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive));
        Assert.Single(MarkerRetries(goal));
        Assert.Single(harness.Dispatches);
        AssertNoOperatorIntent(goal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamesOnlyRecord_SqliteReload_RoutesWithNullDetail(bool spacedNames)
    {
        using var files = new FixtureFiles();
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        var evidence = spacedNames ? IncidentEvidence :
            "cohort-attribution=combined-red cohort-partition=partition-1 reproduced=Tests.Fails";
        RecordCohortFailure(kernel, goal, evidence: evidence);
        var db = Path.Combine(files.Root, "state.db");
        StateDbMigrations.EnsureUpToDate(db);
        await new SqliteOrchestratorStateRepository(db).SaveAsync(kernel);

        var resumed = await new SqliteOrchestratorStateRepository(db).LoadAsync();
        var restored = resumed.GetGoal(goal.Id);
        Assert.All(restored.LatestAcceptanceFailure!.CheckAttributions!,
            attribution => Assert.Equal(evidence, attribution.Evidence));
        Assert.Empty(Directory.GetFiles(files.Root, "*.trx"));
        var failure = Assert.IsType<AttributedFailure>(
            AttributedFailureDeveloperRoute.ReadAcceptanceFailure(restored.LatestAcceptanceFailure));
        Assert.Null(failure.ReproductionDetail);
        var names = spacedNames ? IncidentNames : new[] { "Tests.Fails" };
        Assert.Equal(names, failure.FailingTests);
        var harness = new RouteHarness(resumed);
        var driver = harness.Configure();
        driver.BeginTick(resumed, 1);

        Assert.False(driver.AdvanceOnce(restored, ConductorAutonomyPolicy.Permissive).WasEscalated);
        AssertRoute(restored, restored.Tasks.Single(), harness,
            spacedNames ? IncidentPartition : "partition-1", Candidate, names);
        Assert.DoesNotContain("\nreproduced ", Assert.Single(MarkerRetries(restored)).Message);
        Assert.Empty(harness.Escalations);
    }

    [Fact]
    public async Task RecordedRerun_SqliteRestartWithoutTrx_RebuildsIdenticalFeedback()
    {
        using var files = new FixtureFiles();
        var (kernel, first, second) = CohortGoals();
        var trx = files.WriteTrx();
        Assert.True(File.Exists(trx));
        var verdict = ApplyMember(kernel, first, second, [trx]);
        Assert.True(verdict.ShouldRecord);
        Assert.True(kernel.RouteRecordedAcceptanceFailure(second.Id, "Attributed cohort failure recorded."));
        var before = Assert.IsType<AttributedFailure>(
            AttributedFailureDeveloperRoute.ReadAcceptanceFailure(second.LatestAcceptanceFailure));
        var expectedLines = new[] { "reproduced " + DeveloperName + ": developer failure",
            "reproduced " + PlannerName + ": planner failure" };
        Assert.Equal(string.Join('\n', expectedLines), before.ReproductionDetail);
        var beforeDecision = AttributedFailureDeveloperRoute.Decide(second, before, Candidate);
        Assert.True(beforeDecision.IsRoute);
        var db = Path.Combine(files.Root, "state.db");
        StateDbMigrations.EnsureUpToDate(db);
        await new SqliteOrchestratorStateRepository(db).SaveAsync(kernel);
        File.Delete(trx);
        Assert.False(File.Exists(trx));

        var resumed = await new SqliteOrchestratorStateRepository(db).LoadAsync();
        var restored = resumed.GetGoal(second.Id);
        Assert.Equal(GoalStatus.AcceptanceFailed, restored.Status);
        Assert.Equal(verdict.Detail, Assert.Single(restored.LatestAcceptanceFailure!.CheckAttributions!).Evidence);
        var after = Assert.IsType<AttributedFailure>(
            AttributedFailureDeveloperRoute.ReadAcceptanceFailure(restored.LatestAcceptanceFailure));
        Assert.Equal(before.ReproductionDetail, after.ReproductionDetail);
        Assert.Equal(before.FailingTests, after.FailingTests);
        var harness = new RouteHarness(resumed);
        var driver = harness.Configure();
        driver.BeginTick(resumed, 1);
        Assert.False(driver.AdvanceOnce(restored, ConductorAutonomyPolicy.Permissive).WasEscalated);

        AssertRoute(restored, restored.Tasks.Single(), harness, IncidentPartition, Candidate, IncidentNames);
        var feedback = Assert.Single(MarkerRetries(restored)).Message;
        Assert.Equal(beforeDecision.Feedback, feedback);
        Assert.Equal(expectedLines, feedback.Replace("\r", "").Split('\n')
            .Where(line => line.StartsWith("reproduced ", StringComparison.Ordinal)));
        Assert.DoesNotContain("second line", feedback);
        Assert.DoesNotContain("unreproduced failure", feedback);
        Assert.Empty(harness.Escalations);
    }

    private static string Detail(string message) =>
        string.Concat(IncidentNames.Select(name => "\nreproduced " + name + ": " + message));

    private static void AssertUnhandled(ConductorAdvanceResult result) =>
        Assert.Equal("Unhandled lifecycle state AcceptanceFailed",
            Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome).Reason);

    private static void AssertDeclinedWithoutRetry(AgentOrchestratorKernel kernel, Goal goal)
    {
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 1);
        AssertUnhandled(driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive));
        Assert.Empty(MarkerRetries(goal));
        Assert.Empty(harness.Dispatches);
        AssertNoOperatorIntent(goal);
    }

    private static AcceptanceCohortIdentity CreateIdentity(Goal first, Goal second) =>
        AcceptanceCohortIdentity.Create([Binding(first, Main), Binding(second, Candidate)],
            Main, new string('e', 40), "manifest-v1");

    private static AcceptanceCohortMemberBinding Binding(Goal goal, string candidate) =>
        new(goal.Id, candidate, candidate, ["docs/" + goal.Id.Value + ".md"], ["resource:" + goal.Id.Value],
            ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");

    private static ConductorAcceptanceCohortAttributionVerdict ApplyMember(
        AgentOrchestratorKernel kernel, Goal first, Goal second, string[] paths, bool reproduced = true)
    {
        var cohort = new AcceptanceCohortReceipt("combined-red", CreateIdentity(first, second),
            AcceptanceCohortGateOutcome.Failed, DateTimeOffset.UtcNow, 1, ["combined"], 1, [],
            AcceptanceCohortAttributionOutcome.SecondMemberFailed)
        {
            AttributedMembers = [new(second.Id, 1, Candidate, reproduced ? IncidentNames.Reverse().ToArray() : [])]
        };
        var partition = new AcceptanceCohortPartitionReceipt(IncidentPartition, second.Id, 1, Candidate, Main,
            new string('d', 40), "manifest-v1", AcceptanceCohortGateOutcome.Failed, 1, paths)
        {
            FailedChecks = ["Infrastructure.Tests Remainder"], FailingTestIdentities = IncidentNames
        };
        return ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, second, partition, Candidate, Main, false,
            (goal, checks, branch, main, attributions) =>
                kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attributions), _ => { });
    }

    private sealed class FixtureFiles : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();

        internal string WriteTrx()
        {
            var path = Path.Combine(Root, "attribution.trx");
            var failures = new[] { (PlannerName, "planner failure\r\nsecond line"),
                (DeveloperName, "developer failure\nsecond line"), ("OtherTests.Fails", "unreproduced failure") };
            var definitions = new XElement("TestDefinitions");
            var results = new XElement("Results");
            for (var i = 0; i < failures.Length; i++)
            {
                var (name, message) = failures[i];
                var classEnd = name.IndexOf('.');
                var method = name[(classEnd + 1)..].Split('(')[0];
                definitions.Add(new XElement("UnitTest", new XAttribute("id", i),
                    new XElement("TestMethod", new XAttribute("className", name[..classEnd]),
                        new XAttribute("name", method))));
                results.Add(new XElement("UnitTestResult", new XAttribute("testId", i),
                    new XAttribute("testName", name[(classEnd + 1)..]), new XAttribute("outcome", "Failed"),
                    new XElement("Output", new XElement("ErrorInfo", new XElement("Message", message)))));
            }
            new XDocument(new XElement("TestRun", definitions, results)).Save(path);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
