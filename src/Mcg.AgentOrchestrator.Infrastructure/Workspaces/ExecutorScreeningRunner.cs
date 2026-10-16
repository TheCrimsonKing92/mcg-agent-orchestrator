using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns only screening receipts and occupancy; never constructs the gate coordinator.
internal static class ExecutorScreeningRunner
{
    internal static async Task<ExecutorScreeningReceipt?> RunAsync(ExecutorScreeningCandidate candidate,
        RemoteLaneExecutorEntry entry, ExecutorScreeningLaneSelection selection,
        RemoteLaneExecutorConfiguration configuration, IRemoteLaneExecutor transport,
        string occupancyRoot, string receiptPath, string receiptId, TimeSpan laneTimeout,
        TimeProvider clock, TimeSpan pollInterval, CancellationToken token = default)
    {
        var attempt = "screening-" + receiptId;
        var claims = new List<IDisposable>();
        var started = clock.GetUtcNow();
        var outcomes = new List<ExecutorScreeningLaneOutcome>();
        var pending = selection.Selected.OrderByDescending(lane => lane.EstimatedSerialSeconds).ToList();
        var running = new List<(AcceptanceTestLane Lane, Task<ExecutorScreeningLaneOutcome> Work)>();
        try
        {
            for (var slot = 0; slot < entry.Slots; slot++)
            {
                var claim = RemoteExecutorOccupancy.TryClaimExclusive(occupancyRoot, entry.Id, slot);
                if (claim is null) return null;
                claims.Add(claim);
                RemoteExecutorOccupancy.Claim(occupancyRoot, entry.Id, attempt, slot);
            }
            var unreachable = false;
            while (pending.Count > 0 || running.Count > 0)
            {
                while (running.Count < entry.Slots && pending.Count > 0)
                {
                    var index = pending.FindIndex(lane => !running.Any(active =>
                        lane.ExclusiveResourceKeys.Intersect(active.Lane.ExclusiveResourceKeys, StringComparer.Ordinal).Any()));
                    if (index < 0) break;
                    var lane = pending[index];
                    pending.RemoveAt(index);
                    var request = new RemoteLaneRequest(entry.Id, attempt, candidate.GoalId,
                        ExecutorScreeningPlanner.LaneName(ExecutorScreeningPlanner.Project, lane.Filter),
                        ExecutorScreeningPlanner.Project, lane.Filter, GoalAcceptanceVerifier.ShortHash(lane.Filter),
                        candidate.Commit, candidate.Tree, candidate.MainSha,
                        "screening-" + RemoteFocusedEvidenceShadow.ManifestIdentity(ExecutorScreeningPlanner.Project, lane.Filter, ""));
                    var laneStarted = clock.GetUtcNow();
                    IRemoteLaneHandle? handle = null;
                    if (!unreachable && !token.IsCancellationRequested)
                    {
                        try { handle = (await transport.SubmitAsync(request, token).ConfigureAwait(false)).Handle; }
                        catch (Exception) { /* A failed submission is an observation, never candidate failure. */ }
                    }
                    if (handle is null)
                    {
                        unreachable = true;
                        outcomes.Add(Outcome(lane, request, RemoteLaneOutcomeCode.Unreachable, laneStarted));
                    }
                    else running.Add((lane, PollAsync(lane, request, handle, laneStarted)));
                }
                if (running.Count == 0) continue;
                var done = await Task.WhenAny(running.Select(active => active.Work)).ConfigureAwait(false);
                var completed = running.FindIndex(active => active.Work == done);
                outcomes.Add(await done.ConfigureAwait(false));
                running.RemoveAt(completed);
            }
            var receipt = new ExecutorScreeningReceipt(receiptId, candidate.GoalId, candidate.Commit,
                candidate.Tree, candidate.MainSha, entry.Id, started, clock.GetUtcNow(),
                ExecutorScreeningReceiptLedger.DecideVerdict(outcomes), outcomes, selection.Excluded);
            // Unlike advisory health telemetry, failure to persist this receipt must be visible to the operator.
            ExecutorScreeningReceiptLedger.Append(receiptPath, receipt);
            return receipt;
        }
        finally
        {
            // Poll tasks close their handles before completing. Retain the claims until all have drained.
            await Task.WhenAll(running.Select(active => active.Work)).ConfigureAwait(false);
            for (var slot = claims.Count - 1; slot >= 0; slot--)
            {
                RemoteExecutorOccupancy.Release(occupancyRoot, entry.Id, attempt, slot);
                claims[slot].Dispose();
            }
        }

        ExecutorScreeningLaneOutcome Outcome(AcceptanceTestLane lane, RemoteLaneRequest request,
            RemoteLaneOutcomeCode code, DateTimeOffset at, IReadOnlyList<string>? failing = null,
            IReadOnlyList<string>? ignored = null) => new(lane.Name, request.Lane, request.FilterHash, code,
                failing ?? [], ignored ?? [], Math.Max(0, (clock.GetUtcNow() - at).TotalSeconds));

        async Task<ExecutorScreeningLaneOutcome> PollAsync(AcceptanceTestLane lane, RemoteLaneRequest request,
            IRemoteLaneHandle handle, DateTimeOffset at)
        {
            var observed = false;
            var heartbeat = at;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (handle.NewestHeartbeat is { } newest && newest > heartbeat) heartbeat = newest;
                    if (clock.GetUtcNow() - heartbeat > TimeSpan.FromSeconds(entry.LeaseSeconds))
                        return Outcome(lane, request, RemoteLaneOutcomeCode.LeaseExpired, at);
                    if (clock.GetUtcNow() - at > laneTimeout)
                        return Outcome(lane, request, RemoteLaneOutcomeCode.LaneTimeout, at);
                    if (handle.TryGetResult() is { } result)
                    {
                        observed = true;
                        if (RemoteLaneCoordinator.BindingMismatch(request, result) is { } mismatch)
                            return Outcome(lane, request, mismatch, at);
                        var trx = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(result.TestResultPaths);
                        var decision = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
                            new GoalAcceptanceVerifier.CommandResult(result.ExitCode, ""), trx, requireTrxEvidence: true);
                        if (decision.Passed) return Outcome(lane, request, RemoteLaneOutcomeCode.Accepted, at);
                        var red = decision.FailedPredicate is AcceptanceShardCompletionPredicates.FailingTrx or
                            AcceptanceShardCompletionPredicates.AssemblyCleanupFailure or AcceptanceShardCompletionPredicates.NonzeroExit;
                        if (!red) return Outcome(lane, request, RemoteLaneOutcomeCode.TrxIncomplete, at);
                        var identities = GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities(result.TestResultPaths);
                        return Outcome(lane, request, RemoteLaneOutcomeCode.RemoteRed, at,
                            identities.Where(identity => !configuration.Screening.IsKnownDivergent(identity)).ToArray(),
                            identities.Where(configuration.Screening.IsKnownDivergent).ToArray());
                    }
                    if (pollInterval > TimeSpan.Zero) await Task.Delay(pollInterval, token).ConfigureAwait(false);
                    else await Task.Yield();
                }
            }
            catch (Exception) { return Outcome(lane, request, RemoteLaneOutcomeCode.Unreachable, at); }
            finally
            {
                try
                {
                    if (!observed) (handle as IRemoteLaneQueuedJobCancellation)?.RequestQueuedJobCancellation();
                }
                catch (Exception) { }
                try { handle.Abandon(); } catch (Exception) { }
            }
        }
    }
}
