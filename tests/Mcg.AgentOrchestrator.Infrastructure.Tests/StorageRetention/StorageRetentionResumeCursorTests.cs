using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns its cursor directory and uses an injected clock.
public sealed class StorageRetentionResumeCursorTests
{
    private const string Acceptance = "acceptance-gate-attempts";
    private const string PreReview = "pre-review-evidence-attempts";
    private static readonly DateTimeOffset Now = RetentionReclaimFixture.Now;
    private static readonly TimeSpan Window = RunEventMaintenanceCadence.Interval;

    [Xunit.Fact]
    public void Advance_ReopenedCursor_PreservesNameAndOrdinalBoundary()
    {
        using var fixture = new RetentionReclaimFixture();
        var cursor = Open(fixture);
        Assert.False(File.Exists(StorageRetentionResumeCursor.PathFor(fixture.OrchestratorDirectory)));
        Assert.False(cursor.ShouldSkip(Acceptance, "a"));
        cursor.Advance(Acceptance, "a");

        var reopened = Open(fixture, Now.AddHours(1));
        Assert.Equal("a", reopened.LastCompleted(Acceptance));
        foreach (var name in new[] { "", "0", "B", "Z", "a" })
            Assert.True(reopened.ShouldSkip(Acceptance, name));
        foreach (var name in new[] { "aa", "b", "z" })
            Assert.False(reopened.ShouldSkip(Acceptance, name));
        Assert.False(reopened.ShouldSkip(PreReview, "a"));
    }

    [Xunit.Fact]
    public void MarkFamilyComplete_ReopenedCursor_SkipsOnlyThatFamily()
    {
        using var fixture = new RetentionReclaimFixture();
        var cursor = Open(fixture);
        cursor.Advance(Acceptance, "a");
        cursor.MarkFamilyComplete(Acceptance);
        var reopened = Open(fixture);
        foreach (var name in new[] { "", "a", "zzzz" })
        {
            Assert.True(reopened.ShouldSkip(Acceptance, name));
            Assert.False(reopened.ShouldSkip(PreReview, name));
        }
    }

    [Xunit.Fact]
    public void Complete_RemovesPersistedProgress()
    {
        using var fixture = new RetentionReclaimFixture();
        var cursor = Open(fixture);
        cursor.Advance(Acceptance, "a");
        var path = StorageRetentionResumeCursor.PathFor(fixture.OrchestratorDirectory);
        Assert.True(File.Exists(path));
        cursor.Complete();
        Assert.False(File.Exists(path));
        Assert.False(Open(fixture).ShouldSkip(Acceptance, "a"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("stale")]
    [Xunit.InlineData("future")]
    [Xunit.InlineData("version")]
    [Xunit.InlineData("corrupt")]
    [Xunit.InlineData("empty")]
    [Xunit.InlineData("missing")]
    public void Open_InvalidProgress_StartsFreshWithoutRewriting(string invalidity)
    {
        using var fixture = new RetentionReclaimFixture();
        var cursor = Open(fixture);
        cursor.Advance(Acceptance, "a");
        cursor.MarkFamilyComplete(PreReview);
        var path = StorageRetentionResumeCursor.PathFor(fixture.OrchestratorDirectory);
        var now = Now;
        switch (invalidity)
        {
            case "stale": now = Now.Add(Window).AddSeconds(1); break;
            case "future": now = Now.AddSeconds(-1); break;
            case "version":
                var json = JsonNode.Parse(File.ReadAllText(path))!;
                json["policyVersion"] = EvidenceRetentionPolicy.Version + 1;
                File.WriteAllText(path, json.ToJsonString());
                break;
            case "corrupt": File.WriteAllText(path, "{invalid"); break;
            case "empty": File.WriteAllText(path, ""); break;
            case "missing": File.Delete(path); break;
        }
        var before = File.Exists(path) ? File.ReadAllText(path) : null;
        var reopened = Open(fixture, now);
        Assert.Null(reopened.LastCompleted(Acceptance));
        foreach (var family in new[] { Acceptance, PreReview })
            foreach (var name in new[] { "0", "a", "z" })
                Assert.False(reopened.ShouldSkip(family, name));
        Assert.Equal(before, File.Exists(path) ? File.ReadAllText(path) : null);
    }

    [Xunit.Fact]
    public void Open_WindowBoundaryIsInclusive_AndResumeKeepsOriginalStart()
    {
        using var fixture = new RetentionReclaimFixture();
        Open(fixture).Advance(Acceptance, "a");
        Open(fixture, Now.AddHours(12)).Advance(Acceptance, "b");
        Assert.Equal("b", Open(fixture, Now.Add(Window)).LastCompleted(Acceptance));
        Assert.False(Open(fixture, Now.Add(Window).AddSeconds(1)).ShouldSkip(Acceptance, "b"));
    }

    [Xunit.Fact]
    public void Advance_UnwritableTarget_DoesNotThrowAndDisablesFurtherWrites()
    {
        using var fixture = new RetentionReclaimFixture();
        var path = StorageRetentionResumeCursor.PathFor(fixture.OrchestratorDirectory);
        Directory.CreateDirectory(path); // Portable write denial, including Windows.
        var cursor = Open(fixture);
        cursor.Advance(Acceptance, "a");
        Assert.True(cursor.ShouldSkip(Acceptance, "a"));
        cursor.Complete(); // Delete failure is also best-effort.
        Assert.True(Directory.Exists(path));
        Directory.Delete(path);
        cursor.Advance(Acceptance, "b");
        cursor.MarkFamilyComplete(PreReview);
        Assert.True(cursor.ShouldSkip(Acceptance, "b"));
        Assert.True(cursor.ShouldSkip(PreReview, "z"));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(fixture.OrchestratorDirectory, StorageRetentionResumeCursor.FileName + ".*.tmp"));
    }

    private static StorageRetentionResumeCursor Open(RetentionReclaimFixture fixture, DateTimeOffset? now = null) =>
        StorageRetentionResumeCursor.Open(fixture.OrchestratorDirectory, now ?? Now, Window);
}
