using Mcg.AgentOrchestrator.App.Orchestration;
using static Mcg.AgentOrchestrator.App.Orchestration.ConductorSelfCheckProbe;

// Parallel-safe: scripted processes and timers, no external state or wall-clock assertions.
public sealed class ConductorSelfCheckProbeTests
{
    [Theory]
    [InlineData(false, 0, false, 1, "ok")]
    [InlineData(true, 0, false, 2, "ok")]
    [InlineData(false, 3, false, 1, "failed")]
    [InlineData(true, -1, true, 2, "timeout")]
    public void Process_outcome_controls_attempts_and_events(
        bool firstTimeout, int finalExit, bool finalTimeout, int expectedCalls, string finalOutcome)
    {
        var events = new List<string>();
        var calls = 0;
        var timers = 0;
        string[] arguments = ["successor.dll", "self-check"];
        var timeout = TimeSpan.FromSeconds(12);
        var result = Probe((args, directory, receivedTimeout, token) =>
        {
            Assert.Same(arguments, args);
            Assert.Equal("fixture", directory);
            Assert.Equal(timeout, receivedTimeout);
            Assert.Equal(CancellationToken.None, token);
            calls++;
            return calls == 1 && firstTimeout ? new(-1, "", "", true) : new(finalExit, "out", "err", finalTimeout);
        }, arguments, "fixture", timeout, events.Add, CancellationToken.None,
            startTimer: () => { var ms = ++timers * 17L; return () => ms; });

        Assert.Equal(expectedCalls, calls);
        Assert.Equal(expectedCalls, result.Attempts);
        Assert.Equal(new SuccessorProbeResult(finalExit, "out", "err", finalTimeout), result.Result);
        Assert.Equal(expectedCalls == 1
            ? new[] { $"SELF_CHECK_ELAPSED attempt=1 ms=17 outcome={finalOutcome}" }
            : new[] { "SELF_CHECK_ELAPSED attempt=1 ms=17 outcome=timeout",
                "SELF_CHECK_RETRY reason=timeout firstMs=17", $"SELF_CHECK_ELAPSED attempt=2 ms=34 outcome={finalOutcome}" }, events);
    }

    [Fact]
    public void Caller_cancellation_after_timeout_prevents_retry()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var events = new List<string>();
        Assert.Throws<OperationCanceledException>(() => Probe((_, _, _, _) =>
        {
            calls++;
            cancellation.Cancel();
            return new(-1, "", "", true);
        }, [], "fixture", DefaultTimeout, events.Add, cancellation.Token, () => () => 7));
        Assert.Equal(1, calls);
        Assert.Equal(new[] { "SELF_CHECK_ELAPSED attempt=1 ms=7 outcome=timeout" }, events);
    }

    [Fact]
    public void Thrown_cancellation_is_propagated_without_retry()
    {
        var calls = 0;
        Assert.Throws<OperationCanceledException>(() => Probe((_, _, _, _) =>
        {
            calls++;
            throw new OperationCanceledException();
        }, [], "fixture", DefaultTimeout, null, CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Failure_keeps_last_five_nonblank_lines_from_each_final_stream()
    {
        var stdout = string.Join("\r\n\n", Enumerable.Range(1, 12).Select(n => $"out{n}"));
        var stderr = string.Join("\n \n", Enumerable.Range(1, 12).Select(n => $"err{n}"));
        var probe = Probe((_, _, _, _) => new(3, stdout, stderr, false), [], "fixture", DefaultTimeout, null, CancellationToken.None);
        Assert.Equal("probe successor startup contract failed exit=3 timedOut=False attempts=1 " +
            "stdoutTail=[out8 | out9 | out10 | out11 | out12] stderrTail=[err8 | err9 | err10 | err11 | err12]", FormatFailure(probe));
    }

    [Fact]
    public void Tails_bound_lines_sanitize_controls_and_mark_empty_output()
    {
        Assert.Equal(new string('x', 200), BoundedTail(new string('x', 500)));
        Assert.Equal("key=value | next=still value", BoundedTail("key=value\r\nnext=still\tvalue"));
        Assert.Equal("<empty>", BoundedTail(" \r\n\t"));
        Assert.Equal("<empty>", BoundedTail(""));
        var message = FormatFailure(new(new(-1, "key=a\r\nb", "", true), 2));
        Assert.DoesNotContain('\r', message);
        Assert.DoesNotContain('\n', message);
        Assert.Contains("stderrTail=[<empty>]", message);
    }
}
