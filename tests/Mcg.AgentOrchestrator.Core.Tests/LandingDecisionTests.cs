using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class LandingDecisionTests
{
    // --- Promote: low-risk behavior change, green gates, clean merge, no repeated failures ---

    [Xunit.Fact(DisplayName = "LandingDecision_Promote_for_low_risk_behavior_change_with_all_gates_green")]
    public void LandingDecisionPromoteForLowRiskBehaviorChange()
    {
        var inputs = new LandingInputs(
            BehaviorChangeSummary(),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: 0);

        var decision = LandingDecisionEngine.Decide(inputs);

        Assert.True(decision is LandingDecision.Promote);
    }

    [Xunit.Fact(DisplayName = "LandingDecision_Promote_for_docs_only_change")]
    public void LandingDecisionPromoteForDocsOnlyChange()
    {
        var inputs = new LandingInputs(
            DocsOnlyChangeSummary(),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: 0);

        var decision = LandingDecisionEngine.Decide(inputs);

        Assert.True(decision is LandingDecision.Promote);
    }

    // --- Escalate: acceptance not passed ---

    [Xunit.Fact(DisplayName = "LandingDecision_Escalate_when_acceptance_not_passed")]
    public void LandingDecisionEscalateWhenAcceptanceNotPassed()
    {
        var inputs = new LandingInputs(
            BehaviorChangeSummary(),
            AcceptancePassed: false,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: 0);

        var decision = LandingDecisionEngine.Decide(inputs);

        var escalate = Assert.IsEscalate(decision);
        Assert.True(escalate.Reason.Contains("acceptance", StringComparison.OrdinalIgnoreCase));
    }

    // --- Escalate: security-risk change ---

    [Xunit.Fact(DisplayName = "LandingDecision_Escalate_for_security_change")]
    public void LandingDecisionEscalateForSecurityChange()
    {
        var summary = RepositoryChangeClassifier.Classify([
            "src/Mcg.AgentOrchestrator.App/Dashboard/Api/AuthPolicy.cs"
        ]);

        var inputs = new LandingInputs(
            summary,
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: 0);

        var decision = LandingDecisionEngine.Decide(inputs);

        var escalate = Assert.IsEscalate(decision);
        Assert.True(escalate.Reason.Contains("security", StringComparison.OrdinalIgnoreCase));
    }

    // --- Escalate: broad-impact change ---

    [Xunit.Fact(DisplayName = "LandingDecision_Escalate_for_broad_impact_change")]
    public void LandingDecisionEscalateForBroadImpactChange()
    {
        // Infrastructure path triggers RequiresBroadVerification
        var summary = BroadChangeSummary();

        var inputs = new LandingInputs(
            summary,
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: 0);

        var decision = LandingDecisionEngine.Decide(inputs);

        var escalate = Assert.IsEscalate(decision);
        Assert.True(escalate.Reason.Contains("broad", StringComparison.OrdinalIgnoreCase) ||
            escalate.Reason.Contains("build", StringComparison.OrdinalIgnoreCase) ||
            escalate.Reason.Contains("security", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "LandingDecision_Permissive_promotes_broad_change_with_green_acceptance")]
    public void LandingDecisionPermissivePromotesBroadChangeWithGreenAcceptance()
    {
        var inputs = new LandingInputs(
            BroadChangeSummary(),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: 0,
            Policy: ConductorAutonomyPolicy.Permissive);

        var decision = LandingDecisionEngine.Decide(inputs);

        Assert.True(decision is LandingDecision.Promote);
    }

    [Xunit.Fact(DisplayName = "LandingDecision_Conservative_escalates_broad_change_with_green_acceptance")]
    public void LandingDecisionConservativeEscalatesBroadChangeWithGreenAcceptance()
    {
        var inputs = new LandingInputs(
            BroadChangeSummary(),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: 0,
            Policy: ConductorAutonomyPolicy.Conservative);

        var decision = LandingDecisionEngine.Decide(inputs);

        var escalate = Assert.IsEscalate(decision);
        Assert.True(escalate.Reason.Contains("broad", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "LandingDecision_Manual_escalates_broad_change_with_green_acceptance")]
    public void LandingDecisionManualEscalatesBroadChangeWithGreenAcceptance()
    {
        var inputs = new LandingInputs(
            BroadChangeSummary(),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: 0,
            Policy: ConductorAutonomyPolicy.Manual);

        var decision = LandingDecisionEngine.Decide(inputs);

        var escalate = Assert.IsEscalate(decision);
        Assert.True(escalate.Reason.Contains("broad", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "LandingDecision_Permissive_escalates_security_change")]
    public void LandingDecisionPermissiveEscalatesSecurityChange()
    {
        var inputs = new LandingInputs(
            SecurityChangeSummary(),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: 0,
            Policy: ConductorAutonomyPolicy.Permissive);

        var decision = LandingDecisionEngine.Decide(inputs);

        var escalate = Assert.IsEscalate(decision);
        Assert.True(escalate.Reason.Contains("security", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "LandingDecision_Permissive_escalates_build_change")]
    public void LandingDecisionPermissiveEscalatesBuildChange()
    {
        var inputs = new LandingInputs(
            RepositoryChangeClassifier.Classify(["Directory.Build.props"]),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: 0,
            Policy: ConductorAutonomyPolicy.Permissive);

        var decision = LandingDecisionEngine.Decide(inputs);

        var escalate = Assert.IsEscalate(decision);
        Assert.True(escalate.Reason.Contains("build", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "LandingDecision_Permissive_escalates_broad_change_with_integration_conflict")]
    public void LandingDecisionPermissiveEscalatesBroadChangeWithIntegrationConflict()
    {
        var inputs = new LandingInputs(
            BroadChangeSummary(),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: false,
            GoalFailureRetryCount: 0,
            Policy: ConductorAutonomyPolicy.Permissive);

        var decision = LandingDecisionEngine.Decide(inputs);

        var escalate = Assert.IsEscalate(decision);
        Assert.True(escalate.Reason.Contains("integration", StringComparison.OrdinalIgnoreCase) ||
            escalate.Reason.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "LandingDecision_Permissive_escalates_broad_change_with_repeated_failures")]
    public void LandingDecisionPermissiveEscalatesBroadChangeWithRepeatedFailures()
    {
        var inputs = new LandingInputs(
            BroadChangeSummary(),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: LandingDecisionEngine.RepeatedFailureThreshold,
            Policy: ConductorAutonomyPolicy.Permissive);

        var decision = LandingDecisionEngine.Decide(inputs);

        var escalate = Assert.IsEscalate(decision);
        Assert.True(escalate.Reason.Contains("repeated", StringComparison.OrdinalIgnoreCase) ||
            escalate.Reason.Contains("failure", StringComparison.OrdinalIgnoreCase));
    }

    // --- Escalate: integration->main conflict ---

    [Xunit.Fact(DisplayName = "LandingDecision_Escalate_when_integration_to_main_is_not_clean_fast_forward")]
    public void LandingDecisionEscalateWhenIntegrationToMainConflict()
    {
        var inputs = new LandingInputs(
            BehaviorChangeSummary(),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: false,
            GoalFailureRetryCount: 0);

        var decision = LandingDecisionEngine.Decide(inputs);

        var escalate = Assert.IsEscalate(decision);
        Assert.True(escalate.Reason.Contains("conflict", StringComparison.OrdinalIgnoreCase) ||
            escalate.Reason.Contains("integration", StringComparison.OrdinalIgnoreCase));
    }

    // --- Escalate: repeated failures ---

    [Xunit.Fact(DisplayName = "LandingDecision_Escalate_for_repeated_failures")]
    public void LandingDecisionEscalateForRepeatedFailures()
    {
        var inputs = new LandingInputs(
            BehaviorChangeSummary(),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: LandingDecisionEngine.RepeatedFailureThreshold);

        var decision = LandingDecisionEngine.Decide(inputs);

        var escalate = Assert.IsEscalate(decision);
        Assert.True(escalate.Reason.Contains("repeated", StringComparison.OrdinalIgnoreCase) ||
            escalate.Reason.Contains("failure", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "LandingDecision_Promote_when_failure_count_is_below_threshold")]
    public void LandingDecisionPromoteWhenFailureCountBelowThreshold()
    {
        var inputs = new LandingInputs(
            BehaviorChangeSummary(),
            AcceptancePassed: true,
            IntegrationToMainIsCleanFastForward: true,
            GoalFailureRetryCount: LandingDecisionEngine.RepeatedFailureThreshold - 1);

        var decision = LandingDecisionEngine.Decide(inputs);

        Assert.True(decision is LandingDecision.Promote);
    }

    // --- Risk classification ---

    [Xunit.Fact(DisplayName = "LandingDecision_ClassifyRisk_returns_Security_for_auth_paths")]
    public void LandingDecisionClassifyRiskReturnsSecurityForAuthPaths()
    {
        var summary = RepositoryChangeClassifier.Classify([
            "src/Mcg.AgentOrchestrator.App/Dashboard/Api/AuthPolicy.cs"
        ]);

        var risk = LandingDecisionEngine.ClassifyRisk(summary);

        Assert.Equal(ChangeRiskClass.Security, risk);
    }

    [Xunit.Fact(DisplayName = "LandingDecision_ClassifyRisk_returns_Build_for_build_system_changes")]
    public void LandingDecisionClassifyRiskReturnsBuildForBuildSystemChanges()
    {
        var summary = RepositoryChangeClassifier.Classify(["Directory.Build.props"]);

        var risk = LandingDecisionEngine.ClassifyRisk(summary);

        Assert.Equal(ChangeRiskClass.Build, risk);
    }

    [Xunit.Fact(DisplayName = "LandingDecision_ClassifyRisk_returns_DocsOnly_for_markdown_files")]
    public void LandingDecisionClassifyRiskReturnsDocsOnlyForMarkdownFiles()
    {
        var summary = RepositoryChangeClassifier.Classify(["README.md", "docs/guide.md"]);

        var risk = LandingDecisionEngine.ClassifyRisk(summary);

        Assert.Equal(ChangeRiskClass.DocsOnly, risk);
    }

    [Xunit.Fact(DisplayName = "LandingDecision_ClassifyRisk_returns_Behavior_for_app_source_change")]
    public void LandingDecisionClassifyRiskReturnsBehaviorForAppSourceChange()
    {
        // App/Cli path does NOT trigger broad verification
        var summary = RepositoryChangeClassifier.Classify([
            "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.cs"
        ]);

        var risk = LandingDecisionEngine.ClassifyRisk(summary);

        Assert.Equal(ChangeRiskClass.Behavior, risk);
    }

    private static RepositoryChangeSummary BehaviorChangeSummary() =>
        RepositoryChangeClassifier.Classify([
            "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.cs"
        ]);

    private static RepositoryChangeSummary BroadChangeSummary() =>
        RepositoryChangeClassifier.Classify([
            "src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerProfileDispatcher.cs"
        ]);

    private static RepositoryChangeSummary SecurityChangeSummary() =>
        RepositoryChangeClassifier.Classify([
            "src/Mcg.AgentOrchestrator.App/Dashboard/Api/AuthPolicy.cs"
        ]);

    private static RepositoryChangeSummary DocsOnlyChangeSummary() =>
        RepositoryChangeClassifier.Classify(["README.md"]);
}

internal static partial class Assert
{
    public static LandingDecision.Escalate IsEscalate(LandingDecision decision)
    {
        if (decision is not LandingDecision.Escalate escalate)
        {
            throw new InvalidOperationException(
                $"Expected LandingDecision.Escalate but got {decision.GetType().Name}.");
        }

        return escalate;
    }
}
