using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique attempt folders and injected process identity, launch and liveness.
public sealed class ConductorOwnLaunchLedgerTests
{
    [Fact]
    public void SuccessfulLaunch_IsRememberedOnlyByLaunchingInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"own-launch-{Guid.NewGuid():N}");
        try
        {
            var first = Coordinator(root, _ => new(222, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe"));
            var attempt = first.Create("train", [], "main", "tree", "manifest", "identity", root,
                ConductorAutonomyPolicy.Permissive);
            Assert.False(first.LaunchedByThisInstance(attempt));

            var launched = first.Launch(attempt);

            Assert.True(first.LaunchedByThisInstance(launched));
            Assert.True(first.LaunchedByThisInstance(ConductorGroupedGateAttemptCoordinator.Read(attempt.MetadataPath)));
            Assert.False(first.ShouldAdopt(launched));
            var second = Coordinator(root, _ => throw new InvalidOperationException("Unexpected launch"));
            Assert.Single(second.ReadAll());
            Assert.False(second.LaunchedByThisInstance(launched));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedLaunch_IsNotRemembered(bool missingProcessId)
    {
        var root = Path.Combine(Path.GetTempPath(), $"failed-own-launch-{Guid.NewGuid():N}");
        try
        {
            var coordinator = Coordinator(root, _ => missingProcessId
                ? new(0, null, null) : throw new InvalidOperationException("injected launch failure"));
            var attempt = coordinator.Create("train", [], "main", "tree", "manifest", "identity", root,
                ConductorAutonomyPolicy.Permissive);

            Assert.Throws<InvalidOperationException>(() => coordinator.Launch(attempt));

            var failed = Assert.Single(coordinator.ReadAll());
            Assert.Equal("LaunchFailed", failed.Outcome);
            Assert.NotNull(failed.ReconciledAt);
            Assert.False(coordinator.LaunchedByThisInstance(failed));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static ConductorGroupedGateAttemptCoordinator Coordinator(string root,
        Func<ConductorGroupedGateAttempt, ConductorGroupedGateLaunchResult> launch) => new(root,
            generationId: 111, launch: launch, isProcessAlive: pid => pid == 222,
            stop: _ => throw new InvalidOperationException("Unexpected stop"),
            generationIdentity: new SpawnProcessIdentity(111, DateTimeOffset.UnixEpoch, "C:\\dotnet.exe"));
}
