using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class SshRemoteLaneExecutorTests
{
    [Xunit.Fact]
    public async Task SubmissionUsesOrderedBoundedBatchCommandsAndExactJob()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        await fake.Submit();
        var calls = fake.Calls.ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Equal(new[] { "-c", $"core.sshCommand={SshRemoteLaneExecutor.SshPath.Replace('\\', '/')}", "push",
            "runner:C:/repo/bare.git", "123456789abcdef:refs/heads/c-123456789" }, calls[0].Arguments);
        Assert.Equal(fake.Root, calls[0].Directory);
        Assert.Equal(TimeSpan.FromMinutes(10), calls[0].Bound);
        Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
            "attempt-one-infrastructure-tests-cli-lane.json", "runner:C:/mcg-executor/queue/" }, calls[1].Arguments);
        Assert.Equal(new[] { SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "admin",
            "schtasks", "/run", "/tn", "mcg-executor-lane" }, calls[2].Arguments);
        Assert.DoesNotContain("runner", calls[2].Arguments);
        foreach (var call in calls.Skip(1))
        {
            Assert.True(Path.IsPathFullyQualified(call.Arguments[0]));
            Assert.Contains(Path.Combine("System32", "OpenSSH"), call.Arguments[0]);
            Assert.Equal(new[] { "-o", "BatchMode=yes" }, call.Arguments[1..3]);
            Assert.Equal(TimeSpan.FromMinutes(2), call.Bound);
        }
        Assert.Equal(Path.Combine(fake.Root, "attempt", "remote-one-infrastructure-tests-cli-lane"), calls[1].Directory);
        using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(calls[1].Directory, calls[1].Arguments[3])));
        var request = FakeSshRemoteLaneTransport.Request;
        var expected = new Dictionary<string, string>
        {
            ["sha"] = request.VerifyingCommitSha, ["project"] = request.Project, ["filter"] = request.Filter,
            ["lane"] = request.Lane, ["attemptId"] = request.AttemptId, ["executorId"] = request.ExecutorId,
            ["filterHash"] = request.FilterHash, ["mainSha"] = request.MainSha, ["manifestIdentity"] = request.ManifestIdentity
        };
        Assert.Equal(9, job.RootElement.EnumerateObject().Count());
        foreach (var field in expected) Assert.Equal(field.Value, job.RootElement.GetProperty(field.Key).GetString());
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, "push-failed")]
    [Xunit.InlineData(1, "job-copy-failed")]
    [Xunit.InlineData(2, "trigger-failed")]
    public async Task FailureStopsAtItsStep(int step, string reason)
    {
        await using var fake = new FakeSshRemoteLaneTransport { FailStep = step };
        var submission = await fake.Executor().SubmitAsync(FakeSshRemoteLaneTransport.Request, default);
        Assert.Null(submission.Handle);
        Assert.Equal(reason, submission.FailureReason);
        Assert.Equal(step + 1, fake.Calls.Count);
    }

    [Xunit.Theory]
    [Xunit.InlineData(1, "job-copy-failed")]
    [Xunit.InlineData(2, "trigger-failed")]
    public async Task TimeoutRejectsEvenZeroExit(int step, string reason)
    {
        await using var fake = new FakeSshRemoteLaneTransport { FailStep = step, TimeoutStep = true };
        var submission = await fake.Executor().SubmitAsync(FakeSshRemoteLaneTransport.Request, default);
        Assert.Null(submission.Handle);
        Assert.Equal(reason, submission.FailureReason);
        Assert.Equal(step + 1, fake.Calls.Count);
    }

    [Xunit.Fact]
    public async Task WarningOutputCannotTurnGreenStepsRed()
    {
        await using var fake = new FakeSshRemoteLaneTransport
        { Noise = "WARNING: connection is not using a post-quantum key exchange algorithm.\nWARNING: store now, decrypt later.\nWARNING: server may need upgrading." };
        Assert.NotNull(await fake.Submit());
    }

    [Xunit.Theory]
    [Xunit.InlineData(false, true, "transport-unavailable")]
    [Xunit.InlineData(true, false, "no-attempt-folder")]
    public async Task RefusalMakesNoRunnerCall(bool ssh, bool prefix, string reason)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var submission = await fake.Executor(ssh, prefix).SubmitAsync(FakeSshRemoteLaneTransport.Request, default);
        Assert.Null(submission.Handle);
        Assert.Equal(reason, submission.FailureReason);
        Assert.Empty(fake.Calls);
    }

    [Xunit.Fact]
    public async Task PollsBindAttemptAndObserveHeartbeatWithHostClockInFreshFolders()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        var folders = new List<string>();
        var foreign = FakeSshRemoteLaneTransport.Status("completed");
        foreign["attemptId"] = "other-attempt";
        folders.Add((await fake.PollOnce(new(foreign, "9999-01-01"))).DestinationFolder);
        Assert.Null(handle.TryGetResult());
        Assert.Null(handle.NewestHeartbeat);
        Assert.Equal(4, fake.Calls.Count); // Foreign completion never fetches TRX.
        fake.Clock.Advance(TimeSpan.FromHours(1));
        folders.Add((await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status(), "1900-01-01"))).DestinationFolder);
        var observed = fake.Clock.GetUtcNow();
        Assert.Equal(observed, handle.NewestHeartbeat);
        fake.Clock.Advance(TimeSpan.FromHours(2));
        folders.Add((await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status(), "1900-01-01"))).DestinationFolder);
        Assert.Equal(observed, handle.NewestHeartbeat);
        folders.Add((await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status(), "changed-but-failed-copy", Exit: 1))).DestinationFolder);
        Assert.Equal(observed, handle.NewestHeartbeat);
        Assert.Null(handle.TryGetResult());
        folders.Add((await fake.PollOnce(new(null, Malformed: true))).DestinationFolder);
        Assert.Equal(observed, handle.NewestHeartbeat);
        folders.Add((await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status(), "fresh"))).DestinationFolder);
        Assert.Equal(fake.Clock.GetUtcNow(), handle.NewestHeartbeat);
        Assert.Equal(folders.Count, folders.Distinct().Count());
        foreach (var call in fake.Calls.Skip(3))
        {
            Assert.Equal(SshRemoteLaneExecutor.ScpPath, call.Arguments[0]);
            Assert.Equal(new[] { "-o", "BatchMode=yes" }, call.Arguments[1..3]);
            Assert.EndsWith("/status.json", call.Arguments[3]);
            Assert.EndsWith("/heartbeat.txt", call.Arguments[4]);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task CompletedStatusMapsExecutorFieldsAndFetchesTrxOnce(bool missingTree)
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        var status = FakeSshRemoteLaneTransport.Status("completed");
        if (missingTree) status.Remove("treeSha");
        await fake.PollOnce(new(status));
        var result = Assert.IsType<RemoteLaneResult>(handle.TryGetResult());
        Assert.Equal("reported-executor", result.ExecutorId);
        Assert.Equal("reported-lane", result.Lane);
        Assert.Equal("reported-filter", result.FilterHash);
        Assert.Equal("reported-commit", result.VerifyingCommitSha);
        Assert.Equal(missingTree ? "" : "reported-tree", result.ObservedTreeSha);
        Assert.Equal("reported-main", result.MainSha);
        Assert.Equal("reported-manifest", result.ManifestIdentity);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(2, result.TestResultPaths.Count);
        var fetch = Assert.Single(fake.Calls.Where(call => call.Arguments[^1] == "."));
        Assert.Equal(6, fetch.Arguments.Length);
        Assert.Equal(new[] { SshRemoteLaneExecutor.ScpPath, "-o", "BatchMode=yes",
            "runner:C:/mcg-executor/results/123456789/infrastructure-tests-cli-lane/one.trx",
            "runner:C:/mcg-executor/results/123456789/infrastructure-tests-cli-lane/two.trx", "." }, fetch.Arguments);
        foreach (var path in result.TestResultPaths)
        { Assert.True(File.Exists(path)); Assert.Equal(fetch.Directory, Path.GetDirectoryName(path)); }
    }

    [Xunit.Theory]
    [Xunit.InlineData("failed", "executor-checkout-failed")]
    [Xunit.InlineData("unsafe-name", "invalid-trx-name")]
    [Xunit.InlineData("missing-exit", "missing-or-invalid-exitCode")]
    [Xunit.InlineData("fetch-failed", "result-fetch-failed")]
    public async Task TerminalFaultIsPublishedWithoutUnsafeFetch(string fault, string message)
    {
        await using var fake = new FakeSshRemoteLaneTransport { FetchFailure = fault == "fetch-failed" };
        var handle = await fake.Submit();
        var status = FakeSshRemoteLaneTransport.Status(fault == "failed" ? "failed" : "completed");
        if (fault == "unsafe-name") status["trx"] = new[] { "../unsafe.trx" };
        if (fault == "missing-exit") status.Remove("exitCode");
        await fake.PollOnce(new(status));
        Assert.Equal(message, Assert.Throws<RemoteLaneTransportException>(() => handle.TryGetResult()).Message);
        Assert.Equal(fault == "fetch-failed" ? 5 : 4, fake.Calls.Count);
    }

    [Xunit.Fact]
    public async Task AbandonStopsPollingWithoutRemoteCancellation()
    {
        await using var fake = new FakeSshRemoteLaneTransport();
        var handle = await fake.Submit();
        await fake.PollOnce(new(FakeSshRemoteLaneTransport.Status()));
        handle.Abandon();
        await handle.PollLoop;
        var count = fake.Calls.Count;
        await fake.Polls.Writer.WriteAsync(new(FakeSshRemoteLaneTransport.Status("completed")));
        Assert.Equal(count, fake.Calls.Count);
        Assert.Null(handle.TryGetResult());
    }
}
