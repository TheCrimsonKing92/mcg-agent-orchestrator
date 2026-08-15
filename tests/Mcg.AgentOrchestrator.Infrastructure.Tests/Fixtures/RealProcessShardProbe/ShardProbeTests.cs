public sealed class ShardProbeAlphaTests
{
    [Xunit.Fact]
    public Task SynchronizesWithBetaShard() =>
        ShardProbeSynchronization.SynchronizeAsync(
            "MCG_SHARD_SMOKE_ALPHA_SIGNAL",
            "MCG_SHARD_SMOKE_BETA_SIGNAL");
}

public sealed class ShardProbeBetaTests
{
    [Xunit.Fact]
    public Task SynchronizesWithAlphaShard() =>
        ShardProbeSynchronization.SynchronizeAsync(
            "MCG_SHARD_SMOKE_BETA_SIGNAL",
            "MCG_SHARD_SMOKE_ALPHA_SIGNAL");
}

internal static class ShardProbeSynchronization
{
    internal static async Task SynchronizeAsync(string ownSignalVariable, string peerSignalVariable)
    {
        var ownSignalPath = Environment.GetEnvironmentVariable(ownSignalVariable);
        if (string.IsNullOrWhiteSpace(ownSignalPath))
        {
            return;
        }

        var peerSignalPath = Environment.GetEnvironmentVariable(peerSignalVariable);
        Xunit.Assert.False(string.IsNullOrWhiteSpace(peerSignalPath));

        File.WriteAllText(ownSignalPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await WaitForSignalAsync(peerSignalPath!);
    }

    private static async Task WaitForSignalAsync(string signalPath)
    {
        if (File.Exists(signalPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(signalPath)
            ?? throw new InvalidOperationException($"Signal path has no directory: '{signalPath}'.");
        using var watcher = new FileSystemWatcher(directory, Path.GetFileName(signalPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite
        };
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FileSystemEventHandler onSignal = (_, _) => observed.TrySetResult();
        watcher.Created += onSignal;
        watcher.Changed += onSignal;
        try
        {
            watcher.EnableRaisingEvents = true;
            if (!File.Exists(signalPath))
            {
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        catch (TimeoutException)
        {
            Xunit.Assert.True(File.Exists(signalPath), $"Peer shard did not signal '{signalPath}'.");
        }
        finally
        {
            watcher.Created -= onSignal;
            watcher.Changed -= onSignal;
        }
    }
}
