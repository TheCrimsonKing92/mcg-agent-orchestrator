using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Only the isolated build-root fixture is shared; executor overrides are per instance.
// This collection preserves the class's existing Remainder lane membership.
[Collection("IsolatedProcessSpawning")]
public sealed class RemoteFocusedEvidenceShadowTests : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact]
    public async Task CleanSampledCandidate_SubmitsBoundJobsAndRecordsParityWithoutChangingLocalResults()
    {
        using var scenario = CreateScenario();
        scenario.Configure();
        var fake = scenario.CompletedExecutor();
        scenario.BeforeLocal = async () =>
        {
            await Event(fake.Submitted.Task, "shadow job submitted before local run");
            scenario.Clock.Advance(TimeSpan.FromSeconds(7));
        };
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, result.Summary);
        Assert.Equal(result.Checks.Count, fake.Requests.Count);
        foreach (var request in fake.Requests)
        {
            Assert.Equal(scenario.Head, request.VerifyingCommitSha);
            Assert.Equal(scenario.Tree, request.CandidateTreeSha);
            Assert.Equal(scenario.Goal.Value, request.GoalId);
            Assert.Equal("selected-attempt", request.AttemptId);
            Assert.Equal(Project, request.Project);
            Assert.Equal("FullyQualifiedName~AlphaTests", request.Filter);
            Assert.Matches("^fe-[0-9a-f]{8}$", request.Lane);
            Assert.Equal(RemoteFocusedEvidenceShadow.ManifestIdentity(Project, request.Filter, "mtp"), request.ManifestIdentity);
        }
        var row = Assert.Single(scenario.Health());
        Assert.True(row.EvidenceShadow);
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        Assert.Equal(3, row.RemoteExecutedCount);
        Assert.Empty(row.RemoteFailingTests!);
        Assert.Equal(7, row.LocalDurationSeconds);
        Assert.Equal(7, row.RemoteDurationSeconds);
        Assert.Equal("passed", row.LocalVerdict);
        Assert.True(row.Agree);
        using var json = JsonDocument.Parse(File.ReadAllLines(scenario.LedgerPath).Single());
        Assert.True(json.RootElement.GetProperty("evidence_shadow").GetBoolean());
        Assert.Equal("selected-attempt", json.RootElement.GetProperty("gate_attempt_id").GetString());
        Assert.Equal(scenario.Goal.Value, json.RootElement.GetProperty("goal_id").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("remote_executed_count").GetInt32());
        Assert.Equal(7, json.RootElement.GetProperty("remote_duration_seconds").GetDouble());
        Assert.Equal(7, json.RootElement.GetProperty("local_duration_seconds").GetDouble());
        await AssertControl(scenario, result);
    }

    [Xunit.Theory]
    [Xunit.InlineData("off")]
    [Xunit.InlineData("absent")]
    [Xunit.InlineData("invalid-sample")]
    [Xunit.InlineData("dirty")]
    [Xunit.InlineData("baseline")]
    [Xunit.InlineData("unsampled")]
    [Xunit.InlineData("occupied")]
    public async Task IneligibleOrUnavailableShadow_PreservesLocalResults(string reason)
    {
        using var scenario = CreateScenario();
        scenario.Configure(reason == "off" ? "off" : "shadow",
            reason == "invalid-sample" ? 0 : reason == "unsampled" ? 2 : 1,
            absent: reason == "absent");
        if (reason == "dirty") File.WriteAllText(Path.Combine(scenario.Root, "uncommitted.txt"), "dirty");
        var attempt = reason == "unsampled"
            ? Enumerable.Range(0, 100).Select(i => "attempt-" + i)
                .First(id => !RemoteFocusedEvidenceShadow.IsSampled(id, 2)) : "selected-attempt";
        var fake = scenario.CompletedExecutor();
        using var occupied = reason == "occupied"
            ? RemoteExecutorOccupancy.TryClaimExclusive(scenario.Root, "fixture-executor", 0) : null;
        if (reason == "occupied") Assert.NotNull(occupied);
        var result = await scenario.RunAsync(reason == "baseline", attempt);
        Assert.True(result.Passed, result.Summary);
        Assert.Empty(fake.Requests);
        if (reason == "occupied")
        {
            var row = Assert.Single(scenario.Health());
            Assert.True(row.EvidenceShadow);
            Assert.Equal(RemoteLaneOutcomeCode.ShadowSkippedNoExecutor, row.Outcome);
            Assert.Contains("\"outcome\":\"shadow-skipped-no-executor\"", File.ReadAllText(scenario.LedgerPath));
        }
        else Assert.Empty(scenario.Health());
        if (reason == "invalid-sample")
            Assert.Contains("REMOTE_FOCUSED_EVIDENCE_SHADOW_DISABLED reason=invalid", scenario.Progress);
        await AssertControl(scenario, result, reason == "baseline");
    }

    [Xunit.Fact]
    public async Task RemoteRed_RecordsFailingIdentitiesAndDisagreementWhileLocalStaysGreen()
    {
        using var scenario = CreateScenario();
        scenario.Configure();
        scenario.CompletedExecutor(red: true);
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, result.Summary);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.RemoteRed, row.Outcome);
        Assert.Equal(3, row.RemoteExecutedCount);
        Assert.Equal(new[] { "AlphaTests.RemoteFailure" }, row.RemoteFailingTests);
        Assert.False(row.Agree);
        Assert.Equal("passed", row.LocalVerdict);
        Assert.True(row.EvidenceShadow);
        await AssertControl(scenario, result);
    }

    [Xunit.Fact]
    public async Task PendingRemote_IsCancelledAtSharedGraceDeadlineDrivenByTimeProvider()
    {
        using var scenario = CreateScenario();
        scenario.Configure(grace: 5);
        var handle = new PendingHandle();
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (_, token) =>
            {
                token.Register(() => handle.Cancelled.TrySetResult());
                return Task.FromResult(new RemoteLaneSubmission(handle));
            }
        };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        scenario.BeforeLocal = () => Event(handle.Polled.Task, "pending shadow polled before local completed");
        var run = scenario.RunAsync();
        await Event(scenario.Clock.TimerCreated.Task, "shared grace timer created after local completed");
        scenario.Clock.Advance(TimeSpan.FromSeconds(4));
        // test-design-discipline: allow-negative-wait - only the injected grace timer can complete this pending shadow.
        Assert.False(run.IsCompleted);
        scenario.Clock.Advance(TimeSpan.FromSeconds(1));
        var result = await Event(run, "local result returned when grace expired");
        await Signal(handle.Cancelled.Task, "remote cancellation signalled");
        await Signal(handle.Abandoned.Task, "pending handle abandoned");
        Assert.True(result.Passed, result.Summary);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.CancelledAfterGrace, row.Outcome);
        Assert.True(row.EvidenceShadow);
        Assert.Equal(5, row.RemoteDurationSeconds);
        Assert.Equal(0, row.LocalDurationSeconds);
        Assert.Equal("passed", row.LocalVerdict);
        Assert.Null(row.Agree);
        Assert.Equal(1, handle.AbandonCount);
        Assert.Equal(1, handle.CancelQueuedCount);
        using var released = RemoteExecutorOccupancy.TryClaimExclusive(scenario.Root, "fixture-executor", 0);
        Assert.NotNull(released);
        await AssertControl(scenario, result);
    }

    [Xunit.Fact]
    public async Task NonCooperativeSubmission_CannotExtendGraceAndLateHandleCannotAddAnotherRow()
    {
        using var scenario = CreateScenario();
        scenario.Configure(grace: 5);
        var pending = new TaskCompletionSource<RemoteLaneSubmission>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = new PendingHandle();
        var fake = new FakeRemoteLaneExecutor { Submit = (_, _) => pending.Task };
        TestOverrides.RemoteLaneExecutorForTests = fake;
        scenario.BeforeLocal = () => Event(fake.Submitted.Task, "noncooperative submission started");
        var run = scenario.RunAsync();
        try
        {
            await Event(scenario.Clock.TimerCreated.Task, "grace timer for noncooperative submission");
            scenario.Clock.Advance(TimeSpan.FromSeconds(5));
            var result = await Event(run, "local result returned without waiting for submission");
            Assert.True(result.Passed, result.Summary);
            Assert.Equal(RemoteLaneOutcomeCode.CancelledAfterGrace, Assert.Single(scenario.Health()).Outcome);
            pending.TrySetResult(new RemoteLaneSubmission(handle));
            await Event(handle.Abandoned.Task, "late handle abandoned");
            Assert.Single(scenario.Health());
            Assert.Equal(1, handle.AbandonCount);
            using var released = RemoteExecutorOccupancy.TryClaimExclusive(scenario.Root, "fixture-executor", 0);
            Assert.NotNull(released);
            await AssertControl(scenario, result);
        }
        finally { pending.TrySetResult(new RemoteLaneSubmission(handle)); }
    }

    [Xunit.Fact]
    public async Task DifferentEchoedCommit_RecordsBindingMismatchAndKeepsLocalGreen()
    {
        using var scenario = CreateScenario();
        scenario.Configure();
        scenario.CompletedExecutor(change: result => result with { VerifyingCommitSha = "different-commit" });
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, result.Summary);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.BindingMismatchCommit, row.Outcome);
        Assert.Equal(scenario.Head, row.Expected.Commit);
        Assert.Equal("different-commit", row.Observed!.Commit);
        Assert.Equal(3, row.RemoteExecutedCount);
        Assert.Null(row.Agree);
        await AssertControl(scenario, result);
    }

    [Xunit.Fact]
    public async Task MissingRemoteTrx_RecordsIncompleteAndKeepsLocalGreen()
    {
        using var scenario = CreateScenario();
        scenario.Configure();
        scenario.CompletedExecutor(change: result => result with { TestResultPaths = [] });
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, result.Summary);
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.TrxIncomplete, row.Outcome);
        Assert.Equal("passed", row.LocalVerdict);
        Assert.Null(row.Agree);
        await AssertControl(scenario, result);
    }

    [Xunit.Fact]
    public async Task ExecutorException_RecordsOneRowAndKeepsLocalGreen()
    {
        using var scenario = CreateScenario();
        scenario.Configure();
        TestOverrides.RemoteLaneExecutorForTests = new FakeRemoteLaneExecutor
        { Submit = (_, _) => throw new InvalidOperationException("shadow-fixture-failure") };
        var result = await scenario.RunAsync();
        Assert.True(result.Passed, result.Summary);
        var row = Assert.Single(scenario.Health());
        Assert.True(row.EvidenceShadow);
        Assert.Equal(RemoteLaneOutcomeCode.Unreachable, row.Outcome);
        Assert.Contains("shadow-fixture-failure", row.Reason!);
        Assert.Equal("passed", row.LocalVerdict);
        await AssertControl(scenario, result);
    }

    [Xunit.Theory]
    [Xunit.InlineData("{}", "off", null)]
    [Xunit.InlineData("{\"mode\":\"shadow\"}", "shadow", null)]
    [Xunit.InlineData("{\"mode\":\"invalid\"}", "off", "invalid")]
    [Xunit.InlineData("{\"sampleEvery\":0}", "off", "invalid")]
    [Xunit.InlineData("{\"sampleEvery\":1.5}", "off", "invalid")]
    [Xunit.InlineData("{\"sampleEvery\":\"1\"}", "off", "invalid")]
    [Xunit.InlineData("{\"graceSeconds\":-1}", "off", "invalid")]
    [Xunit.InlineData("{\"graceSeconds\":null}", "off", "invalid")]
    [Xunit.InlineData("null", "off", "invalid")]
    public void OptionalConfigurationFault_DoesNotDisableGate(string block, string mode, string? fault)
    {
        using var scenario = CreateScenario();
        File.WriteAllText(scenario.ConfigurationPath,
            "{\"executors\":[{\"id\":\"fixture-executor\"}],\"lanes\":[\"infrastructure tests: Alpha\"],\"focusedEvidence\":" + block + "}");
        var configuration = RemoteLaneExecutorConfiguration.Load(scenario.ConfigurationPath);
        Assert.True(configuration.Enabled);
        Assert.Null(configuration.DisabledReason);
        Assert.Equal(mode, configuration.FocusedEvidence.Mode);
        Assert.Equal(fault, configuration.FocusedEvidence.FaultReason);
        Assert.Equal(4, configuration.FocusedEvidence.SampleEvery);
        Assert.Equal(300, configuration.FocusedEvidence.GraceSeconds);
    }

    private static int StableSlotIndex(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            path,
            @"(?:slot-|build-)(?<slot>\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success
            ? int.Parse(match.Groups["slot"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Expected build-pool path, got '{path}'.");
    }

    private static bool IsMtpExecutableCall(string[] args, string projectName) =>
        args.Length > 1 &&
        args[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
        args[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileNameWithoutExtension(args[1]).Equals(projectName, StringComparison.OrdinalIgnoreCase);

    private static string GetArtifactsPath(string[] args)
    {
        var artifactsPathIndex = Array.IndexOf(args, "--artifacts-path");
        Assert.True(artifactsPathIndex >= 0);
        Assert.True(artifactsPathIndex + 1 < args.Length);
        return args[artifactsPathIndex + 1];
    }

    private static void WriteMtpTrx(string[] args, int executedTestCount, IReadOnlyList<string> executedTestIdentities)
    {
        var resultsDirectoryIndex = Array.IndexOf(args, "--results-directory");
        var trxFileIndex = Array.IndexOf(args, "--report-trx-filename");
        Assert.True(resultsDirectoryIndex >= 0);
        Assert.True(resultsDirectoryIndex + 1 < args.Length);
        Assert.True(trxFileIndex >= 0);
        Assert.True(trxFileIndex + 1 < args.Length);
        Directory.CreateDirectory(args[resultsDirectoryIndex + 1]);
        var destinationPath = Path.Combine(args[resultsDirectoryIndex + 1], args[trxFileIndex + 1]);
        var definitions = executedTestIdentities.Select((identity, index) =>
        {
            var separator = identity.LastIndexOf('.');
            var className = separator > 0 ? identity[..separator] : identity;
            var methodName = separator > 0 ? identity[(separator + 1)..] : "Executed";
            return new XElement(
                "UnitTest",
                new XAttribute("id", $"test-{index}"),
                new XAttribute("name", identity),
                new XElement(
                    "TestMethod",
                    new XAttribute("className", className),
                    new XAttribute("name", methodName)));
        });
        var results = executedTestIdentities.Select((identity, index) =>
            new XElement(
                "UnitTestResult",
                new XAttribute("testId", $"test-{index}"),
                new XAttribute("testName", identity),
                new XAttribute("outcome", "Passed")));
        new XDocument(
            new XElement(
                "TestRun",
                new XElement("TestDefinitions", definitions),
                new XElement("Results", results),
                new XElement(
                    "ResultSummary",
                    new XAttribute("outcome", "Completed"),
                    new XElement(
                        "Counters",
                        new XAttribute("total", Math.Max(1, executedTestCount)),
                        new XAttribute("executed", executedTestCount),
                        new XAttribute("passed", executedTestCount),
                        new XAttribute("failed", 0),
                        new XAttribute("notExecuted", 0)))))
            .Save(destinationPath);
    }

    private const string Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj";
    private Scenario CreateScenario() => new(this);
    private static async Task AssertControl(Scenario scenario, FocusedEvidenceRunResult shadow, bool baseline = false)
    {
        scenario.Configure("off");
        scenario.BeforeLocal = null;
        var control = await scenario.RunAsync(baseline, "control-attempt");
        Assert.Equal(control.Request, shadow.Request);
        Assert.Equal(control.Passed, shadow.Passed);
        Assert.Equal(control.Accepted, shadow.Accepted);
        Assert.Equal(control.OutcomeReason, shadow.OutcomeReason);
        Assert.Equal(control.Rejection, shadow.Rejection);
        Assert.Equal(NormalizeSummaryArtifacts(control), NormalizeSummaryArtifacts(shadow));
        Assert.Equal(control.Checks.Select(check => (check.Name, check.Passed, check.ExitCode, check.ExecutedTestCount)),
            shadow.Checks.Select(check => (check.Name, check.Passed, check.ExitCode, check.ExecutedTestCount)));
        foreach (var (expected, actual) in control.Checks.Zip(shadow.Checks))
        {
            Assert.Equal(expected.OutputTail, actual.OutputTail);
            Assert.Equal(expected.ResultSummary, actual.ResultSummary);
            Assert.Equal(expected.FailureClassification, actual.FailureClassification);
            Assert.Equal(expected.TestResultPaths, actual.TestResultPaths);
            Assert.Equal(expected.FailingTestIdentities, actual.FailingTestIdentities);
        }
    }
    private static string NormalizeSummaryArtifacts(FocusedEvidenceRunResult result)
    {
        // Independent runs can acquire different rotating build slots. Normalize only
        // their exact build roots; verdict text and fixture-owned TRX paths stay intact.
        var summary = result.Summary;
        foreach (var check in result.Checks)
            if (!string.IsNullOrEmpty(check.ArtifactsPath))
                summary = summary.Replace(check.ArtifactsPath, "<local-build-artifacts>", StringComparison.Ordinal);
        return summary;
    }
    // These asynchronous cleanup signals use the test-run cancellation rather than an elapsed-time bound.
    private static async Task Signal(Task task, string name)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        try { await task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Missing event: " + name, ex, cancellationToken); }
    }

    private static async Task Event(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { throw new TimeoutException("Missing event: " + name); }
    }
    private static async Task<T> Event<T>(Task<T> task, string name)
    { await Event((Task)task, name); return await task; }

    private sealed class Scenario : IDisposable
    {
        private readonly RemoteFocusedEvidenceShadowTests _owner;
        internal string Root { get; }
        internal GoalId Goal { get; } = new(Guid.NewGuid().ToString("N"));
        internal ShadowClock Clock { get; } = new();
        internal string ConfigurationPath => Path.Combine(Root, ".orchestrator", "executors.json");
        internal string LedgerPath => RemoteExecutorHealthLedger.ResolveStorePath(Root);
        internal string Head { get; }
        internal string Tree { get; }
        internal ConcurrentQueue<string> Progress { get; } = new();
        internal Func<Task>? BeforeLocal { get; set; }
        private readonly TaskCompletionSource _localExecuted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Scenario(RemoteFocusedEvidenceShadowTests owner)
        {
            _owner = owner;
            Root = CreateManifestWorkspace("""
                {"version":1,"engine":{"mtpInvocations":[{
                    "project":"tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                    "executablePathTemplate":"bin/{projectName}/{configuration}/{projectName}{executableExtension}",
                    "firewallExecutablePathTemplate":"bin/{projectName}/{configuration}/{projectName}.exe",
                    "arguments":["{executable}","--results-directory","{resultsDirectory}","--report-trx-filename","{trxFileName}"]
                }]},"checks":[],"forbiddenChangedPathGlobs":[]}
                """);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, Project))!);
            File.WriteAllText(Path.Combine(Root, Project), "<Project />");
            File.WriteAllText(Path.Combine(Root, Path.GetDirectoryName(Project)!, "AlphaTests.cs"),
                "public class AlphaTests { [Xunit.Fact] public void Executes() { } }");
            File.WriteAllText(Path.Combine(Root, ".gitignore"), ".orchestrator/\n");
            Git("init", "-b", "main");
            Git("add", ".");
            Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
            Head = Git("rev-parse", "HEAD").Trim();
            Tree = Git("rev-parse", "HEAD^{tree}").Trim();
            Assert.Empty(Git("status", "--porcelain", "--untracked-files=all"));
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
            owner.TestOverrides.RemoteLaneExecutorConfigurationPathForTests = ConfigurationPath;
            owner.TestOverrides.RemoteLaneTimeProviderForTests = Clock;
            owner.TestOverrides.RemoteLanePollInterval = TimeSpan.FromMilliseconds(1);
            owner.TestOverrides.OnRemoteLaneProgressLineForTests = Progress.Enqueue;
        }
        private string Git(params string[] args)
        {
            var result = GitCli.Run(Root, args);
            Assert.True(result.Succeeded, result.Error);
            return result.Output;
        }
        internal void Configure(string mode = "shadow", int sample = 1, int grace = 300, bool absent = false) =>
            File.WriteAllText(ConfigurationPath, "{\"executors\":[{\"id\":\"fixture-executor\"}],\"lanes\":[]" +
                (absent ? "}" : $",\"focusedEvidence\":{{\"mode\":\"{mode}\",\"sampleEvery\":{sample},\"graceSeconds\":{grace}}}}}"));
        internal FakeRemoteLaneExecutor CompletedExecutor(bool red = false, Func<RemoteLaneResult, RemoteLaneResult>? change = null)
        {
            var fake = new FakeRemoteLaneExecutor();
            fake.Submit = async (request, token) =>
            {
                await _localExecuted.Task.WaitAsync(token);
                var handle = new FakeRemoteLaneExecutor.Handle(Clock.GetUtcNow());
                string[] args = ["--results-directory", Path.Combine(Root, ".orchestrator", "remote"),
                    "--report-trx-filename", request.Lane + ".trx"];
                WriteMtpTrx(args, 3, ["AlphaTests.RemoteFailure", "AlphaTests.Second", "AlphaTests.Third"]);
                var path = Path.Combine(args[1], args[3]);
                if (red)
                {
                    var trx = XDocument.Load(path);
                    trx.Descendants("UnitTestResult").First().SetAttributeValue("outcome", "Failed");
                    var counters = trx.Descendants("Counters").Single();
                    counters.SetAttributeValue("failed", 1);
                    counters.SetAttributeValue("passed", 2);
                    trx.Save(path);
                }
                var result = new RemoteLaneResult(request.ExecutorId, request.Lane, request.FilterHash,
                    request.VerifyingCommitSha, request.CandidateTreeSha, request.MainSha, request.ManifestIdentity,
                    red ? 1 : 0, [path]);
                handle.Publish(change?.Invoke(result) ?? result);
                return new RemoteLaneSubmission(handle);
            };
            _owner.TestOverrides.RemoteLaneExecutorForTests = fake;
            return fake;
        }
        internal async Task<FocusedEvidenceRunResult> RunAsync(bool ownerless = false, string attempt = "selected-attempt")
        {
            using var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(TimeSpan.FromSeconds(30));
            var slot = StableSlotIndex(lease.Environment.ArtifactsPath);
            // Both runs use the same fixture-owned TRX location so exact receipt comparisons
            // cannot differ merely because an owner generated a new prefix.
            await using var executionOwner = AcceptanceExecutionOwners.CreateFocusedVerification(Root,
                ownerless ? null : Goal, slot, options: new(RunId: attempt,
                    ResultsPrefix: Path.Combine(Root, ".orchestrator", "focused-local")));
            var verifier = new GoalAcceptanceVerifier(_owner.TestOverrides, Runner);
            return await verifier.RunFocusedEvidenceOwnedAsync(Root, ownerless ? null : Goal,
                "Infrastructure.Tests: AlphaTests", executionOwner, slot, lease);
        }
        private async Task<GoalAcceptanceVerifier.CommandResult> Runner(string[] args, string directory, CancellationToken token)
        {
            if (IsMtpExecutableCall(args, "Mcg.AgentOrchestrator.Infrastructure.Tests"))
            {
                if (BeforeLocal is not null) await BeforeLocal();
                WriteMtpTrx(args, 1, ["AlphaTests.Executes"]);
                _localExecuted.TrySetResult();
                return new(0, "Passed: 1");
            }
            if (args.Length > 1 && args[0] == "dotnet" && args[1] == "build")
            {
                const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
                var output = Path.Combine(GetArtifactsPath(args), "bin", projectName, "debug");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, projectName + ".dll"), "fixture");
                File.WriteAllText(Path.Combine(output, projectName + ".exe"), "fixture");
            }
            return new(0, "Build succeeded.");
        }
        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() =>
            File.Exists(LedgerPath) ? RemoteExecutorHealthLedger.ReadAll(LedgerPath) : [];
        public void Dispose()
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(Goal);
            DeleteDirectoryWithRetry(Root);
        }
    }

    private sealed class ShadowClock : TimeProvider
    {
        private readonly ManualRemoteLaneClock _clock = new();
        private readonly ManualStewardTimeProvider _timers = new();
        internal TaskCompletionSource TimerCreated => _timers.TimerCreated;
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timers.CreateTimer(callback, state, dueTime, period);
        internal void Advance(TimeSpan elapsed) { _clock.Advance(elapsed); _timers.Advance(elapsed); }
    }

    private sealed class PendingHandle : IRemoteLaneHandle, IRemoteLaneQueuedJobCancellation
    {
        internal TaskCompletionSource Polled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Abandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int AbandonCount;
        internal int CancelQueuedCount;
        public DateTimeOffset? NewestHeartbeat => null;
        public RemoteLaneResult? TryGetResult() { Polled.TrySetResult(); return null; }
        public void RequestQueuedJobCancellation() => Interlocked.Increment(ref CancelQueuedCount);
        public void Abandon() { Interlocked.Increment(ref AbandonCount); Abandoned.TrySetResult(); }
    }
}
