using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal static AcceptanceCohortGateOutcome ClassifyCohortVerification(AcceptanceVerificationResult result) =>
        ClassifyCohortVerificationResult(result).Outcome;

    internal static AcceptanceCohortGateClassification ClassifyCohortVerificationResult(
        AcceptanceVerificationResult result) =>
        ClassifyCohortVerificationResultWithPaths(result).Classification;

    private static ClassifiedCohortVerification ClassifyCohortVerificationResultWithPaths(
        AcceptanceVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        IReadOnlyList<string> normalizedTestResultPaths;
        try
        {
            normalizedTestResultPaths = NormalizeCohortTestResultPaths(result.TestResultPaths);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ClassifiedCohortVerification(
                InfrastructureClassification(
                    AcceptanceCohortInfrastructureReasonCodes.ResultPathInvalid,
                    $"{ex.GetType().Name}: {ex.Message}"),
                []);
        }
        if (result.Skipped)
        {
            return new ClassifiedCohortVerification(
                InfrastructureClassification(
                    AcceptanceCohortInfrastructureReasonCodes.VerificationSkipped,
                    result.OutputTail ?? "Acceptance verification was skipped."),
                normalizedTestResultPaths);
        }
        if (result.ExitCode is null)
        {
            return new ClassifiedCohortVerification(
                InfrastructureClassification(
                    AcceptanceCohortInfrastructureReasonCodes.ExitCodeMissing,
                    result.OutputTail ?? "Acceptance verification did not report an exit code."),
                normalizedTestResultPaths);
        }
        if (!AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(normalizedTestResultPaths))
        {
            var classification = IsSourceSizeContentFailure(result)
                ? new AcceptanceCohortGateClassification(AcceptanceCohortGateOutcome.Failed)
                : InfrastructureClassification(
                    AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent,
                    result.OutputTail ?? "Acceptance verification did not produce coherent TRX evidence.");
            return new ClassifiedCohortVerification(classification, normalizedTestResultPaths);
        }
        return new ClassifiedCohortVerification(
            new AcceptanceCohortGateClassification(
                result.Passed && result.ExitCode == 0
                    ? AcceptanceCohortGateOutcome.Passed
                    : AcceptanceCohortGateOutcome.Failed),
            normalizedTestResultPaths);
    }

    private sealed record ClassifiedCohortVerification(
        AcceptanceCohortGateClassification Classification,
        IReadOnlyList<string> NormalizedTestResultPaths);

    internal static AcceptanceCohortGateClassification ClassifyCohortInfrastructureException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var reasonCode = exception switch
        {
            AcceptanceInfrastructureDeferredException deferred => deferred.ReasonCode,
            DotnetBuildSlotsBusyException => AcceptanceCohortInfrastructureReasonCodes.DotnetBuildSlotsBusy,
            BuildLockBlockedException => AcceptanceCohortInfrastructureReasonCodes.BuildLockBlocked,
            OperationCanceledException => AcceptanceCohortInfrastructureReasonCodes.OperationCancelled,
            InvalidDataException => AcceptanceCohortInfrastructureReasonCodes.InvalidData,
            IOException => AcceptanceCohortInfrastructureReasonCodes.IoFailure,
            _ => throw new ArgumentException(
                $"Exception type {exception.GetType().Name} is not a recognized cohort infrastructure failure.",
                nameof(exception))
        };
        return InfrastructureClassification(reasonCode, exception.Message);
    }

    private static AcceptanceCohortGateClassification InfrastructureClassification(
        string reasonCode,
        string detail) => new(
            AcceptanceCohortGateOutcome.InfrastructureFailure,
            reasonCode,
            BoundCohortDetail(detail));

    private static IReadOnlyList<string> NormalizeCohortTestResultPaths(IReadOnlyList<string>? paths) =>
        (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
