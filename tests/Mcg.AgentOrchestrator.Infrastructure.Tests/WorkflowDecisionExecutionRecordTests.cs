using System.Text.RegularExpressions;
using Xunit.Sdk;
using static WorkflowDecisionCoverageRatchetTests;

// Parallel-safe: read-only repository inspection and in-memory negative controls.
public sealed class WorkflowDecisionExecutionRecordTests
{
    [Fact]
    public void ExecutionRecord_HeadSources_CreditsSlicesAndKeepsCurrentRemainder()
    {
        var root = VerifiedRepositoryRoot.Find();
        var document = File.ReadAllText(Path.Combine(root, "docs", "architecture-migration.md"));
        var start = document.IndexOf("**Workflow decisions", StringComparison.Ordinal);
        Assert.True(start >= 0, "Workflow decisions execution record is missing.");
        var end = document.IndexOf("Conductor decomposition landed", start, StringComparison.Ordinal);
        Assert.True(end > start, "Workflow decisions execution record boundary is missing.");
        var record = document[start..end];
        // Check numbered credit structure without restating a goal/commit census.
        var credits = Regex.Matches(record,
            @"slice\s+(?<slice>[1-6])[ab]?,[^`]*`(?<goal>[0-9a-f]{8})`\s+\(`(?<commit>[0-9a-f]{7,40})`\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.Equal(Enumerable.Range(1, 6),
            credits.Select(m => int.Parse(m.Groups["slice"].Value)).Distinct().Order());
        AssertRemainingAttribution(record, Scan(ReadSources()));
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkflowDecisionStageConventionTests.cs", record);
        Assert.Contains("src/Mcg.AgentOrchestrator.Core/Reports/LatestPolicyDecisionReader.cs", record);
        Assert.Contains("src/Mcg.AgentOrchestrator.App/Cli/CliOwnerDigestCommand.cs", record);
        Assert.Matches(@"text\s+and JSON", record);
    }

    [Fact]
    public void Remaining_DecidedMember_FailsAndNamesMember()
    {
        var failure = Assert.ThrowsAny<XunitException>(() => AssertRemainingAttribution(
            "Remaining: ConductorDriver.AcceptanceLanding.cs::ExecuteVerifying.", []));
        Assert.Contains("ConductorDriver.AcceptanceLanding.cs : ExecuteVerifying",
            failure.Message, StringComparison.Ordinal);
    }

    private static void AssertRemainingAttribution(string record, string[] undecided)
    {
        var start = record.IndexOf("Remaining:", StringComparison.Ordinal);
        Assert.True(start >= 0, "Workflow decisions Remaining inventory is missing.");
        var keys = Regex.Matches(record[start..],
            @"(?<file>ConductorDriver[\w.]*\.cs)::(?<member>\w+)", RegexOptions.CultureInvariant)
            .Select(m => $"{m.Groups["file"].Value} : {m.Groups["member"].Value}").ToArray();
        Assert.NotEmpty(keys);
        foreach (var key in keys)
            Assert.True(undecided.Contains(key, StringComparer.Ordinal),
                $"Execution record names a remaining member with no undecided site: {key}");
    }
}
