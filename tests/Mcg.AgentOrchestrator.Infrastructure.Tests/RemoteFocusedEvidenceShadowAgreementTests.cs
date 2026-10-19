using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection("IsolatedProcessSpawning")]
public sealed class RemoteFocusedEvidenceShadowAgreementTests : GoalAcceptanceVerifierTestBase
{
    private const string CheckName = "focused agreement";
    private const string Miss = "DotnetBaseBuildCacheMissDetailTests.BuildPhase_MissReasonIsOneJournalCompatibleField";
    private static readonly string[] Incident =
    [
        Miss,
        "DotnetBuildEnvironmentManagerTestsLocalHostOnlyFocusedRunner.FocusedRunner_Pass_ExecutesUnderLeaseAndWritesReceipt",
        "DotnetBuildEnvironmentManagerTestsLocalHostOnlyLockAttribution.LockAttributionRestartManagerNamesFileHolder"
    ];

    [Theory]
    [InlineData(false, "disagree", "failing-set-differs")]
    [InlineData(true, "agree", "same-failing-set")]
    public async Task Complete_IncidentFailures_RecordsBothSetsAndAgreement(
        bool sameSet, string agreement, string reason)
    {
        string[] localSet = sameSet ? Incident.Reverse().ToArray() : [Miss];
        var (row, json) = await RunShadow(Incident, 1, Failed(localSet));
        Assert.Equal(RemoteLaneOutcomeCode.RemoteRed, row.Outcome);
        Assert.Equal(sameSet, row.Agree);
        Assert.Equal(agreement, row.Agreement);
        Assert.Equal(reason, row.AgreementReason);
        Assert.Equal("failed", row.LocalVerdict);
        Assert.Equal(localSet, row.LocalFailingTests);
        Assert.Equal(Incident, row.RemoteFailingTests);
        Assert.Equal("remote-red", json.GetProperty("outcome").GetString());
        Assert.Equal(sameSet, json.GetProperty("agree").GetBoolean());
        Assert.Equal(agreement, json.GetProperty("agreement").GetString());
        Assert.Equal(reason, json.GetProperty("agreement_reason").GetString());
        Assert.Equal("failed", json.GetProperty("local_verdict").GetString());
        Assert.Equal(localSet, Strings(json.GetProperty("local_failing_tests")));
        Assert.Equal(Incident, Strings(json.GetProperty("remote_failing_tests")));
    }

    [Theory]
    [InlineData("local-timed-out")]
    [InlineData("local-no-failing-identities")]
    [InlineData("local-unavailable")]
    [InlineData("remote-trx-incomplete")]
    [InlineData("remote-no-failing-identities")]
    public async Task Complete_UnusableVerdict_RecordsIncomparableReason(string reason)
    {
        AcceptanceCheckResult? local = reason switch
        {
            "local-timed-out" => Failed(null) with
            {
                CompletionDecision = new(false, "timed-out", true, 1, null, null, "Failed")
            },
            "local-no-failing-identities" => Failed(null) with { CompletionDecision = null },
            "local-unavailable" => null,
            _ => Passed()
        };
        var missingTrx = reason == "remote-trx-incomplete";
        var (row, json) = await RunShadow(reason == "remote-no-failing-identities" ? [] : [Miss],
            missingTrx ? 0 : 1, local, missingTrx);
        Assert.Null(row.Agree);
        Assert.Equal("incomparable", row.Agreement);
        Assert.Equal(reason, row.AgreementReason);
        Assert.False(json.TryGetProperty("agree", out _));
        Assert.Equal("incomparable", json.GetProperty("agreement").GetString());
        Assert.Equal(reason, json.GetProperty("agreement_reason").GetString());
        Assert.Equal(missingTrx ? RemoteLaneOutcomeCode.TrxIncomplete : RemoteLaneOutcomeCode.RemoteRed, row.Outcome);
        if (local is null)
        {
            Assert.Equal("unavailable", row.LocalVerdict);
            Assert.Null(row.LocalFailingTests);
            Assert.False(json.TryGetProperty("local_failing_tests", out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_UsableRemoteAndPassingLocal_KeepsLegacyAgree(bool remoteRed)
    {
        var local = Passed();
        var (row, json) = await RunShadow(remoteRed ? [Miss] : [], remoteRed ? 1 : 0, local);
        Assert.Equal(remoteRed ? RemoteLaneOutcomeCode.RemoteRed : RemoteLaneOutcomeCode.Accepted, row.Outcome);
        Assert.Equal((row.Outcome == RemoteLaneOutcomeCode.Accepted) == local.Passed, row.Agree);
        Assert.Equal(remoteRed ? "disagree" : "agree", row.Agreement);
        Assert.Equal(remoteRed ? "verdict-differs" : "both-passed", row.AgreementReason);
        Assert.Empty(row.LocalFailingTests!);
        Assert.Empty(Strings(json.GetProperty("local_failing_tests")));
    }

    private async Task<(RemoteExecutorHealthRecord Row, JsonElement Json)> RunShadow(
        string[] remoteFailures, int exitCode, AcceptanceCheckResult? local, bool missingTrx = false)
    {
        var root = CreateManifestWorkspace("""{"version":1,"checks":[],"forbiddenChangedPathGlobs":[]}""");
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), ".orchestrator/\n");
            Git("init", "-b", "main");
            Git("add", ".");
            Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
                "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
            var config = Path.Combine(root, ".orchestrator", "executors.json");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(config, """
                {"executors":[{"id":"fixture-executor"}],"lanes":[],
                 "focusedEvidence":{"mode":"shadow","sampleEvery":1,"graceSeconds":300}}
                """);
            var clock = new ManualRemoteLaneClock();
            var fake = new FakeRemoteLaneExecutor
            {
                Submit = (request, _) =>
                {
                    var path = Path.Combine(root, ".orchestrator", "remote.trx");
                    if (!missingTrx) WriteTrx(path, remoteFailures);
                    var handle = new FakeRemoteLaneExecutor.Handle(clock.GetUtcNow());
                    handle.Publish(new(request.ExecutorId, request.Lane, request.FilterHash,
                        request.VerifyingCommitSha, request.CandidateTreeSha, request.MainSha,
                        request.ManifestIdentity, exitCode, missingTrx ? [] : [path]));
                    return Task.FromResult(new RemoteLaneSubmission(handle));
                }
            };
            TestOverrides.RemoteLaneExecutorConfigurationPathForTests = config;
            TestOverrides.RemoteLaneExecutorForTests = fake;
            TestOverrides.RemoteLaneTimeProviderForTests = clock;
            var goal = new GoalId(Guid.NewGuid().ToString("N"));
            await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(root, goal,
                options: new(RunId: "agreement-attempt", IntegrationBranch: "main"));
            var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
            {
                Name = CheckName, Type = "dotnet-test", Runner = "mtp", IsFocusedEvidenceSelection = true,
                Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
            };
            var context = Assert.IsAssignableFrom<IAcceptanceRunExecutionContext>(owner);
            await using var shadow = RemoteFocusedEvidenceShadow.Start(TestOverrides, clock, root, [check], goal, context, default);
            await shadow.CompleteAsync(local is null ? [] : [local]);
            Assert.Single(fake.Requests);
            var ledger = RemoteExecutorHealthLedger.ResolveStorePath(root);
            var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(ledger));
            Assert.True(row.EvidenceShadow);
            using var document = JsonDocument.Parse(File.ReadAllLines(ledger).Single());
            var json = document.RootElement.Clone();
            AssertBaseline(root, row, json);
            return (row, json);

            void Git(params string[] args)
            {
                var result = GitCli.Run(root, args);
                Assert.True(result.Succeeded, result.Error);
            }
        }
        finally { DeleteDirectoryWithRetry(root); }
    }

    private static void AssertBaseline(string root, RemoteExecutorHealthRecord row, JsonElement actual)
    {
        var baselinePath = Path.Combine(root, ".orchestrator", "baseline.jsonl");
        RemoteExecutorHealthLedger.Append(baselinePath, row with
        {
            LocalFailingTests = null, Agreement = null, AgreementReason = null
        });
        using var baseline = JsonDocument.Parse(File.ReadAllLines(baselinePath).Single());
        var names = baseline.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (row.LocalFailingTests is not null) names.Add("local_failing_tests");
        if (row.Agreement is not null) names.Add("agreement");
        if (row.AgreementReason is not null) names.Add("agreement_reason");
        Assert.Equal(names.Order(StringComparer.Ordinal),
            actual.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var property in baseline.RootElement.EnumerateObject())
            Assert.Equal(property.Value.GetRawText(), actual.GetProperty(property.Name).GetRawText());
    }

    private static AcceptanceCheckResult Passed() => new(CheckName, true, 0, null);
    private static AcceptanceCheckResult Failed(IReadOnlyList<string>? identities) => new(CheckName, false, 1, null,
        FailingTestIdentities: identities, CompletionDecision: new(false, "failing-trx", false, 1, null, null, "Failed"));
    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static void WriteTrx(string path, string[] failures)
    {
        var identities = failures.Length == 0 ? new[] { "AlphaTests.Executes" } : failures;
        new XDocument(new XElement("TestRun",
            new XElement("TestDefinitions", identities.Select((identity, index) =>
                new XElement("UnitTest", new XAttribute("id", index), new XAttribute("name", identity),
                    new XElement("TestMethod", new XAttribute("className", identity[..identity.LastIndexOf('.')]),
                        new XAttribute("name", identity[(identity.LastIndexOf('.') + 1)..]))))),
            new XElement("Results", identities.Select((identity, index) =>
                new XElement("UnitTestResult", new XAttribute("testId", index), new XAttribute("testName", identity),
                    new XAttribute("outcome", failures.Length == 0 ? "Passed" : "Failed")))),
            new XElement("ResultSummary", new XAttribute("outcome", failures.Length == 0 ? "Completed" : "Failed"),
                new XElement("Counters", new XAttribute("total", identities.Length), new XAttribute("executed", identities.Length),
                    new XAttribute("passed", failures.Length == 0 ? 1 : 0), new XAttribute("failed", failures.Length),
                    new XAttribute("notExecuted", 0))))).Save(path);
    }
}
