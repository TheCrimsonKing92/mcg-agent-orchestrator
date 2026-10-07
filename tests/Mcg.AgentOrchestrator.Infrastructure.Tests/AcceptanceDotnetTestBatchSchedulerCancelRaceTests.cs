using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every scheduler, cancellation source and event gate belongs to one test.
public sealed class AcceptanceDotnetTestBatchSchedulerCancelRaceTests
{
    [Fact]
    public async Task SlotFreedDuringCancellation_SkipsWaitingGroupAndDrainsStartedGroups()
    {
        using var caller = new CancellationTokenSource();
        var invoked = new ConcurrentQueue<string>();
        // Inline continuations make a release its slot inside the cancellation callback,
        // before the older callback for c's slot wait can remove that waiter.
        var aCompletion = new TaskCompletionSource<(string Part, bool StopBatch)>();
        var bCompletion = new TaskCompletionSource<(string Part, bool StopBatch)>();
        CancellationToken stopToken = default;
        var releasedDuringCancellation = false;

        Task<(string Part, bool StopBatch)> RunSingle(string check, CancellationToken token)
        {
            invoked.Enqueue(check);
            switch (check)
            {
                case "a":
                    stopToken = token;
                    return aCompletion.Task;
                case "b":
                    return bCompletion.Task;
                case "c":
                    // The unfixed scheduler must finish and fail the assertion, not hang.
                    return Task.FromCanceled<(string Part, bool StopBatch)>(token);
                default:
                    throw new InvalidOperationException($"Unexpected check: {check}.");
            }
        }

        // A completed build keeps startup synchronous: a and b hold both slots,
        // and c has registered its slot wait before RunAsync returns.
        var run = AcceptanceDotnetTestBatchScheduler.RunAsync(
            AcceptanceDotnetTestBatchScheduler.Group(new[] { "a", "b", "c" }, _ => false),
            2, Task.CompletedTask, () => true, () => false,
            (_, _, _) => throw new InvalidOperationException("Unexpected partition group."),
            RunSingle, caller.Token);

        try
        {
            Assert.Equal(new[] { "a", "b" }, invoked.ToArray());
            Assert.False(aCompletion.Task.IsCompleted);
            Assert.False(bCompletion.Task.IsCompleted);
            Assert.False(run.IsCompleted);

            // Cancellation callbacks run newest first. Register only after c is waiting;
            // completing a inline grants c the slot while stop is already cancelled.
            using var releaseSlot = stopToken.Register(() =>
            {
                releasedDuringCancellation = caller.IsCancellationRequested;
                aCompletion.TrySetCanceled(stopToken);
            });
            caller.Cancel();

            Assert.True(releasedDuringCancellation);
            Assert.True(aCompletion.Task.IsCanceled);
            Assert.False(bCompletion.Task.IsCompleted);
            Assert.False(run.IsCompleted); // Caller cancellation must still drain b.
            bCompletion.SetCanceled(stopToken);

            var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Observe(run, "caller cancellation after draining started groups"));
            Assert.Equal(caller.Token, thrown.CancellationToken);
            Assert.Equal(new[] { "a", "b" }, invoked.ToArray());
            Assert.DoesNotContain("c", invoked);
        }
        finally
        {
            caller.Cancel();
            aCompletion.TrySetCanceled(caller.Token);
            bCompletion.TrySetCanceled(caller.Token);
            try
            {
                await Observe(run, "cleanup draining started groups");
            }
            catch (Exception) when (run.IsCompleted)
            {
                // Outcome assertions belong to the test; cleanup also drains faults.
            }
        }
    }

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
}
