public sealed class HealthInspectorNetworkSourceGuardTests
{
    [Theory]
    [InlineData("HttpClient")]
    [InlineData("GetAsync")]
    [InlineData("System.Net")]
    public void InspectorSource_ForbiddenNetworkTokens_AreAbsent(string token)
    {
        var root = VerifiedRepositoryRoot.Find();
        var source = File.ReadAllText(Path.Combine(root, "src",
            "Mcg.AgentOrchestrator.Infrastructure", "Diagnostics", "OrchestratorHealthInspector.cs"));

        Assert.DoesNotContain(token, source, StringComparison.Ordinal);
    }
}
