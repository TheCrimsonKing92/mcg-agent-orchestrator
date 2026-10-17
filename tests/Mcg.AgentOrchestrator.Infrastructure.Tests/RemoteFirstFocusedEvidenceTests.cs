using Mcg.AgentOrchestrator.Infrastructure;

[Collection("IsolatedProcessSpawning")]
public sealed class RemoteFirstFocusedEvidenceTests : RemoteFirstFocusedEvidenceFixture
{
    [Fact]
    public async Task Accepted_BoundReceipt_ReturnsRemoteVerdictWithCustody()
    {
        using var scenario = CreateScenario();
        var fake = scenario.CompletedExecutor();
        var result = Assert.Single((await scenario.DirectAsync([FocusedCheck()])).Results);
        Assert.True(result.Passed);
        Assert.Equal("remote-executor=fixture-executor", result.ResultSummary);
        Assert.Equal(3, result.ExecutedTestCount);
        Assert.Equal("direct-attempt", result.TestResultAttemptId);
        Assert.Equal(0, scenario.LocalCalls);
        Assert.All(result.TestResultPaths!, path =>
        {
            Assert.True(File.Exists(path));
            Assert.StartsWith(Path.GetDirectoryName(scenario.Prefix)!, path);
        });
        var request = Assert.Single(fake.Requests);
        Assert.Equal(scenario.Head, request.VerifyingCommitSha);
        Assert.Equal(scenario.Tree, request.CandidateTreeSha);
        Assert.Equal(scenario.Head, request.MainSha);
        Assert.Equal(GoalAcceptanceVerifier.ShortHash(request.Filter), request.FilterHash);
        Assert.Equal(RemoteFocusedEvidenceShadow.ManifestIdentity(Project, request.Filter, "mtp"), request.ManifestIdentity);
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(scenario.Health()).Outcome);
        Assert.Null(Assert.Single(scenario.Health()).EvidenceShadow);
    }

    [Fact]
    public async Task RemoteRed_NamedTestFailure_ReturnsFailedVerdictWithoutLocalRun()
    {
        using var scenario = CreateScenario();
        scenario.CompletedExecutor(red: true);
        var result = Assert.Single((await scenario.DirectAsync([FocusedCheck()])).Results);
        Assert.False(result.Passed);
        Assert.Equal(["AlphaTests.RemoteFailure"], result.FailingTestIdentities);
        Assert.All(result.TestResultPaths!, path => Assert.True(File.Exists(path)));
        Assert.Equal(0, scenario.LocalCalls);
        Assert.Equal(RemoteLaneOutcomeCode.RemoteRed, Assert.Single(scenario.Health()).Outcome);
    }

    [Theory]
    [InlineData("unreachable", "Unreachable")]
    [InlineData("transport-unavailable", "TransportUnavailable")]
    [InlineData("executor-busy", "ExecutorBusy")]
    [InlineData("commit", "BindingMismatchCommit")]
    [InlineData("tree", "BindingMismatchTree")]
    [InlineData("main", "BindingMismatchMain")]
    [InlineData("filter", "BindingMismatchFilter")]
    [InlineData("lane", "BindingMismatchFilter")]
    [InlineData("executor", "BindingMismatchExecutor")]
    [InlineData("manifest", "BindingMismatchManifest")]
    [InlineData("trx", "TrxIncomplete")]
    [InlineData("exception", "Unreachable")]
    public async Task RemoteFault_RejectsVerdictAndRecordsFallback(string fault, string expected)
    {
        using var scenario = CreateScenario();
        var fake = scenario.CompletedExecutor(change: result => fault switch
        {
            "commit" => result with { VerifyingCommitSha = "wrong" },
            "tree" => result with { ObservedTreeSha = "wrong" },
            "main" => result with { MainSha = "wrong" },
            "filter" => result with { FilterHash = "wrong" },
            "lane" => result with { Lane = "wrong" },
            "executor" => result with { ExecutorId = "wrong" },
            "manifest" => result with { ManifestIdentity = "wrong" },
            "trx" => result with { TestResultPaths = [] },
            _ => result
        });
        if (fault is "unreachable" or "transport-unavailable" or "executor-busy")
            fake.Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, fault));
        if (fault == "exception") fake.Submit = (_, _) => throw new InvalidOperationException("fixture-submit-fault");
        var result = Assert.Single((await scenario.DirectAsync([FocusedCheck()])).Results);
        Assert.Equal("local-passed", result.OutputTail);
        Assert.Null(result.ResultSummary);
        Assert.Equal(1, scenario.LocalCalls);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(Enum.Parse<RemoteLaneOutcomeCode>(expected), row.Outcome);
        Assert.StartsWith("remote-first-fallback", row.Reason);
        Assert.Contains(scenario.Progress, line => line.Contains("remote-first-fallback", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "LeaseExpired")]
    [InlineData(true, "LaneTimeout")]
    public async Task PendingLane_InjectedClock_RecordsTimeout(bool fresh, string expected)
    {
        using var scenario = CreateScenario();
        scenario.Configure(lease: fresh ? 600 : 5);
        var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
        var advanced = 0;
        handle.OnHeartbeatRead = () =>
        {
            if (Interlocked.Exchange(ref advanced, 1) == 0)
            {
                scenario.Clock.Advance(TimeSpan.FromSeconds(fresh ? 61 : 6));
                if (fresh) handle.Heartbeat(scenario.Clock.GetUtcNow());
            }
        };
        scenario.Overrides.RemoteLaneExecutorForTests = new FakeRemoteLaneExecutor
        { Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(handle)) };
        var result = Assert.Single((await scenario.DirectAsync([FocusedCheck()])).Results);
        Assert.True(result.Passed);
        Assert.Equal(1, scenario.LocalCalls);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(Enum.Parse<RemoteLaneOutcomeCode>(expected), row.Outcome);
        Assert.StartsWith("remote-first-fallback", row.Reason);
        await Event(handle.Abandoned.Task, "timed out handle abandoned");
    }

    [Fact]
    public async Task NonCooperativeSubmit_GraceExpired_FallsBackAndFencesLateResult()
    {
        using var scenario = CreateScenario();
        scenario.Configure(grace: 3);
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<RemoteLaneSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Overrides.RemoteLaneExecutorForTests = new FakeRemoteLaneExecutor
        { Submit = (_, _) => { submitted.TrySetResult(); return late.Task; } };
        var pending = scenario.DirectAsync([FocusedCheck()]);
        await Event(submitted.Task, "non-cooperative submission started");
        await Event(scenario.Clock.TimerCreated.Task, "grace timer created");
        scenario.Clock.Advance(TimeSpan.FromSeconds(64));
        await Event(pending, "grace fallback completed");
        Assert.Equal(1, scenario.LocalCalls);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.CancelledAfterGrace, row.Outcome);
        Assert.StartsWith("remote-first-fallback", row.Reason);
        var handle = new FakeRemoteLaneExecutor.Handle(scenario.Clock.GetUtcNow());
        late.SetResult(new(handle));
        await Event(handle.Abandoned.Task, "late handle abandoned");
        Assert.Single(scenario.Health());
    }

    [Fact]
    public async Task NoIdleExecutor_RunsLocalAndRecordsNoSlot()
    {
        using var scenario = CreateScenario();
        using var claim = RemoteExecutorOccupancy.TryClaimExclusive(
            AcceptancePartitionVerdictCache.ResolveHostStateRoot(scenario.Root), "fixture-executor");
        Assert.NotNull(claim);
        var fake = scenario.CompletedExecutor();
        await scenario.DirectAsync([FocusedCheck()]);
        Assert.Empty(fake.Requests);
        Assert.Equal(1, scenario.LocalCalls);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.ShadowSkippedNoExecutor, row.Outcome);
        Assert.StartsWith("remote-first-fallback", row.Reason);
    }

    [Theory]
    [InlineData("foreign-key", false, false)]
    [InlineData("machine-local", true, false)]
    [InlineData("machine-local", false, true)]
    public async Task ResourceKeys_OnlyEligibleMachineLocalLaneIsSubmitted(string key, bool build, bool remote)
    {
        using var scenario = CreateScenario();
        var fake = scenario.CompletedExecutor();
        await scenario.DirectAsync([FocusedCheck(keys: [key], buildChange: build)]);
        Assert.Equal(remote ? 1 : 0, fake.Requests.Count);
        Assert.Equal(remote ? 0 : 1, scenario.LocalCalls);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(remote ? RemoteLaneOutcomeCode.Accepted : RemoteLaneOutcomeCode.NotEligibleExclusiveResource, row.Outcome);
        if (!remote) Assert.StartsWith("remote-first-fallback", row.Reason);
    }

    [Theory]
    [InlineData(false, 1, 0)]
    [InlineData(true, 3, 1)]
    public async Task RemoteFailure_ContinuePolicy_TruncatesInOriginalOrder(bool keepGoing, int count, int local)
    {
        using var scenario = CreateScenario();
        scenario.Configure(slots: 3);
        scenario.Overrides.RemoteLaneExecutorForTests = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) => Task.FromResult(request.Filter.Contains("BetaTests", StringComparison.Ordinal)
                ? new RemoteLaneSubmission(null, "fixture-unreachable")
                : scenario.Completion(request, red: request.Filter.Contains("AlphaTests", StringComparison.Ordinal)))
        };
        var result = await scenario.DirectAsync([FocusedCheck(), FocusedCheck("BetaTests"), FocusedCheck("GammaTests")], keepGoing);
        Assert.Equal(count, result.Results.Count);
        Assert.False(result.Results[0].Passed);
        Assert.Equal(local, scenario.LocalCalls);
        if (keepGoing) Assert.Equal(["reviewer focused evidence: AlphaTests", "reviewer focused evidence: BetaTests", "reviewer focused evidence: GammaTests"],
            result.Results.Select(check => check.Name));
    }

    [Fact]
    public async Task LocalFailure_ContinueFalse_OmitsLaterRemoteVerdicts()
    {
        using var scenario = CreateScenario();
        scenario.CompletedExecutor();
        var localCheck = new GoalAcceptanceVerifier.AcceptanceManifestCheck { Name = "local" };
        var result = await scenario.DirectAsync([localCheck, FocusedCheck()], local: checks =>
        {
            Assert.Same(localCheck, Assert.Single(checks));
            return Task.FromResult<IReadOnlyList<AcceptanceCheckResult>>([new("local", false, 1, "local-red")]);
        });
        Assert.Equal("local", Assert.Single(result.Results).Name);
        Assert.False(result.Results[0].Passed);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesWithoutStartingLocalRun()
    {
        using var scenario = CreateScenario();
        using var cancellation = new CancellationTokenSource();
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Overrides.RemoteLaneExecutorForTests = new FakeRemoteLaneExecutor
        { Submit = async (_, token) => { submitted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new(null); } };
        var pending = scenario.DirectAsync([FocusedCheck()], token: cancellation.Token);
        await Event(submitted.Task, "submission before caller cancellation");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, scenario.LocalCalls);
        Assert.StartsWith("remote-first-fallback", Assert.Single(scenario.Health()).Reason);
    }

    [Fact]
    public async Task NonCandidates_RunLocalWithoutRemoteRequestsOrLedgerRows()
    {
        using var scenario = CreateScenario();
        var fake = scenario.CompletedExecutor();
        GoalAcceptanceVerifier.AcceptanceManifestCheck[] checks =
        [
            new() { Name = "unfiltered", Project = Project, IsFocusedEvidenceSelection = true },
            new() { Name = "other-project", Project = "tests/Core.Tests/Core.Tests.csproj", IsFocusedEvidenceSelection = true,
                Arguments = ["--filter", "FullyQualifiedName~AlphaTests"] },
            new() { Name = "not-focused", Project = Project, Arguments = ["--filter", "FullyQualifiedName~AlphaTests"] }
        ];
        var result = await scenario.DirectAsync(checks, keepGoing: true);
        Assert.Equal(3, result.Results.Count);
        Assert.Equal(3, scenario.LocalCalls);
        Assert.Empty(fake.Requests);
        Assert.Empty(scenario.Health());
    }
}
