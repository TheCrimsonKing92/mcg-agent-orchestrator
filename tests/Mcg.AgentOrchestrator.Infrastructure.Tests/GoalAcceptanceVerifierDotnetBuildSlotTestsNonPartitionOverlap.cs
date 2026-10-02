using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Stable build slots and their process accounting are isolated by this collection.
[Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsNonPartitionOverlap
    : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Fact]
    public Task CheckAfterLanes_StartsBeforeAnyLaneCompletes() => AssertOverlapAsync(checkFirst: false);

    [Fact]
    public Task CheckBeforeLanes_StartsBeforeAnyLaneCompletes() => AssertOverlapAsync(checkFirst: true);

    private async Task AssertOverlapAsync(bool checkFirst)
    {
        var laneStarted = NewSignal();
        var checkStarted = NewSignal();
        var waitsExpired = new ConcurrentQueue<string>();
        var laneFinishes = 0;
        var finishesAtCheckStart = -1;
        var root = CreateWorkspace(3, checkFirst);
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunAsync(
                string[] args, string _, CancellationToken token)
            {
                if (WriteBuildArtifacts(args))
                    return new(0, "Build succeeded.");
                if (IsDiscovery(args))
                    return DiscoveryResult();
                if (IsMtpExecutableCall(args, InfrastructureProject))
                {
                    laneStarted.TrySetResult();
                    await ObserveAsync(checkStarted.Task, "lane never observed non-partition start", waitsExpired, token);
                    WriteLaneTrx(args);
                    Interlocked.Increment(ref laneFinishes);
                    return new(0, "Passed: 1");
                }
                if (IsMtpExecutableCall(args, CoreProject))
                {
                    finishesAtCheckStart = Volatile.Read(ref laneFinishes);
                    checkStarted.TrySetResult();
                    await ObserveAsync(laneStarted.Task, "check never observed lane start", waitsExpired, token);
                    WriteMtpTrx(args, 1, ["CoreShardTests.Passes"]);
                    return new(0, "Passed: 1");
                }
                return new(0, string.Empty);
            }

            var result = await RunInSlotAsync(root, RunAsync);
            Assert.Empty(waitsExpired);
            Assert.True(checkStarted.Task.IsCompletedSuccessfully);
            Assert.Equal(0, finishesAtCheckStart);
            Assert.Equal(2, laneFinishes);
            Assert.True(result.Passed, result.OutputTail);
            var testChecks = result.Checks!.Where(check => check.Name == "core tests" ||
                check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal)).ToArray();
            Assert.Equal(checkFirst
                ? ["core tests", "infrastructure tests: Alpha", "infrastructure tests: Remainder"]
                : new[] { "infrastructure tests: Alpha", "infrastructure tests: Remainder", "core tests" },
                testChecks.Select(check => check.Name));
            Assert.All(testChecks, check => Assert.NotEmpty(check.TestResultPaths!));
        }
        finally
        {
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task FourTestProcesses_ShareTwoSlots_AndAllRun()
    {
        var root = CreateWorkspace(2, checkFirst: true, secondCheck: true);
        var release = NewSignal();
        var twoStarted = NewSignal();
        var waitsExpired = new ConcurrentQueue<string>();
        var calls = new ConcurrentQueue<string>();
        var running = 0;
        var peak = 0;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunAsync(
                string[] args, string _, CancellationToken token)
            {
                if (WriteBuildArtifacts(args))
                    return new(0, "Build succeeded.");
                if (IsDiscovery(args))
                    return DiscoveryResult();
                if (!IsTest(args))
                    return new(0, string.Empty);
                var active = Interlocked.Increment(ref running);
                int previous;
                do { previous = Volatile.Read(ref peak); }
                while (active > previous && Interlocked.CompareExchange(ref peak, active, previous) != previous);
                calls.Enqueue(args[Array.IndexOf(args, "--report-trx-filename") + 1]);
                if (calls.Count >= 2)
                    twoStarted.TrySetResult();
                try
                {
                    await ObserveAsync(release.Task, "test process never received release", waitsExpired, token);
                    WritePassingTrx(args);
                    return new(0, "Passed: 1");
                }
                finally { Interlocked.Decrement(ref running); }
            }

            var run = RunInSlotAsync(root, RunAsync);
            await ObserveAsync(twoStarted.Task, "two test processes never started", waitsExpired,
                TestContext.Current.CancellationToken);
            release.TrySetResult();
            var result = await run;
            Assert.Empty(waitsExpired);
            Assert.InRange(peak, 1, 2);
            Assert.Equal(0, running);
            Assert.Equal(4, calls.Distinct(StringComparer.Ordinal).Count());
            Assert.True(result.Passed, result.OutputTail);
        }
        finally
        {
            release.TrySetResult();
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            DeleteDirectoryWithRetry(root);
        }
    }

    [Fact]
    public async Task FailedCheck_CancelsLanesWithoutVerdicts_AndKeepsSerialFailure()
    {
        var serialRoot = CreateWorkspace(3, checkFirst: true);
        var overlapRoot = CreateWorkspace(3, checkFirst: true);
        var lanesStarted = NewSignal();
        var waitsExpired = new ConcurrentQueue<string>();
        var laneStarts = 0;
        var laneCancellations = 0;
        var overlapGoal = GoalId.New();
        SetPartitionVerdictKeyHooks("overlap-tree", "overlap-main", "overlap-candidate");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        try
        {
            async Task<GoalAcceptanceVerifier.CommandResult> RunAsync(
                string[] args, string root, CancellationToken token)
            {
                if (WriteBuildArtifacts(args))
                    return new(0, "Build succeeded.");
                if (IsDiscovery(args))
                    return DiscoveryResult();
                if (IsMtpExecutableCall(args, CoreProject))
                {
                    if (root == overlapRoot)
                        await ObserveAsync(lanesStarted.Task, "failing check never observed both lanes", waitsExpired, token);
                    WriteFailureTrx(args);
                    return new(1, "Failed: 1");
                }
                if (IsMtpExecutableCall(args, InfrastructureProject))
                {
                    Assert.Equal(overlapRoot, root);
                    if (Interlocked.Increment(ref laneStarts) == 2)
                        lanesStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token).WaitAsync(TimeSpan.FromSeconds(30));
                        throw new InvalidOperationException("Lane cancellation was not observed.");
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref laneCancellations);
                        throw;
                    }
                    catch (TimeoutException)
                    {
                        waitsExpired.Enqueue("lane never observed cancellation");
                        throw;
                    }
                }
                return new(0, string.Empty);
            }

            // Budget one selects the unchanged serial loop, providing the fixture expectation.
            TestOverrides.ResolveShardCoreBudgetForTests = () => 1;
            var serial = await RunInSlotAsync(serialRoot, RunAsync);
            TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
            var overlap = await RunInSlotAsync(overlapRoot, RunAsync, overlapGoal);
            Assert.Empty(waitsExpired);
            Assert.False(serial.Passed);
            Assert.False(overlap.Passed);
            Assert.Equal(2, laneStarts);
            Assert.Equal(2, laneCancellations);
            var expected = Assert.Single(serial.Checks!, check => check.Name == "core tests");
            var actual = Assert.Single(overlap.Checks!, check => check.Name == "core tests");
            Assert.Equal("core tests", actual.Name);
            Assert.Equal(1, actual.ExitCode);
            Assert.False(actual.Passed);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.ExitCode, actual.ExitCode);
            Assert.Equal(expected.FailureClassification, actual.FailureClassification);
            Assert.Equal(expected.FailingTestIdentities, actual.FailingTestIdentities);
            Assert.Equal(["CoreShardTests.Fails"], actual.FailingTestIdentities);
            Assert.DoesNotContain(overlap.Checks!, check =>
                check.Name.StartsWith("infrastructure tests:", StringComparison.Ordinal));
            Assert.DoesNotContain(overlap.Checks!, check => check.Name == "infrastructure partition verdict cache");
            Assert.DoesNotContain(GoalOperationJournal.Read(overlapRoot, overlapGoal).Entries,
                entry => entry.Operation == "acceptance:partition-verdict");
        }
        finally
        {
            TestOverrides.ResolveShardCoreBudgetForTests = null;
            ResetPartitionVerdictKeyHooks();
            DeleteDirectoryWithRetry(serialRoot);
            DeleteDirectoryWithRetry(overlapRoot);
        }
    }

    private async Task<AcceptanceVerificationResult> RunInSlotAsync(string root,
        Func<string[], string, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>> runner,
        GoalId? goalId = null)
    {
        using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(2));
        return await new GoalAcceptanceVerifier(TestOverrides, runner).RunAsync(root, goalId ?? GoalId.New(),
            stableSlotIndex: StableSlotIndex(lease.Environment.ArtifactsPath), stableSlotLease: lease,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private const string InfrastructureProject = "Mcg.AgentOrchestrator.Infrastructure.Tests";
    private const string CoreProject = "Mcg.AgentOrchestrator.Core.Tests";
    private const string ExtraProject = "Mcg.AgentOrchestrator.ProviderEnvironment.Tests";

    private static string CreateWorkspace(int budget, bool checkFirst, bool secondCheck = false)
    {
        object Check(string name, string project) => new
        {
            name, type = "dotnet-test", runner = "mtp",
            project = $"tests/{project}/{project}.csproj", arguments = new[] { "--verbosity", "minimal" }
        };
        var checks = new List<object>();
        if (checkFirst) checks.Add(Check("core tests", CoreProject));
        checks.Add(Check("infrastructure tests", InfrastructureProject));
        if (!checkFirst) checks.Add(Check("core tests", CoreProject));
        if (secondCheck) checks.Add(Check("provider environment tests", ExtraProject));
        return CreateManifestWorkspace(JsonSerializer.Serialize(new
        {
            version = 1,
            engine = new
            {
                maxConcurrentShards = budget,
                infrastructureTestLanes = new[]
                {
                    new { name = "Alpha", filter = "FullyQualifiedName~AlphaShardTests" },
                    new { name = "Remainder", filter = "FullyQualifiedName!~AlphaShardTests&Category!=HostIntegration" }
                },
                mtpInvocations = new[] { InfrastructureProject, CoreProject, ExtraProject }.Select(project => new
                {
                    project = $"tests/{project}/{project}.csproj",
                    executablePathTemplate = "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    firewallExecutablePathTemplate = "bin/{projectName}/{configuration}/{projectName}.exe",
                    arguments = new[] { "{executable}", "--no-ansi", "--progress", "off", "--results-directory",
                        "{resultsDirectory}", "--report-trx", "--report-trx-filename", "{trxFileName}" }
                }).ToArray()
            },
            checks, forbiddenChangedPathGlobs = Array.Empty<string>()
        }));
    }

    private static bool WriteBuildArtifacts(string[] args)
    {
        if (args.Length < 2 || args[0] != "dotnet" || args[1] != "build") return false;
        foreach (var project in new[] { InfrastructureProject, CoreProject, ExtraProject })
        {
            var output = Path.Combine(GetArtifactsPath(args), "bin", project, "debug");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, project + ".dll"), "fixture");
            File.WriteAllText(Path.Combine(output, project + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)), "fixture");
        }
        return true;
    }

    private static bool IsDiscovery(string[] args) => args.Contains("--list-tests", StringComparer.OrdinalIgnoreCase);
    private static bool IsTest(string[] args) => new[] { InfrastructureProject, CoreProject, ExtraProject }
        .Any(project => IsMtpExecutableCall(args, project));
    private static GoalAcceptanceVerifier.CommandResult DiscoveryResult() => new(0,
        "DISCOVERED_TEST:AlphaShardTests.Passes\nDISCOVERED_TEST:BetaShardTests.Passes");
    private static void WriteLaneTrx(string[] args) => WriteMtpTrx(args, 1,
        [args.Any(arg => arg.Contains("AlphaShardTests", StringComparison.Ordinal)) &&
         args.Contains("--filter-class", StringComparer.Ordinal) ? "AlphaShardTests.Passes" : "BetaShardTests.Passes"]);
    private static void WritePassingTrx(string[] args)
    {
        if (IsMtpExecutableCall(args, InfrastructureProject)) WriteLaneTrx(args);
        else WriteMtpTrx(args, 1, ["CoreShardTests.Passes"]);
    }
    private static void WriteFailureTrx(string[] args)
    {
        WriteMtpTrx(args, 1, ["CoreShardTests.Fails"]);
        var path = Path.Combine(args[Array.IndexOf(args, "--results-directory") + 1],
            args[Array.IndexOf(args, "--report-trx-filename") + 1]);
        var trx = XDocument.Load(path);
        trx.Descendants("UnitTestResult").Single().SetAttributeValue("outcome", "Failed");
        var counters = trx.Descendants("Counters").Single();
        counters.SetAttributeValue("passed", 0);
        counters.SetAttributeValue("failed", 1);
        trx.Save(path);
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ObserveAsync(Task signal, string missingEvent,
        ConcurrentQueue<string> expired, CancellationToken token)
    {
        try { await signal.WaitAsync(TimeSpan.FromSeconds(30), token); }
        catch (TimeoutException) { expired.Enqueue(missingEvent); }
    }
}
