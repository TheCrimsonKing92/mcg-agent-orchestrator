using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: every test owns a unique temporary directory and no shared state.
public sealed class GroupedGateReceiptTrxEvidenceTests
{
    [Theory]
    [InlineData("AcceptanceAttemptExecutionOwnerTests", 1, 0)]
    [InlineData("ParallelExecutionPlannerTests", 1, 0)]
    [InlineData("CompletionBuildEvidenceLifecycleTests.TerminalBuildOutcomePreservesTypedReasonAndDiagnostics", 1, 1)]
    [InlineData("AcceptanceAttemptExecutionOwnerTestsExtra", 0, 0)]
    [InlineData("acceptanceattemptexecutionownertests", 0, 0)]
    public void DefinitionJoinAndShortIdentityPreserveOrdinalSelectorBoundaries(
        string selector, int passed, int superseded)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var paths = GroupedGateTrxSamples.WriteIncidentAndRerun(fixture.Root);

        var evidence = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([selector], paths, fixture.Root));

        Assert.Equal(selector, evidence.Selector);
        Assert.Equal(passed, evidence.Passed);
        Assert.Equal(0, evidence.Failed);
        Assert.Equal(superseded, evidence.Superseded);
        Assert.Equal(passed > 0, evidence.Satisfied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableSecondTrxInvalidatesEverySelector(bool malformed)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var paths = GroupedGateTrxSamples.WriteIncidentAndRerun(fixture.Root);
        var unreadable = Path.Combine(fixture.Root, "unreadable.trx");
        if (malformed) File.WriteAllText(unreadable, "<TestRun>");

        var evidence = GroupedGateReceiptTrxEvidence.Evaluate(
            GroupedGateTrxSamples.Selectors, [.. paths, unreadable], fixture.Root);

        Assert.Equal(3, evidence.Count);
        Assert.All(evidence, item =>
        {
            Assert.False(item.Satisfied);
            Assert.Equal((0, 0, 0), (item.Passed, item.Failed, item.Superseded));
        });
    }

    [Fact]
    public void FailedDataCaseIsNotSupersededByAnotherCaseOrShortIdentity()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var failed = GroupedGateTrxSamples.Lifecycle;
        var first = GroupedGateTrxSamples.Write(fixture.Root, "first.trx", failed);
        var rerun = GroupedGateTrxSamples.Write(fixture.Root, "rerun.trx",
            failed with { Outcome = "Passed", TestName = failed.TestName.Replace("ApparatusFailure", "Passed") },
            failed with { Outcome = "Passed", ClassName = "Other.Namespace." + failed.ClassName });

        var evidence = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate(
            [GroupedGateTrxSamples.Selectors[0]], [first, rerun], fixture.Root));

        Assert.Equal((2, 1, 0), (evidence.Passed, evidence.Failed, evidence.Superseded));
        Assert.False(evidence.Satisfied);
    }

    [Theory]
    [InlineData("NotExecuted")]
    [InlineData("Error")]
    public void NonPassedRowsBlockUntilIdenticalIdentityPassesInAnotherFile(string outcome)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var first = GroupedGateTrxSamples.Write(fixture.Root, "first.trx",
            GroupedGateTrxSamples.Lifecycle with { Outcome = outcome });
        var selector = GroupedGateTrxSamples.Selectors[0];
        var blocked = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate([selector], [first], fixture.Root));
        Assert.Equal((0, 1, 0, false), (blocked.Passed, blocked.Failed, blocked.Superseded, blocked.Satisfied));
        var rerun = GroupedGateTrxSamples.Write(fixture.Root, "rerun.trx",
            GroupedGateTrxSamples.Lifecycle with { Outcome = "Passed" });

        var passed = Assert.Single(GroupedGateReceiptTrxEvidence.Evaluate(
            [selector], [Path.GetFileName(first), Path.GetFileName(rerun)], fixture.Root));

        Assert.Equal((1, 0, 1, true), (passed.Passed, passed.Failed, passed.Superseded, passed.Satisfied));
    }
}

internal static class GroupedGateTrxSamples
{
    internal sealed record Row(string Id, string ClassName, string Method, string TestName, string Outcome);

    internal static readonly string[] Selectors =
    [
        "CompletionBuildEvidenceLifecycleTests", "AcceptanceAttemptExecutionOwnerTests", "ParallelExecutionPlannerTests"
    ];

    internal static readonly Row Lifecycle = new("u1", Selectors[0],
        "TerminalBuildOutcomePreservesTypedReasonAndDiagnostics",
        "CompletionBuildEvidenceLifecycleTests.TerminalBuildOutcomePreservesTypedReasonAndDiagnostics(phase: ApparatusFailure)",
        "Failed");
    internal static readonly Row Namespaced = new("u2",
        "Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.AcceptanceAttemptExecutionOwnerTests",
        "Invocation_identity_is_unique_and_retains_attempt_attribution",
        "Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests.AcceptanceAttemptExecutionOwnerTests.Invocation_identity_is_unique_and_retains_attempt_attribution",
        "Passed");
    internal static readonly Row MethodOnly = new("u3", Selectors[2],
        "ParallelExecutionPlannerSerializesStateLifecycleAcceptanceAndResourceConflicts",
        "ParallelExecutionPlanner_serializes_state_lifecycle_acceptance_and_resource_conflicts", "Passed");

    internal static string[] WriteIncidentAndRerun(string root) =>
    [
        Write(root, "original.trx", Lifecycle, Namespaced, MethodOnly),
        Write(root, "rerun.trx", Lifecycle with { Outcome = "Passed" })
    ];

    internal static string Write(string root, string name, params Row[] rows)
    {
        var path = Path.Combine(root, name);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun",
            new XElement(ns + "TestDefinitions", rows.Select((row, index) =>
                new XElement(ns + "UnitTest", new XAttribute("id", $"{row.Id}-{index}"),
                    new XElement(ns + "TestMethod", new XAttribute("className", row.ClassName),
                        new XAttribute("name", row.Method))))),
            new XElement(ns + "Results", rows.Select((row, index) =>
                new XElement(ns + "UnitTestResult", new XAttribute("testId", $"{row.Id}-{index}"),
                    new XAttribute("testName", row.TestName), new XAttribute("outcome", row.Outcome)))),
            new XElement(ns + "ResultSummary", new XElement(ns + "Counters",
                new XAttribute("total", rows.Length),
                new XAttribute("executed", rows.Count(row => row.Outcome != "NotExecuted")),
                new XAttribute("passed", rows.Count(row => row.Outcome == "Passed")),
                new XAttribute("failed", rows.Count(row => row.Outcome == "Failed")))))).Save(path);
        return path;
    }
}
