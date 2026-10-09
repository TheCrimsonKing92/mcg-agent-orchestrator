using Mcg.AgentOrchestrator.Infrastructure;

public sealed class FocusedEvidenceDashboardAliasTests
{
    [Xunit.Fact(DisplayName = "Core aliases remain supported and diagnostics no longer advertise dashboard aliases")]
    public void OtherAliasesAndDiagnosticContractRemainSupported()
    {
        Assert.True(GoalAcceptanceVerifier.TryResolveFocusedEvidenceProject("Core.Tests", out var project));
        Assert.Equal(AcceptancePolicyShardPlanner.CoreTestsProject, project);
        Assert.DoesNotContain("Dashboard", DeclaredTestProjectInventory.LegacyAliasProjectForms, StringComparison.Ordinal);
    }
}
