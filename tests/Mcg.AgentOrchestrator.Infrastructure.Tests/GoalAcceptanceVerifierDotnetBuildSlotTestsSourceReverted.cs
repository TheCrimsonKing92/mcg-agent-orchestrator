using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSourceReverted : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string Identity = "AddedProbeTests.AddedBehavior";
    private const string Source = "src/Feature.cs";
    private const string Added = "src/Added.cs";
    private const string Deleted = "src/Deleted.cs";

    [Xunit.Fact]
    public async Task CandidateTestsRunAgainstMergeBaseSourceAndDemonstrateRed()
    {
        var probe = await RunProbeAsync();
        var reverted = Assert.Single(probe.Result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, probe.Result.NegativeControlOutcome);
        Assert.Equal(FindingEvidenceArmDisposition.Red, reverted.Disposition);
        Assert.Contains(reverted.Checks, check => check.FailingTestIdentities?.Contains(Identity) == true);
        Assert.True(probe.Result.Passed);
        Assert.True(probe.Result.IsValidEvidence);
        Assert.Null(probe.Result.OutcomeReason);
        Assert.Equal("baseline source", probe.SourceContent);
        Assert.Equal("deleted baseline source", probe.DeletedContent);
        Assert.Equal(probe.CandidateTestContent, probe.TestContent);
        Assert.Equal(1, probe.RevertedTestCalls);
        Assert.Contains(probe.RevertedArguments!, argument => argument.Contains("AddedProbeTests", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public async Task PassingRevertedTestsDoNotDemonstrateNegativeControl()
    {
        var probe = await RunProbeAsync(revertedGreen: true);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.NotDemonstrated, probe.Result.NegativeControlOutcome);
        Assert.Equal(FindingEvidenceArmDisposition.Green,
            Assert.Single(probe.Result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted).Disposition);
        Assert.True(probe.Result.Passed);
    }

    [Xunit.Fact]
    public async Task RevertedBuildFailureIsInconclusiveWithCompilerError()
    {
        var probe = await RunProbeAsync(buildFailure: true);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Inconclusive, probe.Result.NegativeControlOutcome);
        Assert.Contains("error CS0103", probe.Result.Summary, StringComparison.Ordinal);
        Assert.Equal(FindingEvidenceArmDisposition.Green,
            Assert.Single(probe.Result.Arms!, arm => arm.Arm == FindingEvidenceArm.Candidate).Disposition);
        Assert.True(probe.Result.Passed);
        Assert.True(probe.Result.IsValidEvidence);
        Assert.Equal(0, probe.RevertedTestCalls);
    }

    [Xunit.Fact]
    public async Task AddedSourceIsRemovedAndTemporaryWorktreeDisposedWithoutChangingCandidate()
    {
        var probe = await RunProbeAsync();
        Assert.False(probe.AddedSourceExists);
        Assert.NotNull(probe.RevertedPath);
        Assert.False(Directory.Exists(probe.RevertedPath));
        Assert.DoesNotContain(probe.RevertedPath.Replace('\\', '/'), probe.WorktreeList.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(probe.BeforeHead, probe.AfterHead);
        Assert.Equal(probe.BeforeBranch, probe.AfterBranch);
        Assert.Equal("candidate source", probe.CandidateSourceAfter);
        Assert.True(probe.CandidateAddedAfter);
        Assert.Empty(probe.CandidateStatusAfter);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task DirtyCandidateIsInconclusiveWithoutCreatingRevertedWorktree(bool untracked)
    {
        var probe = await RunProbeAsync(dirty: !untracked, untracked: untracked);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Inconclusive, probe.Result.NegativeControlOutcome);
        Assert.Contains("tracked uncommitted changes", probe.Result.Summary, StringComparison.Ordinal);
        Assert.Null(probe.RevertedPath);
        Assert.True(probe.Result.Passed);
    }

    [Xunit.Fact]
    public async Task NoChangedSourceIsInconclusiveWithoutCreatingRevertedWorktree()
    {
        var probe = await RunProbeAsync(noSourceChanges: true);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Inconclusive, probe.Result.NegativeControlOutcome);
        Assert.Contains("no candidate-changed src/ paths", probe.Result.Summary, StringComparison.Ordinal);
        Assert.Null(probe.RevertedPath);
    }

    [Xunit.Fact]
    public async Task CandidateRedRemainsRedAndControlIsInconclusive()
    {
        var probe = await RunProbeAsync(candidateRed: true);
        Assert.False(probe.Result.Passed);
        Assert.Equal(FindingEvidenceArmDisposition.Red,
            Assert.Single(probe.Result.Arms!, arm => arm.Arm == FindingEvidenceArm.Candidate).Disposition);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Inconclusive, probe.Result.NegativeControlOutcome);
        Assert.Null(probe.RevertedPath);
    }

    [Xunit.Fact]
    public async Task NegativeControlIsAdditiveToExistingBaselineExperiment()
    {
        var probe = await RunProbeAsync(runBaseline: true);
        Assert.Single(probe.Result.Arms!, arm => arm.Arm == FindingEvidenceArm.Baseline);
        Assert.Single(probe.Result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
        // Candidate-added selections stay absent at baseline, which retains its existing semantics.
        Assert.Equal(FindingEvidenceOutcomeReason.VacuousEvidence, probe.Result.OutcomeReason);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, probe.Result.NegativeControlOutcome);
    }

    private async Task<Probe> RunProbeAsync(bool revertedGreen = false, bool buildFailure = false,
        bool dirty = false, bool noSourceChanges = false, bool candidateRed = false, bool runBaseline = false,
        bool untracked = false)
    {
        using var fixture = FindingBaselineProbeFixture.Create();
        if (!noSourceChanges) PrepareSourceChanges(fixture.Root);
        var probe = new Probe
        {
            BeforeHead = Git(fixture.Root, "rev-parse", "HEAD").Trim(),
            BeforeBranch = Git(fixture.Root, "symbolic-ref", "HEAD").Trim(),
            CandidateTestContent = File.ReadAllText(Path.Combine(fixture.Root, fixture.AddedTestPath))
        };
        if (dirty) File.WriteAllText(Path.Combine(fixture.Root, Source), "uncommitted source");
        if (untracked) File.WriteAllText(Path.Combine(fixture.Root, "src/Uncommitted.cs"), "uncommitted source");
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(worktree, Source)) &&
                File.ReadAllText(Path.Combine(worktree, Source)) == "baseline source";
            if (reverted)
            {
                probe.RevertedPath = worktree;
                probe.SourceContent = File.ReadAllText(Path.Combine(worktree, Source));
                probe.DeletedContent = File.ReadAllText(Path.Combine(worktree, Deleted));
                probe.TestContent = File.ReadAllText(Path.Combine(worktree, fixture.AddedTestPath));
                probe.AddedSourceExists = File.Exists(Path.Combine(worktree, Added));
            }
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                if (reverted)
                {
                    probe.RevertedTestCalls++;
                    probe.RevertedArguments = args;
                }
                var red = reverted ? !revertedGreen : candidateRed;
                WriteMtpTrx(args, 1, [Identity]);
                if (red) MakeTrxRed(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(red ? 1 : 0, red ? "Failed: 1" : "Passed: 1"));
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(reverted && buildFailure ? 1 : 0,
                reverted && buildFailure ? "error CS0103: reverted API is absent" : "Build succeeded."));
        });
        var goalId = GoalId.New();
        try
        {
            probe.Result = await verifier.RunFocusedEvidenceAsync(fixture.Root, goalId,
                "Core.Tests: AddedProbeTests", runBaselineArm: runBaseline,
                negativeControl: FindingEvidenceNegativeControl.RevertSrc);
            probe.AfterHead = Git(fixture.Root, "rev-parse", "HEAD").Trim();
            probe.AfterBranch = Git(fixture.Root, "symbolic-ref", "HEAD").Trim();
            probe.CandidateStatusAfter = Git(fixture.Root, "status", "--porcelain", "--untracked-files=no").Trim();
            probe.WorktreeList = Git(fixture.Root, "worktree", "list", "--porcelain");
            if (!noSourceChanges)
            {
                probe.CandidateSourceAfter = File.ReadAllText(Path.Combine(fixture.Root, Source));
                probe.CandidateAddedAfter = File.Exists(Path.Combine(fixture.Root, Added));
            }
            return probe;
        }
        finally { DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId); }
    }

    private static void PrepareSourceChanges(string root)
    {
        Git(root, "checkout", "main");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, Source), "baseline source");
        File.WriteAllText(Path.Combine(root, Deleted), "deleted baseline source");
        Git(root, "add", "src/");
        Git(root, "commit", "-m", "source baseline");
        Git(root, "checkout", "goal/finding-baseline");
        Git(root, "merge", "main", "--no-edit");
        File.WriteAllText(Path.Combine(root, Source), "candidate source");
        File.WriteAllText(Path.Combine(root, Added), "candidate added source");
        File.Delete(Path.Combine(root, Deleted));
        Git(root, "add", "src/");
        Git(root, "commit", "-m", "source candidate");
    }

    private static void MakeTrxRed(string[] args)
    {
        var path = Path.Combine(args[Array.IndexOf(args, "--results-directory") + 1],
            args[Array.IndexOf(args, "--report-trx-filename") + 1]);
        var document = XDocument.Load(path);
        document.Descendants("UnitTestResult").Single().SetAttributeValue("outcome", "Failed");
        var counters = document.Descendants("Counters").Single();
        counters.SetAttributeValue("failed", 1);
        counters.SetAttributeValue("passed", 0);
        document.Save(path);
    }

    private static string Git(string root, params string[] arguments)
    {
        var result = GitCli.Run(root, arguments);
        Assert.True(result.Succeeded, result.Error);
        return result.Output;
    }

    private sealed class SuccessfulLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, true, true);
        public bool SetIntegrity(string path, string level, bool recursive) => true;
    }

    private sealed class Probe
    {
        internal FocusedEvidenceRunResult Result { get; set; } = null!;
        internal string? RevertedPath { get; set; }
        internal string? SourceContent { get; set; }
        internal string? DeletedContent { get; set; }
        internal string? TestContent { get; set; }
        internal string CandidateTestContent { get; init; } = "";
        internal bool AddedSourceExists { get; set; }
        internal int RevertedTestCalls { get; set; }
        internal string[]? RevertedArguments { get; set; }
        internal string BeforeHead { get; init; } = "";
        internal string BeforeBranch { get; init; } = "";
        internal string AfterHead { get; set; } = "";
        internal string AfterBranch { get; set; } = "";
        internal string WorktreeList { get; set; } = "";
        internal string CandidateStatusAfter { get; set; } = "";
        internal string? CandidateSourceAfter { get; set; }
        internal bool CandidateAddedAfter { get; set; }
    }
}
