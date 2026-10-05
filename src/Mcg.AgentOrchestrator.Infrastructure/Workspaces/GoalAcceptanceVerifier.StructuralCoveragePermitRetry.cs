using System.Runtime.ExceptionServices;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private const int MaxStructuralCoveragePermitRetries = 2;
    internal const string StructuralCoveragePermitRetryPhaseName = "structural-coverage-permit-retry";

    private static bool IsStructuralCoveragePermitMiss(StructuralCoveragePreparationOutcome outcome)
    {
        if (outcome.Fault is { } fault)
            return IsPermitMiss(fault);
        return outcome.Preparation?.Projects.Any(project => IsPermitMiss(project.Fault)) == true;

        static bool IsPermitMiss(ExceptionDispatchInfo? fault) => fault?.SourceException is
            AcceptanceInfrastructureDeferredException { ReasonCode: StructuralCoveragePermitWait.UnavailableReasonCode };
    }

    private async Task<StructuralCoveragePreparationOutcome> RetryStructuralCoveragePermitMissAsync(
        StructuralCoveragePreparationOutcome outcome, GoalId? goalId, CancellationToken cancellationToken)
    {
        if (!IsStructuralCoveragePermitMiss(outcome))
            return outcome;

        // The original task has ended its lock hold; retries use the ordinary bounded permit wait.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _coveragePreparationCancellation!.Token);
        for (var retry = 1; retry <= MaxStructuralCoveragePermitRetries && IsStructuralCoveragePermitMiss(outcome); retry++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var now = _timeProvider.GetUtcNow();
            EmitGateProgress(new AcceptanceGateProgress(goalId?.Value, StructuralCoveragePermitRetryPhaseName,
                $"retry={retry}/{MaxStructuralCoveragePermitRetries}", null, Environment.ProcessId, null,
                now, now, now, TimeSpan.Zero, 0, string.Empty), $"retry={retry}");
            var started = _timeProvider.GetTimestamp();
            var priorDuration = outcome.Duration;
            try
            {
                var preparation = await _coveragePreparationFactory!(cancellation.Token).ConfigureAwait(false);
                outcome = new StructuralCoveragePreparationOutcome(preparation, null,
                    priorDuration + _timeProvider.GetElapsedTime(started));
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                             !cancellation.IsCancellationRequested)
            {
                outcome = new StructuralCoveragePreparationOutcome(null, ExceptionDispatchInfo.Capture(exception),
                    priorDuration + _timeProvider.GetElapsedTime(started));
            }
        }
        return outcome;
    }
}
