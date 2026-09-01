using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierCancellationTests : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact(DisplayName = "Acceptance disposition cancellation interrupts the active check process")]
    public async Task AcceptanceDispositionCancellationInterruptsActiveCheck()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [
                { "name": "blocking command", "type": "command", "command": "blocking-tool", "arguments": [] }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var checkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRequested = 0;
        try
        {
            var verifier = new GoalAcceptanceVerifier(async (arguments, _, cancellationToken) =>
            {
                if (arguments.SequenceEqual(["dotnet", "build-server", "shutdown"]))
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, string.Empty);
                }

                checkStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new GoalAcceptanceVerifier.CommandResult(0, string.Empty);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            });
            using var probe = GoalAcceptanceVerifier.PushGateCancellationProbe(
                () => Volatile.Read(ref stopRequested) == 1);
            var verification = verifier.RunAsync(root);
            await checkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var elapsed = Stopwatch.StartNew();
            Volatile.Write(ref stopRequested, 1);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => verification.WaitAsync(TimeSpan.FromSeconds(5)));
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(2),
                $"Active check cancellation took {elapsed.Elapsed.TotalSeconds:F2}s.");
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }
}
