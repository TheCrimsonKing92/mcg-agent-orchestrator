using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class NegativeControlRevertDeclarationTests
{
    [Fact]
    public void Parse_CanonicalizesOnlyLineDeclarations()
    {
        var parsed = NegativeControlRevertDeclaration.Parse("Brief\n  negative-control-revert: tests/A/Support.cs, config\\policy.json , .agents/skills/x/guide.md, tests/A/Support.cs\nEnd");
        Assert.True(parsed.Declared);
        Assert.Null(parsed.Rejection);
        Assert.Equal([".agents/skills/x/guide.md", "config/policy.json", "tests/A/Support.cs"], parsed.Paths);
        var absent = NegativeControlRevertDeclaration.Parse("Use negative-control-revert: tests/A.cs in briefs.");
        Assert.False(absent.Declared);
        Assert.Null(absent.Rejection);
        Assert.Empty(absent.Paths);
    }

    [Theory]
    [InlineData("negative-control-revert: tests/A.cs\nnegative-control-revert: config/a.json", "declaration-duplicate")]
    [InlineData("negative-control-revert:", "declaration-empty")]
    [InlineData("negative-control-revert: tests/*.cs", "declaration-path-invalid")]
    [InlineData("negative-control-revert: tests/../src/A.cs", "declaration-path-invalid")]
    [InlineData("negative-control-revert: C:/tests/A.cs", "declaration-path-invalid")]
    [InlineData("negative-control-revert: /tests/A.cs", "declaration-path-invalid")]
    [InlineData("negative-control-revert: tests//A.cs", "declaration-path-invalid")]
    [InlineData("negative-control-revert: ./tests/A.cs", "declaration-path-invalid")]
    [InlineData("negative-control-revert: tests/A.cs,", "declaration-path-invalid")]
    [InlineData("negative-control-revert: tests/?.cs", "declaration-path-invalid")]
    [InlineData("negative-control-revert: tests/[A].cs", "declaration-path-invalid")]
    [InlineData("negative-control-revert: src/A.cs", "declaration-root-not-allowed")]
    [InlineData("negative-control-revert: docs/a.md", "declaration-root-not-allowed")]
    public void Parse_RejectionAuthorizesNothing(string brief, string code)
    {
        var parsed = NegativeControlRevertDeclaration.Parse(brief);
        Assert.True(parsed.Declared);
        Assert.Equal(code, parsed.Rejection);
        Assert.Empty(parsed.Paths);
    }

    [Theory]
    [InlineData(FindingEvidenceRevertPathsRejection.SelectedTestClass, "revert-paths-selected-test-class")]
    [InlineData(FindingEvidenceRevertPathsRejection.MutationSelectedTestClass, "mutation-selected-test-class")]
    public void SelectedClassRejection_RoundTrips(FindingEvidenceRevertPathsRejection rejection, string wire)
    {
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(rejection));
        Assert.Equal(rejection, JsonSerializer.Deserialize<FindingEvidenceRevertPathsRejection>($"\"{wire}\""));
    }

    [Fact]
    public void ArmReceipt_PathListsRoundTripAndAreOmittedWhenNull()
    {
        var receipt = new FindingEvidenceArmReceipt(FindingEvidenceArm.SourceReverted, "candidate",
            FindingEvidenceArmDisposition.Red, true, false, "red",
            RestoredPaths: ["tests/A/Support.cs"], DroppedPaths: ["tests/A/Counter.cs"]);
        var copy = JsonSerializer.Deserialize<FindingEvidenceArmReceipt>(JsonSerializer.Serialize(receipt))!;
        Assert.Equal(receipt.RestoredPaths, copy.RestoredPaths);
        Assert.Equal(receipt.DroppedPaths, copy.DroppedPaths);
        var legacyJson = JsonSerializer.Serialize(receipt with { RestoredPaths = null, DroppedPaths = null });
        Assert.DoesNotContain("RestoredPaths", legacyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("DroppedPaths", legacyJson, StringComparison.Ordinal);
    }
}
