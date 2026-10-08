using System.Text.RegularExpressions;
using Xunit.Sdk;
using static WorkflowDecisionCoverageRatchetTests;

// Parallel-safe: reads verified repository sources; fixtures are in-memory strings.
public sealed class WorkflowDecisionStageConventionTests
{
    private static readonly string[] StageScope =
    [
        "ConductorDriver.AcceptanceLanding.cs : ExecuteLanding",
        "ConductorDriver.AcceptanceLanding.cs : ExecuteVerifying",
        "ConductorDriver.LandingCompletion.cs : Escalate",
        "ConductorDriver.LifecycleEntryDecisions.cs : ExecuteCreateWorkspace",
        "ConductorDriver.cs : ExecuteDispatchAndStart"
    ];

    private static readonly string[] StageAllowList =
    [
        "ConductorDriver.LandingCompletion.cs : Escalate : The shared escalation factory leaves policy attribution to its callers.",
        "ConductorDriver.cs : ExecuteDispatchAndStart : Failed dispatch recovery still delegates to the undecided escalation factory."
    ];

    // Member keys alone cannot detect another undecided branch in an allowed member.
    private static readonly IReadOnlyDictionary<string, int> StageSiteCounts =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ConductorDriver.LandingCompletion.cs : Escalate"] = 1,
            ["ConductorDriver.cs : ExecuteDispatchAndStart"] = 1
        };

    [Fact]
    public void StageExecutors_CurrentSources_EnforceDecisionContract()
    {
        var sources = ReadSources();
        foreach (var key in StageScope)
        {
            var parts = key.Split(" : ");
            var source = Assert.Single(sources.Where(s => s.FileName == parts[0]));
            Assert.True(Regex.IsMatch(source.Text,
                $@"\bConductorAdvanceResult\s+{Regex.Escape(parts[1])}\s*\("),
                $"Missing stage declaration: {key}");
        }
        AssertStageContract(sources, StageAllowList, AllowList, StageSiteCounts);
    }

    [Theory]
    [InlineData("ConductorDriver.LifecycleEntryDecisions.cs", "ExecuteCreateWorkspace", "Held")]
    [InlineData("ConductorDriver.LifecycleEntryDecisions.cs", "ExecuteCreateWorkspace", "Escalated")]
    [InlineData("ConductorDriver.cs", "ExecuteDispatchAndStart", "Held")]
    [InlineData("ConductorDriver.cs", "ExecuteDispatchAndStart", "Escalated")]
    [InlineData("ConductorDriver.AcceptanceLanding.cs", "ExecuteVerifying", "Held")]
    [InlineData("ConductorDriver.AcceptanceLanding.cs", "ExecuteVerifying", "Escalated")]
    [InlineData("ConductorDriver.AcceptanceLanding.cs", "ExecuteLanding", "Held")]
    [InlineData("ConductorDriver.AcceptanceLanding.cs", "ExecuteLanding", "Escalated")]
    [InlineData("ConductorDriver.LandingCompletion.cs", "Escalate", "Held")]
    [InlineData("ConductorDriver.LandingCompletion.cs", "Escalate", "Escalated")]
    public void StageMember_UndecidedOutcome_FailsAndNamesMember(
        string file, string member, string outcome)
    {
        var source = Fixture(member, $"return new ConductorAdvanceOutcome.{outcome}(state, \"x\");");
        var failure = Assert.Throws<XunitException>(() =>
            AssertStageContract([(file, source)], [], [], new Dictionary<string, int>()));
        Assert.Contains($"{file} : {member}", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteVerifying_AttachedDecision_Passes()
    {
        var source = Fixture("ExecuteVerifying",
            "return MakeResult(new ConductorAdvanceOutcome.Held(state, \"x\") { Decision = decision.ToRecord() });");
        AssertStageContract([("ConductorDriver.AcceptanceLanding.cs", source)],
            [], [], new Dictionary<string, int>());
    }

    [Fact]
    public void AllowList_RemovedMember_FailsAsStale()
    {
        const string key = "ConductorDriver.AcceptanceLanding.cs : ExecuteVerifying";
        var entries = new[] { $"{key} : Existing hold awaiting attribution." };
        var counts = new Dictionary<string, int> { [key] = 1 };
        var source = Fixture("ExecuteVerifying", "return new ConductorAdvanceOutcome.Held(state, \"x\");");
        AssertStageContract([("ConductorDriver.AcceptanceLanding.cs", source)], entries, entries, counts);
        var failure = Assert.Throws<XunitException>(() => AssertStageContract(
            [("ConductorDriver.AcceptanceLanding.cs", "partial class ConductorDriver { }")],
            entries, entries, counts));
        Assert.Contains("Stale allow-list entries", failure.Message, StringComparison.Ordinal);
        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ConductorDriver.cs", "ExecuteDispatchAndStart")]
    [InlineData("ConductorDriver.LandingCompletion.cs", "Escalate")]
    public void AllowedMember_AdditionalUndecidedSite_FailsCount(string file, string member)
    {
        var key = $"{file} : {member}";
        var entries = new[] { $"{key} : Existing exception." };
        var source = Fixture(member, """
            var first = new ConductorAdvanceOutcome.Held(state, "x");
            return new ConductorAdvanceOutcome.Escalated(state, "y");
            """);
        var failure = Assert.ThrowsAny<XunitException>(() => AssertStageContract(
            [(file, source)], entries, entries, new Dictionary<string, int> { [key] = 1 }));
        Assert.Contains($"{key} sites=2 expected=1", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AllowList_EmptyReason_Fails(string reason)
    {
        var entries = new[] { $"ConductorDriver.cs : ExecuteDispatchAndStart : {reason}" };
        var failure = Assert.ThrowsAny<XunitException>(() => AssertStageContract(
            [], entries, AllowList, StageSiteCounts));
        Assert.Contains("Invalid stage allow-list entry", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowList_UnsortedKeys_Fails()
    {
        var failure = Assert.ThrowsAny<XunitException>(() => AssertStageContract(
            [], StageAllowList.Reverse().ToArray(), AllowList, StageSiteCounts));
        Assert.Contains("Stage allow-list keys must be sorted", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowList_MissingRepositoryEntry_Fails()
    {
        var key = StageScope[^1];
        var entries = new[] { $"{key} : Existing exception." };
        var failure = Assert.ThrowsAny<XunitException>(() => AssertStageContract(
            [], entries, [], new Dictionary<string, int> { [key] = 1 }));
        Assert.Contains($"Stage entry absent from repository allow-list: {key}",
            failure.Message, StringComparison.Ordinal);
    }

    private static void AssertStageContract(
        IEnumerable<(string FileName, string Text)> sources, string[] entries,
        string[] repositoryEntries, IReadOnlyDictionary<string, int> siteCounts)
    {
        foreach (var entry in entries)
        {
            var parts = entry.Split(" : ", 3, StringSplitOptions.None);
            Assert.True(parts.Length == 3 && parts.All(p => !string.IsNullOrWhiteSpace(p)),
                $"Invalid stage allow-list entry: {entry}");
        }
        var keys = entries.Select(EntryKey).ToArray();
        Assert.True(keys.SequenceEqual(keys.Order(StringComparer.Ordinal)),
            "Stage allow-list keys must be sorted ordinally.");
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(keys, siteCounts.Keys.Order(StringComparer.Ordinal).ToArray());
        var repositoryKeys = repositoryEntries.Select(EntryKey).ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            Assert.Contains(key, StageScope);
            Assert.True(repositoryKeys.Contains(key),
                $"Stage entry absent from repository allow-list: {key}");
        }
        var found = ScanSites(sources).Where(key => StageScope.Contains(key, StringComparer.Ordinal)).ToArray();
        AssertCoverage(found, entries);
        AssertNoStaleEntries(found, entries);
        foreach (var key in keys)
        {
            var count = found.Count(site => site == key);
            Assert.True(siteCounts[key] > 0 && count == siteCounts[key],
                $"{key} sites={count} expected={siteCounts[key]}");
        }
    }

    private static string Fixture(string member, string body) => $$"""
        partial class ConductorDriver
        {
            private ConductorAdvanceResult {{member}}()
            {
                {{body}}
            }
        }
        """;
}
