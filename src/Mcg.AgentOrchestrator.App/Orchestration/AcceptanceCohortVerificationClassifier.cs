using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceCohortVerificationClassifier
{
    internal static AcceptanceCohortGateOutcome ClassifyCohortVerification(AcceptanceVerificationResult result) =>
        ClassifyCohortVerificationResult(result).Outcome;

    internal static AcceptanceCohortGateClassification ClassifyCohortVerificationResult(
        AcceptanceVerificationResult result) =>
        ClassifyCohortVerificationResultWithPaths(result).Classification;

    internal static ClassifiedCohortVerification ClassifyCohortVerificationResultWithPaths(
        AcceptanceVerificationResult result, bool allowNormalized = true)
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
        if (result.RequiredCrosswalkChecks.Count > 0 && allowNormalized)
        {
            try
            {
                var checks = CohortDeclaredCheckEvidence.Resolve(result);
                var assessment = checks is null ? null : CohortTestResultEvidence.Assess(checks);
                if (assessment?.Coherent != true)
                    return new(InfrastructureClassification(AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent,
                        "Declared cohort test evidence is missing, stale or incoherent."), normalizedTestResultPaths);
                return new(new(result.Passed && result.ExitCode == 0 && assessment.Passed &&
                    (result.Checks ?? []).All(check => check.Advisory || (check.Passed && check.ExitCode is null or 0))
                    ? AcceptanceCohortGateOutcome.Passed : AcceptanceCohortGateOutcome.Failed),
                    NormalizeCohortTestResultPaths(checks!.SelectMany(check => check.AllArtifacts).Select(a => a.Path).ToArray()))
                    { Checks = checks! };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new(InfrastructureClassification(AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent,
                    ex.Message), normalizedTestResultPaths);
            }
        }
        if (result.Checks?.Any(check => check.CrosswalkDiagnostics is not null || check.CohortEvidence is not null) == true ||
            result.RequiredCrosswalkChecks.Count > 0)
        {
            return new ClassifiedCohortVerification(InfrastructureClassification(
                AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent,
                "Crosswalk normalized-only evidence is incompatible with this TRX-required cohort consumer. Use standalone acceptance; cohort normalized evidence support is required."),
                normalizedTestResultPaths);
        }
        if (!AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(normalizedTestResultPaths))
        {
            var classification = IsSourceSizeContentFailure(result)
                ? new AcceptanceCohortGateClassification(AcceptanceCohortGateOutcome.Failed)
                : OwnerProtectedGateDiagnostic.Find(result) is { } ownerProtectedCheck
                ? InfrastructureClassification(
                    AcceptanceCohortInfrastructureReasonCodes.OwnerProtectedConfiguration,
                    ownerProtectedCheck.OutputTail ?? result.OutputTail ?? ownerProtectedCheck.Name)
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

    internal sealed record ClassifiedCohortVerification(
        AcceptanceCohortGateClassification Classification,
        IReadOnlyList<string> NormalizedTestResultPaths)
    {
        internal IReadOnlyList<AcceptanceCohortCheckEvidence> Checks { get; init; } = [];
    }

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
            BoundDetail(detail));

    internal static IReadOnlyList<string> NormalizeCohortTestResultPaths(IReadOnlyList<string>? paths) =>
        (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    private static bool IsSourceSizeContentFailure(AcceptanceVerificationResult result) =>
        result.Checks?.Any(check =>
            !check.Passed &&
            !check.Advisory &&
            string.Equals(check.Name, SourceSizeRatchetPreflight.CheckName, StringComparison.Ordinal)) == true;

    private static string BoundDetail(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return singleLine.Length <= 256 ? singleLine : singleLine[..256];
    }
}
