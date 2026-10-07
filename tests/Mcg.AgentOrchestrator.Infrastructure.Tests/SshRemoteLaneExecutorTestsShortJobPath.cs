using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SshRemoteLaneExecutorTestsShortJobPath
{
    [Xunit.Fact]
    public async Task LongLaneUsesShortLocalJobNameAndFullRemoteNameWithExactJob()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var request = FakeSshRemoteLaneTransport.Request with { Lane = "infrastructure tests: Goal worktree cleanup" };
        var laneKey = SshRemoteLaneExecutor.LaneKey(request.Lane);
        Assert.True(laneKey.Length >= 40);

        var submission = await fake.Executor().SubmitAsync(request, default);
        fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        var calls = fake.Calls.ToArray();
        Assert.Equal(3, calls.Length);
        var copy = calls[1];
        Assert.Equal("job.json", copy.Arguments[3]);
        Assert.Equal($"runner:C:/mcg-executor/queue/{request.AttemptId}-{laneKey}.json", copy.Arguments[4]);
        Assert.Equal(Path.Combine(fake.Root, "attempt", SshRemoteLaneExecutor.StagingFolderName("one", laneKey, 0)), copy.Directory);

        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(copy.Directory, "job.json")));
        var expected = new Dictionary<string, string>
        {
            ["sha"] = request.VerifyingCommitSha, ["project"] = request.Project, ["filter"] = request.Filter,
            ["lane"] = request.Lane, ["attemptId"] = request.AttemptId, ["executorId"] = request.ExecutorId,
            ["filterHash"] = request.FilterHash, ["mainSha"] = request.MainSha, ["manifestIdentity"] = request.ManifestIdentity
        };
        Assert.Equal(9, job.RootElement.EnumerateObject().Count());
        foreach (var field in expected) Assert.Equal(field.Value, job.RootElement.GetProperty(field.Key).GetString());
    }
}
