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
            $"({outcome.ExceptionType ?? "unknown"}, " +
            $"hresult={FormatHResult(outcome.ExceptionHResult)}, " +
            $"attempts={outcome.DeleteAttempts}, " +
            $"at '{outcome.FailurePath ?? "unknown"}'): " +
            $"{outcome.ExceptionMessage ?? "message unavailable"}");
    }

    private static string FormatHResult(int? hresult) =>
        hresult is null ? "unknown" : $"0x{unchecked((uint)hresult.Value):X8}";
}

internal enum AssemblyTempRootCleanupOwner
{
    AssemblyFixture,
    ProcessExitFallback
}
