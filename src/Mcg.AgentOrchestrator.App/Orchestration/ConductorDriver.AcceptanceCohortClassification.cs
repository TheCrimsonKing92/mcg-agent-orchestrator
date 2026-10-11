using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal static AcceptanceCohortGateOutcome ClassifyCohortVerification(AcceptanceVerificationResult result) =>
        AcceptanceCohortVerificationClassifier.ClassifyCohortVerification(result);

    internal static AcceptanceCohortGateClassification ClassifyCohortVerificationResult(
        AcceptanceVerificationResult result) =>
        AcceptanceCohortVerificationClassifier.ClassifyCohortVerificationResult(result);

    private static AcceptanceCohortVerificationClassifier.ClassifiedCohortVerification ClassifyCohortVerificationResultWithPaths(
        AcceptanceVerificationResult result) =>
        AcceptanceCohortVerificationClassifier.ClassifyCohortVerificationResultWithPaths(result);

    internal static AcceptanceCohortGateClassification ClassifyCohortInfrastructureException(Exception exception) =>
        AcceptanceCohortVerificationClassifier.ClassifyCohortInfrastructureException(exception);

    private static IReadOnlyList<string> NormalizeCohortTestResultPaths(IReadOnlyList<string>? paths) =>
        AcceptanceCohortVerificationClassifier.NormalizeCohortTestResultPaths(paths);
}
