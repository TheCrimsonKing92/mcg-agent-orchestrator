using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSourceRevertedPaths : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task TargetedRevert_TwoChangedSources_PreservesUnlistedCandidateSource()
    {
        using var fixture = new SourceRevertedProbeFixture();
        Assert.Contains("Feature.Enabled && Other.AddedMember", fixture.Read(SourceRevertedProbeFixture.Test), StringComparison.Ordinal);
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        string? revertedPath = null;
        var revertedTestCalls = 0;
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (reverted)
            {
                revertedPath = worktree;
                Assert.Equal(SourceRevertedProbeFixture.BaselineFeature, Read(worktree, SourceRevertedProbeFixture.Feature));
                Assert.Equal(SourceRevertedProbeFixture.CandidateOther, Read(worktree, SourceRevertedProbeFixture.Other));
                Assert.Equal(fixture.CandidateTest, Read(worktree, SourceRevertedProbeFixture.Test));
                Assert.True(File.Exists(Path.Combine(worktree, SourceRevertedProbeFixture.Added)));
                Assert.True(File.Exists(Path.Combine(worktree, SourceRevertedProbeFixture.UnlistedAdded)));
                Assert.False(File.Exists(Path.Combine(worktree, SourceRevertedProbeFixture.Deleted)));
            }
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                if (reverted) revertedTestCalls++;
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
                if (reverted) SourceRevertedProbeFixture.MakeTrxRed(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(reverted ? 1 : 0, reverted ? "Failed: 1" : "Passed: 1"));
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });
        var result = await fixture.RunAsync(verifier, [SourceRevertedProbeFixture.Feature]);
        Assert.True(result.Passed, result.Summary);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, result.NegativeControlOutcome);
        Assert.Equal(1, revertedTestCalls);
        Assert.NotNull(revertedPath);
        Assert.False(Directory.Exists(revertedPath));
        Assert.DoesNotContain(revertedPath.Replace('\\', '/'), fixture.Git("worktree", "list", "--porcelain").Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.CandidateSha, fixture.Git("rev-parse", "HEAD").Trim());
        Assert.Equal(SourceRevertedProbeFixture.CandidateFeature, fixture.Read(SourceRevertedProbeFixture.Feature));
        Assert.True(File.Exists(Path.Combine(fixture.Root, SourceRevertedProbeFixture.Added)));
        Assert.Empty(fixture.Git("status", "--porcelain").Trim());
    }

    [Xunit.Fact]
    public async Task TargetedRevert_AddedAndDeletedPaths_RevertsOnlyNamedFiles()
    {
        using var fixture = new SourceRevertedProbeFixture();
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        var revertedTestCalls = 0;
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (reverted)
            {
                Assert.False(File.Exists(Path.Combine(worktree, SourceRevertedProbeFixture.Added)));
                Assert.Equal("public static class Deleted { }", Read(worktree, SourceRevertedProbeFixture.Deleted));
                Assert.True(File.Exists(Path.Combine(worktree, SourceRevertedProbeFixture.UnlistedAdded)));
                Assert.Equal(SourceRevertedProbeFixture.CandidateFeature, Read(worktree, SourceRevertedProbeFixture.Feature));
                Assert.Equal(SourceRevertedProbeFixture.CandidateOther, Read(worktree, SourceRevertedProbeFixture.Other));
            }
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                if (reverted) revertedTestCalls++;
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
        });
        var result = await fixture.RunAsync(verifier, [SourceRevertedProbeFixture.Added, SourceRevertedProbeFixture.Deleted]);
        Assert.True(result.Passed, result.Summary);
        Assert.Equal(1, revertedTestCalls);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.NotDemonstrated, result.NegativeControlOutcome);
        Assert.True(File.Exists(Path.Combine(fixture.Root, SourceRevertedProbeFixture.Added)));
        Assert.False(File.Exists(Path.Combine(fixture.Root, SourceRevertedProbeFixture.Deleted)));
        Assert.Empty(fixture.Git("status", "--porcelain").Trim());
    }

    [Xunit.Theory]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Core.Tests/AddedProbeTests.cs", FindingEvidenceRevertPathsRejection.UnderTests)]
    [Xunit.InlineData("docs/readme.md", FindingEvidenceRevertPathsRejection.OutsideSrc)]
    [Xunit.InlineData("src/Unchanged.cs", FindingEvidenceRevertPathsRejection.NotChangedByGoal)]
    [Xunit.InlineData("src/Missing.cs", FindingEvidenceRevertPathsRejection.NotChangedByGoal)]
    [Xunit.InlineData("src/../tests/Probe.cs", FindingEvidenceRevertPathsRejection.OutsideSrc)]
    [Xunit.InlineData("C:/src/Feature.cs", FindingEvidenceRevertPathsRejection.OutsideSrc)]
    [Xunit.InlineData("/src/Feature.cs", FindingEvidenceRevertPathsRejection.OutsideSrc)]
    [Xunit.InlineData(null, FindingEvidenceRevertPathsRejection.EmptyList)]
    public async Task InvalidPaths_RejectedWithTypedReason_SkipRevertedExecution(string? path, FindingEvidenceRevertPathsRejection expected)
    {
        using var fixture = new SourceRevertedProbeFixture();
        var candidateTestCalls = 0;
        var revertedCalls = 0;
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
        var result = await fixture.RunAsync(verifier, path is null ? [] : [path]);
        Assert.Equal(1, candidateTestCalls);
        Assert.Equal(0, revertedCalls);
        Assert.Equal(expected, result.RevertPathsRejection);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Inconclusive, result.NegativeControlOutcome);
        Assert.Contains(FindingEvidenceRevertPathsRejectionJsonConverter.ToWireValue(expected), result.Summary, StringComparison.Ordinal);
        var arm = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, arm.Disposition);
        Assert.Empty(arm.Checks);
        Assert.Equal(expected, arm.RevertPathsRejection);
    }

    private static string Read(string root, string path) => File.ReadAllText(Path.Combine(root, path));

    private sealed class SuccessfulLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, true, true);
        public bool SetIntegrity(string path, string level, bool recursive) => true;
    }
}
