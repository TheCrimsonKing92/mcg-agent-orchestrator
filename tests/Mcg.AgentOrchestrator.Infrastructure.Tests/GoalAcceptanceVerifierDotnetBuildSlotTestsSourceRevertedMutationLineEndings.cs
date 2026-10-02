using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSourceRevertedMutationLineEndings : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Block = "    public static bool AddedMember => true;\n    public static bool Enabled => true;\n";
    private static readonly string RestoredBlock = Block.Replace("Enabled => true", "Enabled => false", StringComparison.Ordinal);

    [Xunit.Fact]
    public async Task Mutation_MixedCandidateAndAutocrlfCheckout_DemonstratesRed()
    {
        using var fixture = new SourceRevertedProbeFixture(includeMutationProbe: true);
        fixture.Git("config", "core.autocrlf", "true");
        var lfText = SourceRevertedProbeFixture.CandidateBehavior.Replace("\r\n", "\n", StringComparison.Ordinal);
        CommitBehavior(fixture, lfText);
        var freshCheckout = lfText.Replace("\n", "\r\n", StringComparison.Ordinal);
        var mixedText = freshCheckout.Replace(Block.Replace("\n", "\r\n", StringComparison.Ordinal), Block, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(fixture.Root, SourceRevertedProbeFixture.Behavior), mixedText, new UTF8Encoding(false));
        Assert.Contains("\r\n", mixedText, StringComparison.Ordinal);
        Assert.Contains(Block, mixedText, StringComparison.Ordinal);
        // Refresh Git's working-file stat entry after the ending-only rewrite; the tree must stay unchanged.
        fixture.Git("add", SourceRevertedProbeFixture.Behavior);
        Assert.Empty(fixture.Git("diff", "--cached", "--name-only").Trim());
        Assert.Empty(fixture.Git("status", "--porcelain", "--untracked-files=all").Trim());
        var expected = Encoding.UTF8.GetBytes(freshCheckout.Replace(
            Block.Replace("\n", "\r\n", StringComparison.Ordinal),
            RestoredBlock.Replace("\n", "\r\n", StringComparison.Ordinal), StringComparison.Ordinal));

        await AssertDemonstratedAsync(fixture, new(SourceRevertedProbeFixture.Behavior, Block, RestoredBlock), expected);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    public async Task Mutation_OppositeLineEndings_UsesMatchedStyle(bool fileCrLf, bool replacementCrLf)
    {
        using var fixture = new SourceRevertedProbeFixture(includeMutationProbe: true);
        var lfText = SourceRevertedProbeFixture.CandidateBehavior.Replace("\r\n", "\n", StringComparison.Ordinal);
        var fileText = fileCrLf ? lfText.Replace("\n", "\r\n", StringComparison.Ordinal) : lfText;
        if (!fileCrLf) CommitBehavior(fixture, fileText);
        var oldText = fileCrLf ? Block : Block.Replace("\n", "\r\n", StringComparison.Ordinal);
        var newText = replacementCrLf ? RestoredBlock.Replace("\n", "\r\n", StringComparison.Ordinal) : RestoredBlock;
        var expectedLf = lfText.Replace(Block, RestoredBlock, StringComparison.Ordinal);
        var expected = Encoding.UTF8.GetBytes(fileCrLf ? expectedLf.Replace("\n", "\r\n", StringComparison.Ordinal) : expectedLf);

        await AssertDemonstratedAsync(fixture, new(SourceRevertedProbeFixture.Behavior, oldText, newText), expected);
    }

    [Xunit.Fact]
    public async Task Mutation_MixedMatchedSpan_UsesCrLfAndPreservesSurroundingBytes()
    {
        using var fixture = new SourceRevertedProbeFixture(includeMutationProbe: true);
        var mixedBlock = Block.Replace("AddedMember => true;\n", "AddedMember => true;\r\n", StringComparison.Ordinal);
        var fileText = "// café 🌱\npublic static class Behavior {\n" + mixedBlock + "}\n";
        CommitBehavior(fixture, fileText);
        var expected = Encoding.UTF8.GetBytes(fileText.Replace(mixedBlock,
            RestoredBlock.Replace("\n", "\r\n", StringComparison.Ordinal), StringComparison.Ordinal));

        await AssertDemonstratedAsync(fixture, new(SourceRevertedProbeFixture.Behavior, Block, RestoredBlock), expected);
    }

    [Xunit.Fact]
    public async Task Mutation_SingleLineTrailingCarriageReturn_RetainsExactSpan()
    {
        using var fixture = new SourceRevertedProbeFixture(includeMutationProbe: true);
        var oldText = SourceRevertedProbeFixture.BehaviorLine + "\r";
        var newText = SourceRevertedProbeFixture.RestoredBehaviorLine + "\r";
        var expected = Encoding.UTF8.GetBytes(SourceRevertedProbeFixture.CandidateBehavior.Replace(oldText, newText, StringComparison.Ordinal));

        await AssertDemonstratedAsync(fixture, new(SourceRevertedProbeFixture.Behavior, oldText, newText), expected);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, FindingEvidenceRevertPathsRejection.MutationOldTextAmbiguous)]
    [Xunit.InlineData(true, FindingEvidenceRevertPathsRejection.MutationUnchangedText)]
    public async Task Mutation_NormalizedAmbiguityOrNoOp_RejectsBeforeRevertedExecution(
        bool unchanged, FindingEvidenceRevertPathsRejection expected)
    {
        using var fixture = new SourceRevertedProbeFixture(includeMutationProbe: true);
        // One occurrence in each style must become two occurrences in the comparison.
        if (!unchanged) CommitBehavior(fixture, SourceRevertedProbeFixture.CandidateBehavior + Block);
        var revertedCalls = 0;
        var candidateTestCalls = 0;
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            if (!string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase)) revertedCalls++;
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                candidateTestCalls++;
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
        });
        var mutation = new FindingEvidenceMutation(SourceRevertedProbeFixture.Behavior, Block,
            unchanged ? Block.Replace("\n", "\r\n", StringComparison.Ordinal) : RestoredBlock);

        var result = await fixture.RunMutationAsync(verifier, mutation);

        Assert.Equal(1, candidateTestCalls);
        Assert.Equal(0, revertedCalls);
        Assert.Equal(expected, result.RevertPathsRejection);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Inconclusive, result.NegativeControlOutcome);
        var arm = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, arm.Disposition);
        Assert.Equal(expected, arm.RevertPathsRejection);
        Assert.Empty(arm.Checks);
    }

    private async Task AssertDemonstratedAsync(SourceRevertedProbeFixture fixture, FindingEvidenceMutation mutation, byte[] expected)
    {
        var before = File.ReadAllBytes(Path.Combine(fixture.Root, SourceRevertedProbeFixture.Behavior));
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        string? revertedPath = null;
        var revertedTestCalls = 0;
        var candidateTestCalls = 0;
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (reverted)
            {
                revertedPath = worktree;
                Assert.Equal(expected, File.ReadAllBytes(Path.Combine(worktree, SourceRevertedProbeFixture.Behavior)));
            }
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
                if (reverted)
                {
                    revertedTestCalls++;
                    SourceRevertedProbeFixture.MakeTrxRed(args);
                }
                else candidateTestCalls++;
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(reverted ? 1 : 0,
                    reverted ? "Failed: 1; Assert.True() Failure: Behavior.Enabled was false" : "Passed: 1"));
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var result = await fixture.RunMutationAsync(verifier, mutation);

        Assert.True(result.Passed, result.Summary);
        Assert.True(result.IsValidEvidence);
        Assert.Null(result.RevertPathsRejection);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, result.NegativeControlOutcome);
        Assert.Equal(1, candidateTestCalls);
        Assert.Equal(1, revertedTestCalls);
        Assert.Equal(FindingEvidenceArmDisposition.Red,
            Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted).Disposition);
        Assert.NotNull(revertedPath);
        Assert.False(Directory.Exists(revertedPath));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Root, SourceRevertedProbeFixture.Behavior)));
    }

    private static void CommitBehavior(SourceRevertedProbeFixture fixture, string text)
    {
        File.WriteAllText(Path.Combine(fixture.Root, SourceRevertedProbeFixture.Behavior), text, new UTF8Encoding(false));
        fixture.Git("add", SourceRevertedProbeFixture.Behavior);
        fixture.Git("commit", "-m", "line ending candidate");
    }

    private sealed class SuccessfulLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, true, true);
        public bool SetIntegrity(string path, string level, bool recursive) => true;
    }
}
