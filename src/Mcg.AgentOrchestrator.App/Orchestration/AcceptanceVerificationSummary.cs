using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AcceptanceVerificationSummary(
    bool Passed,
    IReadOnlyList<AcceptanceCheckResult> UnmetCriteria,
    string? FailureDetail = null)
{
    public static AcceptanceVerificationSummary PassedWithNoUnmetCriteria { get; } = new(true, []);

    public static AcceptanceVerificationSummary Failed { get; } = new(false, []);
}
