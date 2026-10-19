using Mcg.AgentOrchestrator.Infrastructure;

[Collection("IsolatedProcessSpawning")]
public sealed class RemoteFirstFocusedEvidenceHostBoundTests : RemoteFirstFocusedEvidenceFixture
{
    [Fact]
    public async Task HostCheckFallsBackWithoutSubmittingAndPreservesTheLocalResult()
    {
        using var scenario = HostScenario(includeHost: true);
        var fake = scenario.CompletedExecutor();
        var check = FocusedCheck("HostOnlyFixtureTests");
        var work = scenario.DirectAsync([check]);
        await Event(work, "host-bound local fallback");
        var outcome = await work;
        Assert.Empty(fake.Requests);
        Assert.Equal(1, scenario.LocalCalls);
        Assert.Equal(new AcceptanceCheckResult(check.Name, true, 0, "local-passed"), Assert.Single(outcome.Results));
        var row = Assert.Single(scenario.Health());
        Assert.Equal(RemoteLaneOutcomeCode.NotEligibleExclusiveResource, row.Outcome);
        Assert.StartsWith("remote-first-fallback: exclusive-key:host:DotnetBuildSlotsHostOnly", row.Reason);
        Assert.Null(row.FaultOwner);
        Assert.Empty(row.ExecutorId);
    }

    [Theory]
    [InlineData("AlphaTests")]
    [InlineData("BuildSlotFixtureTests")]
    public async Task EligibleRemoteResultEqualsTheBaseline(string name)
    {
        var actual = await RunEligibleAsync(includeHost: true, name);
        var baseline = await RunEligibleAsync(includeHost: false, name);
        Assert.Equal(1, actual.Requests);
        Assert.Equal(baseline.Requests, actual.Requests);
        Assert.Equal(0, actual.LocalCalls);
        Assert.Equal(baseline.LocalCalls, actual.LocalCalls);
        Assert.True(actual.Result.Passed);
        Assert.Equal(3, actual.Result.ExecutedTestCount);
        Assert.Equal(baseline.Result.Passed, actual.Result.Passed);
        Assert.Equal(baseline.Result.ExecutedTestCount, actual.Result.ExecutedTestCount);
        Assert.Equal(baseline.Result.FailingTestIdentities!.ToArray(), actual.Result.FailingTestIdentities!.ToArray());
        Assert.Equal(RemoteLaneOutcomeCode.Accepted, actual.Row.Outcome);
        Assert.Equal(baseline.Row.Outcome, actual.Row.Outcome);
    }

    private async Task<(int Requests, int LocalCalls, AcceptanceCheckResult Result, RemoteExecutorHealthRecord Row)>
        RunEligibleAsync(bool includeHost, string name)
    {
        using var scenario = HostScenario(includeHost);
        var fake = scenario.CompletedExecutor();
        var work = scenario.DirectAsync([FocusedCheck(name)]);
        await Event(work, "eligible remote-first completion");
        return (fake.Requests.Count, scenario.LocalCalls, Assert.Single((await work).Results), Assert.Single(scenario.Health()));
    }

    private Scenario HostScenario(bool includeHost)
    {
        var scenario = new Scenario(this, FocusedEvidenceRemoteEligibilityTests.FixtureManifest(includeHost));
        FocusedEvidenceRemoteEligibilityTests.WriteFixtureSources(scenario.Root);
        RemoteFocusedEvidenceShadowHostBoundTests.CommitFixture(scenario.Root);
        File.WriteAllText(scenario.ConfigurationPath, """
            {"executors":[{"id":"fixture-executor","slots":1}],"lanes":[],
             "machineLocalResourceKeys":["xunit:DotnetBuildSlots"],
             "focusedEvidence":{"mode":"remote-first","sampleEvery":1,"graceSeconds":300}}
            """);
        return scenario;
    }
}
