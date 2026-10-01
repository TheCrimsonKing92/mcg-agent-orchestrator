using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("ProcessSpawning")]
public sealed class DispatchProcessHostTestsHeartbeatWriteGuardFailure
{
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void FailedPublication_SurfacesFailureCleansTemporaryFileAndCanRecover(bool throwingSink)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcg-heartbeat-failure", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var heartbeatPath = Path.Combine(directory, "heartbeat.json");
        DispatchProcessHost.HeartbeatWriteFailure? received = null;
        var sinkCalls = 0;
        var writer = new DispatchProcessHost.HeartbeatWriteGuard(heartbeatPath, durable: false, onFailure: failure =>
        {
            received = failure;
            sinkCalls++;
            if (throwingSink) throw new InvalidOperationException("injected sink failure");
        });
        try
        {
            DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = (path, state) =>
            {
                if (path == heartbeatPath && state == "launched") throw new IOException("injected move failure");
            };
            Assert.Null(Xunit.Record.Exception(() => writer.Write("launched", "{\"state\":\"launched\"}")));
            Assert.NotNull(received);
            Assert.Equal("launched", received.State);
            Assert.IsType<IOException>(received.Exception);
            Assert.Same(received, writer.LastFailure);
            Assert.Equal(1, sinkCalls);
            Assert.Empty(Directory.EnumerateFiles(directory, "heartbeat.json.*.tmp"));
            Assert.False(File.Exists(heartbeatPath));

            DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = null;
            writer.Write("running", "{\"state\":\"running\"}");
            Assert.Equal("{\"state\":\"running\"}", File.ReadAllText(heartbeatPath));
            writer.WriteTerminal("exited", "{\"state\":\"exited\"}");
            writer.Write("running", "{\"state\":\"running\"}");
            Assert.Equal("{\"state\":\"exited\"}", File.ReadAllText(heartbeatPath));
            Assert.Equal(1, sinkCalls);
            Assert.Empty(Directory.EnumerateFiles(directory, "heartbeat.json.*.tmp"));
        }
        finally
        {
            DispatchProcessHost.HeartbeatWriteGuard.BeforeMoveForTests = null;
            Directory.Delete(directory, recursive: true);
        }
    }
}
