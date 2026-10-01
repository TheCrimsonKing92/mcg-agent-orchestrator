using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private static void AppendAssemblyCleanupStderrEvidence(
        List<string> evidence, AcceptanceCheckResult criterion, IReadOnlyList<string> trxPaths)
    {
        if (AcceptanceAssemblyCleanupEvidence.InspectReceipts(trxPaths).HasCleanupRows)
        {
            evidence.AddRange(AcceptanceAssemblyCleanupEvidence.SelectStderrLines(
                criterion.ProcessStderr, criterion.ProcessStderrPath));
        }
    }

    internal static string[] FormatCriterionRetryFeedbackForTests(IReadOnlyList<AcceptanceCheckResult> criteria) =>
        FormatCriterionRetryFeedback(criteria);
}
