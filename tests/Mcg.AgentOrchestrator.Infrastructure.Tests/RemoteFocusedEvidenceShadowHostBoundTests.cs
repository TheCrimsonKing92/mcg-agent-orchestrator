using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using Fixture = FocusedEvidenceRemoteEligibilityTests.Fixture;

[Collection("IsolatedProcessSpawning")]
public sealed class RemoteFocusedEvidenceShadowHostBoundTests : GoalAcceptanceVerifierTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostCheckDoesNotClaimTheSlotOrBlameTheExecutor(bool includeAlpha)
    {
        var host = FocusedEvidenceRemoteEligibilityTests.Focused("FullyQualifiedName~HostOnlyFixtureTests");
        var alpha = FocusedEvidenceRemoteEligibilityTests.Focused("FullyQualifiedName~AlphaTests");
        var (requests, rows) = await RunAsync(includeHost: true, includeAlpha ? [host, alpha] : [host]);
        Assert.Equal(includeAlpha ? 2 : 1, rows.Count);
        var row = Assert.Single(rows.Where(entry => entry.Lane == RemoteFocusedEvidenceShadow.LaneName(host.Project!, host.Arguments[1])));
        Assert.Equal(RemoteLaneOutcomeCode.NotEligibleExclusiveResource, row.Outcome);
        Assert.Equal("exclusive-key:host:DotnetBuildSlotsHostOnly", row.Reason);
        Assert.Null(row.FaultOwner);
        Assert.True(row.EvidenceShadow);
        Assert.Equal("passed", row.LocalVerdict);
        Assert.Null(row.Agree);
        Assert.Empty(row.ExecutorId);
        Assert.Equal(0, row.RemoteExecutedCount);
        Assert.Empty(row.RemoteFailingTests!);
        if (includeAlpha)
        {
            Assert.Equal(alpha.Arguments[1], Assert.Single(requests).Filter);
            Assert.Equal(RemoteLaneOutcomeCode.Accepted, Assert.Single(rows.Where(entry => entry != row)).Outcome);
        }
        else Assert.Empty(requests);
    }

    [Theory]
    [InlineData("AlphaTests")]
    [InlineData("BuildSlotFixtureTests")]
    public async Task EligibleRequestAndLedgerFieldsEqualTheBaseline(string name)
    {
        var check = FocusedEvidenceRemoteEligibilityTests.Focused("FullyQualifiedName~" + name);
        var actual = await RunAsync(includeHost: true, [check]);
        var baseline = await RunAsync(includeHost: false, [check]);
        Assert.Single(actual.Requests);
        Assert.Single(baseline.Requests);
        var row = Assert.Single(actual.Rows);
        var control = Assert.Single(baseline.Rows);
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, row.Outcome);
        Assert.Equal(1, row.RemoteExecutedCount);
        Assert.True(row.Agree);
        Assert.Equal(control.Outcome, row.Outcome);
        Assert.Equal(control.Lane, row.Lane);
        Assert.Equal(control.Expected.Filter, row.Expected.Filter);
        Assert.Equal(control.RemoteExecutedCount, row.RemoteExecutedCount);
        Assert.Equal(control.RemoteFailingTests!.ToArray(), row.RemoteFailingTests!.ToArray());
        Assert.Equal(control.FaultOwner, row.FaultOwner);
        Assert.Equal(control.LocalVerdict, row.LocalVerdict);
        Assert.Equal(control.Agree, row.Agree);
    }

    private async Task<(IReadOnlyList<RemoteLaneRequest> Requests, IReadOnlyList<RemoteExecutorHealthRecord> Rows)>
        RunAsync(bool includeHost, IReadOnlyList<Check> checks)
    {
        using var fixture = new Fixture(FocusedEvidenceRemoteEligibilityTests.FixtureManifest(includeHost));
        CommitFixture(fixture.Root);
        var configurationPath = Path.Combine(fixture.Root, ".orchestrator", "executors.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
        File.WriteAllText(configurationPath, """
            {"executors":[{"id":"fixture-executor","slots":1}],"lanes":[],
             "machineLocalResourceKeys":["xunit:DotnetBuildSlots"],
             "focusedEvidence":{"mode":"shadow","sampleEvery":1,"graceSeconds":300}}
            """);
        var clock = new ManualRemoteLaneClock();
        var fake = new FakeRemoteLaneExecutor
        {
            Submit = (request, _) =>
            {
                var path = Path.Combine(fixture.Root, ".orchestrator", request.Lane + ".trx");
                var className = request.Filter.Split('~').Last();
                new XDocument(new XElement("TestRun",
                    new XElement("TestDefinitions", new XElement("UnitTest", new XAttribute("id", "one"),
                        new XAttribute("name", className + ".Runs"), new XElement("TestMethod",
                            new XAttribute("className", className), new XAttribute("name", "Runs")))),
                    new XElement("Results", new XElement("UnitTestResult", new XAttribute("testId", "one"),
                        new XAttribute("testName", className + ".Runs"), new XAttribute("outcome", "Passed"))),
                    new XElement("ResultSummary", new XAttribute("outcome", "Completed"), new XElement("Counters",
                        new XAttribute("total", 1), new XAttribute("executed", 1), new XAttribute("passed", 1),
                        new XAttribute("failed", 0), new XAttribute("notExecuted", 0))))).Save(path);
                var handle = new FakeRemoteLaneExecutor.Handle(clock.GetUtcNow());
                handle.Publish(new(request.ExecutorId, request.Lane, request.FilterHash, request.VerifyingCommitSha,
                    request.CandidateTreeSha, request.MainSha, request.ManifestIdentity, 0, [path]));
                return Task.FromResult(new RemoteLaneSubmission(handle));
            }
        };
        TestOverrides.RemoteLaneExecutorConfigurationPathForTests = configurationPath;
        TestOverrides.RemoteLaneExecutorForTests = fake;
        TestOverrides.RemoteLaneTimeProviderForTests = clock;
        var goal = new GoalId(Guid.NewGuid().ToString("N"));
        await using var owner = AcceptanceExecutionOwners.CreateFocusedVerification(fixture.Root, goal,
            options: new(RunId: "host-bound-shadow", IntegrationBranch: "main"));
        await using var shadow = RemoteFocusedEvidenceShadow.Start(TestOverrides, clock, fixture.Root, checks, goal,
            (IAcceptanceRunExecutionContext)owner, default);
        var local = checks.Select(check => new AcceptanceCheckResult(check.Name, true, 0, "local-passed")).ToArray();
        await RemoteFirstFocusedEvidenceFixture.Event(shadow.CompleteAsync(local), "shadow ledger completion");
        return (fake.Requests.ToArray(), RemoteExecutorHealthLedger.ReadAll(RemoteExecutorHealthLedger.ResolveStorePath(fixture.Root)));
    }

    internal static void CommitFixture(string root)
    {
        File.WriteAllText(Path.Combine(root, ".gitignore"), ".orchestrator/\n");
        Git("init", "-b", "main");
        Git("add", ".");
        Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false", "commit", "-m", "fixture sources");
        Assert.Empty(Git("status", "--porcelain", "--untracked-files=all"));

        string Git(params string[] args)
        {
            var result = GitCli.Run(root, args);
            Assert.True(result.Succeeded, result.Error);
            return result.Output;
        }
    }
}
