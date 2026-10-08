using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Classifies an automatic retry from recorded worker evidence without authorizing or scheduling it.
internal static class AutomaticWorkerRetryCause
{
    internal static RetryCause? Resolve(TaskSpec task)
    {
        var verification = task.LastVerification;
        if (verification is null)
        {
            return null;
        }

        return Resolve(task, DispatchFailureClassifier.Classify(task, verification));
    }

    internal static RetryCause? Resolve(TaskSpec task, DispatchOutcome outcome)
    {
        var verification = task.LastVerification;
        if (verification is null)
        {
            return null;
        }

        if (outcome.OutcomeClass == TaskOutcomeClass.Finding)
        {
            return null;
        }

        if (TaskOutcomeClassifier.IsIncompleteScopeDeclaration(
            TaskOutcomeClassifier.TryExtractRule(
                outcome.ClassifierReceipt)))
        {
            return RetryCause.ContractClarification;
        }

        if (outcome.Kind == DispatchOutcomeKind.ProviderInterruption)
            return RetryCause.ProviderInterruption;

        if (verification.ProviderFailureKind is ProviderFailureKind.RateLimit or ProviderFailureKind.Connectivity)
            return RetryCause.ProviderInterruption;
        if (verification.ProviderFailureKind == ProviderFailureKind.Sandbox1312)
            return RetryCause.EnvironmentApparatusFailure;

        var findings = verification.MergedReviewFindings?
            .Where(finding => finding.State == ReviewFindingState.Open && finding.Severity == FindingSeverity.Blocking)
            .ToArray() ?? [];
        if (findings.Any(finding => finding.Category is FindingCategory.OperatorOwned or FindingCategory.SpecDefect))
            return RetryCause.ContractClarification;

        var developerFindings = ReviewFindingRouting.Project(findings)
            .Where(projection => projection.TargetRole == AgentRole.Developer)
            .Select(projection => projection.Finding)
            .ToArray();
        // Match Developer routing precedence, retaining the fallback for uncategorized findings.
        return Rank(developerFindings) ?? Rank(findings);

        static RetryCause? Rank(IReadOnlyList<ReviewFinding> candidates)
        {
            if (candidates.Any(finding => finding.Category is FindingCategory.TestEvidence or FindingCategory.TestCoverage))
                return RetryCause.NewTestFinding;
            if (candidates.Any(finding => finding.Category is FindingCategory.Correctness or FindingCategory.CodeQuality or FindingCategory.SpecCompliance))
                return RetryCause.NewSourceFinding;
            return null;
        }
    }
}
