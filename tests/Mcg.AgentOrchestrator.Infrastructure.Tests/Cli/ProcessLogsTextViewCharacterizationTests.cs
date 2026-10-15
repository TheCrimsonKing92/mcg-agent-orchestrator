using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: Capture routes output through AsyncLocal; snapshots contain supplied data only.
public sealed class ProcessLogsTextViewCharacterizationTests
{
    // Literal expectations characterize ConsoleViews.ProcessLogs.cs before extraction.
    public static IEnumerable<object[]> OutputCases()
    {
        var contents = new (string Out, string Error, string Exit, string ExpectedOut, string ExpectedError, string ExpectedExit)[]
        {
            ("  first\nsecond \r\n\t ", "\tproblem\n detail \r\n ", " \t17\r\n ",
                "  first\nsecond", "\tproblem\n detail", "17"),
            ("", "", "", "<empty>", "<empty>", "<missing>"),
            (" \t\r\n", "\r\n\t ", " \t\r\n", "", "", ""),
            ("stdout without newline", "stderr without newline", "",
                "stdout without newline", "stderr without newline", "<missing>")
        };
        foreach (var stream in new[] { ProcessLogStream.All, ProcessLogStream.Stdout, ProcessLogStream.Stderr, ProcessLogStream.Exit })
        foreach (var available in new[] { true, false })
        foreach (var content in contents)
        {
            yield return [stream, available, content.Out, content.Error, content.Exit,
                content.ExpectedOut, content.ExpectedError, content.ExpectedExit];
        }
    }

    [Theory]
    [MemberData(nameof(OutputCases))]
    public void Print_SelectedStreamsAndHeartbeat_PreservesExactText(
        ProcessLogStream stream, bool available, string stdout, string stderr, string exit,
        string expectedStdout, string expectedStderr, string expectedExit)
    {
        var heartbeat = new DispatchHeartbeatStatus(
            @"C:\logs\worker.heartbeat.json", available, available ? null : "missing",
            777, 888, [777, 888], "running",
            DateTimeOffset.Parse("2026-06-12T20:00:10Z"), DateTimeOffset.Parse("2026-06-12T20:00:00Z"),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(13), 123, 45);
        var logs = new ProcessLogSnapshot(@"C:\logs\worker.out.log", stdout,
            @"C:\logs\worker.err.log", stderr, @"C:\logs\worker.exit.txt", exit, heartbeat);
        var actual = AsyncLocalConsoleRouter.Capture(() => ProcessLogsTextView.PrintProcessLogs(logs, stream));

        var expectedHeartbeat = available
            ? new[]
            {
                "Heartbeat Status:",
                "heartbeat: available state=running pid=777 child_pid=888 owned_pids=777,888",
                @"heartbeat path: C:\logs\worker.heartbeat.json",
                "last observed: 2026-06-12 20:00:10Z age=3s",
                "last progress: 2026-06-12 20:00:00Z idle=13s",
                "log bytes: stdout=123 stderr=45"
            }
            : new[]
            {
                "Heartbeat Status:",
                "heartbeat: unavailable (missing)",
                @"heartbeat path: C:\logs\worker.heartbeat.json",
                "last observed: n/a age=n/a",
                "last progress: n/a idle=n/a",
                "log bytes: stdout=n/a stderr=n/a"
            };
        var expectedSections = stream switch
        {
            ProcessLogStream.All => new[]
            {
                @"stdout: C:\logs\worker.out.log", expectedStdout,
                @"stderr: C:\logs\worker.err.log", expectedStderr,
                @"exit: C:\logs\worker.exit.txt", expectedExit
            },
            ProcessLogStream.Stdout => [@"stdout: C:\logs\worker.out.log", expectedStdout],
            ProcessLogStream.Stderr => [@"stderr: C:\logs\worker.err.log", expectedStderr],
            ProcessLogStream.Exit => [@"exit: C:\logs\worker.exit.txt", expectedExit],
            _ => throw new ArgumentOutOfRangeException(nameof(stream))
        };
        var expected = string.Join(Environment.NewLine, expectedHeartbeat.Concat(expectedSections).Append(""));
        Assert.Equal(expected, actual);
    }
}
