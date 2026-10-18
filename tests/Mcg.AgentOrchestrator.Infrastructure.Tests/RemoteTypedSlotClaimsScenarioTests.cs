using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel safe: private repositories, executors and overrides; gate runs own stable-slot leases.
public sealed class RemoteTypedSlotClaimsScenarioTests
{
    [Fact]
    public async Task Cancellable_ShadowSlotsBecomeTwoRemoteGateLanes()
    {
        await using var s = new JobAccountingScenario();
        var shadow = s.StartShadow();
        await Signal(s.Executor.ShadowsSubmitted.Task, "both shadow submissions");
        var result = await Signal(s.RunGateAsync(), "gate after two shadow preemptions");
        await shadow.CompleteAsync(s.LocalShadowResults());
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Empty(s.LocalStarts);
        var rows = s.Health();
        Assert.Equal(2, rows.Count(row => row.EvidenceShadow is not true && row.Outcome == RemoteLaneOutcomeCode.Accepted));
        var requests = rows.Where(row => row.Outcome == RemoteLaneOutcomeCode.ShadowPreemptRequested).ToArray();
        Assert.Equal(2, requests.Length);
        Assert.All(requests, row => { Assert.Contains("released=true", row.Reason); Assert.Equal("", row.ExecutorId); Assert.Null(row.FaultOwner); });
        var preempted = rows.Where(row => row.Outcome == RemoteLaneOutcomeCode.PreemptedByGate).ToArray();
        Assert.Equal(2, preempted.Length);
        Assert.All(preempted, row => { Assert.Null(row.FaultOwner); Assert.Null(row.Agree); });
        Assert.All(s.Executor.ShadowHandles, handle => Assert.Equal(1, handle.Cancellations));
        s.AssertNoPriorityFiles();
    }

    [Fact]
    public async Task IdleOnly_GateGoesLocal_AndDemandBlocksNewShadowUntilDispose()
    {
        await using var s = new JobAccountingScenario("idle-only");
        var shadow = s.StartShadow();
        await Signal(s.Executor.ShadowsSubmitted.Task, "both shadow submissions");
        var checkedDemand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.BeforeLocal = async () =>
        {
            Assert.Contains("Gate", RemoteExecutorSlotArbiter.LiveDemandClasses(s.Root, "one", s.Clock.GetUtcNow()));
            var next = s.StartShadow("later-shadow", count: 1);
            await next.CompleteAsync(s.LocalShadowResults());
            var row = Assert.Single(s.Health(), row => row.GateAttemptId == "later-shadow");
            Assert.Equal(RemoteLaneOutcomeCode.ShadowSkippedNoExecutor, row.Outcome);
            Assert.Equal("yield-to-gate", row.Reason);
            checkedDemand.TrySetResult();
        };
        var result = await Signal(s.RunGateAsync(), "local gate with shadow-held slots");
        await Signal(checkedDemand.Task, "demand inspected before coordinator disposal");
        Assert.True(result.Passed, JsonSerializer.Serialize(result.Checks));
        Assert.Equal(new[] { 0, 1 }, s.LocalStarts.Order());
        var misses = s.Health().Where(row => row.Outcome == RemoteLaneOutcomeCode.OfferSkippedNoExecutor).ToArray();
        Assert.Equal(2, misses.Length);
        Assert.Equal(2, misses.Select(row => row.Lane).Distinct().Count());
        Assert.DoesNotContain(s.Health(), row => row.Outcome == RemoteLaneOutcomeCode.ShadowPreemptRequested);
        Assert.All(s.Executor.ShadowHandles, handle => Assert.Equal(0, handle.Cancellations));
        s.Executor.FinishShadows();
        await shadow.CompleteAsync(s.LocalShadowResults());
        Assert.Equal(2, s.Health().Count(row => row.GateAttemptId == "shadow-attempt" && row.Outcome == RemoteLaneOutcomeCode.Accepted));
        s.AssertNoPriorityFiles();
    }

    [Theory]
    [InlineData("Gate")]
    [InlineData("FocusedEvidence")]
    [InlineData("Screening")]
    public async Task OtherClassOccupancy_IsNeverPreempted(string kind)
    {
        using var f = new RemoteExecutorSlotArbiterTests.Fixture();
        using var held = RemoteExecutorOccupancy.TryClaimExclusive(f.Root, "one");
        Assert.NotNull(held);
        RemoteExecutorOccupancy.Claim(f.Root, "one", "other", 0, Enum.Parse<RemoteExecutorClaimClass>(kind));
        using var gate = f.Coordinator("gate", RemoteExecutorClaimClass.Gate);
        Assert.Null(await Signal(gate.ClaimForGateAsync(RemoteExecutorSlotArbiterTests.Fixture.Lane(), default), "non-shadow gate miss"));
        Assert.Empty(Directory.GetFiles(f.Folder, "*.preempt*"));
        Assert.Equal(RemoteLaneOutcomeCode.OfferSkippedNoExecutor, Assert.Single(f.Health()).Outcome);
    }

    [Fact]
    public async Task FreeSlots_KeepNormalRemoteAcceptanceWithoutPriorityState()
    {
        await using var s = new JobAccountingScenario();
        Assert.True((await Signal(s.RunGateAsync(), "free-slot remote gate")).Passed);
        Assert.Empty(s.LocalStarts);
        Assert.Equal(2, s.Health().Count);
        Assert.All(s.Health(), row => Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome));
        s.AssertNoPriorityFiles();
    }

    [Fact]
    public async Task RemoteFirst_HoldsItsSlotThroughGateMiss_ThenCompletes()
    {
        await using var s = new JobAccountingScenario();
        s.ConfigureMode("remote-first");
        var focused = s.RunRemoteFirstAsync();
        await Signal(s.Executor.FirstShadowSubmitted.Task, "remote-first submission holding slot");
        using var otherSlot = RemoteExecutorOccupancy.TryClaimExclusive(s.Root, "one", 1);
        Assert.NotNull(otherSlot);
        // Switching config cannot make existing FocusedEvidence work a preemption target.
        s.ConfigureMode("shadow");
        using var gate = s.GateCoordinator();
        Assert.Null(await Signal(gate.ClaimForGateAsync(RemoteExecutorSlotArbiterTests.Fixture.Lane(), default), "gate miss beside remote-first"));
        Assert.Empty(Directory.GetFiles(s.Folder, "*.preempt*"));
        Assert.Equal(0, Assert.Single(s.Executor.ShadowHandles).Cancellations);
        s.Executor.FinishShadows();
        var outcome = await Signal(focused, "remote-first accepted after gate miss");
        Assert.True(Assert.Single(outcome.Results).Passed);
        var row = Assert.Single(s.Health(), row => row.Outcome == RemoteLaneOutcomeCode.Accepted);
        Assert.Null(row.EvidenceShadow);
        // Completion requests cancellation during normal handle cleanup, after the no-preemption assertion above.
        var handle = Assert.Single(s.Executor.ShadowHandles);
        await Signal(handle.Abandoned.Task, "remote-first handle cleanup after completion");
        Assert.Equal(1, handle.Cancellations);
    }

    [Fact]
    public async Task ReleasedAttemptRequest_IsSweptWithoutCancellingAnotherShadow()
    {
        await using var s = new JobAccountingScenario();
        RemoteExecutorOccupancy.Claim(s.Root, "one", "old-shadow", 0, RemoteExecutorClaimClass.Shadow);
        using var arbiter = new RemoteExecutorSlotArbiter(s.Root, "gate", RemoteExecutorClaimClass.Gate, s.Clock);
        Assert.NotNull(arbiter.RequestPreemption(["one"], "gate-lane"));
        RemoteExecutorOccupancy.Release(s.Root, "one", "old-shadow");
        var shadow = s.StartShadow(count: 1);
        await Signal(s.Executor.FirstShadowSubmitted.Task, "new unrelated shadow submission");
        Assert.Empty(Directory.GetFiles(s.Folder, "*.preempt*"));
        s.Executor.FinishShadows();
        await shadow.CompleteAsync(s.LocalShadowResults());
        Assert.Equal(0, Assert.Single(s.Executor.ShadowHandles).Cancellations);
        var row = Assert.Single(s.Health());
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        Assert.True(row.Agree);
    }

    internal static async Task Signal(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
    internal static async Task<T> Signal<T>(Task<T> task, string name)
    { await Signal((Task)task, name); return await task; }

    [Collection(TestCollections.JobAccounting)]
    internal sealed class JobAccountingScenario : GoalAcceptanceVerifierDotnetBuildSlotTests, IAsyncDisposable
    {
        internal string Root { get; }
        internal string Folder => RemoteExecutorSlotArbiter.Folder(Root, "one");
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal PendingExecutor Executor { get; }
        internal ConcurrentQueue<int> LocalStarts { get; } = new();
        internal Func<Task>? BeforeLocal { get; set; }
        private int _localHook;
        private readonly List<RemoteFocusedEvidenceShadow> _shadows = [];
        private readonly List<IAcceptanceRunExecutionContext> _owners = [];
        private readonly string _head;
        private readonly string _tree;
        private readonly string _configurationPath;
        private const string Project = RemoteFirstFocusedEvidenceFixture.Project;
        private static string Lane(int index) => "infrastructure tests: Lane " + index;
        private static string Filter(int index) => "FullyQualifiedName~Lane" + index + "Tests";

        internal JobAccountingScenario(string preemption = "cancellable")
        {
            Root = CreateManifestWorkspace(JsonSerializer.Serialize(new
            {
                version = 1, engine = new
                {
                    maxConcurrentShards = 3,
                    infrastructureTestLanes = Enumerable.Range(0, 2).Select(index => new { name = "Lane " + index, filter = Filter(index) }),
                    mtpInvocations = new[] { new { project = Project,
                        executablePathTemplate = "bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                        firewallExecutablePathTemplate = "bin/{projectName}/{configuration}/{projectName}.exe",
                        arguments = new[] { "{executable}", "--results-directory", "{resultsDirectory}", "--report-trx-filename", "{trxFileName}" } } }
                },
                checks = new[] { new { name = "infrastructure tests", type = "dotnet-test", runner = "mtp", project = Project, timeoutMinutes = 2 } },
                forbiddenChangedPathGlobs = Array.Empty<string>()
            }));
            var source = Path.Combine(Root, Path.GetDirectoryName(Project)!);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(Root, Project), "<Project />");
            for (var index = 0; index < 2; index++) File.WriteAllText(Path.Combine(source, $"Lane{index}Tests.cs"),
                $"public class Lane{index}Tests {{ [Xunit.Fact] public void Executes() {{ }} }}");
            File.WriteAllText(Path.Combine(Root, ".gitignore"), ".orchestrator/\nremote-receipts/\n");
            RemoteLaneOfferSeeding.PrepareFixture(Root, TestOverrides);
            Git("init", "-b", "main"); Git("add", ".");
            Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
            _head = Git("rev-parse", "HEAD").Trim(); _tree = Git("rev-parse", "HEAD^{tree}").Trim();
            SetPartitionVerdictKeyHooks(_tree, _head, _head);
            TestOverrides.PartitionVerdictWithinAttemptRerunEnabled = false;
            TestOverrides.ResolveShardCoreBudgetForTests = () => 3;
            var config = Path.Combine(Root, ".orchestrator", "executors.json");
            _configurationPath = config;
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, JsonSerializer.Serialize(new
            {
                executors = new[] { new { id = "one", slots = 2, leaseSeconds = 300 } },
                lanes = Enumerable.Range(0, 2).Select(Lane),
                focusedEvidence = new { mode = "shadow", sampleEvery = 1, graceSeconds = 300, shadowPreemption = preemption }
            }));
            Executor = new PendingExecutor(Clock, RemoteResult);
            TestOverrides.RemoteLaneExecutorConfigurationPathForTests = config;
            TestOverrides.RemoteLaneExecutorForTests = Executor;
            TestOverrides.RemoteLaneTimeProviderForTests = Clock;
            TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
            Assert.Empty(Git("status", "--porcelain", "--untracked-files=all"));
        }

        private string Git(params string[] args)
        { var result = GitCli.Run(Root, args); Assert.True(result.Succeeded, result.Error); return result.Output; }

        internal void ConfigureMode(string mode)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_configurationPath))!;
            json["focusedEvidence"]!["mode"] = mode;
            File.WriteAllText(_configurationPath, json.ToJsonString());
        }

        internal RemoteLaneCoordinator GateCoordinator() => new(
            RemoteLaneExecutorConfiguration.Load(_configurationPath),
            new RemoteLaneCandidateIdentity("waiting-gate", Goal.Value, _head, _tree, _head, "manifest"),
            Root, null, Executor, Clock, TimeSpan.FromMilliseconds(1), null);

        internal Task<RemoteFirstFocusedEvidence.Outcome> RunRemoteFirstAsync()
        {
            var owner = (IAcceptanceRunExecutionContext)AcceptanceExecutionOwners.CreateFocusedVerification(Root, Goal, null,
                options: new(RunId: "remote-first", ResultsPrefix: Path.Combine(Root, ".orchestrator", "remote-first-result")));
            _owners.Add(owner);
            var check = RemoteFirstFocusedEvidenceFixture.FocusedCheck("Lane0Tests");
            return RemoteFirstFocusedEvidence.RunAsync(TestOverrides, Clock, Root, [check], Goal, owner, false,
                _ => throw new InvalidOperationException("Remote-first must not fall back locally"), default);
        }

        internal RemoteFocusedEvidenceShadow StartShadow(string attempt = "shadow-attempt", int count = 2)
        {
            var owner = (IAcceptanceRunExecutionContext)AcceptanceExecutionOwners.CreateFocusedVerification(Root, Goal, null,
                options: new(RunId: attempt, ResultsPrefix: Path.Combine(Root, ".orchestrator", attempt)));
            _owners.Add(owner);
            var checks = Enumerable.Range(0, count).Select(index => new Check
            {
                Name = "focused " + index, Type = "dotnet-test", Runner = "mtp", Project = Project,
                Arguments = ["--filter", Filter(index)], IsFocusedEvidenceSelection = true
            }).ToArray();
            var shadow = RemoteFocusedEvidenceShadow.Start(TestOverrides, Clock, Root, checks, Goal, owner, default);
            _shadows.Add(shadow);
            return shadow;
        }

        internal AcceptanceCheckResult[] LocalShadowResults() => Enumerable.Range(0, 2)
            .Select(index => new AcceptanceCheckResult("focused " + index, true, 0, null)).ToArray();
        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() =>
            RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(Root));
        internal void AssertNoPriorityFiles()
        {
            if (!Directory.Exists(Folder)) return;
            Assert.Empty(Directory.GetFiles(Folder, "*.demand"));
            Assert.Empty(Directory.GetFiles(Folder, "*.preempt*"));
        }

        internal async Task<AcceptanceVerificationResult> RunGateAsync()
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(60));
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            var attempt = Guid.NewGuid().ToString("N");
            await using var owner = new AcceptanceAttemptExecutionOwner(new AcceptanceAttemptIdentity(
                attempt, Goal.Value, Root, _tree, _head, _head,
                Path.Combine(Root, ".orchestrator", "attempts", attempt, "result"), slot, Environment.ProcessId, null),
                AcceptanceGateEngineSettings.Load(Root));
            return await new GoalAcceptanceVerifier(TestOverrides, Runner).RunOwnedAsync(Root, Goal, null, slot, lease, owner);
        }

        private async Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                var index = Enumerable.Range(0, 2).Single(index => args.Any(arg => arg.Contains($"Lane{index}Tests", StringComparison.Ordinal)));
                LocalStarts.Enqueue(index);
                if (Interlocked.Exchange(ref _localHook, 1) == 0 && BeforeLocal is { } hook) await hook();
                WriteMtpTrx(args, 1, [$"Lane{index}Tests.Executes"]);
                return new(0, "Passed: 1");
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                const string project = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                var output = Path.Combine(GetArtifactsPath(args), "bin", project, "debug");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, project + ".dll"), "fixture");
                File.WriteAllText(Path.Combine(output, project + ".exe"), "fixture");
            }
            return new(0, "Build succeeded.");
        }

        private RemoteLaneResult RemoteResult(RemoteLaneRequest request)
        {
            var directory = Path.Combine(Root, "remote-receipts");
            var name = GoalAcceptanceVerifier.ShortHash(request.AttemptId + request.Lane) + ".trx";
            var index = request.Filter.Contains("Lane0Tests", StringComparison.Ordinal) ? 0 : 1;
            WriteMtpTrx(["--results-directory", directory, "--report-trx-filename", name], 1, [$"Lane{index}Tests.Executes"]);
            return new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [Path.Combine(directory, name)]);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var shadow in _shadows) await shadow.DisposeAsync();
            foreach (var owner in _owners) await owner.DisposeAsync();
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }

    internal sealed class PendingExecutor(ManualRemoteLaneClock clock, Func<RemoteLaneRequest, RemoteLaneResult> result)
        : IRemoteLaneExecutor, IRemoteLaneQueueStateSource
    {
        internal ConcurrentBag<PendingHandle> ShadowHandles { get; } = [];
        internal TaskCompletionSource FirstShadowSubmitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ShadowsSubmitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource QueueRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource QueueGone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldQueue { get; set; }
        internal bool? QueueAnswer { get; set; } = false;
        private readonly ConcurrentBag<PendingHandle> _gates = [];

        public Task<RemoteLaneSubmission> SubmitAsync(RemoteLaneRequest request, CancellationToken token)
        {
            var handle = new PendingHandle(request, clock, result);
            if (request.Lane.StartsWith("fe-", StringComparison.Ordinal))
            {
                ShadowHandles.Add(handle);
                FirstShadowSubmitted.TrySetResult();
                if (ShadowHandles.Count == 2) ShadowsSubmitted.TrySetResult();
            }
            else
            {
                _gates.Add(handle);
                // Keep the first gate slot held until the second lane has obtained its slot.
                if (_gates.Count == 2) foreach (var gate in _gates) gate.Finish();
            }
            return Task.FromResult(new RemoteLaneSubmission(handle));
        }
        public async Task<bool?> IsJobListedAsync(RemoteLaneRequest request, CancellationToken token)
        {
            QueueRead.TrySetResult();
            if (HoldQueue) await QueueGone.Task.WaitAsync(token);
            return QueueAnswer;
        }
        internal void FinishShadows() { foreach (var handle in ShadowHandles) handle.Finish(); }
    }

    internal sealed class PendingHandle(RemoteLaneRequest request, ManualRemoteLaneClock clock,
        Func<RemoteLaneRequest, RemoteLaneResult> result) : IRemoteLaneHandle, IRemoteLaneQueuedJobCancellation
    {
        private RemoteLaneResult? _result;
        private int _cancellations;
        internal int Cancellations => Volatile.Read(ref _cancellations);
        internal TaskCompletionSource Abandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset? NewestHeartbeat => clock.GetUtcNow();
        public RemoteLaneResult? TryGetResult() => Volatile.Read(ref _result);
        internal void Finish() => Volatile.Write(ref _result, result(request));
        public void RequestQueuedJobCancellation() => Interlocked.Increment(ref _cancellations);
        public void Abandon() => Abandoned.TrySetResult();
    }
}
