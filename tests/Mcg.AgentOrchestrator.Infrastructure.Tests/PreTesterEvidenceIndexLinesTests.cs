using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed class PreTesterEvidenceIndexLinesTests
{
    [Fact]
    public void MarkerPreservesTheoryIdentityAndDelimiterBearingResultPath()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var entry = new PreTesterEvidenceEntry(
            "red", "abc1234", "receipt-1",
            ["Infrastructure.Tests:TheoryTests"], ["MissingTests"],
            "C:\\results\\run;one.trx",
            ["TheoryTests.Fails(value: X, expected: True)"]);

        kernel.RecordFindingEvidenceRun(goal.Id, tester.Id,
            PreTesterEvidenceIndexLines.FormatMarker(entry));

        var roundTrip = Assert.IsType<PreTesterEvidenceEntry>(
            PreTesterEvidenceIndexLines.Latest(goal, tester.Id, entry.CandidateSha));
        Assert.Equal(entry.Selections, roundTrip.Selections);
        Assert.Equal(entry.NotRun, roundTrip.NotRun);
        Assert.Equal(entry.ResultPath, roundTrip.ResultPath);
        Assert.Equal(entry.FailingTests, roundTrip.FailingTests);
        Assert.Contains(PreTesterEvidenceIndexLines.ForTester(goal, tester, entry.CandidateSha),
            line => line == "evidence_failing_tests: TheoryTests.Fails(value: X, expected: True)");
    }
}
