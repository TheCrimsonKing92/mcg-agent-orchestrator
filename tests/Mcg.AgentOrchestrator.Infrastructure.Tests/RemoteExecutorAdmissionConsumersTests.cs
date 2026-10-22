using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AdmissionScenario = RemoteExecutorAdmissionTests.Scenario;

// Parallel-safe: direct consumer seams only, private repositories/stores, per-instance overrides.
// No shared build-slot fixture or native test host is used by these tests.
public sealed class RemoteExecutorAdmissionConsumersTests : RemoteFirstFocusedEvidenceFixture
{
    [Theory]
    [InlineData("remote-first")]
    [InlineData("shadow")]
    public async Task Focused_DisabledThenReenabled_UsesLocalThenFutureRemote(string mode)
    {
        using var scenario = CreateScenario();
        scenario.Configure(mode, sample: 1);
        var fake = scenario.CompletedExecutor();
        SetAdmission(scenario, false);
        var check = FocusedCheck();
        if (mode == "remote-first")
        {
            var result = Assert.Single((await scenario.DirectAsync([check])).Results);
            Assert.True(result.Passed);
            Assert.Equal(1, scenario.LocalCalls);
        }
        else
        {
            await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(scenario.Root, scenario.Goal, null,
                options: new(RunId: "disabled-shadow", ResultsPrefix: scenario.Prefix));
            await using var shadow = RemoteFocusedEvidenceShadow.Start(scenario.Overrides, scenario.Clock, scenario.Root,
                [check], scenario.Goal, (IAcceptanceRunExecutionContext)owner, default);
            await shadow.CompleteAsync([new(check.Name, true, 0, "local")]);
        }
        Assert.Empty(fake.Requests);
        var refused = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.ShadowSkippedNoExecutor, refused.Outcome);
        Assert.Contains("no-enabled-executor", refused.Reason);
        Assert.Null(refused.FaultOwner);
        SetAdmission(scenario, true);
        if (mode == "remote-first") Assert.NotNull(Assert.Single((await scenario.DirectAsync([check])).Results).ResultSummary);
        else
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.Submit = (request, _) =>
            {
                var submission = scenario.Completion(request);
                ((FakeRemoteLaneExecutor.Handle)submission.Handle!).OnPolled = () => settled.TrySetResult();
                return Task.FromResult(submission);
            };
            await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(scenario.Root, scenario.Goal, null,
                options: new(RunId: "enabled-shadow", ResultsPrefix: scenario.Prefix));
            await using var shadow = RemoteFocusedEvidenceShadow.Start(scenario.Overrides, scenario.Clock, scenario.Root,
                [check], scenario.Goal, (IAcceptanceRunExecutionContext)owner, default);
            await Event(settled.Task, "reenabled shadow result polled");
            await shadow.CompleteAsync([new(check.Name, true, 0, "local")]);
        }
        Assert.Single(fake.Requests);
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, scenario.Health().Last().Outcome);
        Assert.Equal(RemoteLaneOutcomeCode.ShadowSkippedNoExecutor, scenario.Health().First().Outcome);
    }

    [Theory]
    [InlineData("remote-first", false)]
    [InlineData("remote-first", true)]
    [InlineData("shadow", false)]
    [InlineData("shadow", true)]
    public async Task Focused_DisableDuringSubmission_SettlesCapturedJobOnce(string mode, bool disable)
    {
        using var scenario = CreateScenario();
        using var receipts = new AdmissionScenario();
        scenario.Configure(mode, sample: 1);
        var release = new TaskCompletionSource<RemoteLaneSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeRemoteLaneExecutor { Submit = (_, _) => release.Task };
        scenario.Overrides.RemoteLaneExecutorForTests = fake;
        await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(scenario.Root, scenario.Goal, null,
            options: new(RunId: "owned-shadow", ResultsPrefix: scenario.Prefix));
        var check = FocusedCheck();
        await using var shadow = mode == "shadow" ? RemoteFocusedEvidenceShadow.Start(scenario.Overrides, scenario.Clock, scenario.Root,
            [check], scenario.Goal, (IAcceptanceRunExecutionContext)owner, default) : null;
        var direct = mode == "remote-first" ? scenario.DirectAsync([check]) : null;
        try
        {
            var request = await RemoteExecutorAdmissionTests.Event(fake.Submitted.Task, "focused submission entered"); // Hang guard: owned submission.
            if (disable) SetAdmission(scenario, false);
            using var future = receipts.Coordinator(RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(scenario.ConfigurationPath), root: scenario.Root);
            Assert.Null(future.TryClaimIdleExecutor(out var reason));
            Assert.Equal(disable ? "no-enabled-executor" : null, reason);
            // Focused verdicts require named selection coverage, not just completion counters.
            // Reuse the focused receipt fixture while retaining the counting cleanup handle.
            var completion = scenario.Completion(request);
            var completed = Assert.IsType<RemoteLaneResult>(completion.Handle!.TryGetResult());
            var coverage = GoalAcceptanceVerifierTestTelemetry.InspectFocusedEvidenceSelectionCoverage(
                check.FocusedEvidenceSelections, completed.TestResultPaths);
            Assert.Empty(coverage.UncoveredSelections);
            Assert.Empty(coverage.UnreadableReceiptPaths);
            var handle = new RemoteExecutorAdmissionTests.CountingHandle(completed, scenario.Clock.GetUtcNow());
            release.SetResult(new(handle));
            if (direct is not null)
            {
                var result = Assert.Single((await RemoteExecutorAdmissionTests.Event(direct, "focused settlement")).Results);
                Assert.NotNull(result.ResultSummary);
                Assert.Equal("remote-executor=" + request.ExecutorId, result.ResultSummary);
                Assert.True(result.Passed);
                Assert.Equal(3, result.ExecutedTestCount);
                Assert.NotEmpty(result.TestResultPaths!);
            }
            else await shadow!.CompleteAsync([new(check.Name, true, 0, "local")]);
            await Event(handle.Abandoned.Task, "captured focused handle cleanup");
            Assert.Equal(1, handle.Abandons);
            // Remote-first already requests cleanup cancellation even for a completed handle.
            // Both admission states must retain that baseline; shadow skips it after observation.
            Assert.Equal(mode == "remote-first" ? 1 : 0, handle.Cancellations);
            Assert.Single(fake.Requests);
            Assert.Equal(0, scenario.LocalCalls);
            Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(scenario.Health()).Outcome);
            Assert.False(RemoteExecutorOccupancy.IsOccupied(scenario.Root, request.ExecutorId));
        }
        finally
        {
            release.TrySetResult(new(null, "fixture-cleanup"));
            if (direct is not null) await Event(direct, "owned focused job drained");
            else await shadow!.CompleteAsync([new(check.Name, true, 0, "local")]);
        }
    }

    [Fact]
    public async Task Screening_DisabledSnapshot_DoesNotProbeClaimOrSubmit()
    {
        using var scenario = new AdmissionScenario();
        var disabled = new RemoteLaneExecutorEntry("one", 60, "ssh", AdmissionEnabled: false);
        Assert.Null(ExecutorScreeningPlanner.PickIdleExecutor([disabled], _ => throw new Exception("disabled occupancy probe")));
        var peer = disabled with { Id = "two", AdmissionEnabled = true };
        Assert.Equal(peer, ExecutorScreeningPlanner.PickIdleExecutor([disabled, peer], _ => false));
        var candidate = new ExecutorScreeningCandidate("goal", GoalStatus.Active, "commit", "tree", "main", scenario.Clock.GetUtcNow(), true, true, scenario.Root);
        var receiptPath = Path.Combine(scenario.Root, "screening.jsonl");
        Assert.Null(await ExecutorScreeningRunner.RunAsync(candidate, disabled, new([new("A", "filter", 1)], []),
            scenario.Configuration(false), scenario.Executor, scenario.Root, receiptPath, "disabled", TimeSpan.FromMinutes(1), scenario.Clock, TimeSpan.Zero));
        Assert.Empty(scenario.Executor.Requests);
        Assert.False(File.Exists(receiptPath));
        Assert.False(RemoteExecutorOccupancy.IsOccupied(scenario.Root, "one"));
    }

    [Fact]
    public async Task Screening_DisableAfterCapture_KeepsReceiptAndReleasesClaims()
    {
        using var scenario = new AdmissionScenario();
        var configuration = scenario.Configuration();
        var candidate = new ExecutorScreeningCandidate("goal", GoalStatus.Active, "commit", "tree", "main", scenario.Clock.GetUtcNow(), true, true, scenario.Root);
        var release = new TaskCompletionSource<RemoteLaneSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Executor.Submit = (_, _) => release.Task;
        var receiptPath = Path.Combine(scenario.Root, "screening.jsonl");
        var work = ExecutorScreeningRunner.RunAsync(candidate, configuration.Executors[0], new([new("A", "filter", 1)], []),
            configuration, scenario.Executor, scenario.Root, receiptPath, "owned", TimeSpan.FromMinutes(1), scenario.Clock, TimeSpan.Zero);
        try
        {
            var request = await RemoteExecutorAdmissionTests.Event(scenario.Executor.Submitted.Task, "gate submission entered"); // Hang guard: screening submit.
            var future = configuration with { Executors = [configuration.Executors[0] with { AdmissionEnabled = false }] };
            Assert.Null(ExecutorScreeningPlanner.PickIdleExecutor(future.Executors, _ => throw new Exception("disabled probe")));
            var handle = scenario.CompletedHandle(request);
            release.SetResult(new(handle));
            var receipt = Assert.IsType<ExecutorScreeningReceipt>(await RemoteExecutorAdmissionTests.Event(work, "owned job settlement"));
            Assert.Equal(ExecutorScreeningVerdict.Pass, receipt.Verdict);
            Assert.Single(ExecutorScreeningReceiptLedger.ReadAll(receiptPath));
            Assert.Equal(1, handle.Abandons);
            Assert.Equal(0, handle.Cancellations);
            Assert.False(RemoteExecutorOccupancy.IsOccupied(scenario.Root, "one"));
        }
        finally
        {
            release.TrySetResult(new(null, "fixture-cleanup"));
            await Event(work, "owned screening job drained");
        }
    }

    [Fact]
    public void WorkerBuild_AllDisabled_FallsBackWithAdmissionReasonThenFutureAdmits()
    {
        var fixture = new RemoteWorkerBuildCheckScenarioTests.Fixture();
        fixture.Configuration = fixture.Configuration with { Executors = [fixture.Configuration.Executors[0] with { AdmissionEnabled = false }] };
        var result = fixture.Run(new("remote-first", 300));
        Assert.Equal(CompletionBuildEvidencePhase.Success, result.Phase);
        Assert.Contains("remote_fallback=no-enabled-executor", result.Diagnostic);
        Assert.Equal(1, fixture.LocalCalls);
        Assert.Empty(fixture.Executor.Requests);
        fixture.Configuration = fixture.Configuration with { Executors = [fixture.Configuration.Executors[0] with { AdmissionEnabled = true }] };
        fixture.Publish();
        var enabled = fixture.Run(new("remote-first", 300));
        Assert.Equal(CompletionBuildEvidencePhase.Success, enabled.Phase);
        Assert.Single(fixture.Executor.Requests);
        Assert.Equal(1, fixture.LocalCalls);
    }

    [Fact]
    public async Task WorkerBuild_DisableDuringSubmission_SettlesOwnedBuild()
    {
        var fixture = new RemoteWorkerBuildCheckScenarioTests.Fixture();
        fixture.Publish();
        var complete = fixture.Executor.Submit;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Executor.Submit = async (request, token) =>
        {
            var result = await complete(request, token);
            await release.Task;
            return result;
        };
        var work = Task.Run(() => fixture.Run(new("remote-first", 300)));
        try
        {
            await Event(fixture.Executor.Submitted.Task, "worker-build offer entered with captured configuration");
            fixture.Configuration = fixture.Configuration with
            { Executors = [fixture.Configuration.Executors[0] with { AdmissionEnabled = false }] };
            using var future = fixture.Coordinator("disabled-future");
            Assert.Null(future.TryClaimIdleExecutor(out var reason));
            Assert.Equal("no-enabled-executor", reason);
            release.TrySetResult();
            await Event(work, "captured worker-build settlement");
            Assert.Equal(CompletionBuildEvidencePhase.Success, work.Result.Phase);
            Assert.Equal(0, fixture.LocalCalls);
            Assert.Single(fixture.Executor.Requests);
            Assert.True(fixture.Handle.IsAbandoned);
            Assert.Single(File.ReadAllLines(fixture.LedgerPath));
            Assert.False(RemoteExecutorOccupancy.IsOccupied(fixture.Operation.WorktreeRoot, "mcg-exec-1"));
        }
        finally
        {
            release.TrySetResult();
            await Event(work, "worker-build cleanup after release");
        }
    }

    [Fact]
    public async Task ScreeningCli_AllDisabled_IsIdleWithAdmissionReason()
    {
        using var fixture = new ExecutorSettingIntentScenarioTests.Harness();
        fixture.Write(ExecutorSettingIntentScenarioTests.Harness.Json.Replace("\"slots\":", "\"enabled\":false, \"slots\":")
            .Replace("\"focusedEvidence\":", "\"screening\":{\"mode\":\"shadow\"}, \"focusedEvidence\":"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliExecutorScreenCommand.RunAsync(["executor-screen"], fixture.Path,
            () => throw new Exception("disabled candidate query"), _ => throw new Exception("disabled lane query"),
            (_, _, _) => throw new Exception("disabled transport construction"), new FixedTime(), TimeSpan.Zero,
            fixture.Workspace.RootDirectory, Path.Combine(fixture.Folder, "screening.jsonl"), output, error);
        Assert.Equal(0, exit);
        Assert.Equal("SCREEN idle reason=no-enabled-executor" + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task SelftestAndBackground_Disabled_DoNotInvokeTransportOrGit()
    {
        using var fixture = new ExecutorSettingIntentScenarioTests.Harness();
        fixture.Write(ExecutorSettingIntentScenarioTests.Harness.Json.Replace("\"slots\":", "\"enabled\":false, \"toolsDigestCheck\":true, \"slots\":"));
        var calls = 0;
        Task<GoalAcceptanceVerifier.CommandResult> Transport(string[] args, string root, TimeSpan timeout, CancellationToken token)
        { Interlocked.Increment(ref calls); throw new Exception("disabled transport"); }
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliRemoteLaneSelfTestCommand.RunAsync(["remote-lane-selftest", "--executor", "mcg-exec-1"], fixture.Workspace,
            Transport, (_, _, _) => throw new Exception("disabled git"), new FixedTime(), TimeSpan.Zero, "disabled", output, error);
        Assert.Equal(2, exit);
        Assert.Contains("no-enabled-executor", output.ToString());
        var root = fixture.Workspace.ExecutionDirectory;
        var writer = new ConductEventLogWriter(fixture.Workspace.ConductEventsLogPath);
        var probe = new ConductorRemoteExecutorProbe(root, fixture.Path, new FixedTime(), Transport, _ => throw new Exception("disabled occupancy"), writer);
        probe.Evaluate();
        Assert.Null(probe.CurrentProbe);
        var deploy = new ConductorExecutorToolsDeploy(root, fixture.Path, new FixedTime(), Transport,
            _ => throw new Exception("disabled occupancy"), writer, () => throw new Exception("disabled tools source"));
        deploy.Evaluate();
        Assert.NotNull(deploy.CurrentRun);
        await Event(deploy.CurrentRun, "disabled reconciliation completed"); // Hang guard: reconciliation completed.
        Assert.Equal(0, calls);
        Assert.Empty(fixture.EventLines());
        Assert.False(File.Exists(RemoteExecutorProbeLedger.ResolveStorePath(root)));
    }

    private static void SetAdmission(Scenario scenario, bool enabled) => File.WriteAllBytes(scenario.ConfigurationPath,
        RemoteLaneExecutorSettingRewrite.RewriteEnabled(File.ReadAllBytes(scenario.ConfigurationPath), "fixture-executor", enabled));
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ExecutorSettingIntentScenarioTests.Harness.At;
    }
}
