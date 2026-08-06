using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorDriverGitRecheckTests
{
    [Xunit.Fact]
    public void LaunchFailureMapsToTerminalUnsatisfiable()
    {
        var result = ConductorDriver.ClassifyPreLandingRebaseConflict(
            () => throw new ReviewerMergeTreeStatusException(
                WorkerGitContext.ReviewerMergeTreeUnavailableErrorCode,
                "git could not start",
                gitProcessStarted: false),
            "evidence");

        Xunit.Assert.True(result.TerminalUnsatisfiable);
        Xunit.Assert.Equal("GitMergeTreeCouldNotStart", result.Status);
        Xunit.Assert.False(result.ConditionResolved);
    }

    [Xunit.Fact]
    public void StartedGitConflictRemainsRetryable()
    {
        var result = ConductorDriver.ClassifyPreLandingRebaseConflict(
            () => new ReviewerMergeTreeStatus(false, ["src/conflict.cs"], 1),
            "evidence");

        Xunit.Assert.False(result.TerminalUnsatisfiable);
        Xunit.Assert.Equal("MergeTreeConflict", result.Status);
        Xunit.Assert.False(result.ConditionResolved);
    }
}
