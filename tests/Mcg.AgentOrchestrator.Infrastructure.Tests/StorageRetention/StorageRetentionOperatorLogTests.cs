using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

public sealed class StorageRetentionOperatorLogTests
{
    [Xunit.Fact]
    public void OnlyOldUnprotectedOperatorLogIsDeleted()
    {
        using var fixture = new RetentionReclaimFixture();
        var old = fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, "operator-loop-old.out.log"), 30);
        var young = fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, "operator-loop-young.out.log"), 2);
        var protectedLog = fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, "operator-live.err.log"), 30);
        var events = fixture.WriteAgedFile(Path.Combine(fixture.LogDirectory, "conduct-events-older.log"), 30);
        var options = StorageRetentionReclaimOptions.Default with
        {
            ProtectedOperatorLogPaths = () => [protectedLog]
        };

        var result = fixture.Run(options, fixture.Goal(RetentionReclaimFixture.ActiveId, GoalStatus.Active));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(young));
        Assert.True(File.Exists(protectedLog));
        Assert.True(File.Exists(events));
        Assert.Contains(result.Decisions, decision => decision.Path == old &&
            decision.Action == EvidenceRetentionAction.Deleted && decision.Reason == "operator-log-past-age");
        Assert.Contains(result.Decisions, decision => decision.Path == protectedLog &&
            decision.Reason == "running-conductor-log");
    }
}
