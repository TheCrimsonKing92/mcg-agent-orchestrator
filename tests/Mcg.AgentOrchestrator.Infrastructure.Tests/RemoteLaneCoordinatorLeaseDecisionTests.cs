using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: direct coordinator instances, unique filesystem roots, no global overrides.
public sealed class RemoteLaneCoordinatorLeaseDecisionTests
{
    [Xunit.Fact]
    public async Task ExpiryDecision_SurvivesHeartbeatMutationDuringCapture()
    {
        using var fixture = new Fixture();
        using var handle = new DiagnosticHandle(fixture.Start.AddSeconds(1));
        handle.OnCancellation = () => fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        var failedPoll = new RemoteLaneStep("poll", 1, false, fixture.Start,
            fixture.Start.AddSeconds(1), "original poll error");
        handle.Poll = new(3, 1, "running", fixture.Start.AddSeconds(2), failedPoll);
        handle.Fetches.Add(failedPoll);
        handle.SetStatus("{\"state\":\"running\",\"poll\":3}");
        fixture.Configure(handle, submissionSeconds: 62);

        var run = fixture.RunAsync();
        await CaptureEntered(handle);
        var snapshotsAtCaptureEntry = handle.SnapshotCount;
        fixture.Clock.Advance(TimeSpan.FromSeconds(7));
        handle.Heartbeat = fixture.Clock.GetUtcNow();
        handle.Poll = new(9, 0, "completed", handle.Heartbeat, null);
        handle.Fetches.Clear();
        // Dispose the original JSON document as well as replacing the current status.
        handle.SetStatus("{\"state\":\"completed\",\"poll\":9}");
        var logPath = Path.Combine(fixture.Root, "runner.log");
        File.WriteAllText(logPath, "later runner output");
        handle.CaptureResult.SetResult(new(logPath));

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30)); // Hang guard: fallback did not finish.
        Assert.Null(outcome.Accepted);
        Assert.Same(handle, outcome.Handle);
        var row = fixture.Row();
        Assert.Equal(RemoteLaneOutcomeCode.LeaseExpired, row.Outcome);
        using var json = fixture.Json();
        var attempt = json.RootElement.GetProperty("attempt");
        Assert.True(attempt.TryGetProperty("lease_decision", out var decision),
            "Persisted lease decision is missing before asynchronous diagnostics.");
        AssertDecision(decision, fixture.Start, fixture.Start.AddSeconds(62),
            fixture.Start.AddSeconds(1), TimeSpan.FromSeconds(61));
        var snapshot = decision.GetProperty("snapshot");
        Assert.Equal(3, snapshot.GetProperty("poll").GetProperty("poll_count").GetInt32());
        Assert.Equal("running", snapshot.GetProperty("poll").GetProperty("last_state").GetString());
        Assert.Equal(fixture.Start.AddSeconds(2),
            snapshot.GetProperty("poll").GetProperty("last_heartbeat_change_at").GetDateTimeOffset());
        Assert.Equal("original poll error", snapshot.GetProperty("poll")
            .GetProperty("last_failed_poll").GetProperty("stderr_tail").GetString());
        Assert.Equal("poll", Assert.Single(snapshot.GetProperty("fetches").EnumerateArray())
            .GetProperty("name").GetString());
        Assert.Equal("running", snapshot.GetProperty("last_status").GetProperty("state").GetString());
        Assert.Equal(snapshot.GetProperty("poll").GetRawText(), attempt.GetProperty("poll").GetRawText());
        Assert.Equal(snapshot.GetProperty("fetches").GetRawText(), attempt.GetProperty("fetches").GetRawText());
        Assert.Equal(snapshot.GetProperty("last_status").GetRawText(), attempt.GetProperty("last_status").GetRawText());
        Assert.Equal(logPath, attempt.GetProperty("runner_log_path").GetString());
        Assert.True(File.Exists(logPath));
        var timing = attempt.GetProperty("runner_log_capture_timing");
        Assert.Equal(fixture.Start.AddSeconds(64), timing.GetProperty("started_at").GetDateTimeOffset());
        Assert.Equal(fixture.Start.AddSeconds(71), timing.GetProperty("ended_at").GetDateTimeOffset());
        Assert.Equal(fixture.Start.AddSeconds(71), row.ObservedAt);
        // ReadAll must retain the additive detail, not merely leave it in raw JSON.
        var restored = JsonSerializer.SerializeToElement(row.Attempt,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        Assert.Equal(decision.GetRawText(), restored.GetProperty("lease_decision").GetRawText());
        Assert.Equal(timing.GetRawText(), restored.GetProperty("runner_log_capture_timing").GetRawText());
        Assert.Equal(1, snapshotsAtCaptureEntry);
        Assert.Equal(1, handle.SnapshotCount);
        Assert.Equal(1, handle.CancellationCount);
        fixture.AssertReleasedAndRetired();
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SnapshotAndCaptureFailures_PreserveExpiry(bool cancelCapture)
    {
        using var fixture = new Fixture();
        using var handle = new DiagnosticHandle(fixture.Start) { SnapshotFails = true };
        fixture.Configure(handle, submissionSeconds: 61);
        var run = fixture.RunAsync();
        await CaptureEntered(handle);
        fixture.Clock.Advance(TimeSpan.FromSeconds(4));
        handle.CaptureResult.SetException(cancelCapture
            ? new OperationCanceledException("fixture capture cancelled")
            : new IOException("fixture capture failed"));

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(30)); // Hang guard: fallback did not finish.
        Assert.Null(outcome.Accepted);
        Assert.Equal(RemoteLaneOutcomeCode.LeaseExpired, fixture.Row().Outcome);
        using var json = fixture.Json();
        var attempt = json.RootElement.GetProperty("attempt");
        var decision = attempt.GetProperty("lease_decision");
        AssertDecision(decision, fixture.Start, fixture.Start.AddSeconds(61),
            fixture.Start, TimeSpan.FromSeconds(61));
        Assert.Equal(JsonValueKind.Null, decision.GetProperty("snapshot").ValueKind);
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("poll").ValueKind);
        Assert.Equal(1, handle.SnapshotCount);
        Assert.Equal(1, handle.CancellationCount);
        var timing = attempt.GetProperty("runner_log_capture_timing");
        Assert.Equal(fixture.Start.AddSeconds(61), timing.GetProperty("started_at").GetDateTimeOffset());
        Assert.Equal(fixture.Start.AddSeconds(65), timing.GetProperty("ended_at").GetDateTimeOffset());
        fixture.AssertReleasedAndRetired();
    }

    [Xunit.Fact]
    public async Task WithoutDiagnostics_StillPersistsDecision()
    {
        using var fixture = new Fixture();
        var handle = new FakeRemoteLaneExecutor.Handle(fixture.Start);
        fixture.Configure(handle, submissionSeconds: 61);
        Assert.Null((await fixture.RunAsync()).Accepted);
        Assert.Equal(RemoteLaneOutcomeCode.LeaseExpired, fixture.Row().Outcome);
        using var json = fixture.Json();
        var attempt = json.RootElement.GetProperty("attempt");
        AssertDecision(attempt.GetProperty("lease_decision"), fixture.Start,
            fixture.Start.AddSeconds(61), fixture.Start, TimeSpan.FromSeconds(61));
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("lease_decision").GetProperty("snapshot").ValueKind);
        Assert.Equal(JsonValueKind.Null, attempt.GetProperty("runner_log_capture_timing").ValueKind);
        fixture.AssertReleasedAndRetired();
    }

    [Xunit.Fact]
    public async Task ExactLeaseThreshold_DoesNotExpireUntilNextClockAdvance()
    {
        using var fixture = new Fixture();
        var handle = new FakeRemoteLaneExecutor.Handle(fixture.Start);
        handle.OnPolled = () => fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Configure(handle, submissionSeconds: 60);
        Assert.Null((await fixture.RunAsync()).Accepted);
        Assert.True(handle.Polled.Task.IsCompleted, "Exact threshold must reach result polling before expiry.");
        Assert.Equal(RemoteLaneOutcomeCode.LeaseExpired, fixture.Row().Outcome);
        using var json = fixture.Json();
        AssertDecision(json.RootElement.GetProperty("attempt").GetProperty("lease_decision"),
            fixture.Start, fixture.Start.AddSeconds(61), fixture.Start, TimeSpan.FromSeconds(61));
    }

    [Xunit.Theory]
    [Xunit.InlineData(120, false)]
    [Xunit.InlineData(121, true)]
    public async Task LaneTimeout_PreservesStrictThresholdAndPrecedence(int elapsed, bool timedOut)
    {
        using var fixture = new Fixture();
        fixture.Configure(new FakeRemoteLaneExecutor.Handle(fixture.Start), submissionSeconds: elapsed);
        Assert.Null((await fixture.RunAsync()).Accepted);
        Assert.Equal(timedOut ? RemoteLaneOutcomeCode.LaneTimeout : RemoteLaneOutcomeCode.LeaseExpired,
            fixture.Row().Outcome);
        using var json = fixture.Json();
        Assert.Equal(timedOut ? JsonValueKind.Null : JsonValueKind.Object,
            json.RootElement.GetProperty("attempt").GetProperty("lease_decision").ValueKind);
        fixture.AssertReleasedAndRetired();
    }

    [Xunit.Fact]
    public async Task LegacyAttemptDetail_DeserializesWithoutNewMembers()
    {
        using var fixture = new Fixture();
        fixture.Configure(new FakeRemoteLaneExecutor.Handle(fixture.Start), submissionSeconds: 61);
        await fixture.RunAsync();
        var json = JsonNode.Parse(File.ReadAllText(fixture.Ledger))!;
        var attempt = json["attempt"]!.AsObject();
        attempt.Remove("lease_decision");
        attempt.Remove("runner_log_capture_timing");
        File.WriteAllText(fixture.Ledger, json.ToJsonString() + Environment.NewLine);

        var row = fixture.Row();
        Assert.Equal(RemoteLaneOutcomeCode.LeaseExpired, row.Outcome);
        Assert.NotNull(row.Attempt);
        var restored = JsonSerializer.SerializeToElement(row.Attempt,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("lease_decision").ValueKind);
        Assert.Equal(JsonValueKind.Null, restored.GetProperty("runner_log_capture_timing").ValueKind);
    }

    private static async Task CaptureEntered(DiagnosticHandle handle)
    {
        // This bound detects a hang; all ordering is established by the capture-entry signal.
        await handle.CaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void AssertDecision(JsonElement decision, DateTimeOffset submittedAt,
        DateTimeOffset observedAt, DateTimeOffset heartbeat, TimeSpan age)
    {
        Assert.Equal(observedAt, decision.GetProperty("observed_at").GetDateTimeOffset());
        Assert.Equal(submittedAt, decision.GetProperty("submitted_at").GetDateTimeOffset());
        Assert.Equal(heartbeat, decision.GetProperty("effective_heartbeat").GetDateTimeOffset());
        Assert.Equal(age, TimeSpan.Parse(decision.GetProperty("heartbeat_age").GetString()!));
        Assert.True(age > TimeSpan.FromSeconds(decision.GetProperty("lease_seconds").GetInt32()));
        Assert.Equal(60, decision.GetProperty("lease_seconds").GetInt32());
        Assert.Equal("executor", decision.GetProperty("executor_id").GetString());
        Assert.Equal("runner-host", decision.GetProperty("runner_alias").GetString());
        Assert.Equal(Fixture.Lane, decision.GetProperty("lane").GetString());
        Assert.Equal("attempt", decision.GetProperty("gate_attempt_id").GetString());
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Lane = "infrastructure tests: LeaseDecision";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "remote-lease-decision-" + Guid.NewGuid().ToString("N"));
        internal ManualRemoteLaneClock Clock { get; } = new();
        internal DateTimeOffset Start { get; }
        internal string Ledger => RemoteExecutorHealthLedger.ResolveStorePath(Root);
        private readonly RemoteLaneExecutorEntry _entry = new("executor", 60, RunnerAlias: "runner-host");
        private readonly Check _check = new()
        {
            Name = Lane, Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~LeaseDecisionTests"]
        };
        private RemoteLaneCoordinator? _coordinator;
        internal Fixture() { Start = Clock.GetUtcNow(); Directory.CreateDirectory(Root); }
        internal void Configure(IRemoteLaneHandle handle, int submissionSeconds)
        {
            var executor = new FakeRemoteLaneExecutor
            {
                Submit = (_, _) =>
                {
                    Clock.Advance(TimeSpan.FromSeconds(submissionSeconds));
                    return Task.FromResult(new RemoteLaneSubmission(handle));
                }
            };
            _coordinator = new(new([_entry], [Lane], null),
                new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
                Root, null, executor, Clock, TimeSpan.FromMilliseconds(1), null);
            Assert.True(_coordinator.IsEligible(_check));
            Assert.Equal(_entry, _coordinator.TryClaimIdleExecutor());
        }
        internal Task<RemoteLaneOutcome> RunAsync() =>
            _coordinator!.RunRemoteAsync(_check, _entry, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(120), CancellationToken.None);
        internal RemoteExecutorHealthRecord Row() => Assert.Single(RemoteExecutorHealthLedger.ReadAll(Ledger));
        internal JsonDocument Json() => JsonDocument.Parse(Assert.Single(File.ReadAllLines(Ledger)));
        internal void AssertReleasedAndRetired()
        {
            Assert.Null(_coordinator!.TryClaimIdleExecutor());
            using var claim = RemoteExecutorOccupancy.TryClaimExclusive(Root, _entry.Id, 0);
            Assert.NotNull(claim);
        }
        public void Dispose() { _coordinator?.Dispose(); Directory.Delete(Root, recursive: true); }
    }

    private sealed class DiagnosticHandle(DateTimeOffset heartbeat)
        : IRemoteLaneHandle, IRemoteLaneAttemptDiagnosticsSource, IRemoteLaneQueuedJobCancellation, IDisposable
    {
        internal DateTimeOffset Heartbeat { get; set; } = heartbeat;
        internal RemoteLanePollSummary Poll { get; set; } = new(1, 0, "running", heartbeat, null);
        internal List<RemoteLaneStep> Fetches { get; } = [];
        internal TaskCompletionSource CaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<RemoteLaneRunnerLogCapture> CaptureResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool SnapshotFails { get; init; }
        internal int SnapshotCount { get; private set; }
        internal int CancellationCount { get; private set; }
        internal Action? OnCancellation { get; set; }
        private JsonDocument? _status;
        public DateTimeOffset? NewestHeartbeat => Heartbeat;
        public RemoteLaneResult? TryGetResult() => null;
        public void Abandon() { }
        public void RequestQueuedJobCancellation() { CancellationCount++; OnCancellation?.Invoke(); }
        internal void SetStatus(string json) { _status?.Dispose(); _status = JsonDocument.Parse(json); }
        public RemoteLaneHandleDiagnostics Snapshot()
        {
            SnapshotCount++;
            if (SnapshotFails) throw new IOException("fixture snapshot unavailable");
            return new(Poll, Fetches, _status?.RootElement);
        }
        public Task<RemoteLaneRunnerLogCapture> CaptureRunnerLogAsync(CancellationToken cancellationToken)
        {
            CaptureStarted.SetResult();
            return CaptureResult.Task.WaitAsync(cancellationToken);
        }
        public void Dispose() => _status?.Dispose();
    }
}
