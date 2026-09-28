using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsBaselineIntegrity : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Fact]
    public async Task BaselineWorktreeIsLabeledLowInheritableBeforeBaselineChecksRun()
    {
        var labeler = new RecordingLabeler(setResult: true);
        var (result, baselineRunnerCalls, baselineTestsBeforeLabel) = await RunProbeAsync(labeler);

        Assert.Single(labeler.Calls);
        var call = labeler.Calls[0];
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "mcg-focused-evidence-baselines"),
            call.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkerSandboxPreparer.LowInheritableLevel, call.Level);
        Assert.True(call.Recursive);
        Assert.True(call.GitMetadataExisted);
        Assert.True(baselineRunnerCalls > 0);
        Assert.False(baselineTestsBeforeLabel);
        Assert.Equal(FindingEvidenceArmDisposition.Green,
            Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.Baseline).Disposition);
    }

    [Xunit.Fact]
    public async Task BaselineLabelingFailureReturnsInconclusiveBaselineAndRemovesWorktree()
    {
        var labeler = new RecordingLabeler(setResult: false);
        var (result, baselineRunnerCalls, baselineTestsBeforeLabel) = await RunProbeAsync(labeler);

        var baseline = Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.Baseline);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, baseline.Disposition);
        Assert.False(baseline.Accepted);
        Assert.Empty(baseline.Checks);
        Assert.Contains("integrity labeling failed", baseline.Summary, StringComparison.Ordinal);
        Assert.Equal(FindingEvidenceOutcomeReason.BaselineInconclusive, result.OutcomeReason);
        Assert.Equal(0, baselineRunnerCalls);
        Assert.False(baselineTestsBeforeLabel);
        Assert.False(Directory.Exists(Assert.Single(labeler.Calls).Path));
        Assert.DoesNotContain(result.Arms!, arm => arm.Arm == FindingEvidenceArm.Baseline &&
            arm.Disposition == FindingEvidenceArmDisposition.Red);
    }

    [Xunit.Fact]
    public async Task BaselineAlreadyLowInheritableCountsAsLabeled()
    {
        var labeler = new RecordingLabeler(setResult: false, alreadyLow: true);
        var (result, baselineRunnerCalls, baselineTestsBeforeLabel) = await RunProbeAsync(labeler);

        Assert.Single(labeler.Calls);
        Assert.True(baselineRunnerCalls > 0);
        Assert.False(baselineTestsBeforeLabel);
        Assert.Equal(FindingEvidenceArmDisposition.Green,
            Assert.Single(result.Arms!, arm => arm.Arm == FindingEvidenceArm.Baseline).Disposition);
    }

    private async Task<(FocusedEvidenceRunResult Result, int BaselineRunnerCalls,
        bool BaselineTestsBeforeLabel)> RunProbeAsync(
        RecordingLabeler labeler)
    {
        using var fixture = FindingBaselineProbeFixture.Create();
        TestOverrides.BaselineIntegrityLabelerForTests = labeler;
        var baselineRunnerCalls = 0;
        var baselineTestsBeforeLabel = false;
        var verifier = new GoalAcceptanceVerifier(TestOverrides, (args, worktree, _) =>
        {
            if (worktree.StartsWith(Path.Combine(Path.GetTempPath(), "mcg-focused-evidence-baselines"),
                    StringComparison.OrdinalIgnoreCase) &&
                Array.IndexOf(args, "--results-directory") >= 0 && labeler.Calls.Count == 0)
            {
                baselineTestsBeforeLabel = true;
            }

            if (labeler.Calls.Count > 0 &&
                worktree.StartsWith(labeler.Calls[0].Path, StringComparison.OrdinalIgnoreCase))
            {
                baselineRunnerCalls++;
            }

            if (Array.IndexOf(args, "--results-directory") >= 0)
            {
                WriteMtpTrx(args, executedTestCount: 1,
                    ["PreExistingProbeTests.ExistingBehavior"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1"));
            }

            return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded."));
        });

        var goalId = GoalId.New();
        try
        {
            var result = await verifier.RunFocusedEvidenceAsync(
                fixture.Root, goalId, "Core.Tests: PreExistingProbeTests", runBaselineArm: true);
            return (result, baselineRunnerCalls, baselineTestsBeforeLabel);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goalId);
        }
    }

    private sealed class RecordingLabeler(bool setResult, bool alreadyLow = false) : IWorkerIntegrityLabeler
    {
        internal List<(string Path, string Level, bool Recursive, bool GitMetadataExisted)> Calls { get; } = [];

        public IntegrityLabelState Query(string path) =>
            new(Exists: true, Low: alreadyLow, Inheritable: alreadyLow);

        public bool SetIntegrity(string path, string level, bool recursive)
        {
            Calls.Add((path, level, recursive,
                File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git"))));
            return setResult;
        }
    }
}
