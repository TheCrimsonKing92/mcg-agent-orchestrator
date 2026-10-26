using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = ConductEventsLogTailTests.Fixture;

// Parallel-safe: every folder and heartbeat belongs to a GUID temporary root.
public sealed class ConductorAttemptDirectoryScopeTests
{
    [Fact]
    public void Directories_LiveRecentUnknownAndUnreadable_KeepOnlyEligibleFolders()
    {
        using var fixture = new Fixture();
        var now = fixture.Clock.Now;
        var goals = Goals();
        var live = Folder(goals[0].Id.Value, now.AddHours(-2));
        var recent = Folder(goals[1].Id.Value, now.AddMinutes(-1));
        var stale = Folder(goals[2].Id.Value, now.AddHours(-2));
        var unknown = Folder(new string('d', 32), now.AddHours(-2));
        var other = Folder("operator", now.AddHours(-2));
        // Use a distinct name even on case-insensitive filesystems (the live goal uses 'a').
        var uppercase = Folder(new string('F', 32), now.AddHours(-2));
        var future = Folder(new string('e', 32), now.AddHours(1));
        var window = TimeSpan.FromMinutes(15);
        Assert.Equal(new[] { live, recent, other, uppercase, future }.Order(),
            ConductorAttemptDirectoryScope.HeartbeatDirectories(fixture.Root, goals, now, window).Order());
        Assert.Equal(new[] { live, other, uppercase }.Order(),
            ConductorAttemptDirectoryScope.FinishedAttemptDirectories(fixture.Root, goals).Order());
        foreach (var unauthorized in new[] { false, true })
        {
            var included = ConductorAttemptDirectoryScope.HeartbeatDirectories(fixture.Root, goals, now, window,
                path => path == stale ? throw (unauthorized ? new UnauthorizedAccessException() : new IOException()) :
                    Directory.GetLastWriteTimeUtc(path));
            Assert.Contains(stale, included);
            Assert.DoesNotContain(unknown, included);
        }

        string Folder(string name, DateTimeOffset written)
        {
            var path = Path.Combine(fixture.Root, name);
            Directory.CreateDirectory(path);
            Directory.SetLastWriteTimeUtc(path, written.UtcDateTime);
            return path;
        }
    }

    [Fact]
    public void Read_StaleTerminalFolder_SkipsHeartbeatUntilFolderIsRecent()
    {
        using var fixture = new Fixture();
        var now = fixture.Clock.Now;
        var goals = Goals();
        var root = Path.Combine(fixture.Root, "acceptance-gate-attempts");
        var directory = Path.Combine(root, goals[1].Id.Value);
        var path = Path.Combine(directory, "lane.gate-heartbeat.json");
        var progress = now.AddMinutes(-1);
        GateHeartbeatArtifacts.Write(path,
            new(null, "test", "lane", null, null, null, "running", progress, now, progress, 0, 0, 0));
        File.SetLastWriteTimeUtc(path, progress.UtcDateTime);
        Directory.SetLastWriteTimeUtc(directory, now.AddHours(-2).UtcDateTime);
        var groupedRoot = Path.Combine(fixture.Root, "grouped-gate-attempts");
        var incremental = new ConductorInvariantObservationReader(fixture.Log, groupedRoot, _ => [], root);
        var full = new ConductorInvariantObservationReader(fixture.Log, groupedRoot, _ => [], root, incremental: false);
        Assert.Null(incremental.Read(goals, now).LastGateProgressAt);
        Assert.Equal(progress, full.Read(goals, now).LastGateProgressAt);
        Directory.SetLastWriteTimeUtc(directory, progress.UtcDateTime);
        Assert.Equal(progress, incremental.Read(goals, now).LastGateProgressAt);
        Assert.Equal(progress, full.Read(goals, now).LastGateProgressAt);

        // Root files are still included when no goal folder qualifies.
        File.Move(path, Path.Combine(root, "root.gate-heartbeat.json"));
        Directory.SetLastWriteTimeUtc(directory, now.AddHours(-2).UtcDateTime);
        Assert.Equal(progress, incremental.Read(goals, now).LastGateProgressAt);
    }

    private static (GoalId Id, GoalStatus Status)[] Goals() =>
    [
        (new(new string('a', 32)), GoalStatus.Verified),
        (new(new string('b', 32)), GoalStatus.Completed),
        (new(new string('c', 32)), GoalStatus.Completed)
    ];
}
