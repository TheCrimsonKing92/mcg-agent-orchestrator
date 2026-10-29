using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: immutable discovery inputs and one private temporary receipt per case.
public sealed class DeclaredTestRemovalGateCreditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_FiftyDeclarations_CreditsOnlyDeclaredRemovals(bool removeUndeclared)
    {
        const string survivor = "Example.Tests.OtherTests.Survives";
        const string undeclared = "Example.Tests.FooTests.Undeclared";
        var removals = Enumerable.Range(0, 50).Select(i => $"Example.Tests.FooTests.Removed{i:D2}").ToArray();
        var objective = "## Acceptance criteria\n1. Behavior works.\n" +
            string.Join("\n", removals.Select((identity, i) => $"{i + 2}. test-removal: {identity}"));
        var declarations = MemberDeclaredTestRemovals.FromMembers([(GoalId.New(), objective)]);
        var sanctioned = MemberDeclaredTestRemovals.UnionSanctioned(
            AcceptanceCriteriaParser.Parse(objective).Where(item => item.Type == "test-removal")
                .Select(item => item.TestIdentity!).ToArray(), declarations);
        Assert.Equal(removals, sanctioned);
        Assert.Equal(50, declarations.Count);
        string[] candidate = removeUndeclared ? [survivor] : [survivor, undeclared];
        string[] main = [survivor, undeclared, .. removals];
        var root = Directory.CreateTempSubdirectory("removal-credit-").FullName;
        try
        {
            var trx = Path.Combine(root, "results.trx");
            new XDocument(new XElement("TestRun", new XElement("Results", candidate.Select((identity, i) =>
                new XElement("UnitTestResult", new XAttribute("testId", i),
                    new XAttribute("testName", identity), new XAttribute("outcome", "Passed")))))).Save(trx);

            var result = TestCoverageInvariant.Evaluate(candidate,
                [new TestPartitionCoverage("fixture", true, [trx])], main, [], sanctionedRemovedTests: sanctioned);

            Assert.Equal(!removeUndeclared, result.Passed);
            Assert.Equal(50, result.CountComparison!.DeletedCount);
            foreach (var identity in removals)
                Assert.Contains($"source=declared-test-removal,identity={identity},status=corroborated,credit=1",
                    result.Summary, StringComparison.Ordinal);
            Assert.Equal(removeUndeclared, result.CountComparison.IsShortfall);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
