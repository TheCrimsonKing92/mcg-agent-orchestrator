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
        var receiptPath = TempRootApparatusLossReceiptStore.ResolvePath(
            Path.Combine(root, "attempt", attemptId))!;
        var apparatusParent = Path.Combine(root, "disposable-owned-parent");
        var candidateRoot = Path.Combine(root, "candidate-worktree");
        var candidateMarker = Path.Combine(candidateRoot, "candidate.txt");
        const string candidateSha = "candidate-sha-before-apparatus-loss";
        Directory.CreateDirectory(apparatusParent);
        Directory.CreateDirectory(candidateRoot);
        File.WriteAllText(candidateMarker, candidateSha);

        var releaseName = $"Local\\mcg-shared-apparatus-release-{Guid.NewGuid():N}";
        var firstReadyName = $"Local\\mcg-shared-apparatus-ready-{Guid.NewGuid():N}";
        var secondReadyName = $"Local\\mcg-shared-apparatus-ready-{Guid.NewGuid():N}";
        var deletedName = $"Local\\mcg-shared-apparatus-deleted-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var firstReady = new EventWaitHandle(false, EventResetMode.ManualReset, firstReadyName);
        using var secondReady = new EventWaitHandle(false, EventResetMode.ManualReset, secondReadyName);
        using var deleted = new EventWaitHandle(false, EventResetMode.ManualReset, deletedName);
        MtpProbeProcess? firstProcess = null;
        MtpProbeProcess? secondProcess = null;
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
                releaseName,
                apparatusParentPath: apparatusParent,
                apparatusDeletionEventName: deletedName);
            secondProcess = AssemblyTempRedirectTests.StartMtpProbe(
                executable,
                testAssembly,
                root,
                Path.Combine(root, "second-probe.json"),
                secondReadyName,
                releaseName,
                apparatusParentPath: apparatusParent,
                apparatusDeletionEventName: deletedName);

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
            Assert.False(string.IsNullOrWhiteSpace(firstProbe.HeadCommit));
            Assert.False(string.IsNullOrWhiteSpace(secondProbe.HeadCommit));
            Assert.Equal(
                firstProbe.HeadCommit,
                InfrastructureTestSupport.TryGetGitHead(firstProbe.FixtureRepositoryPath));
            Assert.Equal(
                secondProbe.HeadCommit,
                InfrastructureTestSupport.TryGetGitHead(secondProbe.FixtureRepositoryPath));
            Assert.Equal(2, new[] { firstProbe.ProcessId, secondProbe.ProcessId }.Distinct().Count());
            var liveInspection = WindowsNativeProcessInspection.Read(
                [firstProbe.ProcessId, secondProbe.ProcessId]);
            Assert.Null(liveInspection.Failure);
            var firstIdentity = Assert.Contains(firstProbe.ProcessId, liveInspection.Records);
            var secondIdentity = Assert.Contains(secondProbe.ProcessId, liveInspection.Records);
            Assert.True(firstIdentity.StartedAt.HasValue);
            Assert.True(secondIdentity.StartedAt.HasValue);
            var firstStartedAt = firstIdentity.StartedAt.Value;
            var secondStartedAt = secondIdentity.StartedAt.Value;
            var sharedRoot = AssertSameSharedRoot(firstProbe, secondProbe);
            Assert.Equal(apparatusParent, sharedRoot, ignoreCase: true);

            TempRootJanitorOwnedParentDeleteResult deletion;
            using (TempRootApparatusLossReceiptStore.PushScope(attemptId, receiptPath))
            {
                deletion = TempRootJanitor.DeleteOwnedParentWithReceipt(apparatusParent);
            }
            Assert.Equal(TempRootJanitorDeleteStatus.Deleted, deletion.DeleteResult.Status);
            Assert.Equal(
                new[] { firstProbe.ProcessId, secondProbe.ProcessId }.Order(),
                deletion.CapturedLiveOwners.Select(owner => owner.OwnerProcessId).Order());
            Assert.False(Directory.Exists(apparatusParent));

            var typedReceipt = Assert.Single(TempRootApparatusLossReceiptStore.Read(receiptPath));
            Assert.Equal(attemptId, typedReceipt.GateInvocationId);
            Assert.Equal(sharedRoot, typedReceipt.SharedRoot, ignoreCase: true);
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
                owner =>
                {
                    Assert.Equal(expectedOwners[owner.OwnerProcessId], owner.OwnerStartedAt);
                    Assert.Equal(
                        TempRootJanitor.BuildOwnedRootPath(sharedRoot, owner.OwnerProcessId),
                        owner.OwnedRootPath,
                        ignoreCase: true);
                });
            deleted.Set();

            var childResults = await Task.WhenAll(
                firstProcess.WaitForExitAsync(TestContext.Current.CancellationToken),
                secondProcess.WaitForExitAsync(TestContext.Current.CancellationToken));
            Assert.All(childResults, result =>
            {
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains(
                    "Seeded repository sentinel lost: RepositoryMissing",
                    result.Stdout + result.Stderr,
                    StringComparison.Ordinal);
            });
            await firstProcess.DisposeAsync();
            await secondProcess.DisposeAsync();
            firstProcess = null;
            secondProcess = null;

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
            Assert.Equal(
                [firstProbe.TempRoot, secondProbe.TempRoot],
                invalidation.AffectedOwners.Select(owner => owner.OwnedRootPath));
            Assert.False(cache.ShouldRerunWithinAttempt(beta, second.CompletionDecision));
            Assert.Equal(
                AcceptanceFailureClassifications.SharedGateApparatusInvalidated,
                second.FailureClassification);
            Assert.Contains(
                System.Text.Json.JsonSerializer.Serialize(firstProbe.TempRoot).Trim('"'),
                second.FailureCauseEvidence?.Evidence,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                System.Text.Json.JsonSerializer.Serialize(secondProbe.TempRoot).Trim('"'),
                second.FailureCauseEvidence?.Evidence,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(candidateSha, cache.CandidateTreeSha);
            Assert.Equal(candidateSha, File.ReadAllText(candidateMarker));
            Assert.True(Directory.Exists(candidateRoot));

        }
        finally
        {
            release.Set();
            deleted.Set();
            if (firstProcess is not null)
            {
                await firstProcess.DisposeAsync();
            }
            if (secondProcess is not null)
            {
                await secondProcess.DisposeAsync();
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
        var apparatusParent = Path.Combine(root, "disposable-owned-parent");
        Directory.CreateDirectory(apparatusParent);
        var releaseName = $"Local\\mcg-isolated-apparatus-release-{Guid.NewGuid():N}";
        var readyName = $"Local\\mcg-isolated-apparatus-ready-{Guid.NewGuid():N}";
        var deletedName = $"Local\\mcg-isolated-apparatus-deleted-{Guid.NewGuid():N}";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        using var deleted = new EventWaitHandle(false, EventResetMode.ManualReset, deletedName);
        MtpProbeProcess? process = null;
        try
        {
            process = AssemblyTempRedirectTests.StartMtpProbe(
                InfrastructureTestSupport.ResolveDotnetHostPath(),
                typeof(AssemblyTempRedirectTests).Assembly.Location,
                root,
                Path.Combine(root, "isolated-probe.json"),
                readyName,
                releaseName,
                apparatusParentPath: apparatusParent,
                apparatusDeletionEventName: deletedName);
            var probe = await AssemblyTempRedirectTests.WaitForProbeReceiptAsync(
                "isolated",
                process,
                ready,
                TestContext.Current.CancellationToken);
            Assert.True(Directory.Exists(probe.FixtureRepositoryPath));
            var startedAt = new DateTimeOffset(process.Process.StartTime.ToUniversalTime());

            TempRootJanitorOwnedParentDeleteResult deletion;
            using (TempRootApparatusLossReceiptStore.PushScope(attemptId, receiptPath))
            {
                deletion = TempRootJanitor.DeleteOwnedParentWithReceipt(apparatusParent);
            }
            Assert.Equal(TempRootJanitorDeleteStatus.Deleted, deletion.DeleteResult.Status);
            Assert.Single(deletion.CapturedLiveOwners);
            Assert.False(Directory.Exists(probe.TempRoot));
            deleted.Set();
            var processResult = await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.NotEqual(0, processResult.ExitCode);
            Assert.Contains(
                "Seeded repository sentinel lost: RepositoryMissing",
                processResult.Stdout + processResult.Stderr,
                StringComparison.Ordinal);
            await process.DisposeAsync();
            process = null;

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
            deleted.Set();
            if (process is not null)
            {
                await process.DisposeAsync();
            }
            AssemblyTempRedirectTests.DeleteDirectory(root);
        }
    }

    [Fact]
    public void OwnedParentDeletion_ExactLiveOwnersDeletesAndRecords()
    {
        var parent = CreateRoot("owned-parent-unit-live");
        var startedAt = DateTimeOffset.Parse("2026-09-03T04:00:00Z");
        var first = OwnedProcess(4101, startedAt);
        var second = OwnedProcess(4102, startedAt.AddSeconds(1));
        Directory.CreateDirectory(TempRootJanitor.BuildOwnedRootPath(parent, first.ProcessId));
        Directory.CreateDirectory(TempRootJanitor.BuildOwnedRootPath(parent, second.ProcessId));
        IReadOnlyList<TempRootApparatusDestroyedOwner>? recordedOwners = null;
        try
        {
            var result = TempRootJanitor.DeleteOwnedParentWithReceipt(
                parent,
                _ => Inspected(first, second),
                TempRootJanitor.DeleteTree,
                (_, owners) => recordedOwners = owners.ToArray());

            Assert.Equal(TempRootJanitorDeleteStatus.Deleted, result.DeleteResult.Status);
            Assert.Equal([4101, 4102], result.CapturedLiveOwners.Select(owner => owner.OwnerProcessId));
            Assert.Equal([4101, 4102], Assert.IsAssignableFrom<IReadOnlyList<TempRootApparatusDestroyedOwner>>(recordedOwners)
                .Select(owner => owner.OwnerProcessId));
            Assert.False(Directory.Exists(parent));
        }
        finally
        {
            AssemblyTempRedirectTests.DeleteDirectory(parent);
        }
    }

    [Fact]
    public void OwnedParentDeletion_ExitedOwnerRefusesDeletionAndReceipt()
    {
        var parent = CreateRoot("owned-parent-unit-exited");
        var startedAt = DateTimeOffset.Parse("2026-09-03T04:00:00Z");
        var first = OwnedProcess(4201, startedAt);
        var second = OwnedProcess(4202, startedAt.AddSeconds(1));
        Directory.CreateDirectory(TempRootJanitor.BuildOwnedRootPath(parent, first.ProcessId));
        Directory.CreateDirectory(TempRootJanitor.BuildOwnedRootPath(parent, second.ProcessId));
        var deleteCalls = 0;
        var receiptCalls = 0;
        try
        {
            var result = TempRootJanitor.DeleteOwnedParentWithReceipt(
                parent,
                _ => Inspected(first, second with
                {
                    Status = ProcessInspectionStatus.Exited,
                    StartedAt = null
                }),
                path =>
                {
                    deleteCalls++;
                    return TempRootJanitor.DeleteTree(path);
                },
                (_, _) => receiptCalls++);

            Assert.Equal(TempRootJanitorDeleteStatus.Failed, result.DeleteResult.Status);
            Assert.Equal(0, deleteCalls);
            Assert.Equal(0, receiptCalls);
            Assert.True(Directory.Exists(parent));
        }
        finally
        {
            AssemblyTempRedirectTests.DeleteDirectory(parent);
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

    private static ProcessInspectionRecord OwnedProcess(int processId, DateTimeOffset startedAt) =>
        new(
            processId,
            ParentProcessId: Environment.ProcessId,
            Name: "testhost",
            ExecutablePath: Environment.ProcessPath,
            StartedAt: startedAt,
            CommandLine: "managed owned-parent control",
            ProcessInspectionStatus.Available);

    private static WindowsNativeProcessInspection.ProcessInspectionResult Inspected(
        params ProcessInspectionRecord[] records) =>
        WindowsNativeProcessInspection.ProcessInspectionResult.Success(
            records.ToDictionary(record => record.ProcessId));

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
