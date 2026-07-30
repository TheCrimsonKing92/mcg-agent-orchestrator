using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AcceptanceVerificationSummary(
    bool Passed,
    IReadOnlyList<AcceptanceCheckResult> UnmetCriteria,
    string? FailureDetail = null,
    IReadOnlyList<string>? FailedChecks = null,
    string? BranchHeadSha = null,
    string? MainHeadSha = null,
    IReadOnlyList<string>? TestResultPaths = null,
    IReadOnlyList<AcceptanceCheckAttribution>? CheckAttributions = null,
    string? BaselineAttestation = null)
{
    public static AcceptanceVerificationSummary PassedWithNoUnmetCriteria { get; } = new(true, []);

    public static AcceptanceVerificationSummary Failed { get; } = new(false, []);

    public IReadOnlyList<AcceptanceCheckResult> RequiredUnmetCriteria { get; } = UnmetCriteria
        .Where(check => !check.Advisory && !check.Passed)
        .ToArray();

    public IReadOnlyList<AcceptanceCheckResult> AdvisoryUnmetCriteria { get; } = UnmetCriteria
        .Where(check => check.Advisory && !check.Passed)
        .ToArray();
}
