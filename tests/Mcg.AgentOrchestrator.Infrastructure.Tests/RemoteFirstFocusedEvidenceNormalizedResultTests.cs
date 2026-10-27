using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection("IsolatedProcessSpawning")]
public sealed class RemoteFirstFocusedEvidenceNormalizedResultTests : RemoteFirstFocusedEvidenceFixture
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedRun_RetainedReceipt_NormalizedRoundTrip(bool red)
    {
        using var scenario = CreateScenario();
        string[] sources = [];
        var fake = scenario.CompletedExecutor(red, change: result =>
        {
            sources = result.TestResultPaths.ToArray();
            var document = XDocument.Load(sources[0]);
            var rows = document.Descendants("UnitTestResult").ToArray();
            rows[0].SetAttributeValue("duration", "00:00:00.125");
            rows[0].Add(new XElement("Output", new XElement("ErrorInfo",
                new XElement("Message", "adapter message"), new XElement("StackTrace", "adapter trace"))));
            // Adapter identities include a parameterized method and an opaque fallback ID.
            document.Descendants("TestMethod").ElementAt(1).SetAttributeValue("name", "Second(value: 7)");
            document.Descendants("UnitTest").ElementAt(2).Remove();
            rows[2].Attribute("testName")!.Remove();
            rows[2].SetAttributeValue("testId", "opaque-adapter-id");
            document.Save(sources[0]);
            return result;
        });

        var result = await scenario.RunAsync();

        Assert.Equal(!red, result.Passed);
        var check = Assert.Single(result.Checks);
        AssertSourcePreserved(scenario, fake, check, sources, red);
        Assert.Null(check.NormalizedTestResultWriteFailure);
        var normalizedPath = Assert.Single(check.NormalizedTestResultPaths!);
        Assert.Equal(NormalizedTestResultWriter.PathFor(Assert.Single(check.TestResultPaths!)), normalizedPath);
        AssertRoundTrip(sources[0], normalizedPath);
        var records = new NormalizedTestResultSource().Read(normalizedPath).Records;
        Assert.Equal(3, records.Count);
        Assert.Equal(red ? TestResultStatus.Failed : TestResultStatus.Passed, records[0].Status);
        Assert.Equal(125L, records[0].DurationMs);
        Assert.Equal("adapter message", records[0].Message);
        Assert.Equal("adapter trace", records[0].Trace);
        Assert.Contains("Second(value: 7)", records[1].Id);
        Assert.Equal("opaque-adapter-id", records[2].Id);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task OwnedRun_ConversionFailures_AggregateWithoutChangingSource(bool red, bool multipleFailures, bool allFail)
    {
        using var scenario = CreateScenario();
        string[] sources = [];
        var failedIndices = allFail ? new[] { 0, 1 } : multipleFailures ? new[] { 1, 3 } : new[] { 1 };
        var fake = scenario.CompletedExecutor(red, change: result =>
        {
            var template = XDocument.Load(Assert.Single(result.TestResultPaths));
            sources = Enumerable.Range(0, allFail ? 2 : multipleFailures ? 4 : 3).Select(index =>
            {
                var path = Path.Combine(Path.GetDirectoryName(result.TestResultPaths[0])!, $"retained-{index}.trx");
                var document = new XDocument(template);
                if (failedIndices.Contains(index))
                {
                    // A completed TRX can contain repeated adapter identities. Only conversion rejects it.
                    var rows = document.Descendants("UnitTestResult").ToArray();
                    rows[1].SetAttributeValue("testId", rows[0].Attribute("testId")!.Value);
                    rows[1].SetAttributeValue("testName", rows[0].Attribute("testName")!.Value);
                }
                document.Save(path);
                return path;
            }).ToArray();
            return result with { TestResultPaths = sources };
        });

        var result = await scenario.RunAsync();

        Assert.Equal(!red, result.Passed);
        var check = Assert.Single(result.Checks);
        AssertSourcePreserved(scenario, fake, check, sources, red);
        var retained = check.TestResultPaths!;
        var expectedPaths = retained.Where((_, index) => !failedIndices.Contains(index))
            .Select(NormalizedTestResultWriter.PathFor).ToArray();
        Assert.NotNull(check.NormalizedTestResultPaths);
        Assert.Equal(expectedPaths, check.NormalizedTestResultPaths);
        for (var index = 0; index < sources.Length; index++)
        {
            var target = NormalizedTestResultWriter.PathFor(retained[index]);
            if (failedIndices.Contains(index)) Assert.False(File.Exists(target));
            else AssertRoundTrip(sources[index], target);
        }
        var failure = Assert.IsType<NormalizedTestResultWriteFailure>(check.NormalizedTestResultWriteFailure);
        Assert.Equal(NormalizedTestResultWriter.PathFor(retained[failedIndices[0]]), failure.TargetPath);
        Assert.Equal(NormalizedTestResultWriteFailure.WriteFailed, failure.Reason);
        Assert.StartsWith("ArgumentException: Repeated test Id 'AlphaTests.RemoteFailure'.", failure.Detail);
        if (multipleFailures)
        {
            var secondTarget = NormalizedTestResultWriter.PathFor(retained[failedIndices[1]]);
            Assert.Contains(Environment.NewLine + $"TargetPath={secondTarget}; Reason=write-failed; Detail=ArgumentException: Repeated test Id 'AlphaTests.RemoteFailure'.",
                failure.Detail);
        }
        else
        {
            Assert.DoesNotContain('\r', failure.Detail);
            Assert.DoesNotContain('\n', failure.Detail);
        }
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(retained[0])!, "*.tmp"));
    }

    [Theory]
    [InlineData("binding", "BindingMismatchCommit")]
    [InlineData("incomplete", "TrxIncomplete")]
    [InlineData("selection", "TrxIncomplete")]
    [InlineData("missing", "TrxIncomplete")]
    [InlineData("custody", "Unreachable")]
    public async Task OwnedRun_RejectedReceipt_DoesNotNormalizeRemote(string fault, string expected)
    {
        using var scenario = CreateScenario();
        var remoteFolder = Path.Combine(Path.GetDirectoryName(scenario.Prefix)!, "remote-first");
        Directory.CreateDirectory(remoteFolder);
        var fake = scenario.CompletedExecutor(executed: fault == "incomplete" ? 0 : 3, change: result =>
        {
            var source = Assert.Single(result.TestResultPaths);
            if (fault == "binding") return result with { VerifyingCommitSha = "wrong" };
            if (fault == "selection") File.WriteAllText(source, File.ReadAllText(source).Replace("AlphaTests", "OtherTests"));
            if (fault == "missing") File.Delete(source);
            if (fault == "custody")
            {
                // Force existing retention to fail, while leaving source completion and selection valid.
                Directory.CreateDirectory(Path.Combine(remoteFolder, GoalAcceptanceVerifier.ShortHash("selected-attempt"),
                    result.Lane, Path.GetFileName(source)));
            }
            return result;
        });

        var result = await scenario.RunAsync();

        Assert.Single(fake.Requests);
        Assert.True(result.Passed, result.Summary);
        Assert.Equal(1, scenario.LocalCalls);
        Assert.DoesNotContain("remote-executor=", Assert.Single(result.Checks).ResultSummary ?? "");
        var row = Assert.Single(scenario.Health());
        Assert.Equal(Enum.Parse<RemoteLaneOutcomeCode>(expected), row.Outcome);
        Assert.StartsWith("remote-first-fallback", row.Reason);
        Assert.True(Directory.Exists(remoteFolder));
        Assert.Empty(Directory.EnumerateFiles(remoteFolder, "*.ctrf.json", SearchOption.AllDirectories));
    }

    private static void AssertSourcePreserved(Scenario scenario, FakeRemoteLaneExecutor fake,
        AcceptanceCheckResult check, IReadOnlyList<string> sources, bool red)
    {
        var request = Assert.Single(fake.Requests);
        Assert.Equal(scenario.Head, request.VerifyingCommitSha);
        Assert.Equal(scenario.Tree, request.CandidateTreeSha);
        Assert.Equal(scenario.Head, request.MainSha);
        Assert.Equal("selected-attempt", request.AttemptId);
        Assert.Equal(request.AttemptId, check.TestResultAttemptId);
        Assert.Equal(RemoteFocusedEvidenceShadow.ManifestIdentity(Project, request.Filter, "mtp"), request.ManifestIdentity);
        Assert.Equal(!red, check.Passed);
        Assert.Equal(red ? 1 : 0, check.ExitCode);
        Assert.Equal(Project, check.TestProjectPath);
        Assert.Equal(0, check.TestResultRunOrdinal);
        Assert.False(check.TestResultIsExplicitCrossAttemptReuse);
        Assert.Equal(3 * sources.Count, check.ExecutedTestCount);
        Assert.Equal(3 * sources.Count, check.DiscoveredTestCount);
        var sourceEvidence = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(sources);
        var sourceDecision = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
            new GoalAcceptanceVerifier.CommandResult(red ? 1 : 0, ""), sourceEvidence, requireTrxEvidence: true);
        Assert.Equal(sourceDecision, check.CompletionDecision);
        Assert.Equal(red ? new[] { "AlphaTests.RemoteFailure" } : Array.Empty<string>(), check.FailingTestIdentities);
        Assert.Equal(red ? "remote-executor=fixture-executor remote-red failing=AlphaTests.RemoteFailure" :
            "remote-executor=fixture-executor", check.ResultSummary);
        if (red) Assert.StartsWith("remote-first verdict ", check.OutputTail);
        else Assert.Null(check.OutputTail);
        var retained = check.TestResultPaths!;
        Assert.Equal(sources.Count, retained.Count);
        var folder = Path.Combine(Path.GetDirectoryName(scenario.Prefix)!, "remote-first",
            GoalAcceptanceVerifier.ShortHash(request.AttemptId), request.Lane);
        for (var index = 0; index < sources.Count; index++)
        {
            Assert.Equal(Path.Combine(folder, Path.GetFileName(sources[index])), retained[index]);
            Assert.NotEqual(sources[index], retained[index]);
            Assert.True(File.Exists(retained[index]));
            Assert.Equal(File.ReadAllBytes(sources[index]), File.ReadAllBytes(retained[index]));
            Assert.False(File.Exists(NormalizedTestResultWriter.PathFor(sources[index])));
        }
        Assert.Equal(0, scenario.LocalCalls);
        Assert.Equal(0, scenario.BuildCalls);
        var health = Assert.Single(scenario.Health());
        Assert.Equal(red ? RemoteLaneOutcomeCode.RemoteRed : RemoteLaneOutcomeCode.Accepted, health.Outcome);
        Assert.Equal(health.Expected, health.Observed);
    }

    private static void AssertRoundTrip(string source, string normalizedPath)
    {
        Assert.True(File.Exists(normalizedPath));
        var original = new TrxTestResultSource().Read(source);
        var normalized = new NormalizedTestResultSource().Read(normalizedPath);
        Assert.Equal(TestResultReadStatus.Readable, original.Status);
        Assert.Equal(TestResultReadStatus.Readable, normalized.Status);
        Assert.NotEmpty(original.Records);
        Assert.Equal(original.Records.Count, normalized.Records.Count);
        for (var index = 0; index < original.Records.Count; index++)
        {
            var expected = original.Records[index];
            var actual = normalized.Records[index];
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.DisplayName, actual.DisplayName);
            Assert.Equal(expected.SuitePath, actual.SuitePath);
            Assert.Equal(expected.Attempts, actual.Attempts);
            Assert.Equal(expected with { SuitePath = actual.SuitePath, Attempts = actual.Attempts }, actual);
            Assert.Equal("trx", actual.ReportedBy);
        }
    }
}
