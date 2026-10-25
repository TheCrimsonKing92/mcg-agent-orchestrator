using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

[Collection("IsolatedProcessSpawning")]
public sealed class RemoteFocusedEvidenceShadowPhaseTests : GoalAcceptanceVerifierTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Complete_TrxTiming_WritesOnlyAvailableShadowPhases(bool withTimes)
    {
        var root = CreateManifestWorkspace("""
            {"version":1,"checks":[],"forbiddenChangedPathGlobs":[]}
            """);
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
                 "machineLocalResourceKeys":["machine:GitHeavyLanes"],
                 "focusedEvidence":{"mode":"shadow","sampleEvery":1,"graceSeconds":300}}
                """);
            var clock = new ManualRemoteLaneClock();
            var fake = new FakeRemoteLaneExecutor
            {
                Submit = (request, _) =>
                {
                    var path = Path.Combine(root, ".orchestrator", "remote.trx");
                    var start = clock.GetUtcNow();
                    new XDocument(new XElement("TestRun",
                        withTimes ? new XElement("Times", new XAttribute("start", start.ToString("o")),
                            new XAttribute("finish", start.AddSeconds(425).ToString("o"))) : null,
                        new XElement("TestDefinitions", new XElement("UnitTest", new XAttribute("id", "one"),
                            new XAttribute("name", "AlphaTests.Executes"), new XElement("TestMethod",
                                new XAttribute("className", "AlphaTests"), new XAttribute("name", "Executes")))),
                        new XElement("Results", new XElement("UnitTestResult", new XAttribute("testId", "one"),
                            new XAttribute("testName", "AlphaTests.Executes"), new XAttribute("outcome", "Passed"))),
                        new XElement("ResultSummary", new XAttribute("outcome", "Completed"), new XElement("Counters",
                            new XAttribute("total", 1), new XAttribute("executed", 1), new XAttribute("passed", 1),
                            new XAttribute("failed", 0), new XAttribute("notExecuted", 0))))).Save(path);
                    var handle = new FakeRemoteLaneExecutor.Handle(start);
                    handle.Publish(new(request.ExecutorId, request.Lane, request.FilterHash,
                        request.VerifyingCommitSha, request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [path]));
                    return Task.FromResult(new RemoteLaneSubmission(handle));
                }
            };
            TestOverrides.RemoteLaneExecutorConfigurationPathForTests = config;
            TestOverrides.RemoteLaneExecutorForTests = fake;
            TestOverrides.RemoteLaneTimeProviderForTests = clock;
            var goal = new GoalId(Guid.NewGuid().ToString("N"));
            await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(root, goal,
                options: new(RunId: "selected-attempt", IntegrationBranch: "main"));
            var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
            {
                Name = "focused timing", Type = "dotnet-test", Runner = "mtp", IsFocusedEvidenceSelection = true,
                Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
            };
            var context = Assert.IsAssignableFrom<IAcceptanceRunExecutionContext>(owner);
            await using var shadow = RemoteFocusedEvidenceShadow.Start(TestOverrides, clock, root, [check], goal, context, default);
            await shadow.CompleteAsync([]);
            var request = Assert.Single(fake.Requests);
            Assert.Null(request.TestHostTimeoutSeconds);
            var ledger = RemoteExecutorHealthLedger.ResolveStorePath(root);
            var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(ledger));
            Assert.True(row.EvidenceShadow);
            Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
            using var json = JsonDocument.Parse(File.ReadAllLines(ledger).Single());
            Assert.True(json.RootElement.GetProperty("evidence_shadow").GetBoolean());
            if (withTimes)
                Assert.Equal(425, json.RootElement.GetProperty("phases").GetProperty("test_seconds").GetDouble());
            else Assert.False(json.RootElement.TryGetProperty("phases", out _));

            string Git(params string[] args)
            {
                var result = GitCli.Run(root, args);
                Assert.True(result.Succeeded, result.Error);
                return result.Output;
            }
        }
        finally { DeleteDirectoryWithRetry(root); }
    }
}
