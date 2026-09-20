using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core;

public enum ChangeRiskClass
{
    DocsOnly,
    Behavior,
    Build,
    Security,
    Broad
}

public abstract record LandingDecision
{
    public sealed record Promote : LandingDecision;
    public sealed record Escalate(string Reason) : LandingDecision;
}

public sealed record LandingInputs(
    RepositoryChangeSummary ChangeSummary,
    bool AcceptancePassed,
    bool IntegrationToMainIsCleanFastForward,
    int GoalFailureRetryCount,
    ConductorAutonomyPolicy? Policy = null,
    string? AcceptanceHoldDescription = null);

public static class LandingDecisionEngine
{
    public const int RepeatedFailureThreshold = 2;

    public static ChangeRiskClass ClassifyRisk(RepositoryChangeSummary summary)
    {
        if (summary.HasSecuritySensitiveChanges) return ChangeRiskClass.Security;
        if (summary.HasBuildSystemChanges) return ChangeRiskClass.Build;
        if (summary.RequiresBroadVerification) return ChangeRiskClass.Broad;
        if (summary.IsDocsOnly) return ChangeRiskClass.DocsOnly;
        return ChangeRiskClass.Behavior;
    }

    public static LandingDecision Decide(LandingInputs inputs)
    {
        if (!inputs.AcceptancePassed)
            return new LandingDecision.Escalate(
                string.IsNullOrWhiteSpace(inputs.AcceptanceHoldDescription)
                    ? "acceptance verification not passed"
                    : inputs.AcceptanceHoldDescription.Trim());

        var riskClass = ClassifyRisk(inputs.ChangeSummary);

        if (riskClass == ChangeRiskClass.Security)
            return new LandingDecision.Escalate("security-risk change requires review");

        if (riskClass == ChangeRiskClass.Build)
            return new LandingDecision.Escalate("build-system change requires review");

        if (!inputs.IntegrationToMainIsCleanFastForward)
            return new LandingDecision.Escalate("integration->main conflict");

        if (inputs.GoalFailureRetryCount >= RepeatedFailureThreshold)
            return new LandingDecision.Escalate($"repeated failures (count: {inputs.GoalFailureRetryCount})");

        if (riskClass == ChangeRiskClass.Broad && !AllowsAutoLanding(inputs.Policy, riskClass))
            return new LandingDecision.Escalate("broad-impact change requires review");

        return new LandingDecision.Promote();
    }

    private static bool AllowsAutoLanding(ConductorAutonomyPolicy? policy, ChangeRiskClass riskClass)
    {
        var effectivePolicy = policy ?? ConductorAutonomyPolicy.Conservative;
        var riskTier = (ChangeRiskTier)(int)riskClass;

        return effectivePolicy.GetTransitionDecision(GoalLifecycleState.Merged, riskTier) ==
            ConductorTransitionDecision.Auto;
    }
}
