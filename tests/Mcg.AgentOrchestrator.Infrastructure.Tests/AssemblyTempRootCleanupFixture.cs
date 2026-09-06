[assembly: Xunit.AssemblyFixture(typeof(AssemblyTempRootCleanupFixture))]

public sealed class AssemblyTempRootCleanupFixture : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        var outcome = AssemblyTempRedirect.ReleaseOwnedRoot(AssemblyTempRootCleanupOwner.AssemblyFixture);
        EnsureSuccessful(outcome);
        return ValueTask.CompletedTask;
    }

    internal static void EnsureSuccessful(TempRootDeleteOutcome? outcome)
    {
        if (outcome?.Status != TempRootDeleteStatus.Failed)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assembly temp-root cleanup failed for '{outcome.Path}' " +
            $"({outcome.ExceptionType ?? "unknown"} at '{outcome.FailurePath ?? "unknown"}').");
    }
}

internal enum AssemblyTempRootCleanupOwner
{
    AssemblyFixture,
    ProcessExitFallback
}
