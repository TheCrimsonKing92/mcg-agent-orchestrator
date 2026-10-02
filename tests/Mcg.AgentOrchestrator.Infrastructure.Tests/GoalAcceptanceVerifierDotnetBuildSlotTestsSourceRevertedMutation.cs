using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSourceRevertedMutation : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Theory]
    [Xunit.InlineData("utf8")]
    [Xunit.InlineData("utf8-bom")]
    [Xunit.InlineData("utf16-le")]
    [Xunit.InlineData("utf16-be")]
    [Xunit.InlineData("utf32-le")]
    [Xunit.InlineData("utf32-be")]
    public async Task Mutation_OneBehaviorLine_PreservesCandidateMembersAndDemonstratesRed(string encodingName)
    {
        var encoding = EncodingFor(encodingName);
        using var fixture = new SourceRevertedProbeFixture(includeMutationProbe: true, mutationEncoding: encoding);
        var before = File.ReadAllBytes(Path.Combine(fixture.Root, SourceRevertedProbeFixture.Behavior));
        Assert.Contains("Behavior.Enabled && Behavior.AddedMember", fixture.CandidateTest, StringComparison.Ordinal);
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        string? revertedPath = null;
        var revertedBuildCalls = 0;
        var revertedBuildSucceeded = false;
        var revertedTestCalls = 0;
        var candidateTestCalls = 0;
        var expectedText = SourceRevertedProbeFixture.CandidateBehavior.Replace(SourceRevertedProbeFixture.BehaviorLine,
            SourceRevertedProbeFixture.RestoredBehaviorLine, StringComparison.Ordinal);
        var expectedBytes = encoding.GetPreamble().Concat(encoding.GetBytes(expectedText)).ToArray();
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (reverted)
            {
                revertedPath = worktree;
                Assert.Equal(expectedBytes, File.ReadAllBytes(Path.Combine(worktree, SourceRevertedProbeFixture.Behavior)));
                Assert.Contains("public static bool AddedMember => true;", Read(worktree, SourceRevertedProbeFixture.Behavior), StringComparison.Ordinal);
                Assert.Equal(SourceRevertedProbeFixture.CandidateFeature, Read(worktree, SourceRevertedProbeFixture.Feature));
                Assert.Equal(SourceRevertedProbeFixture.CandidateOther, Read(worktree, SourceRevertedProbeFixture.Other));
                Assert.Equal(fixture.CandidateTest, Read(worktree, SourceRevertedProbeFixture.Test));
                Assert.True(File.Exists(Path.Combine(worktree, SourceRevertedProbeFixture.Added)));
                Assert.False(File.Exists(Path.Combine(worktree, SourceRevertedProbeFixture.Deleted)));
            }
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
                if (reverted)
                {
                    revertedTestCalls++;
                    Assert.True(revertedBuildCalls > 0, "The source-reverted build must complete before its named test runs.");
                    Assert.Contains(args, argument => argument.Contains("AddedProbeTests", StringComparison.Ordinal));
                    SourceRevertedProbeFixture.MakeTrxRed(args);
                }
                else candidateTestCalls++;
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(reverted ? 1 : 0,
                    reverted ? "Failed: 1; Assert.True() Failure: Behavior.Enabled was false" : "Passed: 1"));
            }
            if (reverted)
            {
                Assert.Contains("build", args);
                revertedBuildCalls++;
                revertedBuildSucceeded = true;
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });
        var result = await fixture.RunMutationAsync(verifier, new(SourceRevertedProbeFixture.Behavior,
            SourceRevertedProbeFixture.BehaviorLine, SourceRevertedProbeFixture.RestoredBehaviorLine));
        Assert.True(result.Passed, result.Summary);
        Assert.True(result.IsValidEvidence);
        Assert.Null(result.RevertPathsRejection);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, result.NegativeControlOutcome);
        Assert.Equal(1, candidateTestCalls);
        Assert.True(revertedBuildCalls > 0);
        Assert.True(revertedBuildSucceeded);
        Assert.Equal(1, revertedTestCalls);
        var arm = SourceReverted(result);
        Assert.Equal(FindingEvidenceArmDisposition.Red, arm.Disposition);
        Assert.Contains(arm.Checks, check => check.FailingTestIdentities?.Contains(SourceRevertedProbeFixture.Identity) == true &&
            check.OutputTail?.Contains("Behavior.Enabled was false", StringComparison.Ordinal) == true);
        Assert.NotNull(revertedPath);
        Assert.StartsWith(OrchestratorTempRoot.GetPurposeDirectory("mcg-focused-evidence-baselines") + Path.DirectorySeparatorChar,
            revertedPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(revertedPath));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Root, SourceRevertedProbeFixture.Behavior)));
    }

    [Xunit.Theory]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Core.Tests/AddedProbeTests.cs", "", "", FindingEvidenceRevertPathsRejection.MutationUnderTests)]
    [Xunit.InlineData("docs/readme.md", "", "", FindingEvidenceRevertPathsRejection.MutationOutsideSrc)]
    [Xunit.InlineData("src/../tests/Probe.cs", "Enabled", "Disabled", FindingEvidenceRevertPathsRejection.MutationOutsideSrc)]
    [Xunit.InlineData("C:/src/Feature.cs", "Enabled", "Disabled", FindingEvidenceRevertPathsRejection.MutationOutsideSrc)]
    [Xunit.InlineData("/src/Feature.cs", "Enabled", "Disabled", FindingEvidenceRevertPathsRejection.MutationOutsideSrc)]
    [Xunit.InlineData("src//Feature.cs", "Enabled", "Disabled", FindingEvidenceRevertPathsRejection.MutationOutsideSrc)]
    [Xunit.InlineData("", "Enabled", "Disabled", FindingEvidenceRevertPathsRejection.MutationOutsideSrc)]
    [Xunit.InlineData(null, "Enabled", "Disabled", FindingEvidenceRevertPathsRejection.MutationOutsideSrc)]
    [Xunit.InlineData("src/Unchanged.cs", "", "", FindingEvidenceRevertPathsRejection.MutationNotChangedByGoal)]
    [Xunit.InlineData("src/Missing.cs", "Enabled", "Disabled", FindingEvidenceRevertPathsRejection.MutationNotChangedByGoal)]
    [Xunit.InlineData("src/Feature.cs", "", "", FindingEvidenceRevertPathsRejection.MutationEmptyOldText)]
    [Xunit.InlineData("src/Feature.cs", null, "Enabled", FindingEvidenceRevertPathsRejection.MutationEmptyOldText)]
    [Xunit.InlineData("src/Feature.cs", "missing", "missing", FindingEvidenceRevertPathsRejection.MutationUnchangedText)]
    [Xunit.InlineData("src/Feature.cs", "missing", "Enabled", FindingEvidenceRevertPathsRejection.MutationOldTextNotFound)]
    [Xunit.InlineData("src/Deleted.cs", "public static", "", FindingEvidenceRevertPathsRejection.MutationOldTextNotFound)]
    [Xunit.InlineData("src/Feature.cs", "public static", "", FindingEvidenceRevertPathsRejection.MutationOldTextAmbiguous)]
    public async Task InvalidMutation_TypedReason_SkipsRevertedExecution(string? path, string? oldText,
        string newText, FindingEvidenceRevertPathsRejection expected)
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
        var result = await fixture.RunMutationAsync(verifier, new(path!, oldText!, newText));
        Assert.Equal(1, candidateTestCalls);
        Assert.Equal(0, revertedCalls);
        Assert.Equal(expected, result.RevertPathsRejection);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Inconclusive, result.NegativeControlOutcome);
        Assert.Contains(FindingEvidenceRevertPathsRejectionJsonConverter.ToWireValue(expected), result.Summary, StringComparison.Ordinal);
        var arm = SourceReverted(result);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, arm.Disposition);
        Assert.Empty(arm.Checks);
        Assert.Equal(expected, arm.RevertPathsRejection);
    }

    [Xunit.Theory]
    [Xunit.InlineData("green", FindingEvidenceNegativeControlOutcome.NotDemonstrated)]
    [Xunit.InlineData("compile-red", FindingEvidenceNegativeControlOutcome.CompileRed)]
    [Xunit.InlineData("host-crash", FindingEvidenceNegativeControlOutcome.Inconclusive)]
    public async Task Mutation_ArmFailureModes_ReuseExistingOutcomes(string mode, FindingEvidenceNegativeControlOutcome expected)
    {
        using var fixture = new SourceRevertedProbeFixture(includeMutationProbe: true);
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        var revertedTestCalls = 0;
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                if (reverted) revertedTestCalls++;
                if (reverted && mode == "host-crash")
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "Test host crashed without results."));
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
            }
            else if (reverted && mode == "compile-red")
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, "error CS0103: Mutated symbol does not exist"));
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
        });
        var result = await fixture.RunMutationAsync(verifier, new(SourceRevertedProbeFixture.Behavior,
            SourceRevertedProbeFixture.BehaviorLine, SourceRevertedProbeFixture.RestoredBehaviorLine));
        Assert.True(result.Passed, result.Summary);
        Assert.Equal(expected, result.NegativeControlOutcome);
        Assert.Equal(mode == "compile-red" ? 0 : 1, revertedTestCalls);
        Assert.Equal(mode == "green" ? FindingEvidenceArmDisposition.Green : FindingEvidenceArmDisposition.Inconclusive,
            SourceReverted(result).Disposition);
        if (mode == "compile-red") Assert.Contains("error CS0103", result.Summary, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task Mutation_MultilineOrDeletion_PreservesUnmatchedBytes(bool deletion)
    {
        using var fixture = new SourceRevertedProbeFixture(includeMutationProbe: true);
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        var oldText = deletion ? "// café 🌱\r\n" : "    public static bool AddedMember => true;\r\n    public static bool Enabled => true;";
        var newText = deletion ? "" : oldText.Replace("Enabled => true", "Enabled => false", StringComparison.Ordinal);
        var expected = Encoding.UTF8.GetBytes(SourceRevertedProbeFixture.CandidateBehavior.Replace(oldText, newText, StringComparison.Ordinal));
        var revertedTestCalls = 0;
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (reverted) Assert.Equal(expected, File.ReadAllBytes(Path.Combine(worktree, SourceRevertedProbeFixture.Behavior)));
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                if (reverted) revertedTestCalls++;
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
            }
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
        });
        var result = await fixture.RunMutationAsync(verifier, new(SourceRevertedProbeFixture.Behavior, oldText, newText));
        Assert.Equal(FindingEvidenceNegativeControlOutcome.NotDemonstrated, result.NegativeControlOutcome);
        Assert.Equal(1, revertedTestCalls);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Mutation_InvalidDirectCombination_ThrowsBeforeAnyExecution(bool missingControl)
    {
        using var fixture = new SourceRevertedProbeFixture();
        var calls = 0;
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (_, _, _) =>
        {
            calls++;
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Unexpected execution."));
        });
        var error = await Assert.ThrowsAsync<ArgumentException>(() => verifier.RunFocusedEvidenceAsync(
            fixture.Root, null, "Core.Tests: AddedProbeTests",
            negativeControl: missingControl ? null : FindingEvidenceNegativeControl.RevertSrc,
            revertPaths: missingControl ? null : [SourceRevertedProbeFixture.Feature],
            mutation: new(SourceRevertedProbeFixture.Feature, "true", "false")));
        Assert.Equal(missingControl ? "mutation requires negative_control 'revert-src'" : "mutation and revert_paths are mutually exclusive", error.Message);
        Assert.Equal(0, calls);
    }

    private static Encoding EncodingFor(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false), "utf8-bom" => new UTF8Encoding(true),
        "utf16-le" => new UnicodeEncoding(false, true), "utf16-be" => new UnicodeEncoding(true, true),
        "utf32-le" => new UTF32Encoding(false, true), "utf32-be" => new UTF32Encoding(true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    private static FocusedEvidenceArmRunResult SourceReverted(FocusedEvidenceRunResult result) =>
        Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);
    private static string Read(string root, string path) => File.ReadAllText(Path.Combine(root, path));
    private sealed class SuccessfulLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, true, true);
        public bool SetIntegrity(string path, string level, bool recursive) => true;
    }
}
