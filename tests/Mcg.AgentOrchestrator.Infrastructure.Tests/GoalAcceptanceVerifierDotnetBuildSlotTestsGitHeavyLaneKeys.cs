using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;
using Xunit;

// Uses shared build-slot accounting and test overrides; serialized by JobAccounting.
[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsGitHeavyLaneKeys : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public Task ShippedKeys_SerializeGitHeavyLanesWhileKeylessLaneOverlaps() =>
        RunScenarioAsync(removeKeys: false);

    [Fact]
    public Task RemovedKeys_AllowGitHeavyLanesToOverlap() =>
        RunScenarioAsync(removeKeys: true);

    private async Task RunScenarioAsync(bool removeKeys)
    {
        var root = CreateScenarioWorkspace(removeKeys);
        TestOverrides.ResolveShardCoreBudgetForTests = () => 4;
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        var identity = removeKeys ? "git-heavy-keyless" : "git-heavy-shipped";
        SetPartitionVerdictKeyHooks("tree-" + identity, "main-" + identity, "commit-" + identity);
        var firstKeyedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondKeyedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseKeyedLanes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activityLock = new object();
        var active = new bool[4];
        var entries = new int[4];
        var exits = new int[4];
        var peakKeyed = 0;
        var keylessOverlapped = false;
        var missingEvents = new List<string>();
        try
        {
            async Task WaitForEventAsync(Task signal, string eventName, CancellationToken cancellationToken)
            {
                try
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
                catch (TimeoutException)
                {
                    lock (activityLock)
                    {
                        missingEvents.Add(eventName);
                    }
                }
            }

            async Task<GoalAcceptanceVerifier.CommandResult> RunShardAsync(
                string[] args, string _, CancellationToken cancellationToken)
            {
                if (TryWriteMtpBuildArtifacts(args))
                {
                    return new GoalAcceptanceVerifier.CommandResult(0, "Build succeeded.");
                }

                WriteMtpTrx(args);
                var filterIndex = Array.IndexOf(args, "--filter-class");
                Assert.True(filterIndex >= 0 && filterIndex + 1 < args.Length);
                var laneIndex = Array.FindIndex(FixtureTokens,
                    token => args[filterIndex + 1].Contains(token, StringComparison.Ordinal));
                Assert.InRange(laneIndex, 0, 3);
                lock (activityLock)
                {
                    entries[laneIndex]++;
                    active[laneIndex] = true;
                    var keyedCount = active.Take(3).Count(value => value);
                    peakKeyed = Math.Max(peakKeyed, keyedCount);
                    keylessOverlapped |= active[3] && keyedCount > 0;
                    if (keyedCount > 0) firstKeyedEntered.TrySetResult();
                    if (keyedCount > 1) secondKeyedEntered.TrySetResult();
                }

                try
                {
                    if (laneIndex < 3)
                    {
                        await WaitForEventAsync(releaseKeyedLanes.Task, "keyless lane released keyed lanes",
                            cancellationToken);
                    }
                    else
                    {
                        await WaitForEventAsync(firstKeyedEntered.Task, "first git-heavy lane entered",
                            cancellationToken);
                        if (removeKeys)
                        {
                            await WaitForEventAsync(secondKeyedEntered.Task, "two git-heavy lanes entered together",
                                cancellationToken);
                        }

                        releaseKeyedLanes.TrySetResult();
                    }

                    return new GoalAcceptanceVerifier.CommandResult(0, "Passed: 1");
                }
                finally
                {
                    if (laneIndex == 3) releaseKeyedLanes.TrySetResult();
                    lock (activityLock)
                    {
                        active[laneIndex] = false;
                        exits[laneIndex]++;
                    }
                }
            }

            var verifier = new GoalAcceptanceVerifier(TestOverrides, RunShardAsync);
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                TimeSpan.FromSeconds(2));
            var result = await verifier.RunAsync(
                root,
                new GoalId(removeKeys ? "abababababababababababababababab" : "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd"),
                stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath),
                stableSlotLease: lease);

            Assert.True(result.Passed);
            Assert.True(missingEvents.Count == 0, "Missing fixture events: " + string.Join(", ", missingEvents));
            Assert.Equal(new[] { 1, 1, 1, 1 }, entries);
            Assert.Equal(entries, exits);
            Assert.All(active, value => Assert.False(value));
            Assert.True(keylessOverlapped, "The keyless balance B lane must overlap a git-heavy lane.");
            if (removeKeys)
            {
                Assert.True(peakKeyed >= 2, "Removing the keys must allow at least two git-heavy lanes at once.");
            }
            else
            {
                Assert.Equal(1, peakKeyed);
            }
        }
        finally
        {
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = true;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(root);
        }
    }

    private static readonly string[] FixtureTokens =
    [
        "GitHeavyRemainderFixtureTests",
        "GitHeavyBalanceAFixtureTests",
        "GitHeavyMergeTrainFixtureTests",
        "KeylessBalanceBFixtureTests"
    ];

    private static string CreateScenarioWorkspace(bool removeKeys)
    {
        var shippedLanes = AcceptanceGateEngineSettings.Load(
            InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;
        string[] names = ["Remainder", "Remainder balance A", "Remainder merge train and ratchet", "Remainder balance B"];
        var lanes = names.Select((name, index) =>
        {
            var shippedLane = Assert.Single(shippedLanes, lane => lane.Name == name);
            if (index == 3) Assert.Empty(shippedLane.ExclusiveResourceKeys);
            return new
            {
                name,
                filter = "FullyQualifiedName~" + FixtureTokens[index],
                estimatedSerialSeconds = 100 - index * 10,
                exclusiveResourceKeys = removeKeys && index < 3 ? [] : shippedLane.ExclusiveResourceKeys.ToArray()
            };
        }).ToArray();
        const string project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
        return CreateManifestWorkspace(JsonSerializer.Serialize(new
        {
            version = 1,
            engine = new
            {
                maxConcurrentShards = 4,
                infrastructureTestLanes = lanes,
                mtpInvocations = new[]
                {
                    new
                    {
                        project,
                        executablePathTemplate = "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                        firewallExecutablePathTemplate = "bin/{projectName}/{configuration}/{projectName}.exe",
                        arguments = new[] { "{executable}", "--results-directory", "{resultsDirectory}",
                            "--report-trx-filename", "{trxFileName}" }
                    }
                }
            },
            checks = new[] { new { name = "infrastructure tests", type = "dotnet-test", runner = "mtp", project } },
            forbiddenChangedPathGlobs = Array.Empty<string>()
        }));
    }

    private static bool TryWriteMtpBuildArtifacts(string[] arguments)
    {
        if (arguments.Length == 0 ||
            !arguments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            arguments.Length >= 2 && arguments[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (arguments.Length < 2 || !arguments[1].Equals("build", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
        var outputDirectory = Path.Combine(GetArtifactsPath(arguments), "bin", projectName, "debug");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory,
            projectName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)), "git-heavy lane fixture");
        File.WriteAllText(Path.Combine(outputDirectory, projectName + ".dll"), "git-heavy lane fixture");
        return true;
    }
}
