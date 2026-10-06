using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;
using ReferenceRow = Mcg.AgentOrchestrator.Infrastructure.AcceptanceLaneReuseShadowMiss.ReferenceRow;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

// Pure, in-memory comparisons; safe to run in parallel.
public sealed class AcceptanceLaneReuseShadowMissTests
{
    [Fact]
    public void ExecutedGreenAgainstRecordedRed_RecordsGreenAgainstRedMiss()
    {
        var reference = AcceptanceLaneReuseShadowMiss.ResolveReference(
            [new ReferenceRow("goal", "attempt", "Beta", "beta", true, "RED")], "Beta", "beta");
        var result = AcceptanceLaneReuseShadowMiss.Evaluate("would-reuse", true, "GREEN", reference);
        Assert.True(result.MissEvaluated);
        Assert.True(result.ShadowMiss);
        Assert.Equal("green-against-red", result.MissReason);
        Assert.Equal("RED", result.ReferenceVerdict);
        Assert.Equal("recorded:goal/attempt", result.ReferenceSource);
    }

    [Theory]
    [InlineData("would-reuse", false, "GREEN")]
    [InlineData("must-run", true, "RED")]
    [InlineData("would-reuse", true, null)]
    public void IneligibleRow_DoesNotEvaluateOrExposeReference(string decision, bool executed, string? verdict)
    {
        var result = AcceptanceLaneReuseShadowMiss.Evaluate(decision, executed, verdict, ("GREEN", "landing-rule"));
        Assert.False(result.MissEvaluated);
        Assert.False(result.ShadowMiss);
        Assert.Null(result.ReferenceVerdict);
        Assert.Null(result.ReferenceSource);
        Assert.Null(result.MissReason);
    }

    [Fact]
    public void NestedGenericTheoryIdentity_ReducesToDistinctSortedOuterClasses()
    {
        Assert.Equal(["AlphaTests", "OuterTests"], AcceptanceLaneReuseShadowMiss.ReduceFailingClasses(
            ["Ns.OuterTests`1+Inner`1.Method(path: \"a.b.c\")", "Ns.AlphaTests.Example", "Ns.OuterTests.Other"]));
        Assert.Empty(AcceptanceLaneReuseShadowMiss.ReduceFailingClasses(null));
    }

    [Fact]
    public void MixedReferenceVerdicts_GreenWinsWithOrdinalGoalThenAttemptOrder()
    {
        ReferenceRow[] rows =
        [
            new("a", "a", "Beta", "beta", true, "RED"),
            new("z", "a", "Beta", "beta", true, "GREEN"),
            new("b", "z", "Beta", "beta", true, "GREEN"),
            new("b", "a", "Beta", "beta", true, "GREEN")
        ];
        Assert.Equal(("GREEN", "recorded:b/a"),
            AcceptanceLaneReuseShadowMiss.ResolveReference(rows, "Beta", "beta"));
    }

    [Fact]
    public void RedReferences_ChooseOrdinalSourceAndIgnoreOtherOrUnexecutedRows()
    {
        ReferenceRow[] rows =
        [
            new("a", "a", "Beta", "beta", false, "GREEN"),
            new("a", "a", "Alpha", "beta", true, "GREEN"),
            new("a", "a", "Beta", "other", true, "GREEN"),
            new("a", "a", "Beta", "beta", true, null),
            new("z", "a", "Beta", "beta", true, "RED"),
            new("b", "z", "Beta", "beta", true, "RED"),
            new("b", "a", "Beta", "beta", true, "RED")
        ];
        Assert.Equal(("RED", "recorded:b/a"),
            AcceptanceLaneReuseShadowMiss.ResolveReference(rows, "Beta", "beta"));
        Assert.Equal(("GREEN", "landing-rule"),
            AcceptanceLaneReuseShadowMiss.ResolveReference(rows, "Missing", "missing"));
    }

    [Theory]
    [InlineData("BLUE", "GREEN")]
    [InlineData("RED", "BLUE")]
    public void InvalidVerdict_FailsLoudly(string verdict, string reference)
    {
        Assert.Throws<ArgumentException>(() => AcceptanceLaneReuseShadowMiss.Evaluate(
            "would-reuse", true, verdict, (reference, "landing-rule")));
    }

    [Theory]
    [InlineData("main-tree", 1)]
    [InlineData(null, 0)]
    public void ReferenceRead_ExcludesSelfOtherTreesAndWholeMalformedRecords(string? tree, int count)
    {
        var root = Path.Combine(Path.GetTempPath(), $"lane-shadow-reference-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "goal");
        Directory.CreateDirectory(directory);
        try
        {
            WriteReference(directory, "valid", "main-tree");
            WriteReference(directory, "other-tree", "other-tree");
            File.WriteAllText(Path.Combine(directory, "self.json"), "{invalid but excluded");
            File.WriteAllText(Path.Combine(directory, "bad-root.json"), "[]");
            // A valid first row must not escape a record containing a malformed later row.
            File.WriteAllText(Path.Combine(directory, "bad-row.json"), """
                {"candidate_tree_sha":"main-tree","lanes":[
                  {"lane":"Beta","partition_id":"beta","executed":true,"verdict":"GREEN"},
                  {"lane":"Beta","partition_id":"beta","executed":"yes","verdict":"RED"}]}
                """);
            var read = AcceptanceLaneReuseShadowReferenceReader.Read(root, "goal", "self", tree);
            Assert.Equal(2, read.UnreadableCount);
            Assert.Equal(count, read.Rows.Count);
            if (count == 1)
            {
                Assert.Equal(new ReferenceRow("goal", "valid", "Beta", "beta", true, "RED"), Assert.Single(read.Rows));
                Assert.Equal(("RED", "recorded:goal/valid"),
                    AcceptanceLaneReuseShadowMiss.ResolveReference(read.Rows, "Beta", "beta"));
            }
            else Assert.Empty(read.Rows);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingRootAndUnresolvableMainTree_UseLandingRule()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lane-shadow-missing-{Guid.NewGuid():N}");
        var tree = AcceptanceLaneReuseShadowReferenceReader.ResolveMainTreeSha(root, "missing-main");
        Assert.Null(tree);
        var read = AcceptanceLaneReuseShadowReferenceReader.Read(root, "goal", "attempt", tree);
        Assert.Empty(read.Rows);
        Assert.Equal(0, read.UnreadableCount);
        Assert.Equal(("GREEN", "landing-rule"),
            AcceptanceLaneReuseShadowMiss.ResolveReference(read.Rows, "Beta", "beta"));
    }

    private static void WriteReference(string directory, string attempt, string tree) =>
        File.WriteAllText(Path.Combine(directory, $"{attempt}.json"), JsonSerializer.Serialize(new
        {
            candidate_tree_sha = tree,
            lanes = new[] { new { lane = "Beta", partition_id = "beta", executed = true, verdict = "RED" } }
        }));
}
