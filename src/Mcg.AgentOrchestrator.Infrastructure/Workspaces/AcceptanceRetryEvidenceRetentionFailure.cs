using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceRetryEvidenceRetentionFailure
{
    internal static AcceptanceCheckResult Build(
        AcceptanceCheckResult original,
        AcceptanceRetainedDiagnostic? retainedDiagnostic,
        IOException exception)
    {
        var detail =
            $"Within-attempt retry refused: {AcceptanceFailureClassifications.RetryEvidenceRetentionFailed}; " +
            $"{exception.Message}" +
            (retainedDiagnostic is null
                ? string.Empty
                : $" Retained diagnostic remains at '{retainedDiagnostic.Path}' (sha256={retainedDiagnostic.Sha256}).");
        return original with
        {
            Passed = false,
            OutputTail = string.IsNullOrWhiteSpace(original.OutputTail)
                ? detail
                : $"{original.OutputTail}{Environment.NewLine}{detail}",
            ResultSummary = AcceptanceDotnetBuildPhase.PrefixResultSummary(detail, original.ResultSummary),
            FailureClassification = AcceptanceFailureClassifications.RetryEvidenceRetentionFailed,
            CompletionDecision = original.CompletionDecision is null
                ? null
                : original.CompletionDecision with
                {
                    PolicySignal = AcceptanceFailureClassifications.RetryEvidenceRetentionFailed
                },
            ProcessStderrPath = retainedDiagnostic?.Path ?? original.ProcessStderrPath
        };
    }
}
