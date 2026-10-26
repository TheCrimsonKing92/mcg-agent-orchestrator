using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: each case owns its source, filter and coalescer.
public sealed class OwnerConsoleEventRefreshFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("sweep-owned-root-observed", null, "SWEEP_OWNED_ROOT_OBSERVED detail=observe-only unregistered build root")]
    [InlineData("speculative-cohort-plan", null, "SPECULATIVE_COHORT_PLAN tick=94 advisory=true ready=1")]
    [InlineData("loop-handoff-spawn", null, "")]
    [InlineData("loop-handoff-launch-authority", null, "")]
    [InlineData("candidate-identity-unavailable", null, "")]
    [InlineData("blocked-recheck-heartbeat", null, "")]
    [InlineData("author", null, "kind=other")]
    [InlineData("goal", null, "")]
    [InlineData("GATE-PROGRESS", null, "")]
    [InlineData("other", null, "acceptance result=passed")]
    public void UnrenderedEvents_DoNotChangeView(string kind, string? goalId, string detail) =>
        Assert.False(OwnerConsoleEventRefreshFilter.ChangesView(new(Now, kind, goalId, detail)));

    [Theory]
    [InlineData("acceptance", "11111111", "ACCEPTANCE goal=11111111 result=passed")]
    [InlineData("acceptance-cohort", null, "")]
    [InlineData("gate-progress", null, "")]
    [InlineData("remote-lane", null, "")]
    [InlineData("EVIDENCE_START", null, "")]
    [InlineData("EVIDENCE_END", null, "")]
    [InlineData("goal", "11111111", "")]
    [InlineData("goal-escalation", "11111111", "")]
    [InlineData("goal-lifecycle", null, "TaskDispatched task=task-1")]
    [InlineData("loop-relaunch", "11111111", "LOOP_RELAUNCH_SCHEDULED goal=11111111")]
    [InlineData("program-health", null, "")]
    [InlineData("tick", null, "TICK tick=1")]
    [InlineData("TiCk-completed", null, "")]
    [InlineData("speculative-cohort-plan", null, "ACCEPTANCE_COHORT_ENTRY members=11111111")]
    public void RenderedEvents_ChangeView(string kind, string? goalId, string detail) =>
        Assert.True(OwnerConsoleEventRefreshFilter.ChangesView(new(Now, kind, goalId, detail)));

    [Fact]
    public void FoldLast_UsesNewestSkippedTimestampOnce_WithoutMovingLastBackwards()
    {
        var filter = new OwnerConsoleEventRefreshFilter();
        var calls = 0;
        var coalescer = new OwnerConsoleRefreshCoalescer((_, _) => { calls++; return Task.CompletedTask; });
        Assert.Equal(Now, filter.FoldLast(Now));
        Assert.Null(filter.FoldLast(null));

        filter.Route(new(Now.AddSeconds(3), "sweep-owned-root-observed", null, ""), coalescer);
        filter.Route(new(Now.AddSeconds(1), "speculative-cohort-plan", null, ""), coalescer);
        Assert.Equal(Now.AddSeconds(3), filter.FoldLast(null));
        Assert.Null(filter.FoldLast(null));

        filter.Route(new(Now.AddSeconds(1), "sweep-owned-root-observed", null, ""), coalescer);
        Assert.Equal(Now.AddSeconds(5), filter.FoldLast(Now.AddSeconds(5)));
        Assert.Null(filter.FoldLast(null));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("acceptance")]
    [InlineData("skipped-only")]
    [InlineData("gate-progress")]
    public async Task Pump_RoutesOnlyEventsThatChangeView(string script)
    {
        var skipped = Enumerable.Range(0, 26).Select(index =>
            new OwnerConductEvent(Now.AddSeconds(index), "sweep-owned-root-observed", null,
                "SWEEP_OWNED_ROOT_OBSERVED detail=observe-only unregistered build root"))
            .Append(new(Now.AddSeconds(26), "speculative-cohort-plan", null,
                "SPECULATIVE_COHORT_PLAN tick=94 advisory=true ready=1")).ToArray();
        var rendered = new OwnerConductEvent(Now.AddSeconds(27), script, "11111111",
            script == "acceptance" ? "ACCEPTANCE goal=11111111 result=passed" : "");
        OwnerConductEvent[] items = script switch
        {
            "acceptance" => [.. skipped, rendered],
            "skipped-only" => skipped,
            _ => [rendered]
        };
        await using var source = new ScriptedSource(items);
        var filter = new OwnerConsoleEventRefreshFilter();
        var calls = new List<OwnerConductEvent[]>();
        var coalescer = new OwnerConsoleRefreshCoalescer((batch, _) =>
        {
            calls.Add(batch.ToArray());
            return Task.CompletedTask;
        });
        var errors = new List<string>();
        var pump = new OwnerConsoleEventPump(source, TimeProvider.System, errors.Add);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = pump.RunAsync(item => { filter.Route(item, coalescer); return Task.CompletedTask; }, stop.Token);
        try
        {
            await AwaitSignal(source.Drained.Task, "pump requested the item after the script");
            await AwaitSignal(coalescer.Completion, "coalescer drained all routed events");
            Assert.Empty(errors);
            if (script == "skipped-only")
            {
                Assert.Empty(calls);
                Assert.Equal(skipped[^1].Timestamp, filter.FoldLast(null));
            }
            else
            {
                Assert.Equal([rendered], Assert.Single(calls));
            }
        }
        finally
        {
            stop.Cancel();
            await AwaitSignal(run, "pump observed cancellation");
        }
    }

    private static async Task AwaitSignal(Task task, string signal)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        catch (TimeoutException ex) { throw new TimeoutException("Hang guard: " + signal, ex); }
    }

    private sealed class ScriptedSource(OwnerConductEvent[] items) : IConductEventSource
    {
        private int _next;
        internal readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset? LastActivity => null;
        public async ValueTask<OwnerConductEvent> ReadAsync(CancellationToken cancellationToken)
        {
            if (_next < items.Length) return items[_next++];
            Drained.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The script resumes only by cancellation.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
