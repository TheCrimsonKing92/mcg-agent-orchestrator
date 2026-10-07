using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SshRemoteLaneExecutorTestsAttemptTimeline
{
    [Fact]
    public async Task FailedJobCopyRetainsBoundedOutputAndSubmitTimeline()
    {
        await using var fake = new FakeSshRemoteLaneTransport { FailStep = 1, Noise = new string('x', 97_952) + new string('é', 2048) };
        var submission = await fake.Executor().SubmitAsync(FakeSshRemoteLaneTransport.Request, default);
        Assert.Equal("job-copy-failed", submission.FailureReason);
        Assert.Null(submission.Handle);
        Assert.Equal(new[] { "push", "job-copy" }, submission.Steps!.Select(step => step.Name));
        Assert.Null(submission.Steps[0].TimedOut);
        var step = submission.Steps[1];
        Assert.Equal(1, step.ExitCode);
        Assert.False(step.TimedOut);
        Assert.Equal(fake.Noise[^2048..], step.StderrTail);
        var staging = Path.Combine(fake.Root, "attempt", SshRemoteLaneExecutor.StagingFolderName("one", "infrastructure-tests-cli-lane", 0));
        Assert.Equal(Path.Combine(staging, "job-copy.out.txt"), step.StdoutPath);
        Assert.Equal(Path.Combine(staging, "job-copy.err.txt"), step.StderrPath);
        foreach (var path in new[] { step.StdoutPath!, step.StderrPath! })
        {
            Assert.True(File.Exists(path));
            Assert.InRange(new FileInfo(path).Length, 1L, 32L * 1024);
            Assert.EndsWith(new string('é', 2048), File.ReadAllText(path));
        }
    }

    [Fact]
    public async Task TimedOutTriggerRetainsTimeoutWithoutChangingFailureReason()
    {
        await using var fake = new FakeSshRemoteLaneTransport { FailStep = 2, TimeoutStep = true };
        var submission = await fake.Executor().SubmitAsync(FakeSshRemoteLaneTransport.Request, default);
        Assert.Equal("trigger-failed", submission.FailureReason);
        Assert.Equal(new[] { "push", "job-copy", "trigger" }, submission.Steps!.Select(step => step.Name));
        Assert.True(submission.Steps[2].TimedOut);
        Assert.Equal(0, submission.Steps[2].ExitCode);
        Assert.True(File.Exists(submission.Steps[2].StdoutPath));
        Assert.True(File.Exists(submission.Steps[2].StderrPath));
    }

    [Fact]
    public async Task FailedPollAndExecutorFailureRemainInSnapshot()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        const string stderr = "ssh: connect to host x port 22: Connection timed out";
        await fake.PollOnce(new(null, Exit: 255, Stderr: stderr));
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status()));
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status("failed")));
        var source = Assert.IsAssignableFrom<IRemoteLaneAttemptDiagnosticsSource>(handle);
        var snapshot = source.Snapshot();
        Assert.Equal(3, snapshot.Poll.PollCount);
        Assert.Equal(1, snapshot.Poll.FailedPollCount);
        Assert.Equal("failed", snapshot.Poll.LastState);
        Assert.Equal(fake.Clock.GetUtcNow(), snapshot.Poll.LastHeartbeatChangeAt);
        var step = Assert.IsType<RemoteLaneStep>(snapshot.Poll.LastFailedPoll);
        Assert.Equal(255, step.ExitCode);
        Assert.Equal(stderr, step.StderrTail);
        var staging = Path.Combine(fake.Root, "attempt", SshRemoteLaneExecutor.StagingFolderName("one", "infrastructure-tests-cli-lane", 0));
        Assert.Equal(Path.Combine(staging, "poll.out.txt"), step.StdoutPath);
        Assert.Equal(Path.Combine(staging, "poll.err.txt"), step.StderrPath);
        Assert.True(File.Exists(step.StdoutPath));
        Assert.Equal(stderr, File.ReadAllText(step.StderrPath!));
        Assert.Equal("executor-checkout-failed", snapshot.LastStatus!.Value.GetProperty("error").GetString());
        Assert.Contains("executor-checkout-failed", Assert.Throws<RemoteLaneTransportException>(() => handle.TryGetResult()).Message);
    }

    [Theory]
    [InlineData('x')]
    [InlineData('é')]
    public async Task RunnerCaptureRetainsExactUtf8ByteTailAndCleansTemporaryFile(char character)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        fake.FetchContents["runner.log"] = new string(character, 40_000) + Environment.NewLine;
        var handle = await fake.Submit();
        var capture = await ((IRemoteLaneAttemptDiagnosticsSource)handle).CaptureRunnerLogAsync(default);
        Assert.NotNull(capture.Path);
        Assert.Null(capture.FailedStep);
        var expectedCharacters = (32 * 1024 - System.Text.Encoding.UTF8.GetByteCount(Environment.NewLine)) /
            System.Text.Encoding.UTF8.GetByteCount(character.ToString());
        Assert.Equal(new string(character, expectedCharacters) + Environment.NewLine, File.ReadAllText(capture.Path));
        Assert.InRange(new FileInfo(capture.Path).Length, 1L, 32L * 1024);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(capture.Path)!, "*.part"));
    }

    [Fact]
    public async Task CanceledRunnerCaptureReturnsDiagnosticsAndCleansTemporaryFile()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var capture = await ((IRemoteLaneAttemptDiagnosticsSource)handle).CaptureRunnerLogAsync(canceled.Token);
        Assert.Null(capture.Path);
        Assert.True(capture.FailedStep!.TimedOut);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(capture.FailedStep.StdoutPath!)!, "*.part"));
    }

    [Fact]
    public void DiagnosticWriteFailureDoesNotEscape()
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var blocked = Path.Combine(root, "file");
            File.WriteAllText(blocked, "parent is a file");
            var step = RemoteLaneDiagnosticFiles.Step(blocked, "poll", 255, false,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "out", "err");
            Assert.Equal(255, step.ExitCode);
            Assert.Equal("err", step.StderrTail);
            Assert.Null(step.StdoutPath);
            Assert.Null(step.StderrPath);
        }
        finally { Directory.Delete(root, true); }
    }
}
