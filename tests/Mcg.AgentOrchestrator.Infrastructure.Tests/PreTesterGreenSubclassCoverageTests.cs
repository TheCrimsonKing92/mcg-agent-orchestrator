using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fixture owns its temporary TRX directory; no processes or global state.
public sealed class PreTesterGreenSubclassCoverageTests
{
    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("C")]
    public void RealGreenShapesWithAbstractBaseSelectionsClassifyGreen(string shape)
    {
        using var fixture = new TrxFixture(shape);

        var result = PreTesterApparatusReceiptClassifier.ClassifyOutcome(fixture.Evidence,
            completed: true, TrxFixture.CandidateSha, fixture.Selections, _ => false);

        Assert.Equal("green", result.Outcome);
        Assert.Null(result.Unusable);
    }

    [Theory]
    [InlineData("failed-row", "trx-content-not-green")]
    [InlineData("invalid-xml", "trx-content-not-green")]
    [InlineData("missing-file", "trx-artifacts-missing")]
    [InlineData("unrelated-class", "required-class-missing")]
    [InlineData("wrong-case", "required-class-missing")]
    [InlineData("wrong-count", "executed-count-mismatch")]
    [InlineData("red", null)]
    public void RedCorruptOrUncoveredShapesStayUnusable(string mutation, string? expectedCheck)
    {
        using var fixture = new TrxFixture("A");
        var evidence = fixture.Evidence;
        var arm = Assert.Single(evidence.Arms!);
        switch (mutation)
        {
            case "failed-row":
                fixture.Write(2, new("ConductorBatchLoopTestsPersistenceFailure"),
                    new("ConductorBatchLoopTestsReapFault", "Failed"));
                break;
            case "invalid-xml": File.WriteAllText(fixture.Paths[1], "not xml"); break;
            case "missing-file": File.Delete(fixture.Paths[1]); break;
            case "unrelated-class":
                fixture.Write(2, new("OtherBatchLoopTestsReapFault"), new("OtherBatchLoopTestsReapFault"));
                break;
            case "wrong-case":
                fixture.Write(2, new("conductorBatchLoopTestsReapFault"), new("conductorBatchLoopTestsReapFault"));
                break;
            case "wrong-count":
                arm = arm with { Checks = arm.Checks.Select((check, index) => index == 0
                    ? check with { ExecutedTestCount = 3 } : check).ToArray() };
                break;
            case "red":
                arm = arm with
                {
                    Disposition = FindingEvidenceArmDisposition.Red,
                    Checks = [arm.Checks[0] with
                    {
                        ExecutedTestCount = 1, FailingTestIdentities = ["ExternalTests.Fails"]
                    }]
                };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        evidence = evidence with { Arms = [arm] };

        var result = PreTesterApparatusReceiptClassifier.ClassifyOutcome(evidence,
            completed: true, TrxFixture.CandidateSha, fixture.Selections, _ => false);

        if (mutation == "red")
        {
            Assert.Equal("red", result.Outcome);
            Assert.Null(result.Unusable);
            return;
        }
        Assert.Equal("unusable", result.Outcome);
        var reason = Assert.IsType<PreTesterUnusableReason>(result.Unusable);
        Assert.Equal(expectedCheck, reason.Check);
        Assert.Equal(expectedCheck == "required-class-missing" ? ["ConductorBatchLoopTests"] :
            Array.Empty<string>(), reason.MissingClasses);
    }

    internal sealed record TestRow(string ClassName, string Outcome = "Passed");

    internal sealed class TrxFixture : IDisposable
    {
        internal const string CandidateSha = "bbb2222";
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        internal string[] Paths { get; }
        internal FindingEvidenceSelection[] Selections { get; }
        internal FocusedEvidenceRunResult Evidence { get; }

        internal TrxFixture(string shape)
        {
            (string[] Classes, TestRow[][] Rows) data = shape switch
            {
                "A" => (["ConductorDriverTests", "AcceptanceLaneMembershipTests", "ConductorBatchLoopTests"],
                    [[new("ConductorDriverTests"), new("ConductorDriverTests")],
                     [new("AcceptanceLaneMembershipTests")],
                     [new("ConductorBatchLoopTestsPersistenceFailure"), new("ConductorBatchLoopTestsReapFault")]]),
                "B" => (["AcceptanceGateEngineSettingsTests", "GoalAcceptanceVerifierDotnetBuildSlotTests"],
                    [[new("AcceptanceGateEngineSettingsTests"), new("AcceptanceGateEngineSettingsTests")],
                     [new("GoalAcceptanceVerifierDotnetBuildSlotTestsFocusedEvidenceProducerRouting"),
                      new("GoalAcceptanceVerifierDotnetBuildSlotTestsFocusedEvidenceProducerRouting"),
                      new("GoalAcceptanceVerifierDotnetBuildSlotTestsTestTamperGuard", "NotExecuted")]]),
                "C" => (["AcceptanceCohortWorkflowTests", "ConductorBatchLoopTests", "LaneIsolatedRootGuardTests"],
                    [[new("AcceptanceCohortWorkflowTestsGateOutcomes"), new("AcceptanceCohortWorkflowTestsGateOutcomes"),
                      new("AcceptanceCohortWorkflowTestsMergeTrain")],
                     [new("ConductorBatchLoopTestsGoalStallExit")], [new("LaneIsolatedRootGuardTests")]]),
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };
            Paths = data.Rows.Select((_, index) => Path.Combine(_root, $"check-{index + 1}.trx")).ToArray();
            Selections = data.Classes.Select(name => new FindingEvidenceSelection("Infrastructure.Tests", name)).ToArray();
            for (var index = 0; index < data.Rows.Length; index++) Write(index, data.Rows[index]);
            var checks = data.Rows.Select((rows, index) => new AcceptanceCheckResult($"check-{index + 1}", true, 0,
                "passed", TestResultPaths: [Paths[index]],
                ExecutedTestCount: rows.Count(row => row.Outcome != "NotExecuted"))).ToArray();
            Evidence = new(string.Join(';', Selections.Select(selection => $"{selection.TestProject}:{selection.TestClass}")),
                true, true, "green fixture", checks,
                Arms: [new(FindingEvidenceArm.Candidate, CandidateSha, FindingEvidenceArmDisposition.Green,
                    true, true, "green candidate", checks)], OutcomeReason: FindingEvidenceOutcomeReason.ValidEvidence);
        }

        internal void Write(int index, params TestRow[] rows)
        {
            XNamespace trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(trx + "TestRun",
                new XElement(trx + "TestDefinitions", rows.Select((row, rowIndex) =>
                    new XElement(trx + "UnitTest", new XAttribute("id", $"test-{rowIndex}"),
                        new XElement(trx + "TestMethod",
                            new XAttribute("className", "Mcg.AgentOrchestrator.Infrastructure.Tests." + row.ClassName),
                            new XAttribute("name", "Runs"))))),
                new XElement(trx + "Results", rows.Select((row, rowIndex) =>
                    new XElement(trx + "UnitTestResult", new XAttribute("testId", $"test-{rowIndex}"),
                        new XAttribute("outcome", row.Outcome)))),
                new XElement(trx + "ResultSummary", new XElement(trx + "Counters",
                    new XAttribute("total", rows.Length),
                    new XAttribute("executed", rows.Count(row => row.Outcome != "NotExecuted")),
                    new XAttribute("passed", rows.Count(row => row.Outcome == "Passed")),
                    new XAttribute("failed", rows.Count(row => row.Outcome == "Failed")))))).Save(Paths[index]);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
