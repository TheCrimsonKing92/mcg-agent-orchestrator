// Parallel-safe: each fact owns a unique temporary directory and starts no process.
public sealed class DispatchProcessHostLaunchedHeartbeatDiagnosticsTests
{
    [Fact]
    public void FaultedRun_IncludesArtifactsAndExceptionBeforeDirectoryRemoval()
    {
        var directory = NewDirectory();
        string message;
        const string heartbeat = "{\"state\":\"launched\",\"childPid\":123}";
        string[] lines = ["host starting", "worker attached", "launched write failed"];
        try
        {
            File.WriteAllText(Path.Combine(directory, "heartbeat.json"), heartbeat);
            File.WriteAllLines(Path.Combine(directory, "host.err.log"), lines);
            var runTask = Task.FromException<int>(new IOException("injected host failure before launched write"));

            message = DispatchProcessHostTestsLaunchedHeartbeat.DescribeHostState(
                "The launched heartbeat was not published while the worker was held.", directory, runTask);
        }
        finally { Directory.Delete(directory, recursive: true); }

        Assert.StartsWith("The launched heartbeat was not published while the worker was held.", message);
        Assert.Contains($"heartbeat.json: {heartbeat}", message);
        Assert.Contains(string.Join(Environment.NewLine, lines), message);
        Assert.Contains("exit.txt: absent", message);
        Assert.Contains("run task: faulted: System.IO.IOException: injected host failure before launched write", message);
    }

    [Fact]
    public void UnfinishedRun_ReportsStateWithoutWaiting()
    {
        var directory = NewDirectory();
        try
        {
            var runTask = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            var message = DispatchProcessHostTestsLaunchedHeartbeat.DescribeHostState("held", directory, runTask);

            Assert.Contains("run task: not completed", message);
            Assert.Contains("heartbeat.json: absent", message);
            Assert.Contains("host.err.log (last 40 lines): absent", message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CompletedRun_IncludesExitAndOnlyLastFortyLogLines()
    {
        var directory = NewDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "heartbeat.json"), "");
            File.WriteAllText(Path.Combine(directory, "exit.txt"), "known exit artifact");
            var lines = Enumerable.Range(1, 43).Select(index => $"line {index:D2}").ToArray();
            File.WriteAllLines(Path.Combine(directory, "host.err.log"), lines);

            var message = DispatchProcessHostTestsLaunchedHeartbeat.DescribeHostState(
                "released", directory, Task.FromResult(17));

            Assert.Contains("heartbeat.json: <empty>", message);
            Assert.Contains("exit.txt: known exit artifact", message);
            Assert.Contains("run task: completed: 17", message);
            Assert.Contains("host.err.log (last 40 lines): " + string.Join(Environment.NewLine, lines.Skip(3)), message);
            foreach (var omitted in lines.Take(3)) Assert.DoesNotContain(omitted, message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LockedHeartbeat_ReportsReadFailureAndKeepsOtherDiagnostics()
    {
        var directory = NewDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "host.err.log"), "available diagnostic");
            using var locked = new FileStream(Path.Combine(directory, "heartbeat.json"),
                FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            var message = DispatchProcessHostTestsLaunchedHeartbeat.DescribeHostState("held", directory, null);

            Assert.Contains("heartbeat.json: unreadable: System.IO.IOException:", message);
            Assert.Contains("host.err.log (last 40 lines): available diagnostic", message);
            Assert.Contains("exit.txt: absent", message);
            Assert.Contains("run task: none", message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mcg-heartbeat-diagnostics", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
