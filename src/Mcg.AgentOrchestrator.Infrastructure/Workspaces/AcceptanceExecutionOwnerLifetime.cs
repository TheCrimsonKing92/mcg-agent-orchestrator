namespace Mcg.AgentOrchestrator.Infrastructure;

public static class AcceptanceExecutionOwnerLifetime
{
    private const string TeardownFailureKey = "acceptance-execution-owner-teardown-failure";

    public static T Run<T>(IAsyncDisposable owner, Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(operation);
        Exception? executionFailure = null;
        try { return operation(); }
        catch (Exception exception) { executionFailure = exception; throw; }
        finally { DisposePreservingExecutionFailure(owner, executionFailure); }
    }

    public static async Task<T> RunAsync<T>(IAsyncDisposable owner, Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(operation);
        Exception? executionFailure = null;
        try { return await operation().ConfigureAwait(false); }
        catch (Exception exception) { executionFailure = exception; throw; }
        finally
        {
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception teardownFailure) when (executionFailure is not null)
            {
                executionFailure.Data[TeardownFailureKey] = teardownFailure.ToString();
            }
        }
    }

    private static void DisposePreservingExecutionFailure(IAsyncDisposable owner, Exception? executionFailure)
    {
        try { owner.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception teardownFailure) when (executionFailure is not null)
        {
            executionFailure.Data[TeardownFailureKey] = teardownFailure.ToString();
        }
    }
}
