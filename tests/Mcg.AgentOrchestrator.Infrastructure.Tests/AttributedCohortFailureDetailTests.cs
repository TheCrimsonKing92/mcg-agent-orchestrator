using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static AttributedFailureDeveloperRouteTests;

// Each test owns its temporary TRX files and in-memory kernel; no shared state.
public sealed class AttributedCohortFailureDetailTests
{
    [Fact]
    public void FirstLinesAreFilteredOrderedAndBounded()
    {
        using var files = new TrxFiles();
        var first = files.Write("first.trx",
            ("Tests.Z", new string('z', 2001) + "\r\nsecond line"),
            ("Tests.A", "\r\n  first message  \r\nsecond line"),
            ("Tests.Unreproduced", "must not leak"),
            ("Tests.Blank", " \r\n\t"),
            ("Tests.NoMessage", null));
        var duplicate = files.Write("duplicate.trx", ("Tests.A", "later message"));

        var detail = AttributedCohortFailureDetail.Format(
            ["Tests.Z", "Tests.A", "Tests.Blank", "Tests.NoMessage", "Tests.Missing"], [first, duplicate]);

        Assert.Equal("\nreproduced Tests.A: first message\nreproduced Tests.Z: " + new string('z', 2000), detail);
    }

    [Fact]
    public void MissingMalformedAndUnreadableTrxLeaveNamesOnly()
    {
        using var files = new TrxFiles();
        var malformed = Path.Combine(files.Root, "malformed.trx");
        File.WriteAllText(malformed, "<TestRun>");
        var locked = files.Write("locked.trx", ("Tests.A", "locked message"));
        using var heldFile = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(string.Empty, AttributedCohortFailureDetail.Format(["Tests.A"],
            [Path.Combine(files.Root, "missing.trx"), malformed, locked]));
    }

    [Fact]
    public void RerunMessagesReachDeveloperRetryThroughRecordedVerdict()
    {
        using var files = new TrxFiles();
        const string name = "Sample.WorkerTests.Fails(role: Planner)";
        var trx = files.Write("attribution.trx", (name, "first failure line\r\nsecond line"),
            ("Sample.OtherTests.Fails", "unreproduced message"));
        var (kernel, first, second) = CohortGoals();
        var bindings = new[] { Binding(first, Main), Binding(second, Candidate) };
        var cohort = new AcceptanceCohortReceipt("combined-red",
            AcceptanceCohortIdentity.Create(bindings, Main, new string('e', 40), "manifest-v1"),
            AcceptanceCohortGateOutcome.Failed, DateTimeOffset.UtcNow, 1, ["combined"], 1, [],
            AcceptanceCohortAttributionOutcome.SecondMemberFailed)
        {
            AttributedMembers = [new(second.Id, 1, Candidate, [name])]
        };
        var partition = new AcceptanceCohortPartitionReceipt("partition-1", second.Id, 1, Candidate, Main,
            new string('d', 40), "manifest-v1", AcceptanceCohortGateOutcome.Failed, 1, [trx])
        {
            FailedChecks = ["Infrastructure.Tests Remainder"], FailingTestIdentities = [name]
        };

        var verdict = ConductorAcceptanceCohortAttributionVerdicts.Apply(cohort, second, partition,
            Candidate, Main, false,
            (goal, checks, branch, main, attributions) =>
                kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attributions), _ => { });

        Assert.True(verdict.ShouldRecord);
        const string expectedLine = "reproduced " + name + ": first failure line";
        Assert.Equal($"cohort-attribution={cohort.Identity.Value} cohort-partition=partition-1 reproduced={name}\n" +
            expectedLine, Assert.Single(second.LatestAcceptanceFailure!.CheckAttributions!).Evidence);
        var failure = Assert.IsType<AttributedFailure>(
            AttributedFailureDeveloperRoute.ReadAcceptanceFailure(second.LatestAcceptanceFailure));
        Assert.Equal(new[] { name }, failure.FailingTests);
        Assert.Equal(expectedLine, failure.ReproductionDetail);
        // Recording a verdict retains the failure; lifecycle admission routes the Verified member separately.
        Assert.True(kernel.RouteRecordedAcceptanceFailure(second.Id, "Attributed cohort failure recorded."));
        Assert.Equal(GoalStatus.AcceptanceFailed, second.Status);
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 1);
        driver.AdvanceOnce(second, ConductorAutonomyPolicy.Permissive);

        var feedback = Assert.Single(MarkerRetries(second)).Message;
        Assert.Equal(second.Tasks.Single().Id, Assert.Single(harness.Dispatches));
        Assert.Equal(RetryCause.NewTestFinding, second.Tasks.Single().PendingRetryCause);
        Assert.Empty(harness.Escalations);
        Assert.Contains("failing_tests=" + name + Environment.NewLine, feedback);
        Assert.Contains(expectedLine, feedback);
        Assert.DoesNotContain("second line", feedback);
        Assert.DoesNotContain("unreproduced message", feedback);
        Assert.Empty(MarkerRetries(first));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void StoredDetailsParseWithoutReadingTrx(string separator)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CompletedGoal(kernel);
        const string name = "Tests.M(role: Developer)";
        const string detail = "reproduced " + name + ": first message";
        RecordCohortFailure(kernel, goal, evidence:
            "cohort-attribution=combined-red cohort-partition=partition-1 reproduced=" + name + separator + detail);

        var failure = Assert.IsType<AttributedFailure>(
            AttributedFailureDeveloperRoute.ReadAcceptanceFailure(goal.LatestAcceptanceFailure));

        Assert.Equal(detail, failure.ReproductionDetail);
        Assert.Equal(new[] { name }, failure.FailingTests);
        var decision = AttributedFailureDeveloperRoute.Decide(goal, failure, Candidate);
        Assert.True(decision.IsRoute);
        Assert.Contains(detail, decision.Feedback);
    }

    private static AcceptanceCohortMemberBinding Binding(Goal goal, string candidate) =>
        new(goal.Id, candidate, candidate, ["docs/" + goal.Id.Value + ".md"], ["resource:" + goal.Id.Value],
            ChangeRiskTier.DocsOnly, ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected");

    private sealed class TrxFiles : IDisposable
    {
        internal string Root { get; } = ConductorDriverTests.CreateTempDirectory();

        internal string Write(string fileName, params (string Name, string? Message)[] failures)
        {
            var definitions = new XElement("TestDefinitions");
            var results = new XElement("Results");
            for (var index = 0; index < failures.Length; index++)
            {
                var (name, message) = failures[index];
                var suffixStart = name.IndexOfAny(['(', '[']);
                var method = suffixStart < 0 ? name : name[..suffixStart];
                var classEnd = method.LastIndexOf('.');
                definitions.Add(new XElement("UnitTest", new XAttribute("id", index),
                    new XElement("TestMethod", new XAttribute("className", method[..classEnd]),
                        new XAttribute("name", method[(classEnd + 1)..]))));
                results.Add(new XElement("UnitTestResult", new XAttribute("testId", index),
                    new XAttribute("testName", name[(classEnd + 1)..]), new XAttribute("outcome", "Failed"),
                    new XElement("Output", new XElement("ErrorInfo",
                        message is null ? null : new XElement("Message", message)))));
            }
            var path = Path.Combine(Root, fileName);
            new XDocument(new XElement("TestRun", definitions, results)).Save(path);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
