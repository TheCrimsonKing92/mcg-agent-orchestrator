using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierCrosswalkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manifest_DispatchesListThenRun_AndPreservesOpaqueIds(bool explicitSelection)
    {
        using var fixture = new CrosswalkAcceptanceFixture(explicitSelection ? [CrosswalkAcceptanceFixture.Id] : []);
        Assert.NotNull(GoalAcceptanceVerifier.ValidateStartupContract(fixture.Root));
        var check = await fixture.RunAsync();
        Assert.True(check.Passed, check.OutputTail);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Contains(" list ", fixture.Calls[0].Command);
        Assert.Contains(" run ", fixture.Calls[1].Command);
        Assert.EndsWith(" --id '" + CrosswalkAcceptanceFixture.Id.Replace("'", "''") + "'; exit $LASTEXITCODE", fixture.Calls[1].Command);
        Assert.DoesNotContain("--filter", fixture.Calls[1].Command);
        Assert.All(fixture.Calls, call => Assert.Equal(fixture.Root, call.Root));
        Assert.All(fixture.Calls, call => Assert.True(call.Token.CanBeCanceled));
        Assert.Equal(1, check.ExecutedTestCount);
        Assert.Equal(1, check.DiscoveredTestCount);
        Assert.Equal("attempt-current", check.TestResultAttemptId);
        Assert.Equal(0, check.TestResultRunOrdinal);
        Assert.Null(check.TestResultPaths);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.trx", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("case")]
    [InlineData("discovery-empty")]
    [InlineData("discovery-duplicate")]
    [InlineData("discovery-blank")]
    public async Task InvalidSelectionOrDiscovery_FailsBeforeRun(string kind)
    {
        var selectors = kind switch
        {
            "empty" => new[] { "" }, "blank" => [" "], "duplicate" => ["a", "a"],
            "unknown" => ["missing"], "case" => ["A"], _ => Array.Empty<string>()
        };
        using var fixture = new CrosswalkAcceptanceFixture(selectors);
        fixture.Discovery = kind switch
        {
            "discovery-empty" => "[]", "discovery-duplicate" => CrosswalkAcceptanceFixture.List("a", "a"),
            "discovery-blank" => CrosswalkAcceptanceFixture.List(" "), _ => CrosswalkAcceptanceFixture.List("a")
        };
        var check = await fixture.RunAsync();
        Assert.False(check.Passed);
        Assert.Single(fixture.Calls);
        Assert.Contains(" list ", fixture.Calls[0].Command);
        Assert.NotEmpty(check.CrosswalkDiagnostics!);
        Assert.Empty(check.NormalizedTestResultPaths!);
    }

    [Fact]
    public async Task NonCrosswalkCommand_DoesNotInvokeListOrRun()
    {
        using var fixture = new CrosswalkAcceptanceFixture(type: "command");
        var check = await fixture.RunAsync();
        Assert.True(check.Passed);
        Assert.Empty(fixture.Calls);
        Assert.Null(check.CrosswalkDiagnostics);
    }
}
