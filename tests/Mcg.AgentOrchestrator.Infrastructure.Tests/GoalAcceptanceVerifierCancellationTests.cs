using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierCancellationTests : GoalAcceptanceVerifierTestBase
{
    private const string InfrastructureProject =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";

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
            var verification = verifier.RunOwnedAsync(
                root, null, null, null, null, CancellationToken.None,
                new AcceptanceRunExecutionOptions(
                    CancellationProbe: () => Volatile.Read(ref stopRequested) == 1));
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

    [Xunit.Fact(DisplayName = "Acceptance disposition cancellation interrupts the shared MTP prebuild")]
    public async Task AcceptanceDispositionCancellationInterruptsSharedMtpPrebuild()
    {
        var root = CreatePartitionedMtpManifestWorkspace();
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRequested = 0;
        try
        {
            var verifier = new GoalAcceptanceVerifier(async (arguments, _, cancellationToken) =>
            {
                if (IsInfrastructureBuild(arguments))
                {
                    buildStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved.TrySetResult();
                        throw;
                    }
                }

                return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
            });
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var verification = verifier.RunOwnedAsync(
                root,
                goalId: null,
                changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs"],
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease,
                cancellationToken: CancellationToken.None,
                executionOptions: new AcceptanceRunExecutionOptions(
                    CancellationProbe: () => Volatile.Read(ref stopRequested) == 1));
            await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var elapsed = Stopwatch.StartNew();
            Volatile.Write(ref stopRequested, 1);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => verification.WaitAsync(TimeSpan.FromSeconds(5)));
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(2),
                $"Shared prebuild cancellation took {elapsed.Elapsed.TotalSeconds:F2}s.");
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    [Xunit.Fact(DisplayName = "Caller cancellation interrupts the shared MTP prebuild without a disposition stop")]
    public async Task CallerCancellationInterruptsSharedMtpPrebuildWithoutDispositionStop()
    {
        var root = CreatePartitionedMtpManifestWorkspace();
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispositionProbeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCancellation = new CancellationTokenSource();
        var dispositionProbeCalls = 0;
        try
        {
            var verifier = new GoalAcceptanceVerifier(async (arguments, _, cancellationToken) =>
            {
                if (IsInfrastructureBuild(arguments))
                {
                    buildStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        cancellationObserved.TrySetResult();
                        throw;
                    }
                }

                return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
            });
            Func<bool> dispositionProbe = () =>
            {
                Interlocked.Increment(ref dispositionProbeCalls);
                dispositionProbeObserved.TrySetResult();
                return false;
            };
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var verification = verifier.RunOwnedAsync(
                root,
                goalId: null,
                changedFiles: ["src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs"],
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease,
                cancellationToken: callerCancellation.Token,
                executionOptions: new AcceptanceRunExecutionOptions(CancellationProbe: dispositionProbe));
            await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await dispositionProbeObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

            callerCancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => verification.WaitAsync(TimeSpan.FromSeconds(5)));
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(Volatile.Read(ref dispositionProbeCalls) > 0);
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static string CreatePartitionedMtpManifestWorkspace() =>
        CreateManifestWorkspace("""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 2,
                "infrastructureTestLanes": [
                  { "name": "Cancellation", "filter": "FullyQualifiedName~CancellationTests" },
                  { "name": "Remainder", "filter": "FullyQualifiedName!~CancellationTests" }
                ]
              },
              "checks": [
                {
                  "name": "infrastructure tests",
                  "type": "dotnet-test",
                  "runner": "mtp",
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "arguments": ["--verbosity", "minimal"]
                }
              ],
              "forbiddenChangedPathGlobs": []
            }
            """);

    private static bool IsInfrastructureBuild(string[] arguments) =>
        arguments.Length >= 3 &&
        arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
        arguments[1].Equals("build", StringComparison.OrdinalIgnoreCase) &&
        arguments.Contains(InfrastructureProject, StringComparer.OrdinalIgnoreCase);

    private static int StableSlotIndex(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            path,
            @"(?:slot-|build-)(?<slot>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success
            ? int.Parse(match.Groups["slot"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Expected build-pool path, got '{path}'.");
    }
}
