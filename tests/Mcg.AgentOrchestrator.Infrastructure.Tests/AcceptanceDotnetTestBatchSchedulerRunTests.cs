using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every scheduler, cancellation source and event gate belongs to one test.
public sealed class AcceptanceDotnetTestBatchSchedulerRunTests
{
    [Fact]
    public async Task LaterGroupsWaitForTheFirstGroupOrBuild()
    {
        await using var fixture = new RunFixture("a", "b");
        fixture.Start();
        await Observe(fixture.Started[0].Task, "first group started");
        Assert.False(fixture.Build.Task.IsCompleted);
        Assert.False(fixture.Release[0].Task.IsCompleted);
        Assert.False(fixture.Started[1].Task.IsCompleted);
        Assert.Equal(0, fixture.PrebuildReads);

        fixture.Build.SetResult();
        await Observe(fixture.Started[1].Task, "second group after successful build");
        Assert.False(fixture.Release[0].Task.IsCompleted);
        fixture.ReleaseAll();
        Assert.Equal(new[] { "a", "b" }, await fixture.Results());
        Assert.Equal(1, fixture.PrebuildReads);
    }

    [Fact]
    public async Task SuccessfulPrebuildSharesTwoSlotsAndReturnsPartsInGroupOrder()
    {
        await using var fixture = new RunFixture("a", "b", "c", "d");
        var active = 0;
        var entries = new ConcurrentQueue<int>();
        fixture.Build.SetResult();
        fixture.Start(async (check, token) =>
        {
            entries.Enqueue(Interlocked.Increment(ref active));
            var index = fixture.Index(check);
            fixture.Started[index].SetResult();
            try
            {
                await fixture.Release[index].Task.WaitAsync(token);
                return (check, false);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        await Observe(fixture.Started[1].Task, "two occupied slots");
        Assert.Equal(2, Volatile.Read(ref active));
        Assert.False(fixture.Started[2].Task.IsCompleted);
        Assert.False(fixture.Started[3].Task.IsCompleted);
        fixture.Release[1].SetResult();
        await Observe(fixture.Started[2].Task, "third group after slot release");
        fixture.Release[2].SetResult();
        await Observe(fixture.Started[3].Task, "fourth group after slot release");
        fixture.Release[3].SetResult();
        fixture.Release[0].SetResult();

        Assert.Equal(new[] { "a", "b", "c", "d" }, await fixture.Results());
        Assert.Equal(4, entries.Count);
        Assert.All(entries, count => Assert.InRange(count, 1, 2));
        Assert.Equal(0, Volatile.Read(ref active));
        Assert.Equal(1, fixture.PrebuildReads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOrAbsentPrebuildRunsSeriallyAndStopsBeforeLaterGroups(bool buildCompleted)
    {
        await using var fixture = new RunFixture("a", "b", "c", "d");
        var order = new ConcurrentQueue<string>();
        var active = 0;
        var entries = new ConcurrentQueue<int>();
        fixture.PrebuildPassed = false;
        if (buildCompleted)
            fixture.Build.SetResult();
        fixture.Start(async (check, token) =>
        {
            order.Enqueue(check);
            entries.Enqueue(Interlocked.Increment(ref active));
            var index = fixture.Index(check);
            fixture.Started[index].SetResult();
            try
            {
                await fixture.Release[index].Task.WaitAsync(token);
                return (check, check == "c");
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        await Observe(fixture.Started[0].Task, "first serial group");
        Assert.False(fixture.Started[1].Task.IsCompleted);
        fixture.Release[0].SetResult();
        await Observe(fixture.Started[1].Task, "second serial group");
        Assert.False(fixture.Started[2].Task.IsCompleted);
        fixture.Release[1].SetResult();
        await Observe(fixture.Started[2].Task, "third serial group");
        Assert.False(fixture.Started[3].Task.IsCompleted);
        fixture.Release[2].SetResult();

        Assert.Equal(new[] { "a", "b", "c" }, await fixture.Results());
        Assert.Equal(new[] { "a", "b", "c" }, order.ToArray());
        Assert.All(entries, count => Assert.Equal(1, count));
        Assert.False(fixture.Started[3].Task.IsCompleted);
        Assert.Equal(1, fixture.PrebuildReads);
    }

    [Fact]
    public async Task PartitionGroupReceivesTheFullSlotBudgetWithoutTakingASlot()
    {
        await using var fixture = new RunFixture("p1", "p2", "a");
        var partitionStarted = Gate();
        var availableSlots = -1;
        fixture.Start(partition: async (group, slots, token) =>
        {
            Assert.Equal(new[] { "p1", "p2" }, group.Checks);
            availableSlots = slots.CurrentCount;
            partitionStarted.SetResult();
            await fixture.Release[0].Task.WaitAsync(token);
            return "partitions";
        });

        await Observe(partitionStarted.Task, "partition group entry");
        Assert.Equal(2, availableSlots);
        Assert.False(fixture.Started[2].Task.IsCompleted);
        fixture.Build.SetResult();
        await Observe(fixture.Started[2].Task, "single group beside partition group");
        fixture.ReleaseAll();
        Assert.Equal(new[] { "partitions", "a" }, await fixture.Results());
    }

    [Fact]
    public async Task StopBatchDropsAGroupWaitingForASlotAndDrainsStartedGroups()
    {
        await using var fixture = new RunFixture("a", "b", "c");
        var stopObserved = Gate();
        fixture.Build.SetResult();
        fixture.Start(async (check, token) =>
        {
            var index = fixture.Index(check);
            fixture.Started[index].SetResult();
            using var registration = token.Register(() => stopObserved.TrySetResult());
            // The first started group deliberately finishes only when its event is released.
            await fixture.Release[index].Task;
            return (check, check == "b");
        });

        await Observe(fixture.Started[1].Task, "two groups holding both slots");
        Assert.False(fixture.Started[2].Task.IsCompleted);
        fixture.Release[1].SetResult();
        await Observe(stopObserved.Task, "batch stop requested");
        Assert.False(fixture.Run!.IsCompleted);
        fixture.Release[0].SetResult();
        Assert.Equal(new[] { "a", "b" }, await fixture.Results());
        Assert.False(fixture.Started[2].Task.IsCompleted);
    }

    [Fact]
    public async Task DelegateFailureRethrowsTheSameExceptionAfterEveryStartedGroupFinishes()
    {
        await using var fixture = new RunFixture("a", "b", "c");
        var failure = new InvalidOperationException("delegate failure");
        var stopObserved = Gate();
        var secondFinished = Gate();
        fixture.Build.SetResult();
        fixture.Start(async (check, token) =>
        {
            var index = fixture.Index(check);
            fixture.Started[index].SetResult();
            using var registration = token.Register(() => stopObserved.TrySetResult());
            await fixture.Release[index].Task;
            if (check == "a")
                throw failure;
            secondFinished.SetResult();
            return (check, false);
        });

        await Observe(fixture.Started[1].Task, "second group before delegate failure");
        fixture.Release[0].SetResult();
        await Observe(stopObserved.Task, "failure requested stop");
        Assert.False(fixture.Run!.IsCompleted);
        Assert.False(secondFinished.Task.IsCompleted);
        fixture.Release[1].SetResult();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Results());
        Assert.Same(failure, thrown);
        Assert.True(secondFinished.Task.IsCompleted);
        Assert.False(fixture.Started[2].Task.IsCompleted);
    }

    [Fact]
    public async Task InvalidationBeforeAGroupStartsContributesNoPart()
    {
        await using var fixture = new RunFixture("a", "b", "c");
        var invalidated = 0;
        fixture.Start(invalidated: () => Volatile.Read(ref invalidated) != 0);
        await Observe(fixture.Started[0].Task, "first group before invalidation");
        Volatile.Write(ref invalidated, 1);
        fixture.Build.SetResult();
        fixture.Release[0].SetResult();
        Assert.Equal(new[] { "a" }, await fixture.Results());
        Assert.False(fixture.Started[1].Task.IsCompleted);
        Assert.False(fixture.Started[2].Task.IsCompleted);
    }

    [Fact]
    public async Task InvalidationWhileWaitingForASlotIsRecheckedAfterAcquiringIt()
    {
        await using var fixture = new RunFixture("a", "b", "c");
        var invalidated = 0;
        fixture.Build.SetResult();
        fixture.Start(invalidated: () => Volatile.Read(ref invalidated) != 0);
        await Observe(fixture.Started[1].Task, "two occupied slots before invalidation");
        Assert.False(fixture.Started[2].Task.IsCompleted);
        Volatile.Write(ref invalidated, 1);
        fixture.ReleaseAll();
        Assert.Equal(new[] { "a", "b" }, await fixture.Results());
        Assert.False(fixture.Started[2].Task.IsCompleted);
    }

    [Fact]
    public async Task CallerCancellationIsRethrownAfterDraining()
    {
        await using var fixture = new RunFixture("a", "b", "c");
        var finished = new ConcurrentQueue<string>();
        fixture.Build.SetResult();
        fixture.Start(async (check, token) =>
        {
            var index = fixture.Index(check);
            fixture.Started[index].SetResult();
            try
            {
                await fixture.Release[index].Task.WaitAsync(token);
                return (check, false);
            }
            finally
            {
                finished.Enqueue(check);
            }
        });

        await Observe(fixture.Started[1].Task, "started groups before caller cancellation");
        fixture.Cancellation.Cancel();
        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Results());
        Assert.Equal(fixture.Cancellation.Token, thrown.CancellationToken);
        Assert.Equal(new[] { "a", "b" }, finished.Order().ToArray());
        Assert.False(fixture.Started[2].Task.IsCompleted);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Observe(Task task, string expectedEvent)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Hang guard: did not observe {expectedEvent}.");
        }
    }

    private sealed class RunFixture(params string[] checks) : IAsyncDisposable
    {
        internal readonly TaskCompletionSource Build = Gate();
        internal readonly TaskCompletionSource[] Started = checks.Select(_ => Gate()).ToArray();
        internal readonly TaskCompletionSource[] Release = checks.Select(_ => Gate()).ToArray();
        internal readonly CancellationTokenSource Cancellation = new();
        internal bool PrebuildPassed = true;
        internal int PrebuildReads;
        internal Task<IReadOnlyList<string>>? Run;

        internal int Index(string check) => Array.IndexOf(checks, check);

        internal void Start(
            Func<string, CancellationToken, Task<(string Part, bool StopBatch)>>? single = null,
            Func<AcceptanceDotnetTestBatchGroup<string>, SemaphoreSlim, CancellationToken, Task<string>>? partition = null,
            Func<bool>? invalidated = null)
        {
            Run = AcceptanceDotnetTestBatchScheduler.RunAsync(
                AcceptanceDotnetTestBatchScheduler.Group(checks, check => check.StartsWith('p')),
                2, Build.Task,
                () => { Interlocked.Increment(ref PrebuildReads); return PrebuildPassed; },
                invalidated ?? (() => false),
                partition ?? ((_, _, _) => throw new InvalidOperationException("Unexpected partition group.")),
                single ?? RunSingle, Cancellation.Token);
        }

        private async Task<(string Part, bool StopBatch)> RunSingle(string check, CancellationToken token)
        {
            var index = Index(check);
            Started[index].SetResult();
            await Release[index].Task.WaitAsync(token);
            return (check, false);
        }

        internal async Task<IReadOnlyList<string>> Results()
        {
            await Observe(Run!, "batch completion after draining");
            return await Run!;
        }

        internal void ReleaseAll()
        {
            foreach (var gate in Release)
                gate.TrySetResult();
        }

        public async ValueTask DisposeAsync()
        {
            Cancellation.Cancel();
            Build.TrySetResult();
            ReleaseAll();
            try
            {
                if (Run is not null)
                    await Observe(Run, "fixture cleanup draining started groups");
            }
            catch (Exception) when (Run?.IsCompleted == true)
            {
                // Outcome assertions belong to the test; cleanup drains faults as well as successes.
            }
            finally
            {
                Cancellation.Dispose();
            }
        }
    }
}
