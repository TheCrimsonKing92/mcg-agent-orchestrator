using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsSourceRevertedCompileRed : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    private const string CompilerOutput = "error CS1061: Other has no AddedMember\nerror CS0246: AddedType could not be found";

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task BuildFailure_CompilerErrors_RecordsCompileRedAndKeepsInconclusive(bool stderrOnly)
    {
        var result = await RunProbeAsync(CompilerOutput, stderrOnly: stderrOnly);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.CompileRed, result.NegativeControlOutcome);
        Assert.True(result.Passed, result.Summary);
        Assert.True(result.IsValidEvidence);
        Assert.Null(result.OutcomeReason);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, SourceReverted(result).Disposition);
        Assert.All(SourceReverted(result).Checks, check => Assert.Empty(check.FailingTestIdentities ?? []));
        Assert.Contains("CS1061: Other has no AddedMember", result.Summary, StringComparison.Ordinal);
        Assert.Contains("CS0246: AddedType could not be found", result.Summary, StringComparison.Ordinal);
        Assert.Contains("negative-control-compile-red", result.Summary, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task TestFailure_CompilerTextWithFailingIdentity_DemonstratesBehavioralRed()
    {
        var result = await RunProbeAsync(CompilerOutput, failTest: true);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Demonstrated, result.NegativeControlOutcome);
        Assert.Equal(FindingEvidenceArmDisposition.Red, SourceReverted(result).Disposition);
        Assert.Contains(SourceReverted(result).Checks,
            check => check.FailingTestIdentities?.Contains(SourceRevertedProbeFixture.Identity) == true);
        Assert.DoesNotContain("negative-control-compile-red", result.Summary, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public async Task BuildFailure_RepeatedCodes_ListsFirstThreeDistinctCodesInOrder()
    {
        var result = await RunProbeAsync(CompilerOutput + "\nerror CS1061: duplicate\nerror CS0103: Name absent\nerror CS0117: Fourth code");
        Assert.Equal(FindingEvidenceNegativeControlOutcome.CompileRed, result.NegativeControlOutcome);
        Assert.Contains("compiler errors: error CS1061: Other has no AddedMember; error CS0246: AddedType could not be found; error CS0103: Name absent",
            result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("duplicate", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("CS0117", result.Summary, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Build failed without compiler diagnostics")]
    [Xunit.InlineData("error CS106: three digits\nerror CS10610: five digits")]
    public async Task BuildFailure_WithoutCompilerErrorCode_RemainsInconclusive(string output)
    {
        var result = await RunProbeAsync(output);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.Inconclusive, result.NegativeControlOutcome);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, SourceReverted(result).Disposition);
    }

    [Xunit.Fact]
    public async Task BuildFailure_WholeSourceRequest_AlsoRecordsCompileRed()
    {
        var result = await RunProbeAsync(CompilerOutput, wholeSource: true);
        Assert.Equal(FindingEvidenceNegativeControlOutcome.CompileRed, result.NegativeControlOutcome);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, SourceReverted(result).Disposition);
    }

    private async Task<FocusedEvidenceRunResult> RunProbeAsync(string output, bool stderrOnly = false,
        bool failTest = false, bool wholeSource = false)
    {
        using var fixture = new SourceRevertedProbeFixture();
        TestOverrides.BaselineIntegrityLabelerForTests = new SuccessfulLabeler();
        var revertedCalls = 0;
        var revertedTestCalls = 0;
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            var reverted = !string.Equals(worktree, fixture.Root, StringComparison.OrdinalIgnoreCase);
            if (reverted) revertedCalls++;
            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                if (reverted) revertedTestCalls++;
                WriteMtpTrx(args, 1, [SourceRevertedProbeFixture.Identity]);
                if (reverted && failTest) SourceRevertedProbeFixture.MakeTrxRed(args);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(reverted && failTest ? 1 : 0,
                    reverted && failTest ? output : "Passed: 1"));
            }
            if (reverted && !failTest)
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(1, stderrOnly ? "Build failed." : output,
                    Stderr: stderrOnly ? output : null));
            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });
        var result = await fixture.RunAsync(verifier, wholeSource ? null : [SourceRevertedProbeFixture.Feature]);
        Assert.True(revertedCalls > 0, result.Summary);
        Assert.Equal(failTest ? 1 : 0, revertedTestCalls);
        return result;
    }

    private static FocusedEvidenceArmRunResult SourceReverted(FocusedEvidenceRunResult result) =>
        Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.SourceReverted);

    private sealed class SuccessfulLabeler : IWorkerIntegrityLabeler
    {
        public IntegrityLabelState Query(string path) => new(true, true, true);
        public bool SetIntegrity(string path, string level, bool recursive) => true;
    }
}
