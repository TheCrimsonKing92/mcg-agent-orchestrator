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
    int GoalFailureRetryCount);

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
            return new LandingDecision.Escalate("acceptance verification not passed");

        var riskClass = ClassifyRisk(inputs.ChangeSummary);

        if (riskClass == ChangeRiskClass.Security)
            return new LandingDecision.Escalate("security-risk change requires review");

        if (riskClass == ChangeRiskClass.Build)
            return new LandingDecision.Escalate("build-system change requires review");

        if (riskClass == ChangeRiskClass.Broad)
            return new LandingDecision.Escalate("broad-impact change requires review");

        if (!inputs.IntegrationToMainIsCleanFastForward)
            return new LandingDecision.Escalate("integration->main conflict");

        if (inputs.GoalFailureRetryCount >= RepeatedFailureThreshold)
            return new LandingDecision.Escalate($"repeated failures (count: {inputs.GoalFailureRetryCount})");

        return new LandingDecision.Promote();
    }
}
