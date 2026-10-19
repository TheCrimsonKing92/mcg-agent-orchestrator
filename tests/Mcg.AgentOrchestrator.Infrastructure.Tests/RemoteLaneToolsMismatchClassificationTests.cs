using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

public sealed class RemoteLaneToolsMismatchClassificationTests
{
    [Theory]
    [InlineData("executor-tools-mismatch", "ExecutorToolsMismatch", null)]
    [InlineData("executor-tools-unverified", "ExecutorToolsUnverified", "executor")]
    [InlineData("executor-busy", "ExecutorBusy", null)]
    [InlineData("fixture-unreachable", "Unreachable", "executor")]
    public async Task GateRefusalRecordsSpecificOutcomeAndFaultOwner(string reason, string code, string? owner)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var clock = new ManualRemoteLaneClock();
            var entry = new RemoteLaneExecutorEntry("one", 60);
            var fake = new FakeRemoteLaneExecutor { Submit = (_, _) => Task.FromResult(new RemoteLaneSubmission(null, reason)) };
            using var coordinator = new RemoteLaneCoordinator(new([entry], ["lane"], null),
                new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"), root, Path.Combine(root, "result"),
                fake, clock, TimeSpan.FromSeconds(1), null);
            Assert.Equal(entry, coordinator.TryClaimIdleExecutor());
            var outcome = await coordinator.RunRemoteAsync(new Check
            {
                Name = "lane", Type = "dotnet-test",
                Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
                Arguments = ["--filter", "FullyQualifiedName~FixtureTests"]
            }, entry, TimeSpan.FromMinutes(1), default);
            Assert.Null(outcome.Accepted);
            Assert.Single(fake.Requests);
            var path = RemoteExecutorHealthLedger.ResolveStorePath(root);
            var row = Assert.Single(RemoteExecutorHealthLedger.ReadAll(path));
            Assert.Equal(Enum.Parse<RemoteLaneOutcomeCode>(code), row.Outcome);
            Assert.Equal(owner, row.FaultOwner);
            using var json = JsonDocument.Parse(Assert.Single(File.ReadAllLines(path)));
            Assert.Equal(JsonNamingPolicy.KebabCaseLower.ConvertName(code), json.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(owner, json.RootElement.GetProperty("fault_owner").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("executor-tools-mismatch", 1, 1.0)]
    [InlineData("unreachable", 2, 0.5)]
    [InlineData("executor-tools-unverified", 2, 0.5)]
    public void ReportExcludesOnlyVerifiedMismatchFromHealthAttempts(string outcome, int attempts, double share)
    {
        var now = new ManualRemoteLaneClock().GetUtcNow();
        var report = RemoteExecutorReport.Build(["one"],
            [new(now, "one", "accepted-attempt", "lane", "accepted", null),
             new(now, "one", "refused-attempt", "lane", outcome, null)], []);
        var row = Assert.Single(report.Executors);
        Assert.Equal(attempts, row.Attempts);
        Assert.Equal(share, row.AcceptedShare);
    }
}
