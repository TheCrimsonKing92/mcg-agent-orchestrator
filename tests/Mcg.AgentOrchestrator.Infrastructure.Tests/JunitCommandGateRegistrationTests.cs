using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each case owns its manifest; no child process or global state changes.
public sealed class JunitCommandGateRegistrationTests : GoalAcceptanceVerifierTestBase
{
    [Fact]
    public void ValidDeclaration_IsTestProducing_AndAcceptedAtStartup()
    {
        using var fixture = new JunitCommandFixture();
        Assert.True(GateCheckSelectorMatcher.IsTestResultProducing(fixture.Check));
        Assert.Equal(1, GoalAcceptanceVerifier.ValidateStartupContract(fixture.Root).ManifestCheckCount);
    }

    [Theory]
    [InlineData("", ".", "reports/*.xml")]
    [InlineData("runner", "../outside", "reports/*.xml")]
    [InlineData("runner", "/absolute", "reports/*.xml")]
    [InlineData("runner", "C:\\outside", "reports/*.xml")]
    [InlineData("runner", ".", "../reports/*.xml")]
    [InlineData("runner", ".", "reports\\..\\*.xml")]
    [InlineData("runner", ".", "C:\\reports\\*.xml")]
    [InlineData("runner", ".", "/reports/*.xml")]
    [InlineData("runner", ".", "")]
    public void InvalidDeclaration_StartupRefuses(string command, string project, string report)
    {
        var root = CreateManifestWorkspace(JsonSerializer.Serialize(new { version = 1,
            checks = new[] { new Check { Type = "junit-command", Command = command, Project = project, Reports = [report] } } }));
        try { Assert.Throws<ArgumentException>(() => GoalAcceptanceVerifier.ValidateStartupContract(root)); }
        finally { DeleteDirectoryWithRetry(root); }
    }
}
