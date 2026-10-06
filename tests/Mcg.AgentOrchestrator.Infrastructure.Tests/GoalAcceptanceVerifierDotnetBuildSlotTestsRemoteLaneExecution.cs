using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.JobAccounting)]
public sealed class GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneExecution : GoalAcceptanceVerifierDotnetBuildSlotTests
{
    [Xunit.Theory]
    [Xunit.InlineData(null, "missing")]
    [Xunit.InlineData("{\"executors\":[],\"lanes\":[\"infrastructure tests: Lane 0\"]}", "empty")]
    [Xunit.InlineData("{", "invalid")]
    public async Task DisabledConfiguration_RunsLocallyAndPreservesVerdict(string? json, string reason)
    {
        using var scenario = CreateScenario();
        if (json is not null) File.WriteAllText(scenario.ConfigurationPath, json);
        var fake = new FakeRemoteLaneExecutor();
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var withFake = await scenario.RunAsync();
        Assert.True(withFake.Passed, JsonSerializer.Serialize(withFake.Checks));
        Assert.Empty(fake.Requests);
        Assert.Equal(new[] { 0, 1, 2 }, scenario.LocalStarts.Order().ToArray());
        Assert.Equal(new[] { $"REMOTE_LANES_DISABLED reason={reason}" }, scenario.Progress.ToArray());

        using var control = CreateScenario();
        if (json is not null) File.WriteAllText(control.ConfigurationPath, json);
        TestOverrides.RemoteLaneExecutorForTests = null;
        var withoutFake = await control.RunAsync();
        Assert.Equal(withoutFake.Passed, withFake.Passed);
        Assert.Equal(Lanes(withoutFake).Select(check => (check.Name, check.Passed)),
            Lanes(withFake).Select(check => (check.Name, check.Passed)));
        Assert.Equal(new[] { 0, 1, 2 }, control.LocalStarts.Order().ToArray());
    }

    [Xunit.Fact]
    public async Task DefaultExecutor_RefusesAndRecordsTransportUnavailable()
    {
        using var scenario = CreateScenario();
        scenario.Configure(0);
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Contains(0, scenario.LocalStarts);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.TransportUnavailable, row.Outcome);
        Assert.Equal("executor", row.FaultOwner);
        Assert.Contains("\"fault_owner\":\"executor\"", File.ReadAllText(RemoteExecutorHealthLedger.ResolveStorePath(scenario.Root)));
    }

    [Xunit.Fact]
    public async Task Eligibility_ExcludesKeyedUnlistedAndFocusedLanes()
    {
        using var scenario = CreateScenario(keyedLane: 1);
        scenario.Configure(0, 1);
        var fake = GreenExecutor(scenario);
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(Lane(0), Assert.Single(fake.Requests).Lane);
        Assert.Equal(new[] { 1, 2 }, scenario.LocalStarts.Order().ToArray());
        var excluded = Assert.Single(scenario.Health().Where(row => row.Outcome == RemoteLaneOutcomeCode.NotEligibleExclusiveResource));
        Assert.Equal(Lane(1), excluded.Lane);
        Assert.Null(excluded.FaultOwner);

        // The same file and executor are used by a fresh focused owner.
        var before = fake.Requests.Count;
        var focused = await scenario.FocusedAsync();
        Assert.True(focused.Accepted, focused.Summary);
        Assert.True(focused.Passed, focused.Summary);
        Assert.Equal(before, fake.Requests.Count);
        Assert.Equal(2, scenario.LocalStarts.Count(index => index == 1));
    }

    [Xunit.Fact]
    public async Task BoundGreen_RetainsReceiptsWithoutLocalTimingOrClosureReuse()
    {
        using var scenario = CreateScenario();
        scenario.Configure(0);
        TestOverrides.ResolvePartitionVerdictClosureHashForTests = check => "closure-" + check.Name;
        var fake = GreenExecutor(scenario);
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.DoesNotContain(0, scenario.LocalStarts);
        var request = Assert.Single(fake.Requests);
        var check = Assert.Single(Lanes(result).Where(check => check.Name == Lane(0)));
        Assert.StartsWith("remote-executor=" + request.ExecutorId, check.ResultSummary!);
        Assert.Equal(request.AttemptId, check.TestResultAttemptId);
        Assert.NotEmpty(check.TestResultPaths!);
        var attemptDirectory = Path.GetDirectoryName(Assert.Single(check.TestResultPaths!))!;
        Assert.Equal(scenario.LastAttemptFolder, attemptDirectory);
        Assert.All(check.TestResultPaths!, path =>
        {
            Assert.True(File.Exists(path));
            Assert.Equal(scenario.LastAttemptFolder, Path.GetDirectoryName(path));
        });
        Assert.DoesNotContain(scenario.RemoteDirectory, attemptDirectory);
        var journal = scenario.Journal();
        var remoteRecord = Assert.Single(journal.Where(row => row.PartitionId == "lane-0" && row.Operation == "acceptance:partition-verdict"));
        Assert.Contains("verdict_source=remote_first_run", remoteRecord.Detail!);
        Assert.Single(journal.Where(row => row.PartitionId == "lane-1" && row.Operation == "acceptance:partition-verdict"));

        var closurePath = Path.Combine(scenario.Root, ".orchestrator", "acceptance-closure-verdicts.jsonl");
        var closures = ReadJsonRows(closurePath);
        Assert.Contains(closures, row => row.GetProperty("partitionFilterHash").GetString() == Hash(1));
        Assert.DoesNotContain(closures, row => row.GetProperty("partitionFilterHash").GetString() == Hash(0));
        var durations = ReadJsonRows(AcceptanceLaneDurationStore.ResolveStorePath(scenario.Root));
        Assert.Contains(durations, row => row.GetProperty("laneName").GetString() == Lane(1));
        Assert.DoesNotContain(durations, row => row.GetProperty("laneName").GetString() == Lane(0));
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(scenario.Health()).Outcome);
    }

    [Xunit.Theory]
    [Xunit.InlineData("commit", nameof(RemoteLaneOutcomeCode.BindingMismatchCommit))]
    [Xunit.InlineData("tree", nameof(RemoteLaneOutcomeCode.BindingMismatchTree))]
    [Xunit.InlineData("main", nameof(RemoteLaneOutcomeCode.BindingMismatchMain))]
    [Xunit.InlineData("filter", nameof(RemoteLaneOutcomeCode.BindingMismatchFilter))]
    [Xunit.InlineData("executor", nameof(RemoteLaneOutcomeCode.BindingMismatchExecutor))]
    [Xunit.InlineData("manifest", nameof(RemoteLaneOutcomeCode.BindingMismatchManifest))]
    [Xunit.InlineData("lane", nameof(RemoteLaneOutcomeCode.BindingMismatchFilter))]
    public async Task BindingMismatch_UsesLocalRedAsTheOnlyVerdict(string field, string outcomeName)
    {
        using var scenario = CreateScenario();
        scenario.Configure(0);
        scenario.RedLane = 0;
        GreenExecutor(scenario, result => field switch
        {
            "commit" => result with { VerifyingCommitSha = "different-commit" },
            "tree" => result with { ObservedTreeSha = "different-tree" },
            "main" => result with { MainSha = "different-main" },
            "filter" => result with { FilterHash = "different-filter" },
            "executor" => result with { ExecutorId = "different-executor" },
            "manifest" => result with { ManifestIdentity = "different-manifest" },
            _ => result with { Lane = Lane(2) }
        });
        var result = await scenario.RunAsync();
        Assert.False(result.Passed);
        var local = Assert.Single(Lanes(result).Where(check => !check.Passed));
        Assert.Equal(Lane(0), local.Name);
        Assert.DoesNotContain("remote-executor=", local.ResultSummary ?? "");
        Assert.Contains(0, scenario.LocalStarts);
        var row = Assert.Single(scenario.Health().Where(row => row.Outcome == Enum.Parse<RemoteLaneOutcomeCode>(outcomeName)));
        Assert.Equal("executor", row.FaultOwner);
        Assert.NotEqual(row.Expected, row.Observed);
    }

    [Xunit.Theory]
    [Xunit.InlineData("refused", nameof(RemoteLaneOutcomeCode.Unreachable))]
    [Xunit.InlineData("zero", nameof(RemoteLaneOutcomeCode.TrxIncomplete))]
    [Xunit.InlineData("red", nameof(RemoteLaneOutcomeCode.RemoteRed))]
    [Xunit.InlineData("missing-trx", nameof(RemoteLaneOutcomeCode.TrxIncomplete))]
    [Xunit.InlineData("throw-submit", nameof(RemoteLaneOutcomeCode.Unreachable))]
    public async Task RemoteFault_RunsLocallyWithoutFailingTheGoal(string fault, string outcomeName)
    {
        using var scenario = CreateScenario();
        scenario.Configure(0);
        var fake = new FakeRemoteLaneExecutor();
        fake.Submit = (request, _) =>
        {
            if (fault == "throw-submit") throw new InvalidOperationException("fixture submit failed");
            if (fault == "refused") return Task.FromResult(new RemoteLaneSubmission(null, "fixture-unreachable"));
            var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
            var result = scenario.RemoteResult(request, fault == "red" ? 1 : 0, fault == "zero" ? 0 : 1);
            if (fault == "missing-trx") result = result with { TestResultPaths = [Path.Combine(scenario.RemoteDirectory, "absent.trx")] };
            handle.Publish(result);
            return Task.FromResult(new RemoteLaneSubmission(handle));
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        var verdict = await scenario.RunAsync();
        Assert.True(verdict.Passed, JsonSerializer.Serialize(verdict.Checks));
        Assert.Single(fake.Requests);
        Assert.Contains(0, scenario.LocalStarts);
        var row = Assert.Single(scenario.Health().Where(row => row.Outcome == Enum.Parse<RemoteLaneOutcomeCode>(outcomeName)));
        Assert.Equal("executor", row.FaultOwner);
        Assert.All(Lanes(verdict), check => Assert.Null(check.FailureClassification));
    }

    [Xunit.Fact]
    public async Task ExpiredLease_RunsLocallyAndIgnoresLateRed()
    {
        using var scenario = CreateScenario();
        scenario.Configure(0);
        var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
        var fake = PendingExecutor(handle);
        var submittedAt = scenario.Clock.GetUtcNow();
        var renewed = Signal();
        var survivedOriginalLease = Signal();
        DateTimeOffset pollClock = default;
        handle.OnHeartbeatRead = () => pollClock = scenario.Clock.GetUtcNow();
        handle.OnPolled = () =>
        {
            if (handle.LastReadHeartbeat != submittedAt.AddSeconds(30)) return;
            if (pollClock == submittedAt.AddSeconds(30)) renewed.TrySetResult();
            else if (pollClock == submittedAt.AddSeconds(61)) survivedOriginalLease.TrySetResult();
        };
        var localStarted = Signal();
        var release = Signal();
        scenario.BeforeLocal = async (index, token) =>
        {
            if (index != 0) return;
            localStarted.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        using var cancellation = new CancellationTokenSource();
        var gate = scenario.RunAsync(cancellation.Token);
        try
        {
            await Event(handle.Polled.Task, "first remote poll");
            scenario.Clock.Advance(TimeSpan.FromSeconds(30));
            handle.Heartbeat(scenario.Clock.GetUtcNow());
            await Event(renewed.Task, "renewed heartbeat observed");
            scenario.Clock.Advance(TimeSpan.FromSeconds(31));
            await Event(survivedOriginalLease.Task, "renewal preserved the remote lane past its original lease");
            Assert.False(localStarted.Task.IsCompleted);
            scenario.Clock.Advance(TimeSpan.FromSeconds(30));
            await Event(localStarted.Task, "local fallback started");
            handle.Publish(scenario.RemoteResult(Assert.Single(fake.Requests), exit: 1));
            release.TrySetResult();
            var result = await Event(gate, "gate completed after local fallback");
            Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
            Assert.Contains(0, scenario.LocalStarts);
            var health = scenario.Health();
            Assert.Equal("executor", Assert.Single(health.Where(row => row.Outcome == RemoteLaneOutcomeCode.LeaseExpired)).FaultOwner);
            Assert.Equal("executor", Assert.Single(health.Where(row => row.Outcome == RemoteLaneOutcomeCode.LateAfterFallback)).FaultOwner);
            Assert.DoesNotContain(health, row => row.Outcome == RemoteLaneOutcomeCode.Accepted);
            Assert.All(result.Checks!, check =>
            {
                Assert.Null(check.FailureClassification);
                Assert.DoesNotContain("fixture-executor", check.Name);
            });
            Assert.True(handle.IsAbandoned);
        }
        finally { release.TrySetResult(); await cancellation.CancelAsync(); await Drain(gate); }
    }

    [Xunit.Fact]
    public async Task RemoteExecution_UsesNoLocalSlotAndFallbackHonorsTheBound()
    {
        using var scenario = CreateScenario(keyedLane: 1, keyedSecondLane: 2);
        scenario.Configure(0);
        TestOverrides.ResolveShardCoreBudgetForTests = () => 2;
        TestOverrides.ShardPermitRootForTests = Path.Combine(scenario.Root, "permits");
        TestOverrides.ResolveGateShardBudgetForTests = () => 5;
        var events = new ConcurrentQueue<(long Sequence, string Kind, string Lane)>();
        long sequence = 0;
        TestOverrides.OnShardPermitAcquiredForTests = lane => events.Enqueue((Interlocked.Increment(ref sequence), "acquire", lane));
        TestOverrides.OnShardPermitReleasedForTests = lane => events.Enqueue((Interlocked.Increment(ref sequence), "release", lane));
        var expired = Signal();
        var fallbackQueuedAtCapacity = Signal();
        TestOverrides.OnRemoteLaneFallbackWaitingForLocalSlotForTests = _ => fallbackQueuedAtCapacity.TrySetResult();
        TestOverrides.OnRemoteLaneOutcomeForTests = (_, code) =>
        {
            if (code == RemoteLaneOutcomeCode.LeaseExpired) expired.TrySetResult();
        };
        var bothStarted = Signal();
        var fallbackStarted = Signal();
        var releaseOne = Signal();
        var releaseTwo = Signal();
        var localCount = 0;
        scenario.BeforeLocal = async (index, token) =>
        {
            if (index == 0) { fallbackStarted.TrySetResult(); return; }
            if (Interlocked.Increment(ref localCount) == 2) bothStarted.TrySetResult();
            await (index == 1 ? releaseOne.Task : releaseTwo.Task).WaitAsync(token);
        };
        var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
        var fake = PendingExecutor(handle);
        fake.Submit = async (_, token) =>
        {
            await bothStarted.Task.WaitAsync(token);
            return new RemoteLaneSubmission(handle);
        };
        using var cancellation = new CancellationTokenSource();
        var gate = scenario.RunAsync(cancellation.Token);
        try
        {
            await Event(bothStarted.Task, "two blocked local hosts started");
            await Event(handle.Polled.Task, "remote submitted while local hosts blocked");
            Assert.Equal(Lane(0), Assert.Single(fake.Requests).Lane);
            Assert.Equal(2, events.Count(row => row.Kind == "acquire"));
            Assert.DoesNotContain(events, row => row.Kind == "acquire" && row.Lane == Lane(0));
            scenario.Clock.Advance(TimeSpan.FromSeconds(61));
            await Event(expired.Task, "remote lease expired");
            await Event(fallbackQueuedAtCapacity.Task, "fallback queued behind two local hosts");
            Assert.False(fallbackStarted.Task.IsCompleted);
            releaseOne.TrySetResult();
            await Event(fallbackStarted.Task, "fallback started after a local slot released");
            var priorRelease = Assert.Single(events.Where(row => row.Kind == "release" && row.Lane == Lane(1)));
            var fallbackPermit = Assert.Single(events.Where(row => row.Kind == "acquire" && row.Lane == Lane(0)));
            Assert.True(priorRelease.Sequence < fallbackPermit.Sequence);
            releaseTwo.TrySetResult();
            var result = await Event(gate, "gate completed with bounded fallback");
            Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
            Assert.Equal(new[] { 0, 1, 2 }, scenario.LocalStarts.Order().ToArray());
        }
        finally
        {
            releaseOne.TrySetResult(); releaseTwo.TrySetResult();
            await cancellation.CancelAsync(); await Drain(gate);
        }
    }

    [Xunit.Fact]
    public async Task RemoteTimeout_UsesInjectedClockAndFallsBackLocally()
    {
        using var scenario = CreateScenario();
        scenario.Configure([0], leaseSeconds: 600);
        var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
        PendingExecutor(handle);
        using var cancellation = new CancellationTokenSource();
        var gate = scenario.RunAsync(cancellation.Token);
        try
        {
            await Event(handle.Polled.Task, "remote polled before timeout");
            scenario.Clock.Advance(TimeSpan.FromSeconds(121));
            var result = await Event(gate, "gate completed after remote timeout");
            Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
            Assert.Contains(0, scenario.LocalStarts);
            Assert.Equal(RemoteLaneOutcomeCode.LaneTimeout, Assert.Single(scenario.Health()).Outcome);
        }
        finally { await cancellation.CancelAsync(); await Drain(gate); }
    }

    [Xunit.Fact]
    public async Task Cancellation_AbandonsEveryOpenRemoteHandle()
    {
        using var scenario = CreateScenario();
        File.WriteAllText(scenario.ConfigurationPath,
            """{"executors":[{"id":"one"},{"id":"two"}],"lanes":["infrastructure tests: Lane 0","infrastructure tests: Lane 1"]}""");
        var handles = Enumerable.Range(0, 2).Select(_ => new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow())).ToArray();
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) => Task.FromResult(new RemoteLaneSubmission(handles[request.Lane == Lane(0) ? 0 : 1]))
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        using var cancellation = new CancellationTokenSource();
        var gate = scenario.RunAsync(cancellation.Token);
        try
        {
            await Event(Task.WhenAll(handles.Select(handle => handle.Polled.Task)), "two remote handles opened");
            Assert.Equal(2, fake.Requests.Count);
            await cancellation.CancelAsync();
            await Event(Task.WhenAll(handles.Select(handle => handle.Abandoned.Task)), "every remote handle abandoned on cancellation");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Event(gate, "cancelled gate drained"));
            Assert.DoesNotContain(0, scenario.LocalStarts);
        }
        finally { await cancellation.CancelAsync(); await Drain(gate); }
    }

    [Xunit.Fact]
    public async Task CachedLane_IsNeverSubmittedAgain()
    {
        using var scenario = CreateScenario();
        scenario.Configure(0);
        var fake = GreenExecutor(scenario);
        var first = await scenario.RunAsync();
        Assert.True(first.Passed, JsonSerializer.Serialize(first.Checks));
        Assert.Single(fake.Requests);
        var second = await scenario.RunAsync();
        Assert.True(second.Passed, JsonSerializer.Serialize(second.Checks));
        Assert.Single(fake.Requests);
        Assert.StartsWith("partition-verdict-cache reused", Assert.Single(Lanes(second).Where(check => check.Name == Lane(0))).ResultSummary!);
    }

    private FakeRemoteLaneExecutor GreenExecutor(Scenario scenario, Func<RemoteLaneResult, RemoteLaneResult>? change = null)
    {
        var fake = new FakeRemoteLaneExecutor();
        fake.Submit = (request, _) =>
        {
            var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
            var result = scenario.RemoteResult(request);
            handle.Publish(change?.Invoke(result) ?? result);
            return Task.FromResult(new RemoteLaneSubmission(handle));
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        return fake;
    }

    private FakeRemoteLaneExecutor PendingExecutor(FakeRemoteLaneExecutor.Handle handle)
    {
        var fake = new FakeRemoteLaneExecutor { Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(handle)) };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        return fake;
    }

    private Scenario CreateScenario(int? keyedLane = null, int? keyedSecondLane = null)
    {
        SetPartitionVerdictKeyHooks("candidate-tree", "main-sha", "verifying-commit");
        TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
        TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
        var scenario = new Scenario(this, CreateShardWorkspace(keyedLane, keyedSecondLane));
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = scenario.ConfigurationPath;
        TestOverrides.RemoteLaneTimeProviderForTests = scenario.Clock;
        TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
        TestOverrides.OnRemoteLaneProgressLineForTests = scenario.Progress.Enqueue;
        return scenario;
    }

    private static string Lane(int index) => $"infrastructure tests: Lane {index}";
    private static string Filter(int index) => $"FullyQualifiedName~Lane{index}Tests";
    private static string Hash(int index) => GoalAcceptanceVerifier.ShortHash(Filter(index));
    private static AcceptanceCheckResult[] Lanes(AcceptanceVerificationResult result) =>
        result.Checks!.Where(check => check.Name.StartsWith("infrastructure tests: ", StringComparison.Ordinal)).ToArray();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
    private static async Task<T> Event<T>(Task<T> task, string name)
    {
        await Event((Task)task, name);
        return await task;
    }
    private static async Task Drain(Task task)
    {
        try { await Event(task, "gate cleanup"); }
        catch (OperationCanceledException) { }
    }
    private static JsonElement[] ReadJsonRows(string path) => File.ReadAllLines(path).Select(line =>
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }).ToArray();

    private static string CreateShardWorkspace(int? keyedLane, int? keyedSecondLane)
    {
        var rows = Enumerable.Range(0, 3).Select(index => new
        {
            name = $"Lane {index}", filter = Filter(index),
            exclusiveResourceKeys = index == keyedLane || index == keyedSecondLane ? new[] { "fixture-resource-" + index } : Array.Empty<string>()
        });
        var root = CreateManifestWorkspace($$"""
            {
              "version": 1,
              "engine": {
                "maxConcurrentShards": 3,
                "infrastructureTestLanes": {{JsonSerializer.Serialize(rows)}},
                "mtpInvocations": [{
                  "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                  "executablePathTemplate": "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                  "firewallExecutablePathTemplate": "bin/{projectName}/{configuration}/{projectName}.exe",
                  "arguments": ["{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}"]
                }]
              },
              "checks": [{
                "name": "infrastructure tests", "type": "dotnet-test", "runner": "mtp",
                "project": "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "timeoutMinutes": 2
              }],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var sourceDirectory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(sourceDirectory);
        foreach (var index in Enumerable.Range(0, 3))
            File.WriteAllText(Path.Combine(sourceDirectory, $"Lane{index}Tests.cs"), $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
        return root;
    }

    private sealed class Scenario(GoalAcceptanceVerifierDotnetBuildSlotTestsRemoteLaneExecution owner, string root) : IDisposable
    {
        internal string Root { get; } = root;
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal string ConfigurationPath => Path.Combine(Root, "executors.json");
        internal string RemoteDirectory => Path.Combine(Root, "remote-receipts");
        internal string? LastAttemptFolder { get; private set; }
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal ConcurrentQueue<string> Progress { get; } = new();
        internal int? RedLane { get; set; }
        internal Func<int, CancellationToken, Task>? BeforeLocal { get; set; }
        internal void Configure(params int[] lanes) => Configure(lanes, 60);
        internal void Configure(int[] lanes, int leaseSeconds) => File.WriteAllText(ConfigurationPath,
            JsonSerializer.Serialize(new { executors = new[] { new { id = "fixture-executor", leaseSeconds } }, lanes = lanes.Select(Lane).ToArray() }));
        internal async Task<AcceptanceVerificationResult> RunAsync(CancellationToken token = default)
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var verifier = new GoalAcceptanceVerifier(owner.TestOverrides, Runner);
            var attemptId = Guid.NewGuid().ToString("N");
            LastAttemptFolder = Path.Combine(Root, ".orchestrator", "attempts", attemptId);
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            await using var executionOwner = new AcceptanceAttemptExecutionOwner(
                new AcceptanceAttemptIdentity(attemptId, Goal.Value, Root, "candidate-tree", "main-sha", "verifying-commit",
                    Path.Combine(LastAttemptFolder, "result"), slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(Root), token);
            return await verifier.RunOwnedAsync(Root, Goal, null, slot, lease, executionOwner);
        }
        internal Task<FocusedEvidenceRunResult> FocusedAsync() =>
            new GoalAcceptanceVerifier(owner.TestOverrides, Runner).RunFocusedEvidenceAsync(Root, Goal, "Infrastructure.Tests: Lane1Tests");
        private async Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                var index = Enumerable.Range(0, 3).Single(index => args.Any(argument => argument.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                if (BeforeLocal is not null) await BeforeLocal(index, token);
                if (RedLane == index) WriteMtpTrx(args, MtpFailureFixturePath());
                else WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                return new(RedLane == index ? 1 : 0, RedLane == index ? "Failed: 1" : "Passed: 1");
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                var output = Path.Combine(GetArtifactsPath(args), "bin", projectName, "debug");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, projectName + ".dll"), "deterministic fixture");
                File.WriteAllText(Path.Combine(output, projectName + ".exe"), "deterministic fixture");
            }
            return new(0, "Build succeeded.");
        }
        internal RemoteLaneResult RemoteResult(RemoteLaneRequest request, int exit = 0, int count = 1)
        {
            var filename = GoalAcceptanceVerifier.ShortHash(request.Lane) + ".trx";
            string[] args = ["--results-directory", RemoteDirectory, "--report-trx-filename", filename];
            if (exit != 0) WriteMtpTrx(args, MtpFailureFixturePath());
            else WriteMtpTrx(args, count, ["Lane0Tests.Executes"]);
            return new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, exit, [Path.Combine(RemoteDirectory, filename)]);
        }
        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() => RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(Root));
        internal PartitionVerdictJournalEntry[] Journal() => File.ReadAllLines(Path.Combine(Root, ".orchestrator", "goal-operations", Goal.Value + ".jsonl"))
            .Select(line => JsonSerializer.Deserialize<PartitionVerdictJournalEntry>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        public void Dispose()
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }
}
