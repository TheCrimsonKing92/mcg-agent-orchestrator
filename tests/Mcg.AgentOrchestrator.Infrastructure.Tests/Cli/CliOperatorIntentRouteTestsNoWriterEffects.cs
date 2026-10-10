using System.Security.Cryptography;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each test owns its workspace and probe; console capture is async-local.
public sealed class CliOperatorIntentRouteTestsNoWriterEffects : CliTaskQueryTestSupport
{
    [Theory]
    [InlineData(OperatorIntentStatus.Pending, false, 0)]
    [InlineData(OperatorIntentStatus.Claimed, false, 0)]
    [InlineData(OperatorIntentStatus.Applied, false, 0)]
    [InlineData(OperatorIntentStatus.Rejected, false, 0)]
    [InlineData(OperatorIntentStatus.Applied, true, 0)]
    [InlineData(OperatorIntentStatus.Rejected, true, 2)]
    public async Task StatusDoesNotEvenInvokeStateRepositoryFactoryOrModifyWorkspaceFiles(
        OperatorIntentStatus status, bool wait, int expectedCode)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = Seed(workspace);
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            File.WriteAllText(Path.Combine(root, "other-store.db"), "untouched store");
            var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var goal = Assert.Single(kernel.Goals);
            var intent = await store.EnqueueAsync(new OperatorIntentRecord("intent-one", "key", "retry", goal.Id.Value,
                null, "{}", [], "operator", "cli", "local-process", DateTimeOffset.UtcNow));
            if (status != OperatorIntentStatus.Pending)
                Assert.NotNull(await store.ClaimNextAsync(goal.Id.Value, "fixture"));
            if (status is OperatorIntentStatus.Applied or OperatorIntentStatus.Rejected)
                await store.CompleteAsync(intent.Id, "fixture", status, "recorded outcome", DateTimeOffset.UtcNow);
            var probe = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            var factoryCalls = 0;
            var before = Snapshot(root, allowReadSidecars: false);
            string[] args = wait ? ["operator-intent-status", intent.Id, "--wait"] : ["operator-intent-status", intent.Id];
            Assert.True(CliOperatorIntentRoute.IsServed(args));
            var output = CaptureConsole(() => Assert.Equal(expectedCode, CliOperatorIntentRoute.Run(args, workspace, _ =>
            {
                factoryCalls++;
                return probe;
            })));
            var outcome = status is OperatorIntentStatus.Applied or OperatorIntentStatus.Rejected ? "recorded outcome" : "pending";
            Assert.Equal($"Operator intent {intent.Id}: verb=retry goal={goal.Id.Value[..8]} task=none " +
                $"status={status} actor=operator channel=cli auth=local-process outcome={outcome}{Environment.NewLine}", output);
            Assert.Equal(0, factoryCalls);
            Assert.Equal(0, probe.ListGoalMetadataCount);
            Assert.Equal(0, probe.LoadGoalCount);
            Assert.Empty(probe.ObservedWriteOperationTags);
            AssertNoWrites(probe);
            AssertSnapshotsEqual(before, Snapshot(root, allowReadSidecars: false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingIntentsStoreDoesNotCreateAnythingOrOpenState(bool wait)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var before = Snapshot(root, allowReadSidecars: false);
            string[] args = wait ? ["operator-intent-status", "unknown", "--wait"] : ["operator-intent-status", "unknown"];
            var output = CaptureConsole(() =>
            {
                var error = AsyncLocalConsoleRouter.Error.CaptureLocal(() => Assert.Equal(1,
                    CliOperatorIntentRoute.Run(args, workspace,
                        _ => throw new InvalidOperationException("Status must not open state"))));
                Assert.Equal($"KeyNotFoundException: Operator intent 'unknown' was not found.{Environment.NewLine}", error);
            });
            Assert.Empty(output);
            AssertSnapshotsEqual(before, Snapshot(root, allowReadSidecars: false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MultiMapReadsMetadataAndExactlyOneGoalWithoutWriterEffects()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = Seed(workspace);
            var goal = Assert.Single(kernel.Goals);
            var probe = new ProbeStateRepository(kernel) { ThrowOnOutbox = true };
            var calls = 0;
            string[] args = ["criterion-evidence-map", "--goal", goal.Id.Value[..8], "--criterion", "1,2,3,4,5",
                "operator", "observation", "finding", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"];
            Assert.True(CliOperatorIntentRoute.IsServed(args));
            CaptureConsole(() => Assert.Equal(0, CliOperatorIntentRoute.Run(args, workspace, path =>
            {
                Assert.Equal(workspace.SqliteStatePath, path);
                calls++;
                return probe;
            })));
            Assert.Equal(1, calls);
            Assert.Equal(1, probe.ListGoalMetadataCount);
            Assert.Equal(1, probe.LoadGoalCount);
            Assert.All(probe.ObservedWriteOperationTags, tag => Assert.Null(tag));
            AssertNoWrites(probe);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RealMapRoutePreservesStateAndEveryOtherMainFile()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var kernel = Seed(workspace);
            var goal = Assert.Single(kernel.Goals);
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            File.WriteAllText(Path.Combine(root, "other-store.db"), "untouched store");
            var before = Snapshot(root, allowReadSidecars: true);
            CaptureConsole(() => Assert.Equal(0, CliOperatorIntentRoute.Run(
                ["criterion-evidence-map", "--goal", goal.Id.Value[..8], "--criterion", "1,2,3", "operator",
                    "observation", "finding", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"], workspace)));
            AssertSnapshotsEqual(before, Snapshot(root, allowReadSidecars: true));
            var store = SqliteOperatorIntentStore.OpenExisting(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var intents = await store.ListForGoalAsync(goal.Id.Value);
            Assert.Equal(3, intents.Count);
            Assert.Equal(3, intents.Select(intent => intent.Id).Distinct().Count());
            Assert.All(intents, intent => Assert.Equal(intent.Id, intent.IdempotencyKey));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static AgentOrchestratorKernel Seed(OrchestratorWorkspace workspace)
    {
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Intent route goal");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Five criteria",
            Enumerable.Range(1, 5).Select(n => $"Requirement {n}").ToArray(), VerificationClass.TestVerifiable, [], []));
        return kernel;
    }

    private static void AssertNoWrites(ProbeStateRepository probe)
    {
        Assert.Equal(0, probe.FullLoadAttempts);
        Assert.Equal(0, probe.LoadGoalsCount);
        Assert.Equal(0, probe.SaveAttempts);
        Assert.Equal(0, probe.MergeSaveAttempts);
        Assert.Equal(0, probe.MutationAttempts);
        Assert.Equal(0, probe.ListOutboxMessagesCount);
        Assert.Equal(0, probe.OutboxClaimAttempts);
        Assert.Null(SqliteOrchestratorStateRepository.AmbientWriteOperationTag);
    }

    private static Dictionary<string, string> Snapshot(string root, bool allowReadSidecars) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) is not ("operator-intents.db" or "operator-intents.db-wal" or "operator-intents.db-shm"))
            .Where(path => !allowReadSidecars || !(path.EndsWith("state.db-wal", StringComparison.Ordinal) ||
                path.EndsWith("state.db-shm", StringComparison.Ordinal) || path.EndsWith(SqliteOperatorIntentStore.WakeFileSuffix, StringComparison.Ordinal)))
            .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static void AssertSnapshotsEqual(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        Assert.Equal(before.Keys.OrderBy(key => key, StringComparer.Ordinal), after.Keys.OrderBy(key => key, StringComparer.Ordinal));
        foreach (var (path, hash) in before) Assert.Equal(hash, after[path]);
    }
}
