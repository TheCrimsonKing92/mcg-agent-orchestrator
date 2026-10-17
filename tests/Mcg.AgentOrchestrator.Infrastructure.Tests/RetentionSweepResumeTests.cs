using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fixtures own all filesystem state, sweep leases and injected clocks.
public sealed class RetentionSweepResumeTests
{
    internal const string Acceptance = "acceptance-gate-attempts";
    internal const string PreReview = "pre-review-evidence-attempts";
    internal static string[] Ids => Enumerable.Range(1, 5).Select(index => index.ToString("x32")).ToArray();

    [Xunit.Fact]
    public void Run_InterruptedOnThirdDirectory_ResumesRemainingThree()
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = Seed(fixture);
        Interrupt(fixture);
        Assert.All(paths.Take(2).SelectMany(group => group), path => Assert.False(File.Exists(path)));
        Assert.All(paths.Skip(2).SelectMany(group => group), path => Assert.True(File.Exists(path)));

        var visited = new List<string>();
        var result = Run(fixture, visited: visited);
        Assert.False(result.Failed);
        Assert.Equal(Ids.Skip(2), visited);
        Assert.All(paths.SelectMany(group => group), path => Assert.False(File.Exists(path)));
        Assert.False(File.Exists(CursorPath(fixture)));
    }

    [Xunit.Fact]
    public void Run_InterruptedInSecondFamily_SkipsCompletedFirstFamily()
    {
        using var fixture = new RetentionReclaimFixture();
        Seed(fixture);
        // Copy valid flat identities into the second family, preserving their ages.
        foreach (var id in Ids)
        {
            var target = Path.Combine(fixture.AttemptRoot(PreReview), id);
            Directory.CreateDirectory(target);
            foreach (var source in Directory.GetFiles(Path.Combine(fixture.AttemptRoot(), id)))
            {
                var destination = Path.Combine(target, Path.GetFileName(source));
                File.Copy(source, destination);
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            }
        }
        var first = Run(fixture, hook: directory =>
        {
            if (Path.GetDirectoryName(directory) == fixture.AttemptRoot(PreReview) &&
                Path.GetFileName(directory) == Ids[2]) throw new IOException("interrupted second family");
        });
        Assert.True(first.Failed);
        var cursor = StorageRetentionResumeCursor.Open(fixture.OrchestratorDirectory,
            RetentionReclaimFixture.Now, RunEventMaintenanceCadence.Interval);
        Assert.True(cursor.ShouldSkip(Acceptance, "zzzz"));
        Assert.Equal(Ids[1], cursor.LastCompleted(PreReview));
        var passes = new List<AttemptDirectoryPassStats>();
        var second = Run(fixture, observer: passes.Add);
        Assert.False(second.Failed);
        Assert.Equal(Ids.Skip(2), passes.Select(pass => Path.GetFileName(pass.Directory)));
        Assert.All(passes, pass => Assert.Equal(fixture.AttemptRoot(PreReview), Path.GetDirectoryName(pass.Directory)));
        Assert.False(File.Exists(CursorPath(fixture)));
    }

    [Xunit.Theory]
    [Xunit.InlineData("stale")]
    [Xunit.InlineData("version")]
    [Xunit.InlineData("corrupt")]
    public void Run_InvalidCursor_VisitsEveryDirectory(string invalidity)
    {
        using var fixture = new RetentionReclaimFixture();
        var paths = Seed(fixture);
        Interrupt(fixture);
        var now = RetentionReclaimFixture.Now;
        if (invalidity == "stale") now = now.AddHours(24).AddSeconds(1);
        else if (invalidity == "version")
        {
            var json = JsonNode.Parse(File.ReadAllText(CursorPath(fixture)))!;
            json["policyVersion"] = EvidenceRetentionPolicy.Version + 1;
            File.WriteAllText(CursorPath(fixture), json.ToJsonString());
        }
        else File.WriteAllText(CursorPath(fixture), "{corrupt");

        var visited = new List<string>();
        var result = Run(fixture, now: now, visited: visited);
        Assert.False(result.Failed);
        Assert.Equal(Ids, visited);
        Assert.All(paths.SelectMany(group => group), path => Assert.False(File.Exists(path)));
        Assert.False(File.Exists(CursorPath(fixture)));
    }

    [Xunit.Fact]
    public void Run_AfterCompletedPass_StartsFullPass()
    {
        using var fixture = new RetentionReclaimFixture();
        Seed(fixture);
        Assert.False(Run(fixture).Failed);
        Assert.False(File.Exists(CursorPath(fixture)));
        var visited = new List<string>();
        Assert.False(Run(fixture, visited: visited).Failed);
        Assert.Equal(Ids, visited);
        Assert.False(File.Exists(CursorPath(fixture)));
    }

    [Xunit.Fact]
    public void Run_CompletedDirectoryGainsCandidate_WaitsForNextFullPass()
    {
        using var fixture = new RetentionReclaimFixture();
        Seed(fixture);
        Interrupt(fixture);
        var added = fixture.WriteAgedFile(Path.Combine(fixture.AttemptRoot(), Ids[0], "old.999.out.log"), 30);
        var resumed = new List<string>();
        Assert.False(Run(fixture, visited: resumed).Failed);
        Assert.Equal(Ids.Skip(2), resumed);
        Assert.True(File.Exists(added));
        var full = new List<string>();
        Assert.False(Run(fixture, visited: full).Failed);
        Assert.Equal(Ids, full);
        Assert.False(File.Exists(added));
    }

    internal static string[][] Seed(RetentionReclaimFixture fixture)
    {
        // Deliberately create in reverse order; the walk must sort rather than inherit creation order.
        var paths = new Dictionary<string, string[]>();
        foreach (var id in Ids.Reverse())
            paths[id] = RetentionSweepPassCostTests.CreateCandidates(fixture, id, 2);
        return Ids.Select(id => paths[id]).ToArray();
    }

    private static void Interrupt(RetentionReclaimFixture fixture)
    {
        var reached = new List<string>();
        var first = Run(fixture, hook: directory =>
        {
            reached.Add(Path.GetFileName(directory));
            if (reached.Count == 3) throw new IOException("interrupted sweep");
        });
        Assert.True(first.Failed);
        Assert.Equal(Ids.Take(3), reached);
        Assert.True(File.Exists(CursorPath(fixture)));
        Assert.Equal(Ids[1], StorageRetentionResumeCursor.Open(fixture.OrchestratorDirectory,
            RetentionReclaimFixture.Now, RunEventMaintenanceCadence.Interval).LastCompleted(Acceptance));
    }

    private static string CursorPath(RetentionReclaimFixture fixture) =>
        StorageRetentionResumeCursor.PathFor(fixture.OrchestratorDirectory);

    private static StorageRetentionResult Run(RetentionReclaimFixture fixture,
        Action<string>? hook = null, List<string>? visited = null, DateTimeOffset? now = null,
        Action<AttemptDirectoryPassStats>? observer = null) =>
        StorageRetentionMaintenance.Run(fixture.LogDirectory, fixture.OrchestratorDirectory,
            fixture.ExecutionDirectory, Ids.Select(id => fixture.Goal(id, GoalStatus.Completed)).ToArray(),
            now ?? RetentionReclaimFixture.Now, mtpResultsRoot: Path.Combine(fixture.ExecutionDirectory, "empty-mtp"),
            beforeAttemptCandidateDeletionForTests: hook, attemptDirectoryPassObserverForTests: pass =>
            {
                visited?.Add(Path.GetFileName(pass.Directory));
                observer?.Invoke(pass);
            });
}
