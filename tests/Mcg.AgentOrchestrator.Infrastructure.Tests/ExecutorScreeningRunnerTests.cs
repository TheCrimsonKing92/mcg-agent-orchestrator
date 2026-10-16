using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class ExecutorScreeningRunnerTests
{
    [Fact]
    public async Task PassingLanesPersistOneIsolatedReceiptAndReleaseEverySlot()
    {
        using var fixture = new ExecutorScreeningFixture();
        fixture.Executor.Submit = (request, _) =>
        {
            Assert.True(RemoteExecutorOccupancy.IsOccupied(fixture.Root, fixture.Entry.Id));
            for (var slot = 0; slot < fixture.Entry.Slots; slot++)
                Assert.Null(RemoteExecutorOccupancy.TryClaimExclusive(fixture.Root, fixture.Entry.Id, slot));
            return Task.FromResult(fixture.PassingSubmission(request));
        };
        var receipt = Assert.IsType<ExecutorScreeningReceipt>(await fixture.Run(Lanes("A", "B", "C")));
        Assert.Equal(ExecutorScreeningVerdict.Pass, receipt.Verdict);
        Assert.Equal(3, receipt.LaneOutcomes.Count);
        Assert.All(receipt.LaneOutcomes, lane => Assert.Equal(RemoteLaneOutcomeCode.Accepted, lane.Outcome));
        Assert.Single(ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath));
        Assert.All(fixture.Executor.Requests, request =>
        {
            Assert.StartsWith("sc-", request.Lane);
            Assert.Equal(11, request.Lane.Length);
            Assert.StartsWith("screening-", request.ManifestIdentity);
            Assert.Equal(fixture.Candidate.Commit, request.VerifyingCommitSha);
        });
        fixture.AssertReleased();
        Assert.False(File.Exists(RemoteExecutorHealthLedger.ResolveStorePath(fixture.Root)));
    }

    [Theory]
    [InlineData(false, "Fail")]
    [InlineData(true, "Pass")]
    public async Task OnlyBoundCompleteFailuresCountAndKnownDivergenceIsRecorded(bool known, string verdict)
    {
        using var fixture = new ExecutorScreeningFixture();
        if (known) fixture.Configuration = fixture.Configuration with
        { Screening = new() { Mode = "shadow", KnownDivergentTests = ["ExampleTests.Broken"] } };
        fixture.Executor.Submit = (request, _) => Task.FromResult(fixture.Submission(request, failure: true));
        var receipt = Assert.IsType<ExecutorScreeningReceipt>(await fixture.Run(Lanes("A")));
        Assert.Equal(verdict, receipt.Verdict.ToString());
        var lane = Assert.Single(receipt.LaneOutcomes);
        Assert.Equal(RemoteLaneOutcomeCode.RemoteRed, lane.Outcome);
        Assert.Equal(known ? [] : new[] { "Suite.ExampleTests.Broken" }, lane.FailingTests);
        Assert.Equal(known ? new[] { "Suite.ExampleTests.Broken" } : [], lane.IgnoredKnownDivergentTests);
        fixture.AssertReleased();
    }

    [Fact]
    public async Task BindingMismatchDiscardsFailingIdentities()
    {
        using var fixture = new ExecutorScreeningFixture();
        fixture.Executor.Submit = (request, _) => Task.FromResult(fixture.Submission(request, failure: true, wrongCommit: true));
        var receipt = Assert.IsType<ExecutorScreeningReceipt>(await fixture.Run(Lanes("A")));
        Assert.Equal(ExecutorScreeningVerdict.Incomplete, receipt.Verdict);
        var lane = Assert.Single(receipt.LaneOutcomes);
        Assert.Equal(RemoteLaneOutcomeCode.BindingMismatchCommit, lane.Outcome);
        Assert.Empty(lane.FailingTests);
        Assert.Empty(lane.IgnoredKnownDivergentTests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonzeroExitWithoutAFailingIdentityIsIncomplete(bool missingTrx)
    {
        using var fixture = new ExecutorScreeningFixture();
        fixture.Executor.Submit = (request, _) => Task.FromResult(fixture.Submission(request, exit: 1, missingTrx: missingTrx));
        var receipt = Assert.IsType<ExecutorScreeningReceipt>(await fixture.Run(Lanes("A")));
        Assert.Equal(ExecutorScreeningVerdict.Incomplete, receipt.Verdict);
        Assert.Empty(Assert.Single(receipt.LaneOutcomes).FailingTests);
    }

    [Fact]
    public async Task IncompleteTrxCannotReportCandidateFailure()
    {
        using var fixture = new ExecutorScreeningFixture();
        fixture.Executor.Submit = (request, _) => Task.FromResult(fixture.Submission(request, failure: true, incomplete: true));
        var receipt = Assert.IsType<ExecutorScreeningReceipt>(await fixture.Run(Lanes("A")));
        Assert.Equal(ExecutorScreeningVerdict.Incomplete, receipt.Verdict);
        Assert.Equal(RemoteLaneOutcomeCode.TrxIncomplete, Assert.Single(receipt.LaneOutcomes).Outcome);
    }

    [Theory]
    [InlineData(false, "LeaseExpired")]
    [InlineData(true, "LaneTimeout")]
    public async Task ClockSignalsTerminateMissingResults(bool laneTimeout, string code)
    {
        using var fixture = new ExecutorScreeningFixture();
        FakeRemoteLaneExecutor.Handle? handle = null;
        fixture.Executor.Submit = (_, _) =>
        {
            handle = new(fixture.Clock.GetUtcNow());
            handle.OnHeartbeatRead = () => fixture.Clock.Advance(TimeSpan.FromSeconds(laneTimeout ? 2 : 61));
            return Task.FromResult(new RemoteLaneSubmission(handle));
        };
        var receipt = Assert.IsType<ExecutorScreeningReceipt>(await fixture.Run(Lanes("A"),
            laneTimeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromHours(1)));
        Assert.Equal(ExecutorScreeningVerdict.Incomplete, receipt.Verdict);
        Assert.Equal(code, Assert.Single(receipt.LaneOutcomes).Outcome.ToString());
        Assert.True(handle!.IsAbandoned);
        fixture.AssertReleased();
    }

    [Fact]
    public async Task SchedulerBackfillsWithoutSharingKeysOrExceedingSlots()
    {
        using var fixture = new ExecutorScreeningFixture();
        var handles = new Dictionary<string, (RemoteLaneRequest Request, FakeRemoteLaneExecutor.Handle Handle)>();
        var cSubmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bSubmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCounts = new List<int>();
        fixture.Executor.Submit = (request, _) =>
        {
            var name = request.Filter;
            var handle = new FakeRemoteLaneExecutor.Handle(fixture.Clock.GetUtcNow());
            lock (handles)
            {
                if (name == "B") Assert.True(handles["A"].Handle.IsAbandoned);
                handles.Add(name, (request, handle));
                activeCounts.Add(handles.Values.Count(item => !item.Handle.IsAbandoned));
            }
            if (name == "C") cSubmitted.TrySetResult();
            if (name == "B") bSubmitted.TrySetResult();
            return Task.FromResult(new RemoteLaneSubmission(handle));
        };
        var lanes = new[]
        {
            new AcceptanceTestLane("B", "B", 200) { ExclusiveResourceKeys = ["xunit:EnvMutation"] },
            new AcceptanceTestLane("C", "C", 100),
            new AcceptanceTestLane("A", "A", 300) { ExclusiveResourceKeys = ["xunit:EnvMutation"] }
        };
        var work = fixture.Run(lanes);
        await cSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only bound: C submission event.
        Assert.Equal(new[] { "A", "C" }, fixture.Executor.Requests.Select(request => request.Filter));
        Publish("A");
        await bSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only bound: B after A completion.
        Publish("B");
        Publish("C");
        var receipt = Assert.IsType<ExecutorScreeningReceipt>(await work.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(ExecutorScreeningVerdict.Pass, receipt.Verdict);
        Assert.Equal(new[] { "A", "C", "B" }, fixture.Executor.Requests.Select(request => request.Filter));
        Assert.Equal(2, activeCounts.Max());
        fixture.AssertReleased();

        void Publish(string name)
        {
            lock (handles)
            {
                var item = handles[name];
                item.Handle.Publish(fixture.Result(item.Request));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredQueuedLaneHoldsSlotsUntilPollAndCancellationDrain(bool cancelFirst)
    {
        // Parallel-safe: real handle lifecycle with a fake transport and a fixture-owned root.
        using var fixture = new ExecutorScreeningFixture();
        var pollEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new RemoteLaneRequest(fixture.Entry.Id, "screening-queued", fixture.Candidate.GoalId,
            "sc-12345678", "project", "A", "hash", fixture.Candidate.Commit, fixture.Candidate.Tree,
            fixture.Candidate.MainSha, "screening-manifest");
        var handle = new SshRemoteLaneHandle(request, fixture.Entry, fixture.Root, "candidate", request.Lane,
            fixture.Clock, async (arguments, _, _, _) =>
            {
                if (arguments.Contains("cancel.txt"))
                {
                    cancelEntered.TrySetResult();
                    await releaseCancel.Task;
                }
                else
                {
                    pollEntered.TrySetResult();
                    await releasePoll.Task;
                }
                return new GoalAcceptanceVerifier.CommandResult(1, "");
            }, TimeSpan.Zero, null);
        Task<ExecutorScreeningReceipt?>? work = null;
        try
        {
            await pollEntered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only event bound.
            fixture.Executor.Submit = (_, _) =>
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(61));
                return Task.FromResult(new RemoteLaneSubmission(handle));
            };
            // Lease expiry and cleanup entry run synchronously until the held drain task is awaited.
            work = fixture.Run(Lanes("A"));
            AssertHeld();
            await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var cancellation = Assert.IsAssignableFrom<Task>(handle.QueuedJobCancellation);
            (cancelFirst ? releaseCancel : releasePoll).TrySetResult();
            await (cancelFirst ? cancellation : handle.PollLoop).WaitAsync(TimeSpan.FromSeconds(30));
            AssertHeld(); // The other drain still owns every executor slot.
            (cancelFirst ? releasePoll : releaseCancel).TrySetResult();
            var receipt = Assert.IsType<ExecutorScreeningReceipt>(await work.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(ExecutorScreeningVerdict.Incomplete, receipt.Verdict);
            Assert.Equal(RemoteLaneOutcomeCode.LeaseExpired, Assert.Single(receipt.LaneOutcomes).Outcome);
            Assert.True(handle.PollLoop.IsCompleted);
            Assert.True(cancellation.IsCompleted);
            Assert.Single(ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath));
            fixture.AssertReleased();
        }
        finally
        {
            releasePoll.TrySetResult();
            releaseCancel.TrySetResult();
            handle.Abandon();
            await handle.PollLoop.WaitAsync(TimeSpan.FromSeconds(30));
            if (handle.QueuedJobCancellation is { } cancellation)
                await cancellation.WaitAsync(TimeSpan.FromSeconds(30));
            if (work is not null) await work.WaitAsync(TimeSpan.FromSeconds(30));
        }

        void AssertHeld()
        {
            Assert.True(RemoteExecutorOccupancy.IsOccupied(fixture.Root, fixture.Entry.Id));
            for (var slot = 0; slot < fixture.Entry.Slots; slot++)
            {
                using var claim = RemoteExecutorOccupancy.TryClaimExclusive(fixture.Root, fixture.Entry.Id, slot);
                Assert.Null(claim);
            }
        }
    }

    [Fact]
    public async Task LosingAnySlotClaimSubmitsNothingAndLeavesNoReceipt()
    {
        using var fixture = new ExecutorScreeningFixture();
        using (var held = RemoteExecutorOccupancy.TryClaimExclusive(fixture.Root, fixture.Entry.Id, 1))
        {
            Assert.NotNull(held);
            Assert.Null(await fixture.Run(Lanes("A")));
            Assert.Empty(fixture.Executor.Requests);
            Assert.False(File.Exists(fixture.ReceiptPath));
            Assert.False(RemoteExecutorOccupancy.IsOccupied(fixture.Root, fixture.Entry.Id));
            using var first = RemoteExecutorOccupancy.TryClaimExclusive(fixture.Root, fixture.Entry.Id, 0);
            Assert.NotNull(first);
        }
        fixture.AssertReleased();
    }

    [Fact]
    public async Task UnreachableExecutorStopsSubmissionsAndRecordsEveryLane()
    {
        using var fixture = new ExecutorScreeningFixture();
        fixture.Executor.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, "offline"));
        var receipt = Assert.IsType<ExecutorScreeningReceipt>(await fixture.Run(Lanes("A", "B", "C")));
        Assert.Single(fixture.Executor.Requests);
        Assert.Equal(3, receipt.LaneOutcomes.Count);
        Assert.All(receipt.LaneOutcomes, lane => Assert.Equal(RemoteLaneOutcomeCode.Unreachable, lane.Outcome));
        Assert.Equal(ExecutorScreeningVerdict.Incomplete, receipt.Verdict);
    }

    [Fact]
    public async Task EmptySelectionStillRetiresTheCommitAsIncomplete()
    {
        using var fixture = new ExecutorScreeningFixture();
        var receipt = Assert.IsType<ExecutorScreeningReceipt>(await fixture.Run([]));
        Assert.Equal(ExecutorScreeningVerdict.Incomplete, receipt.Verdict);
        Assert.Single(ExecutorScreeningReceiptLedger.ReadAll(fixture.ReceiptPath));
        Assert.Empty(fixture.Executor.Requests);
    }

    private static AcceptanceTestLane[] Lanes(params string[] names) => names.Select(name => new AcceptanceTestLane(name, name)).ToArray();

    internal sealed class ExecutorScreeningFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "screening-" + Guid.NewGuid().ToString("N"));
        internal string ReceiptPath => ExecutorScreeningReceiptLedger.ResolveStorePath(Root);
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal FakeRemoteLaneExecutor Executor { get; } = new();
        internal RemoteLaneExecutorEntry Entry { get; } = new("laptop", 60, "ssh", Slots: 2);
        internal RemoteLaneExecutorConfiguration Configuration { get; set; }
        internal ExecutorScreeningCandidate Candidate { get; }

        internal ExecutorScreeningFixture()
        {
            Directory.CreateDirectory(Root);
            Configuration = new([Entry], ["*"], null) { AllInfrastructureLanes = true, Screening = new() { Mode = "shadow" } };
            Candidate = new("0123456789abcdef", GoalStatus.Active, new string('a', 40), new string('b', 40),
                new string('c', 40), Clock.GetUtcNow(), true, true, Root);
            Executor.Submit = (request, _) => Task.FromResult(PassingSubmission(request));
        }

        internal Task<ExecutorScreeningReceipt?> Run(IReadOnlyList<AcceptanceTestLane> lanes, TimeSpan? timeout = null) =>
            ExecutorScreeningRunner.RunAsync(Candidate, Entry, new(lanes, []), Configuration, Executor, Root,
                ReceiptPath, Guid.NewGuid().ToString("N"), timeout ?? TimeSpan.FromHours(1), Clock, TimeSpan.FromMilliseconds(1));

        internal RemoteLaneSubmission PassingSubmission(RemoteLaneRequest request) => Submission(request);
        internal RemoteLaneSubmission Submission(RemoteLaneRequest request, bool failure = false,
            bool wrongCommit = false, int exit = 0, bool missingTrx = false, bool incomplete = false)
        {
            var handle = new FakeRemoteLaneExecutor.Handle(Clock.GetUtcNow());
            handle.Publish(Result(request, failure, wrongCommit, exit, missingTrx, incomplete));
            return new(handle);
        }

        internal RemoteLaneResult Result(RemoteLaneRequest request, bool failure = false, bool wrongCommit = false,
            int exit = 0, bool missingTrx = false, bool incomplete = false)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".trx");
            if (!missingTrx)
            {
                new XDocument(new XElement("TestRun",
                    new XElement("TestDefinitions", new XElement("UnitTest", new XAttribute("id", "test-1"),
                        new XAttribute("name", "Suite.ExampleTests.Broken"), new XElement("TestMethod",
                            new XAttribute("className", "Suite.ExampleTests"), new XAttribute("name", "Broken")))),
                    new XElement("Results", new XElement("UnitTestResult", new XAttribute("testId", "test-1"),
                        new XAttribute("testName", "Suite.ExampleTests.Broken"), new XAttribute("outcome", failure ? "Failed" : "Passed"))),
                    new XElement("ResultSummary", new XAttribute("outcome", failure ? "Failed" : "Completed"),
                        new XElement("Counters", new XAttribute("total", incomplete ? 2 : 1), new XAttribute("executed", 1),
                            new XAttribute("passed", failure ? 0 : 1), new XAttribute("failed", failure ? 1 : 0),
                            new XAttribute("notExecuted", 0))))).Save(path);
            }
            return new(request.ExecutorId, request.Lane, request.FilterHash, wrongCommit ? "wrong" : request.VerifyingCommitSha,
                request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, failure ? 1 : exit, missingTrx ? [] : [path]);
        }

        internal void AssertReleased()
        {
            Assert.False(RemoteExecutorOccupancy.IsOccupied(Root, Entry.Id));
            for (var slot = 0; slot < Entry.Slots; slot++)
            {
                using var claim = RemoteExecutorOccupancy.TryClaimExclusive(Root, Entry.Id, slot);
                Assert.NotNull(claim);
            }
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
