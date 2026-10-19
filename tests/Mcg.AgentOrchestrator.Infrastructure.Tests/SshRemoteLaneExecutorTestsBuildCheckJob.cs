using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fake SSH, injected time and fixture-owned staging files; no executor is contacted.
public sealed class SshRemoteLaneExecutorTestsBuildCheckJob
{
    [Theory]
    [InlineData(true, 11)]
    [InlineData(false, 9)]
    public async Task Submit_JobKind_PreservesLaneContract(bool build, int fields)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var request = FakeSshRemoteLaneTransport.Request with
        {
            Kind = build ? "build-check" : "lane", BuildProjects = ["src/One.csproj", "src/Two.csproj"]
        };
        var submission = await Executor(fake).SubmitAsync(request, default);
        fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        var copy = fake.Calls.ToArray()[1];
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(copy.Directory, "job.json")));
        Assert.Equal(fields, job.RootElement.EnumerateObject().Count());
        Assert.Equal(request.VerifyingCommitSha, job.RootElement.GetProperty("sha").GetString());
        if (build)
        {
            Assert.Equal("build-check", job.RootElement.GetProperty("kind").GetString());
            Assert.Equal(request.BuildProjects, job.RootElement.GetProperty("projects").EnumerateArray().Select(p => p.GetString()!));
        }
        else Assert.False(job.RootElement.TryGetProperty("projects", out _));
    }

    [Fact]
    public async Task Completion_BuildFiles_FetchesOutputAndReceipt()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        fake.FetchContents["build-output.txt"] = RemoteWorkerBuildCheckScenarioTests.Fixture.Pass;
        fake.FetchContents["worker-build-receipt.json"] = "{\"schemaVersion\":1}";
        var submission = await Executor(fake).SubmitAsync(FakeSshRemoteLaneTransport.Request with { Kind = "build-check" }, default);
        fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        var status = FakeSshRemoteLaneTransport.Status("completed");
        status["exitCode"] = 0;
        status["files"] = new[] { "build-output.txt", "worker-build-receipt.json" };
        await fake.Polls.Writer.WriteAsync(new(status));
        await Complete(fake.Handle);
        var result = Assert.IsType<RemoteLaneResult>(fake.Handle.TryGetResult());
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.TestResultPaths);
        Assert.Equal(fake.FetchContents["build-output.txt"], File.ReadAllText(result.BuildOutputPath!));
        Assert.Equal(fake.FetchContents["worker-build-receipt.json"], File.ReadAllText(result.BuildReceiptPath!));
    }

    [Theory]
    [InlineData("build-check", "missing-or-invalid-files")]
    [InlineData("lane", "no-trx-produced exitCode=0")]
    public async Task Completion_MissingEvidence_FaultsByJobKind(string kind, string error)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var submission = await Executor(fake).SubmitAsync(FakeSshRemoteLaneTransport.Request with { Kind = kind }, default);
        fake.Handle = Assert.IsType<SshRemoteLaneHandle>(submission.Handle);
        var status = FakeSshRemoteLaneTransport.Status("completed");
        status["exitCode"] = 0;
        status["trx"] = Array.Empty<string>();
        await fake.Polls.Writer.WriteAsync(new(status));
        await Complete(fake.Handle);
        Assert.Equal(error, Assert.Throws<RemoteLaneTransportException>(() => fake.Handle.TryGetResult()).Message);
    }

    [Theory]
    [InlineData("{\"exitCode\":0,\"files\":[\"../build-output.txt\"]}")]
    [InlineData("{\"exitCode\":0,\"files\":[\"build-output.txt\"]}")]
    [InlineData("{\"exitCode\":0,\"files\":[1]}")]
    [InlineData("{\"exitCode\":\"0\",\"files\":[]}")]
    public void BuildCompletion_UnsafeOrIncompleteFiles_RejectsStatus(string json)
    {
        using var document = JsonDocument.Parse(json);
        var status = new SshRemoteLaneStatus(document.RootElement);
        Assert.Throws<RemoteLaneTransportException>(() => status.BuildCheckCompletion());
    }

    private static async Task Complete(SshRemoteLaneHandle handle)
    {
        try { await handle.PollLoop.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: completed SSH build poll"); }
    }

    private static SshRemoteLaneExecutor Executor(FakeSshRemoteLaneTransport fake) => new(
        new([new("one", 60, "ssh", "runner", "admin", "C:/repo/bare.git", "C:/mcg-executor")], [], null),
        fake.Root, Path.Combine(fake.Root, "attempt", "result"), fake.Clock,
        (args, directory, bound, token) =>
        {
            // The existing fake treats every .json source as a job upload. Route remote receipt downloads here.
            if (args.Length == 5 && args[3].Contains(":", StringComparison.Ordinal) && args[3].EndsWith("/worker-build-receipt.json", StringComparison.Ordinal))
            {
                File.WriteAllText(Path.Combine(directory, args[^1]), fake.FetchContents["worker-build-receipt.json"]);
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            }
            return fake.Transport(args, directory, bound, token);
        }, fake.Git, TimeSpan.Zero);
}
