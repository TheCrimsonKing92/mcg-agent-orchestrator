using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: unique temporary roots, injected executor and clock, no processes or global overrides.
public sealed class RemoteLaneCoordinatorNormalizedResultTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RetainedGreen_AttachesEveryReadablePeerWithSourceIdentities(int count)
    {
        using var fixture = new Fixture(count);

        var receipt = fixture.AssertAccepted(await fixture.RunAsync());

        Assert.Null(receipt.NormalizedTestResultWriteFailure);
        Assert.Equal(fixture.RetainedPaths.Select(NormalizedTestResultWriter.PathFor), receipt.NormalizedTestResultPaths);
        Assert.All(receipt.NormalizedTestResultPaths!, path => AssertReadablePeer(path));
        Assert.Empty(Directory.EnumerateFiles(fixture.RemoteDirectory, "*.ctrf.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObstructedPeers_PreserveSuccessesFailuresAndAcceptedSource(bool multiple)
    {
        using var fixture = new Fixture(4);
        var blockedIndices = multiple ? new[] { 1, 2 } : new[] { 1 };
        foreach (var index in blockedIndices)
            Directory.CreateDirectory(NormalizedTestResultWriter.PathFor(fixture.RetainedPaths[index]));

        var receipt = fixture.AssertAccepted(await fixture.RunAsync());

        var expected = fixture.RetainedPaths.Where((_, index) => !blockedIndices.Contains(index))
            .Select(NormalizedTestResultWriter.PathFor).ToArray();
        Assert.Equal(expected, receipt.NormalizedTestResultPaths);
        Assert.All(expected, path => AssertReadablePeer(path));
        var failure = Assert.IsType<NormalizedTestResultWriteFailure>(receipt.NormalizedTestResultWriteFailure);
        Assert.Equal(NormalizedTestResultWriter.PathFor(fixture.RetainedPaths[1]), failure.TargetPath);
        Assert.Equal(NormalizedTestResultWriteFailure.WriteFailed, failure.Reason);
        Assert.True(failure.Detail.Contains(nameof(IOException), StringComparison.Ordinal) ||
            failure.Detail.Contains(nameof(UnauthorizedAccessException), StringComparison.Ordinal), failure.Detail);
        if (multiple)
        {
            var secondTarget = NormalizedTestResultWriter.PathFor(fixture.RetainedPaths[2]);
            Assert.Contains($"TargetPath={secondTarget}; Reason={NormalizedTestResultWriteFailure.WriteFailed}; Detail=", failure.Detail);
            var secondDetail = failure.Detail.Split("; Detail=", StringSplitOptions.None)[1];
            Assert.True(secondDetail.StartsWith(nameof(IOException), StringComparison.Ordinal) ||
                secondDetail.StartsWith(nameof(UnauthorizedAccessException), StringComparison.Ordinal), secondDetail);
        }
        Assert.Empty(Directory.EnumerateFiles(fixture.AttemptDirectory, "*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(fixture.RemoteDirectory, "*.ctrf.json"));
    }

    [Theory]
    [InlineData("binding", nameof(RemoteLaneOutcomeCode.BindingMismatchCommit))]
    [InlineData("incomplete", nameof(RemoteLaneOutcomeCode.TrxIncomplete))]
    [InlineData("missing-trx", nameof(RemoteLaneOutcomeCode.TrxIncomplete))]
    [InlineData("no-custody", nameof(RemoteLaneOutcomeCode.TrxIncomplete))]
    [InlineData("blocked-custody", nameof(RemoteLaneOutcomeCode.TrxIncomplete))]
    [InlineData("red", nameof(RemoteLaneOutcomeCode.RemoteRed))]
    public async Task RejectedResult_PreservesFallbackAndCreatesNoNormalizedFiles(
        string fault, string expected)
    {
        using var fixture = new Fixture(1, fault);

        var outcome = await fixture.RunAsync();

        Assert.Null(outcome.Accepted);
        Assert.Same(fixture.Handle, outcome.Handle);
        Assert.Single(fixture.Executor.Requests);
        Assert.Equal(Enum.Parse<RemoteLaneOutcomeCode>(expected), Assert.Single(fixture.Health()).Outcome);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.ctrf.json", SearchOption.AllDirectories));
        Assert.All(fixture.SourcePaths, path => Assert.Equal(fixture.SourceContents[path], File.ReadAllText(path)));
    }

    private static void AssertReadablePeer(string path)
    {
        Assert.True(File.Exists(path), path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("trx", document.RootElement.GetProperty("results").GetProperty("tool").GetProperty("name").GetString());
        var normalized = new NormalizedTestResultSource().Read(path);
        var source = new TrxTestResultSource().Read(path[..^NormalizedTestResultWriter.FileSuffix.Length] + ".trx");
        Assert.Equal(TestResultReadStatus.Readable, source.Status);
        Assert.Equal(TestResultReadStatus.Readable, normalized.Status);
        Assert.Equal(2, normalized.Records.Count);
        Assert.EndsWith(".Case(value: 1)", normalized.Records[0].Id);
        Assert.EndsWith(".Case(value: 2)", normalized.Records[1].Id);
        for (var index = 0; index < source.Records.Count; index++)
        {
            var original = source.Records[index];
            var roundTrip = normalized.Records[index];
            Assert.Equal((original.Id, original.DisplayName, original.Status, original.DurationMs, original.ReportedBy),
                (roundTrip.Id, roundTrip.DisplayName, roundTrip.Status, roundTrip.DurationMs, roundTrip.ReportedBy));
            Assert.Equal(original.SuitePath, roundTrip.SuitePath);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal string RemoteDirectory => Path.Combine(Root, "remote");
        internal string AttemptDirectory => Path.Combine(Root, "attempt");
        internal string[] SourcePaths { get; }
        internal string[] RetainedPaths { get; }
        internal Dictionary<string, string> SourceContents { get; } = new();
        internal FakeRemoteLaneExecutor Executor { get; } = new();
        internal FakeRemoteLaneExecutor.Handle Handle { get; }
        private readonly RemoteLaneExecutorEntry _entry = new("executor", 60);
        private readonly Check _check = new()
        {
            Name = "infrastructure tests: NormalizedRemote", Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~NormalizedResultTests"]
        };
        private readonly RemoteLaneCoordinator _coordinator;

        internal Fixture(int count, string? fault = null)
        {
            Directory.CreateDirectory(RemoteDirectory);
            Directory.CreateDirectory(AttemptDirectory);
            SourcePaths = Enumerable.Range(0, count).Select(index => Path.Combine(RemoteDirectory, $"lane-{index}.trx")).ToArray();
            RetainedPaths = SourcePaths.Select(path => Path.Combine(AttemptDirectory, Path.GetFileName(path))).ToArray();
            for (var index = 0; index < count; index++)
            {
                var content = Trx(index);
                if (fault == "incomplete") content = content.Replace("executed=\"2\"", "executed=\"1\"", StringComparison.Ordinal);
                if (fault == "red") content = content.Replace("outcome=\"Passed\"", "outcome=\"Failed\"", StringComparison.Ordinal);
                SourceContents.Add(SourcePaths[index], content);
                File.WriteAllText(SourcePaths[index], content);
            }
            if (fault == "blocked-custody") Directory.CreateDirectory(RetainedPaths[0]);
            var clock = new ManualRemoteLaneClock();
            Handle = new(clock.GetUtcNow());
            Executor.Submit = (request, _) =>
            {
                var result = new RemoteLaneResult(request.ExecutorId, request.Lane, request.FilterHash,
                    request.VerifyingCommitSha, request.CandidateTreeSha, request.MainSha, request.ManifestIdentity,
                    fault == "red" ? 1 : 0, fault == "missing-trx" ? [Path.Combine(RemoteDirectory, "missing.trx")] : SourcePaths);
                if (fault == "binding") result = result with { VerifyingCommitSha = "different" };
                Handle.Publish(result);
                return Task.FromResult(new RemoteLaneSubmission(Handle));
            };
            _coordinator = new(new([_entry], [_check.Name], null),
                new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
                Root, fault == "no-custody" ? null : Path.Combine(AttemptDirectory, "result"),
                Executor, clock, TimeSpan.FromMilliseconds(1), null);
            Assert.True(_coordinator.IsEligible(_check));
            Assert.Equal(_entry, _coordinator.TryClaimIdleExecutor());
        }

        internal Task<RemoteLaneOutcome> RunAsync() =>
            _coordinator.RunRemoteAsync(_check, _entry, TimeSpan.FromMinutes(10), CancellationToken.None);

        internal AcceptanceCheckResult AssertAccepted(RemoteLaneOutcome outcome)
        {
            var receipt = Assert.IsType<AcceptanceCheckResult>(outcome.Accepted);
            var request = Assert.Single(Executor.Requests);
            Assert.Equal(request, outcome.Request);
            Assert.Null(outcome.Handle);
            Assert.True(Handle.IsAbandoned);
            Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(Health()).Outcome);
            Assert.Equal("attempt", request.AttemptId);
            Assert.Equal("commit", request.VerifyingCommitSha);
            Assert.Equal("tree", request.CandidateTreeSha);
            var source = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(SourcePaths);
            var decision = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
                new GoalAcceptanceVerifier.CommandResult(0, ""), source, requireTrxEvidence: true);
            Assert.True(decision.Passed);
            var expected = new AcceptanceCheckResult(_check.Name, true, 0, null,
                ResultSummary: "remote-executor=executor", TestResultPaths: receipt.TestResultPaths,
                TestResultAttemptId: request.AttemptId, ExecutedTestCount: SourcePaths.Length * 2,
                DiscoveredTestCount: SourcePaths.Length * 2, CompletionDecision: decision, TestProjectPath: _check.Project);
            Assert.Equal(expected, receipt with { NormalizedTestResultPaths = null, NormalizedTestResultWriteFailure = null });
            Assert.Equal(RetainedPaths, receipt.TestResultPaths);
            foreach (var path in SourcePaths)
            {
                Assert.Equal(SourceContents[path], File.ReadAllText(path));
                Assert.Equal(SourceContents[path], File.ReadAllText(Path.Combine(AttemptDirectory, Path.GetFileName(path))));
            }
            return receipt;
        }

        internal IReadOnlyList<RemoteExecutorHealthRecord> Health() =>
            RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(Root));

        public void Dispose()
        {
            _coordinator.Dispose();
            SharedTestSupport.RemoveTempDirectory(Root);
        }

        private static string Trx(int index) => $$"""
            <TestRun>
              <TestDefinitions>
                <UnitTest id="one" name="Case(value: 1)"><TestMethod className="Lane{{index}}Tests" name="Case" /></UnitTest>
                <UnitTest id="two" name="Case(value: 2)"><TestMethod className="Lane{{index}}Tests" name="Case" /></UnitTest>
              </TestDefinitions>
              <Results>
                <UnitTestResult testId="one" testName="Case(value: 1)" outcome="Passed" duration="00:00:00.012" />
                <UnitTestResult testId="two" testName="Case(value: 2)" outcome="Passed" duration="00:00:00.034" />
              </Results>
              <ResultSummary outcome="Passed"><Counters total="2" executed="2" passed="2" failed="0" notExecuted="0" /></ResultSummary>
            </TestRun>
            """;
    }
}
