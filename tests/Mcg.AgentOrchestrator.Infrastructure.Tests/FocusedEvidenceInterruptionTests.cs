using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

[Collection("IsolatedProcessSpawning")]
public sealed class FocusedEvidenceInterruptionTests
{
    [Fact]
    public void FirstDeathWithoutTerminalArtifactsIsInterrupted() =>
        Assert.Equal(FocusedEvidenceInterruptionKind.Interrupted,
            FocusedEvidenceInterruption.Classify(Attempt(failures: 1), _ => false));

    [Fact]
    public void SecondDeathWithoutTerminalArtifactsExhaustsRerun() =>
        Assert.Equal(FocusedEvidenceInterruptionKind.RerunExhausted,
            FocusedEvidenceInterruption.Classify(Attempt(failures: 2), _ => false));

    [Theory]
    [InlineData("result.json")]
    [InlineData("exit.txt")]
    public void EitherTerminalArtifactExcludesInterruption(string existingPath) =>
        Assert.Equal(FocusedEvidenceInterruptionKind.NotInterrupted,
            FocusedEvidenceInterruption.Classify(Attempt(failures: 1), path => path == existingPath));

    [Theory]
    [InlineData("CorruptArtifacts")]
    [InlineData("Failed")]
    [InlineData("Passed")]
    public void OtherOutcomesAreNotInterrupted(string outcome) =>
        Assert.Equal(FocusedEvidenceInterruptionKind.NotInterrupted,
            FocusedEvidenceInterruption.Classify(Attempt(failures: 1) with
            {
                Outcome = Enum.Parse<ConductorParallelAcceptanceAttemptOutcome>(outcome)
            }, _ => false));

    [Fact]
    public void MissingAttemptIsNotInterrupted() =>
        Assert.Equal(FocusedEvidenceInterruptionKind.NotInterrupted,
            FocusedEvidenceInterruption.Classify(null, _ => throw new InvalidOperationException("No attempt has paths.")));

    [Fact]
    public void InterruptedMarkerRoundTripsWithoutResultOrFailures()
    {
        var entry = new DeferredNoChangeEvidenceEntry(
            "interrupted", new TaskId("developer"), new string('b', 40), "receipt-interrupted",
            ["Infrastructure.Tests:DeferredAlphaTests"], [], null, [], Declared: ["DeferredAlphaTests"]);

        Assert.True(DeferredNoChangeEvidenceIndexLines.TryParse(
            DeferredNoChangeEvidenceIndexLines.FormatMarker(entry), out var parsed));

        Assert.Equal("interrupted", parsed.Outcome);
        Assert.Equal(entry.DeveloperTaskId, parsed.DeveloperTaskId);
        Assert.Equal(entry.CandidateSha, parsed.CandidateSha);
        Assert.Equal(entry.ReceiptId, parsed.ReceiptId);
        Assert.Equal(entry.Selections, parsed.Selections);
        Assert.Equal(entry.Declared, parsed.Declared);
        Assert.Empty(parsed.NotRun);
        Assert.Null(parsed.ResultPath);
        Assert.Empty(parsed.FailingTests);
    }

    private static ConductorParallelAcceptanceAttempt Attempt(int failures) => new(
        AttemptId: "attempt-1", GoalId: "goal", GoalPrefix: "goal", SlotIndex: 0,
        BranchHeadSha: new string('b', 40), MainHeadSha: null,
        StartedAt: DateTimeOffset.UnixEpoch, LastHeartbeatAt: DateTimeOffset.UnixEpoch,
        OwnerProcessId: 7301, Outcome: ConductorParallelAcceptanceAttemptOutcome.ProcessDied,
        StdoutPath: "out.log", StderrPath: "err.log", ExitCodePath: "exit.txt",
        HeartbeatPath: "heartbeat.txt", ResultPath: "result.json", MetadataPath: "attempt.json",
        TransientFailureCount: failures);
}
