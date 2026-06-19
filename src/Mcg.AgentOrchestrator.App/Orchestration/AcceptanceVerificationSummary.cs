using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AcceptanceVerificationSummary(
    bool Passed,
    IReadOnlyList<AcceptanceCheckResult> UnmetCriteria)
{
    public static AcceptanceVerificationSummary PassedWithNoUnmetCriteria { get; } = new(true, []);

    public static AcceptanceVerificationSummary Failed { get; } = new(false, []);
}
