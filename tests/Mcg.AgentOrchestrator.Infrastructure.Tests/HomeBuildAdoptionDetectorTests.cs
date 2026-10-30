using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns a separate home record and staged-output fixture.
public sealed class HomeBuildAdoptionDetectorTests
{
    [Xunit.Fact]
    public void SuccessfulAdoptionIsEligibleAndEqualRunningBuildIsNot()
    {
        using var fixture = new HomeBuildAdoptionFixture();
        Assert.Equal(fixture.Candidate, fixture.Detector().Detect());
        var running = fixture.Running with { CommitSha = fixture.Candidate.AdoptedCommit };
        Assert.Null(new HomeBuildAdoptionDetector(fixture.Root, running).Detect());
        running = fixture.Running with { RunDirectory = fixture.Candidate.RunDirectory };
        Assert.Null(new HomeBuildAdoptionDetector(fixture.Root, running).Detect());
    }

    [Xunit.Theory]
    [Xunit.InlineData("reverted")]
    [Xunit.InlineData("wrong-home")]
    [Xunit.InlineData("wrong-version")]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("malformed")]
    [Xunit.InlineData("unavailable-build")]
    [Xunit.InlineData("wrong-build-identity")]
    public void IneligibleOrUnavailableRecordsNeverAuthorizeRelaunch(string kind)
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var record = fixture.Candidate;
        if (kind == "reverted") fixture.Publish(record with { Status = "reverted" });
        if (kind == "wrong-home") fixture.Publish(record with { HomeSourceRoot = Path.Combine(fixture.Root, "other") });
        if (kind == "wrong-version") fixture.Publish(record with { SchemaVersion = 2 });
        if (kind == "missing") File.Delete(fixture.RecordPath);
        if (kind == "malformed") File.WriteAllText(fixture.RecordPath, "{");
        if (kind == "unavailable-build") File.Delete(record.AppDllPath);
        if (kind == "wrong-build-identity") File.WriteAllText(record.AppDllPath + ".git-head", fixture.Running.CommitSha);
        Assert.Null(fixture.Detector().Detect());
    }

    [Xunit.Fact]
    public void SequenceOrdersBuildsAndBlockedCommitDoesNotRetry()
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var identity = fixture.Candidate with { AdoptedCommit = fixture.Running.CommitSha, Sequence = 3 };
        HomeBuildAdoptionRecord.Write(HomeBuildAdoptionRecord.BuildIdentityPath(fixture.RecordPath, identity.AdoptedCommit), identity);
        Assert.Null(fixture.Detector().Detect());
        fixture.Publish(fixture.Candidate with { Sequence = 4 });
        Assert.NotNull(fixture.Detector().Detect());
        Assert.Null(new HomeBuildAdoptionDetector(fixture.Root, fixture.Running, fixture.Candidate.AdoptedCommit).Detect());
    }

    [Xunit.Fact]
    public void HomePublisherWritesMonotonicAdoptionAndInvalidatesOnlyThePublishedCommit()
    {
        using var fixture = new HomeBuildAdoptionFixture();
        var publisher = new HomeBuildAdoptionPublisher(fixture.Root, fixture.RecordPath);
        var build = new ConductorActivationBuild(fixture.Candidate.AdoptedCommit, fixture.Candidate.RunDirectory,
            ["dotnet", fixture.Candidate.AppDllPath], null);
        publisher.Record("adopted", build);
        Assert.Equal(3, HomeBuildAdoptionRecord.Read(fixture.RecordPath)!.Sequence);
        publisher.Record("reverted", build with { CommitSha = fixture.Running.CommitSha });
        Assert.Equal("adopted", HomeBuildAdoptionRecord.Read(fixture.RecordPath)!.Status);
        publisher.Record("reverted", build);
        Assert.Null(fixture.Detector().Detect());
        publisher.Record("adopted", build);
        Assert.Equal(4, HomeBuildAdoptionRecord.Read(fixture.RecordPath)!.Sequence);
    }
}
