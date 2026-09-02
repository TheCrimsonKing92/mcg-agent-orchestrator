using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class AcceptanceSharedApparatusRealProcessControlTests
{
    [Fact(Timeout = 90_000)]
    public async Task CorrelatedRootLoss_StopsRetriesAndPreservesCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("correlated");
        var attemptId = $"real-process-{Guid.NewGuid():N}";
        var attemptPrefix = Path.Combine(root, "attempt", attemptId);
        var receiptPath = TempRootApparatusLossReceiptStore.ResolvePath(attemptPrefix)!;
        var candidateRoot = Path.Combine(root, "candidate-worktree");
        var candidateMarker = Path.Combine(candidateRoot, "candidate.txt");
        const string candidateSha = "candidate-sha-before-apparatus-loss";
        Directory.CreateDirectory(candidateRoot);
        File.WriteAllText(candidateMarker, candidateSha);

        var releaseName = $"Local\\mcg-shared-apparatus-release-{Guid.NewGuid():N}";
        var firstReadyName = $"Local\\mcg-shared-apparatus-ready-{Guid.NewGuid():N}";
        var secondReadyName = $"Local\\mcg-shared-apparatus-ready-{Guid.NewGuid():N}";
        var reaperReadyName = $"Local\\mcg-shared-apparatus-reaper-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var firstReady = new EventWaitHandle(false, EventResetMode.ManualReset, firstReadyName);
        using var secondReady = new EventWaitHandle(false, EventResetMode.ManualReset, secondReadyName);
        using var reaperReady = new EventWaitHandle(false, EventResetMode.ManualReset, reaperReadyName);
        MtpProbeProcess? firstProcess = null;
        MtpProbeProcess? secondProcess = null;
        MtpProbeProcess? reaperProcess = null;
        try
        {
            var executable = InfrastructureTestSupport.ResolveDotnetHostPath();
            var testAssembly = typeof(AssemblyTempRedirectTests).Assembly.Location;
            firstProcess = AssemblyTempRedirectTests.StartMtpProbe(
                executable,
                testAssembly,
                root,
                Path.Combine(root, "first-probe.json"),
                firstReadyName,
                releaseName);
            secondProcess = AssemblyTempRedirectTests.StartMtpProbe(
                executable,
                testAssembly,
                root,
                Path.Combine(root, "second-probe.json"),
                secondReadyName,
                releaseName);

            var firstReceiptTask = AssemblyTempRedirectTests.WaitForProbeReceiptAsync(
                "first",
                firstProcess,
                firstReady,
                TestContext.Current.CancellationToken);
            var secondReceiptTask = AssemblyTempRedirectTests.WaitForProbeReceiptAsync(
                "second",
                secondProcess,
                secondReady,
                TestContext.Current.CancellationToken);
            var firstProbe = await firstReceiptTask;
            var secondProbe = await secondReceiptTask;
            Assert.True(Directory.Exists(firstProbe.FixtureRepositoryPath));
            Assert.True(Directory.Exists(secondProbe.FixtureRepositoryPath));
            Assert.Equal(2, new[] { firstProbe.ProcessId, secondProbe.ProcessId }.Distinct().Count());
            var firstStartedAt = new DateTimeOffset(firstProcess.Process.StartTime.ToUniversalTime());
            var secondStartedAt = new DateTimeOffset(secondProcess.Process.StartTime.ToUniversalTime());
            var sharedRoot = AssertSameSharedRoot(firstProbe, secondProbe);

            firstProcess.Process.Kill(entireProcessTree: true);
            secondProcess.Process.Kill(entireProcessTree: true);
            _ = await Task.WhenAll(
                firstProcess.WaitForExitAsync(TestContext.Current.CancellationToken),
                secondProcess.WaitForExitAsync(TestContext.Current.CancellationToken));
            await firstProcess.DisposeAsync();
            await secondProcess.DisposeAsync();
            firstProcess = null;
            secondProcess = null;

            reaperProcess = AssemblyTempRedirectTests.StartMtpProbe(
                executable,
                testAssembly,
                root,
                Path.Combine(root, "reaper-probe.json"),
                reaperReadyName,
                releaseName,
                attemptId,
                receiptPath);
            var reaperProbe = await AssemblyTempRedirectTests.WaitForProbeReceiptAsync(
                "reaper",
                reaperProcess,
                reaperReady,
                TestContext.Current.CancellationToken);
            Assert.Equal(sharedRoot, Path.GetDirectoryName(reaperProbe.TempRoot), ignoreCase: true);
            Assert.False(Directory.Exists(firstProbe.TempRoot));
            Assert.False(Directory.Exists(secondProbe.TempRoot));
            release.Set();
            _ = await reaperProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
            await reaperProcess.DisposeAsync();
            reaperProcess = null;

            var alpha = Partition("Alpha");
            var beta = Partition("Beta");
            var cache = CreateCache(
                candidateRoot,
                attemptId,
                candidateSha,
                [alpha, beta],
                receiptPath);
            var first = cache.ObserveSharedApparatusEvidence(
                alpha,
                FailedOwner(alpha.Name, firstProbe.ProcessId, firstStartedAt));
            Assert.True(cache.ShouldRerunWithinAttempt(alpha, first.CompletionDecision));
            var second = cache.ObserveSharedApparatusEvidence(
                beta,
                FailedOwner(beta.Name, secondProbe.ProcessId, secondStartedAt));

            var invalidation = Assert.IsType<AcceptanceSharedApparatusInvalidation>(
                cache.SharedApparatusInvalidation);
            Assert.Equal(attemptId, invalidation.FirstReceipt.GateInvocationId);
            Assert.Equal(["alpha", "beta"], invalidation.AffectedOwners.Select(owner => owner.PartitionId));
            Assert.False(cache.ShouldRerunWithinAttempt(beta, second.CompletionDecision));
            Assert.Equal(
                AcceptanceFailureClassifications.SharedGateApparatusInvalidated,
                second.FailureClassification);
            Assert.Equal(candidateSha, cache.CandidateTreeSha);
            Assert.Equal(candidateSha, File.ReadAllText(candidateMarker));
            Assert.True(Directory.Exists(candidateRoot));
            Assert.False(Directory.Exists(firstProbe.TempRoot));
            Assert.False(Directory.Exists(secondProbe.TempRoot));

            var typedReceipt = Assert.Single(TempRootApparatusLossReceiptStore.Read(receiptPath));
            var expectedOwners = new Dictionary<int, DateTimeOffset>
            {
                [firstProbe.ProcessId] = firstStartedAt,
                [secondProbe.ProcessId] = secondStartedAt
            };
            Assert.Equal(
                expectedOwners.Keys.Order(),
                typedReceipt.DestroyedOwners.Select(owner => owner.OwnerProcessId).Order());
            Assert.All(
                typedReceipt.DestroyedOwners,
                owner => Assert.Equal(expectedOwners[owner.OwnerProcessId], owner.OwnerStartedAt));
        }
        finally
        {
            release.Set();
            if (firstProcess is not null)
            {
                await firstProcess.DisposeAsync();
            }
            if (secondProcess is not null)
            {
                await secondProcess.DisposeAsync();
            }
            if (reaperProcess is not null)
            {
                await reaperProcess.DisposeAsync();
            }
            AssemblyTempRedirectTests.DeleteDirectory(root);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task IsolatedRootLoss_RemainsCandidateFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot("isolated");
        var attemptId = $"isolated-{Guid.NewGuid():N}";
        var receiptPath = TempRootApparatusLossReceiptStore.ResolvePath(
            Path.Combine(root, "attempt", attemptId))!;
        var releaseName = $"Local\\mcg-isolated-apparatus-release-{Guid.NewGuid():N}";
        var readyName = $"Local\\mcg-isolated-apparatus-ready-{Guid.NewGuid():N}";
        var reaperReadyName = $"Local\\mcg-isolated-apparatus-reaper-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var reaperReady = new EventWaitHandle(false, EventResetMode.ManualReset, reaperReadyName);
        MtpProbeProcess? process = null;
        MtpProbeProcess? reaperProcess = null;
        try
        {
            process = AssemblyTempRedirectTests.StartMtpProbe(
                InfrastructureTestSupport.ResolveDotnetHostPath(),
                typeof(AssemblyTempRedirectTests).Assembly.Location,
                root,
                Path.Combine(root, "isolated-probe.json"),
                readyName,
                releaseName);
            var probe = await AssemblyTempRedirectTests.WaitForProbeReceiptAsync(
                "isolated",
                process,
                ready,
                TestContext.Current.CancellationToken);
            Assert.True(Directory.Exists(probe.FixtureRepositoryPath));
            var startedAt = new DateTimeOffset(process.Process.StartTime.ToUniversalTime());
            process.Process.Kill(entireProcessTree: true);
            _ = await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await process.DisposeAsync();
            process = null;

            reaperProcess = AssemblyTempRedirectTests.StartMtpProbe(
                InfrastructureTestSupport.ResolveDotnetHostPath(),
                typeof(AssemblyTempRedirectTests).Assembly.Location,
                root,
                Path.Combine(root, "isolated-reaper-probe.json"),
                reaperReadyName,
                releaseName,
                attemptId,
                receiptPath);
            _ = await AssemblyTempRedirectTests.WaitForProbeReceiptAsync(
                "isolated reaper",
                reaperProcess,
                reaperReady,
                TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(probe.TempRoot));
            release.Set();
            _ = await reaperProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
            await reaperProcess.DisposeAsync();
            reaperProcess = null;

            var partition = Partition("Isolated");
            var cache = CreateCache(root, attemptId, "isolated-candidate", [partition], receiptPath);
            var failure = cache.ObserveSharedApparatusEvidence(
                partition,
                FailedOwner(partition.Name, probe.ProcessId, startedAt));

            Assert.Empty(TempRootApparatusLossReceiptStore.Read(receiptPath));
            Assert.Null(cache.SharedApparatusInvalidation);
            Assert.Null(failure.FailureCauseEvidence);
            Assert.True(cache.ShouldRerunWithinAttempt(partition, failure.CompletionDecision));
        }
        finally
        {
            release.Set();
            if (process is not null)
            {
                await process.DisposeAsync();
            }
            if (reaperProcess is not null)
            {
                await reaperProcess.DisposeAsync();
            }
            AssemblyTempRedirectTests.DeleteDirectory(root);
        }
    }

    private static AcceptancePartitionVerdictCache CreateCache(
        string worktreePath,
        string attemptId,
        string candidateSha,
        IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> checks,
        string receiptPath) =>
        Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                new GoalId("6f9ddf547adb404187dc00863bb749c1"),
                worktreePath,
                checks,
                FullRerunEveryN: 3,
                WithinAttemptRerunEnabled: true,
                _ => candidateSha,
                _ => "main-sha",
                _ => "verifying-sha",
                () => attemptId,
                () => "real-process-control-manifest",
                () => true,
                () => TempRootApparatusLossReceiptStore.Read(receiptPath))));

    private static GoalAcceptanceVerifier.AcceptanceManifestCheck Partition(string id) => new()
    {
        Name = $"infrastructure tests: {id}",
        Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", $"FullyQualifiedName~{id}"]
    };

    private static AcceptanceCheckResult FailedOwner(
        string name,
        int processId,
        DateTimeOffset startedAt) =>
        new(
            name,
            false,
            1,
            "sentinel repository lost",
            CompletionDecision: new AcceptanceShardCompletionDecision(
                false,
                AcceptanceShardCompletionPredicates.NonzeroExit,
                false,
                1,
                1,
                1,
                "failed"),
            ChildProcessId: processId,
            ChildProcessStartedAt: startedAt);

    private static string AssertSameSharedRoot(TempRootProbeReceipt first, TempRootProbeReceipt second)
    {
        var firstRoot = Path.GetDirectoryName(first.TempRoot)!;
        var secondRoot = Path.GetDirectoryName(second.TempRoot)!;
        Assert.Equal(firstRoot, secondRoot, ignoreCase: true);
        return firstRoot;
    }

    private static string CreateRoot(string arm)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "mcg-shared-apparatus-control",
            arm,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
