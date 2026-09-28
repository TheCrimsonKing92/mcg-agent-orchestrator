using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class AcceptanceEngineCircuitBreaker
{
    private void RaiseStateUnavailableItem(
        Exception exception,
        AcceptanceEngineAcceptanceDecision decision,
        string failureReason)
    {
        if (_operatorItems is null)
        {
            return;
        }

        var detail = $"{exception.GetType().Name}: {exception.Message}";
        var body = failureReason == StateReadBudgetExpiredFailureReason
            ? FormattableString.Invariant(
                $"Canary state read exceeded its total budget of {_stateReadTotalBudget.TotalMilliseconds:0.###}ms before all {StateReadMaxAttempts} attempts completed; {decision.Reason}.\n{detail}")
            : $"Canary state could not be read after {StateReadMaxAttempts} attempts; {decision.Reason}.\n{detail}";
        try
        {
            using var cancellation = new CancellationTokenSource();
            var write = _operatorItems.RaiseAsync(
                CollaborationItemType.Verify,
                goalId: null,
                subject: "Post-landing canary state is unavailable",
                body: body,
                correlationKey: StateUnavailableOperatorItemCorrelationKey,
                cancellationToken: cancellation.Token);
            WaitForOwnedWrite(write, cancellation);
        }
        catch (Exception itemException)
        {
            var fallback =
                $"CANARY_GATE result=operator-item-error reason={failureReason} " +
                $"decision=\"{decision.Reason}\" item-error={itemException.GetType().Name}: {itemException.Message}";
            try
            {
                _stateUnavailableFallback?.Invoke(fallback);
            }
            catch
            {
                // Stderr below remains the final non-database operator channel.
            }

            Console.Error.WriteLine(fallback);
            Console.Error.Flush();
        }
    }

    private void ResolveStateUnavailableItem()
    {
        if (_operatorItems is null ||
            !PendingStateUnavailableItemResolutions.TryAdd(_events.Identity, 0))
        {
            return;
        }

        try
        {
            using var cancellation = new CancellationTokenSource();
            var write = _operatorItems.TryResolveAsync(
                StateUnavailableOperatorItemCorrelationKey,
                "Post-landing canary state is readable again.",
                cancellation.Token);
            WaitForOwnedWrite(write, cancellation);
        }
        catch
        {
            // Operator-item cleanup is advisory and cannot change circuit health.
        }
        finally
        {
            PendingStateUnavailableItemResolutions.TryRemove(_events.Identity, out _);
        }
    }

    private void WaitForOwnedWrite(Task write, CancellationTokenSource cancellation)
    {
        try
        {
            write.WaitAsync(_operatorItemWriteBudget).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            try
            {
                cancellation.Cancel();
            }
            finally
            {
                try
                {
                    write.GetAwaiter().GetResult();
                }
                catch
                {
                    // Preserve the budget failure after observing the completed write.
                }
            }

            throw;
        }
    }
}
