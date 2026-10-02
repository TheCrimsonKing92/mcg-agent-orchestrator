using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Real git repositories are isolated per fixture; shared verifier overrides use JobAccounting.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSourceRevertedPolicyFile : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Policy = ".gitattributes";
    private const string BaselinePolicy = "*.probe probe-policy=baseline";
    private const string CandidatePolicy = "*.probe probe-policy=candidate";
    private const string PolicyTest = """
        public sealed class AddedProbeTests {
            [Xunit.Fact] public void AddedBehavior() => Xunit.Assert.Equal(
                "*.probe probe-policy=candidate", System.IO.File.ReadAllText(PolicyPath()));
            private static string PolicyPath([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "..", "..", ".gitattributes");
        }
        """;

    [Xunit.Fact]
    public async Task PolicyFileRevert_PreservesCandidateSourceAndTests()
    {
        using var fixture = CreateFixture(BaselinePolicy, CandidatePolicy);
        var revertedTestCalls = 0;
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (reverted)
            {
                Assert.Equal(BaselinePolicy, Read(worktree, Policy));
                Assert.Equal(SourceRevertedProbeFixture.CandidateFeature, Read(worktree, SourceRevertedProbeFixture.Feature));
                Assert.Equal(PolicyTest, Read(worktree, SourceRevertedProbeFixture.Test));
            }
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                if (reverted) revertedTestCalls++;
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
        });
        var result = await fixture.RunAsync(verifier, [Policy]);
        Assert.True(result.Passed, result.Summary);
        Assert.Null(result.RevertPathsRejection);
        Assert.Equal(FindingEvidenceArmDisposition.Green, SourceReverted(result).Disposition);
        Assert.Equal(1, revertedTestCalls);
        Assert.Equal(CandidatePolicy, fixture.Read(Policy));
        Assert.Empty(fixture.Git("status", "--porcelain").Trim());
    }

    [Xunit.Fact]
    public async Task PolicyReadingTest_BaselinePolicy_DemonstratesNegativeControl()
    {
        using var fixture = CreateFixture(BaselinePolicy, CandidatePolicy);
        var candidateTestCalls = 0;
        var revertedTestCalls = 0;
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(SourceRevertedProbeFixture.CandidateFeature, Read(worktree, SourceRevertedProbeFixture.Feature));
            Assert.Equal(PolicyTest, Read(worktree, SourceRevertedProbeFixture.Test));
            if (Array.IndexOf(args, "--results-directory") < 0)
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
            if (reverted) revertedTestCalls++; else candidateTestCalls++;
            var policy = Read(worktree, Policy);
            Assert.Equal(reverted ? BaselinePolicy : CandidatePolicy, policy);
            // Match the fixture test's assertion: its verdict depends only on policy data.
            var passed = string.Equals(CandidatePolicy, policy, StringComparison.Ordinal);
            WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
            if (!passed) SourceRevertedProbeFixture.MakeTrxRed(args);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(passed ? 0 : 1,
                passed ? "Passed: 1" : "Failed: 1; Assert.Equal() Failure: repository policy is baseline"));
        });
        var result = await fixture.RunAsync(verifier, [Policy]);
        Assert.True(result.Passed, result.Summary);
        Assert.Null(result.RevertPathsRejection);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, result.NegativeControlOutcome);
        Assert.Contains("negative-control-demonstrated", result.Summary, StringComparison.Ordinal);
        var arm = SourceReverted(result);
        Assert.Equal(FindingEvidenceArmDisposition.Red, arm.Disposition);
        Assert.Contains(arm.Checks, check => check.FailingTestIdentities?.Contains(SourceRevertedProbeFixture.Identity) == true);
        Assert.Equal(1, candidateTestCalls);
        Assert.Equal(1, revertedTestCalls);
    }

    [Xunit.Theory]
    [Xunit.InlineData(".gitattributes", FindingEvidenceRevertPathsRejection.NotChangedByGoal)]
    [Xunit.InlineData("scripts/Invoke-TestSummary.ps1", FindingEvidenceRevertPathsRejection.OutsideSrc)]
    [Xunit.InlineData("Directory.Build.props", FindingEvidenceRevertPathsRejection.OutsideSrc)]
    [Xunit.InlineData("docs/readme.md", FindingEvidenceRevertPathsRejection.OutsideSrc)]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Core.Tests/AddedProbeTests.cs", FindingEvidenceRevertPathsRejection.UnderTests)]
    [Xunit.InlineData(".GITATTRIBUTES", FindingEvidenceRevertPathsRejection.OutsideSrc)]
    [Xunit.InlineData("sub/.gitattributes", FindingEvidenceRevertPathsRejection.OutsideSrc)]
    public async Task InvalidPolicyPaths_RejectWithTypedReason_SkipRevertedArm(
        string path, FindingEvidenceRevertPathsRejection expected)
    {
        using var fixture = CreateFixture(BaselinePolicy, BaselinePolicy);
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
        var result = await fixture.RunAsync(verifier, [path]);
        Assert.Equal(expected, result.RevertPathsRejection);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Inconclusive, result.NegativeControlOutcome);
        Assert.Equal(expected, SourceReverted(result).RevertPathsRejection);
        Assert.Empty(SourceReverted(result).Checks);
        Assert.Equal(1, candidateTestCalls);
        Assert.Equal(0, revertedCalls);
    }

    [Xunit.Fact]
    public async Task WholeChangeRevert_ChangedPolicy_LeavesCandidatePolicy()
    {
        using var fixture = CreateFixture(BaselinePolicy, CandidatePolicy);
        var revertedTestCalls = 0;
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (reverted)
            {
                Assert.Equal(CandidatePolicy, Read(worktree, Policy));
                Assert.Equal(SourceRevertedProbeFixture.BaselineFeature, Read(worktree, SourceRevertedProbeFixture.Feature));
                Assert.Equal(PolicyTest, Read(worktree, SourceRevertedProbeFixture.Test));
            }
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                if (reverted) revertedTestCalls++;
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
        });
        var result = await fixture.RunAsync(verifier, paths: null);
        Assert.True(result.Passed, result.Summary);
        Assert.Null(result.RevertPathsRejection);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.NotDemonstrated, result.NegativeControlOutcome);
        Assert.Equal(1, revertedTestCalls);
    }

    [Xunit.Theory]
    [Xunit.InlineData(null, CandidatePolicy)]
    [Xunit.InlineData(BaselinePolicy, null)]
    public async Task PolicyFileRevert_AdditionOrDeletion_RestoresBaseline(string? baseline, string? candidate)
    {
        using var fixture = CreateFixture(baseline, candidate);
        var revertedTestCalls = 0;
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (reverted)
            {
                Assert.Equal(baseline is not null, File.Exists(Path.Combine(worktree, Policy)));
                if (baseline is not null) Assert.Equal(baseline, Read(worktree, Policy));
                Assert.Equal(SourceRevertedProbeFixture.CandidateFeature, Read(worktree, SourceRevertedProbeFixture.Feature));
            }
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                if (reverted) revertedTestCalls++;
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
        });
        var result = await fixture.RunAsync(verifier, [Policy]);
        Assert.True(result.Passed, result.Summary);
        Assert.Null(result.RevertPathsRejection);
        Assert.Equal(FindingEvidenceArmDisposition.Green, SourceReverted(result).Disposition);
        Assert.Equal(1, revertedTestCalls);
        Assert.Equal(candidate is not null, File.Exists(Path.Combine(fixture.Root, Policy)));
    }

    [Xunit.Fact]
    public async Task PolicyMutation_AllowListedFile_StillRejectsOutsideSrc()
    {
        using var fixture = CreateFixture(BaselinePolicy, CandidatePolicy);
        var revertedCalls = 0;
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            if (!string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase)) revertedCalls++;
            if (Array.IndexOf(args, "--results-directory") >= 0)
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
        });
        var result = await fixture.RunMutationAsync(verifier, new(Policy, CandidatePolicy, BaselinePolicy));
        Assert.Equal(FindingEvidenceRevertPathsRejection.MutationOutsideSrc, result.RevertPathsRejection);
        Assert.Equal(0, revertedCalls);
    }

    private static SourceRevertedProbeFixture CreateFixture(string? baseline, string? candidate)
    {
        var fixture = new SourceRevertedProbeFixture();
        try
        {
            fixture.Git("config", "core.autocrlf", "false");
            if (baseline is not null)
            {
                fixture.Git("checkout", "main");
                File.WriteAllText(Path.Combine(fixture.Root, Policy), baseline);
                fixture.Git("add", Policy);
                fixture.Git("commit", "-m", "policy baseline");
                fixture.Git("checkout", "goal/source-reverted");
                fixture.Git("merge", "main", "--no-edit");
            }
            if (candidate is null) File.Delete(Path.Combine(fixture.Root, Policy));
            else File.WriteAllText(Path.Combine(fixture.Root, Policy), candidate);
            File.WriteAllText(Path.Combine(fixture.Root, SourceRevertedProbeFixture.Test),
                candidate is null ? fixture.CandidateTest : PolicyTest.Replace(CandidatePolicy, candidate, StringComparison.Ordinal));
            // Harness paths really changed in this candidate, yet must still be refused.
            File.AppendAllText(Path.Combine(fixture.Root, "Directory.Build.props"), "\n<!-- candidate -->");
            Directory.CreateDirectory(Path.Combine(fixture.Root, "scripts"));
            File.WriteAllText(Path.Combine(fixture.Root, "scripts/Invoke-TestSummary.ps1"), "# candidate harness");
            Directory.CreateDirectory(Path.Combine(fixture.Root, "docs"));
            File.WriteAllText(Path.Combine(fixture.Root, "docs/readme.md"), "candidate documentation");
            fixture.Git("add", ".");
            fixture.Git("commit", "-m", "policy candidate");
            Assert.Empty(fixture.Git("status", "--porcelain").Trim());
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static FocusedEvidenceArmRunResult SourceReverted(FocusedEvidenceRunResult result) =>
        Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
    private static string Read(string root, string path) => File.ReadAllText(Path.Combine(root, path));
    private sealed class SuccessfulLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, true, true);
        public bool SetIntegrity(string path, string level, bool recursive) => true;
    }
}
