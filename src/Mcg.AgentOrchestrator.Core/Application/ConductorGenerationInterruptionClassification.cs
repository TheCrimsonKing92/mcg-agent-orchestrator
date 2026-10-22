namespace Mcg.AgentOrchestrator.Core;

public static class ConductorGenerationInterruptionClassification
{
    public static bool IsMarked(TaskVerificationRecord verification) =>
        (verification.AuthoritativeStandardError ?? verification.StandardError).Split('\n')
            .Any(line => line.Trim() == DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.ConductorGenerationInterrupted));

    public static DispatchOutcome Outcome(TaskVerificationRecord verification) =>
        new(DispatchOutcomeKind.ProviderInterruption, verification.ExitCode,
            !string.IsNullOrWhiteSpace(verification.StandardOutputPath) && File.Exists(verification.StandardOutputPath)
                ? new FileInfo(verification.StandardOutputPath).Length == 0 : verification.StandardOutput.Length == 0,
            null, null, RecoveryRecommendation.AutoRetry,
            "conductor-generation-interrupted: " +
            ((verification.AuthoritativeStandardError ?? verification.StandardError).Split('\n')
                .Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("CONDUCTOR_GENERATION_INTERRUPTED ", StringComparison.Ordinal)) ?? "receipt-missing"));
}
