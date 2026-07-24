using Mcg.AgentOrchestrator.App.Orchestration;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ConductorSuccessorSelfCheckTests
{
    [Xunit.Fact(DisplayName = "ConductorSuccessorSelfCheck_rejects_incomplete_protocol_arguments")]
    public void RejectsIncompleteProtocolArguments()
    {
        var result = ConductorSuccessorSelfCheck.Run(
            [ConductorSuccessorSelfCheck.SubcommandName, "only-one-argument"]);

        Assert.Equal(2, result);
    }
}
