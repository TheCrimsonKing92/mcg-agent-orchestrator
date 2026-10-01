using Mcg.AgentOrchestrator.Infrastructure;

// Serialized because BeforeMoveForTests is a shared static hook.
[Xunit.Collection("ProcessSpawning")]
public sealed class DispatchProcessHostTestsHeartbeatPublishRetry : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "mcg-heartbeat-retry", Guid.NewGuid().ToString("n"));
    private readonly string _heartbeatPath;
    private const string PreviousPayload = "{\"state\":\"starting\"}";
    private const string LaunchedPayload = "{\"state\":\"launched\"}";
    private const string TerminalPayload = "{\"state\":\"exited\"}";

    public DispatchProcessHostTestsHeartbeatPublishRetry()
    {
        Directory.CreateDirectory(_directory);
        _heartbeatPath = Path.Combine(_directory, "heartbeat.json");
        File.WriteAllText(_heartbeatPath, PreviousPayload);
    }

    [Xunit.Fact]
    public void FirstTwoPublishesDenied_ThirdSucceeds_ReportsNoFailure()
    {
        var failures = new List<DispatchProcessHost.HeartbeatWriteFailure>();
        var writer = new DispatchProcessHost.HeartbeatWriteGuard(
            _heartbeatPath, durable: false, onFailure: failures.Add);
        var attempts = 0;
        string? temporaryPath = null;
        DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = (path, state) =>
        {
            if (path != _heartbeatPath || state != "launched") return;
            var currentTemporaryPath = Assert.Single(
                Directory.EnumerateFiles(_directory, "heartbeat.json.*.tmp"));
            temporaryPath ??= currentTemporaryPath;
            Assert.Equal(temporaryPath, currentTemporaryPath);
            Assert.Equal(LaunchedPayload, File.ReadAllText(currentTemporaryPath));
            if (++attempts <= 2) throw new UnauthorizedAccessException("injected publish denial");
        };

        Assert.Null(Xunit.Record.Exception(() => writer.Write("launched", LaunchedPayload)));

        Assert.Empty(failures);
        Assert.Null(writer.LastFailure);
        Assert.Equal(3, attempts);
        Assert.Equal(LaunchedPayload, File.ReadAllText(_heartbeatPath));
        AssertNoTemporaryFiles();
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void EveryPublishDenied_SurfacesOncePreservesContentAndDoesNotThrow(bool throwingSink)
    {
        var failures = new List<DispatchProcessHost.HeartbeatWriteFailure>();
        var writer = new DispatchProcessHost.HeartbeatWriteGuard(
            _heartbeatPath, durable: false, onFailure: failure =>
            {
                failures.Add(failure);
                if (throwingSink) throw new InvalidOperationException("injected sink failure");
            });
        var attempts = 0;
        Exception? finalDenial = null;
        DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = (path, state) =>
        {
            if (path != _heartbeatPath || state != "launched") return;
            attempts++;
            finalDenial = new UnauthorizedAccessException("injected publish denial");
            throw finalDenial;
        };

        Assert.Null(Xunit.Record.Exception(() => writer.Write("launched", LaunchedPayload)));

        var failure = Assert.Single(failures);
        Assert.Equal("launched", failure.State);
        Assert.IsType<UnauthorizedAccessException>(failure.Exception);
        Assert.Same(finalDenial, failure.Exception);
        Assert.Same(failure, writer.LastFailure);
        Assert.Equal(5, attempts);
        Assert.Equal(PreviousPayload, File.ReadAllText(_heartbeatPath));
        AssertNoTemporaryFiles();
    }

    [Xunit.Fact]
    public void TerminalPublishedBetweenAttempts_NonTerminalRetryLeavesTerminalPayload()
    {
        var failures = new List<DispatchProcessHost.HeartbeatWriteFailure>();
        var writer = new DispatchProcessHost.HeartbeatWriteGuard(
            _heartbeatPath, durable: false, onFailure: failures.Add);
        var attempts = 0;
        var terminalPublished = false;
        DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = (path, state) =>
        {
            if (path != _heartbeatPath || state != "running") return;
            if (++attempts == 1) throw new UnauthorizedAccessException("injected publish denial");

            // The retry hook orders terminal publication before the retry's locked move.
            writer.WriteTerminal("exited", TerminalPayload);
            terminalPublished = File.ReadAllText(_heartbeatPath) == TerminalPayload;
        };

        Assert.Null(Xunit.Record.Exception(() => writer.Write("running", "{\"state\":\"running\"}")));

        Assert.True(terminalPublished);
        Assert.Equal(2, attempts);
        Assert.Equal(TerminalPayload, File.ReadAllText(_heartbeatPath));
        Assert.Empty(failures);
        Assert.Null(writer.LastFailure);
        AssertNoTemporaryFiles();
    }

    [Xunit.Theory]
    [Xunit.InlineData(32)]
    [Xunit.InlineData(33)]
    public void SharingOrLockConflict_RetriesSamePublicationSuccessfully(int errorCode)
    {
        var failures = new List<DispatchProcessHost.HeartbeatWriteFailure>();
        var writer = new DispatchProcessHost.HeartbeatWriteGuard(
            _heartbeatPath, durable: false, onFailure: failures.Add);
        var attempts = 0;
        DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = (path, state) =>
        {
            if (path == _heartbeatPath && state == "launched" && ++attempts == 1)
                throw new IOException("injected sharing or lock conflict", unchecked((int)0x80070000) | errorCode);
        };

        Assert.Null(Xunit.Record.Exception(() => writer.Write("launched", LaunchedPayload)));

        Assert.Empty(failures);
        Assert.Null(writer.LastFailure);
        Assert.Equal(2, attempts);
        Assert.Equal(LaunchedPayload, File.ReadAllText(_heartbeatPath));
        AssertNoTemporaryFiles();
    }

    [Xunit.Theory]
    [Xunit.InlineData("io")]
    [Xunit.InlineData("missing-file")]
    [Xunit.InlineData("missing-directory")]
    [Xunit.InlineData("unexpected")]
    public void NonRetryablePublishFailure_SurfacesImmediatelyWithoutReplacingContent(string kind)
    {
        Exception denial = kind switch
        {
            "io" => new IOException("injected ordinary IO failure"),
            "missing-file" => new FileNotFoundException("injected missing file"),
            "missing-directory" => new DirectoryNotFoundException("injected missing directory"),
            _ => new InvalidOperationException("injected unexpected failure")
        };
        var failures = new List<DispatchProcessHost.HeartbeatWriteFailure>();
        var writer = new DispatchProcessHost.HeartbeatWriteGuard(
            _heartbeatPath, durable: false, onFailure: failures.Add);
        var attempts = 0;
        DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = (path, state) =>
        {
            if (path != _heartbeatPath || state != "launched") return;
            attempts++;
            throw denial;
        };

        Assert.Null(Xunit.Record.Exception(() => writer.Write("launched", LaunchedPayload)));

        var failure = Assert.Single(failures);
        Assert.Equal("launched", failure.State);
        Assert.Same(denial, failure.Exception);
        Assert.Same(failure, writer.LastFailure);
        Assert.Equal(1, attempts);
        Assert.Equal(PreviousPayload, File.ReadAllText(_heartbeatPath));
        AssertNoTemporaryFiles();
    }

    private void AssertNoTemporaryFiles() =>
        Assert.Empty(Directory.EnumerateFiles(_directory, "heartbeat.json.*.tmp"));

    public void Dispose()
    {
        DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = null;
        Directory.Delete(_directory, recursive: true);
    }
}
