using Mcg.AgentOrchestrator.Infrastructure;

public sealed class IcaclsIntegrityLabelerTests
{
    [Xunit.Fact]
    public void QueryOutput_MediumInheritable_ReportsMediumNotLow()
    {
        const string output = """
            C:\workspace BUILTIN\Users:(OI)(CI)(RX)
                        Mandatory Label\Medium Mandatory Level:(OI)(CI)(NW)
            Successfully processed 1 files; Failed processing 0 files
            """;

        var state = IcaclsIntegrityLabeler.ParseQueryOutput(output);

        Assert.True(state.Exists);
        Assert.True(state.Medium);
        Assert.True(state.Inheritable);
        Assert.False(state.Low);
    }
}
