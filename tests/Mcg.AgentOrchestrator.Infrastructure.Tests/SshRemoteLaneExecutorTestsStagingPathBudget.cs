using System.Collections.Concurrent;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: a private fake owns the root; each lane has its own poll-completion channel.
public sealed class SshRemoteLaneExecutorTestsStagingPathBudget
{
    [Fact]
    public async Task TwoLongLanesKeepAllStagingWritesInsideRepresentativePathBudget()
    {
        await using var fake = new FakeSshRemoteLaneTransport { Noise = "transport diagnostic" };
        var cancellationToken = TestContext.Current.CancellationToken;
        var attempt = Path.Combine(fake.Root, "attempt");
        var observedEntries = new ConcurrentQueue<string>();
        var failedRequest = FakeSshRemoteLaneTransport.Request with { Lane = "failed lane " + new string('a', 160) };
        var completedRequest = FakeSshRemoteLaneTransport.Request with { Lane = "completed lane " + new string('b', 160) };

        var failedEvents = Channel.CreateUnbounded<SshPollObservation>();
        var failedSubmission = await Executor(failedRequest, failedEvents).SubmitAsync(failedRequest, cancellationToken);
        var failed = fake.Handle = Assert.IsType<SshRemoteLaneHandle>(failedSubmission.Handle);
        await fake.Polls.Writer.WriteAsync(new(null, Exit: 255), cancellationToken);
        await Event(failedEvents.Reader.ReadAsync(cancellationToken).AsTask(), "failed lane poll completed");
        Assert.Equal(1, failed.Snapshot().Poll.FailedPollCount);
        failed.RequestQueuedJobCancellation();
        Assert.NotNull(failed.QueuedJobCancellation);
        await Event(failed.QueuedJobCancellation, "queued cancellation copy completed");
        fake.FetchFailure = true;
        var failedCapture = await failed.CaptureRunnerLogAsync(cancellationToken);
        Assert.Null(failedCapture.Path);
        Assert.NotNull(failedCapture.FailedStep);
        failed.Abandon();
        await Event(failed.PollLoop, "failed lane poll loop stopped");

        fake.FetchFailure = false;
        var completedEvents = Channel.CreateUnbounded<SshPollObservation>();
        var completedSubmission = await Executor(completedRequest, completedEvents).SubmitAsync(completedRequest, cancellationToken);
        var completed = fake.Handle = Assert.IsType<SshRemoteLaneHandle>(completedSubmission.Handle);
        await fake.Polls.Writer.WriteAsync(new(FakeSshRemoteLaneTransport.Status("completed")), cancellationToken);
        await Event(completedEvents.Reader.ReadAsync(cancellationToken).AsTask(), "completed lane poll and fetches completed");
        await Event(completed.PollLoop, "completed lane poll loop stopped");
        var result = Assert.IsType<RemoteLaneResult>(completed.TryGetResult());
        Assert.Equal(2, result.TestResultPaths.Count);
        Assert.All(result.TestResultPaths, path => Assert.True(File.Exists(path)));
        Assert.Equal(2, completed.Snapshot().Fetches.Count);
        var successfulCapture = await completed.CaptureRunnerLogAsync(cancellationToken);
        Assert.Null(successfulCapture.FailedStep);
        Assert.NotNull(successfulCapture.Path);
        Assert.True(File.Exists(successfulCapture.Path));

        var stagingFolders = Directory.GetDirectories(attempt);
        Assert.Equal(2, stagingFolders.Length);
        foreach (var staging in stagingFolders)
        {
            Assert.Equal(10, Path.GetFileName(staging).Length);
            observedEntries.Enqueue(staging);
            foreach (var path in Directory.EnumerateFileSystemEntries(staging, "*", SearchOption.AllDirectories))
                observedEntries.Enqueue(path);
        }
        var entries = observedEntries.Distinct().ToArray();
        Assert.Contains(entries, path => Path.GetFileName(path) == "lane.json");
        Assert.Contains(entries, path => Path.GetFileName(path) == "job.json");
        Assert.Contains(entries, path => Path.GetFileName(path) == "cancel.txt");
        Assert.Contains(entries, path => Path.GetFileName(path) == "status.json");
        Assert.Contains(entries, path => Path.GetFileName(path) == "heartbeat.txt");
        Assert.Contains(entries, path => Path.GetFileName(path) == "poll.err.txt");
        Assert.Contains(entries, path => Path.GetFileName(path) == "runner-log.err.txt");
        Assert.Contains(entries, path => Path.GetFileName(path) == "runner-tail.log");
        Assert.Contains(entries, path => path.EndsWith(".part", StringComparison.Ordinal));
        foreach (var path in entries.Except(result.TestResultPaths))
        {
            var representativeLength = SshRemoteLaneExecutor.RepresentativeAttemptFolderLength + 1 +
                Path.GetRelativePath(attempt, path).Length + 4;
            Assert.True(representativeLength < SshRemoteLaneExecutor.StagingPathBudget,
                $"Staging entry exceeds path budget: {path} ({representativeLength})");
        }

        SshRemoteLaneExecutor Executor(RemoteLaneRequest request, Channel<SshPollObservation> observations) => new(
            new([new("one", 60, "ssh", "runner", "admin", "C:/repo/bare.git", "C:/mcg-executor")], [request.Lane], null),
            fake.Root, Path.Combine(attempt, "result"), fake.Clock, Transport, fake.Git, TimeSpan.Zero,
            observation => observations.Writer.TryWrite(observation));

        async Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string directory, TimeSpan bound, CancellationToken token)
        {
            var command = await fake.Transport(args, directory, bound, token);
            // Observe temporary fetch writes before the handle moves or deletes them.
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
                observedEntries.Enqueue(path);
            return command;
        }
    }

    private static async Task Event(Task task, string missingEvent)
    {
        try { await task.WaitAsync(TestContext.Current.CancellationToken); }
        catch (OperationCanceledException exception) when (TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Missing event: " + missingEvent, exception,
                TestContext.Current.CancellationToken);
        }
    }
}
