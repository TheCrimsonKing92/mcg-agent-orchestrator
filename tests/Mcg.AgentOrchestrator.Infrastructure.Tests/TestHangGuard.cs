internal static class TestHangGuard
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan HangSafetyBound = TimeSpan.FromSeconds(120);

    internal static async Task CompletesWithinAsync(Task task, TimeSpan bound, string operation)
    {
        using var expiryCancellation = new CancellationTokenSource();
        var expiry = Task.Delay(bound, expiryCancellation.Token);
        // Wait for a terminal state without observing the task's own fault.
        if (await Task.WhenAny(task, expiry) == expiry)
        {
            throw new TestHangGuardExpiredException(
                $"Test hang-safety bound of {bound} expired: {operation} did not reach a terminal state.");
        }

        expiryCancellation.Cancel();
    }

    internal static async Task WaitAsync(Task task, string eventName)
    {
        try { await task.WaitAsync(Bound); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Hang guard: {eventName} did not complete within {Bound}.", ex);
        }
    }

    internal static async Task<T> WaitAsync<T>(Task<T> task, string eventName)
    {
        try { return await task.WaitAsync(Bound); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"Hang guard: {eventName} did not complete within {Bound}.", ex);
        }
    }
}

internal sealed class TestHangGuardExpiredException(string message) : Exception(message);
