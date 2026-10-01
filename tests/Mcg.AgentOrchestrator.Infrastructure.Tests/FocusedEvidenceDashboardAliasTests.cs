using Mcg.AgentOrchestrator.Infrastructure;

public sealed class FocusedEvidenceDashboardAliasTests
{
    [Xunit.Theory(DisplayName = "Removed dashboard aliases report the existing unknown-project diagnostic")]
    [Xunit.InlineData("Dashboard.Tests")]
    [Xunit.InlineData("Dashboard")]
    [Xunit.InlineData("Mcg.AgentOrchestrator.Dashboard.Tests")]
    [Xunit.InlineData("tests/Mcg.AgentOrchestrator.Dashboard.Tests/Mcg.AgentOrchestrator.Dashboard.Tests.csproj")]
    public void DashboardAliasesAreUnknownWithoutManifestDeclarations(string alias)
    {
        var settings = new AcceptanceGateEngineSettings();
        Assert.False(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject(alias, settings, out _));
        var resolved = GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
            $"{alias}:FullyQualifiedName~DashboardRenderingTests", settings, Path.GetTempPath(),
            out var checks, out _, out var rejection);

        Assert.False(resolved);
        Assert.Empty(checks);
        Assert.Equal(FocusedEvidenceRejectionCode.UnsupportedProject, rejection.Code);
        Assert.Equal($"unsupported evidence request project alias '{alias}'", rejection.Detail);
    }

    [Xunit.Fact(DisplayName = "Core aliases remain supported and diagnostics no longer advertise dashboard aliases")]
    public void OtherAliasesAndDiagnosticContractRemainSupported()
    {
        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject("Core.Tests", out var project));
        Assert.Equal(AcceptancePolicyShardPlanner.CoreTestsProject, project);
        Assert.DoesNotContain("Dashboard", DeclaredTestProjectInventory.LegacyAliasProjectForms, StringComparison.Ordinal);
    }
}
