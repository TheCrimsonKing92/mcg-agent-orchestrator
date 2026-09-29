internal static class TestHangGuard
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

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
