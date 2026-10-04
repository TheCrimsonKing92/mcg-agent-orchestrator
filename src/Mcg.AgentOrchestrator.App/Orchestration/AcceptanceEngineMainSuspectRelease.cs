namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IMainTipReader
{
    string? ReadMainTip();
}

internal interface IMainSuspectReleaseProbe
{
    Task<MainSuspectReleaseProbeResult> RunAsync(string tip, IReadOnlyList<string> tests, CancellationToken cancellationToken);
}

internal enum MainSuspectReleaseProbeOutcome { Passed, Failed, CouldNotRun }

internal sealed record MainSuspectReleaseProbeResult(
    MainSuspectReleaseProbeOutcome Outcome, string ProbeReceipt, string? Reason = null);

internal enum MainSuspectReleaseCheckOutcome
{
    NotEligible, SuspectStillTip, TipUnreadable, AlreadyResolvedForTip, AttemptsExhausted,
    InFlight, Released, StillFailing, Deferred, TipMovedDuringProbe, ReceiptsChangedDuringProbe
}

internal sealed class AcceptanceEngineMainSuspectRelease(
    PostLandingCanaryEventStore events, AcceptanceEngineCircuitBreaker circuit,
    IMainTipReader tipReader, IMainSuspectReleaseProbe probe, Action<string> progress)
{
    internal const int MaxReleaseProbeAttemptsPerTip = 3;
    private readonly HashSet<string> _terminalTips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _deferredAttempts = new(StringComparer.OrdinalIgnoreCase);
    private int _inFlight;

    internal async Task<MainSuspectReleaseCheckOutcome> CheckAsync(CancellationToken cancellationToken = default)
    {
        // Serialize evaluations too: no tick can start a duplicate probe while state is being read.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
            return MainSuspectReleaseCheckOutcome.InFlight;
        try { return await CheckCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { Volatile.Write(ref _inFlight, 0); }
    }

    private async Task<MainSuspectReleaseCheckOutcome> CheckCoreAsync(CancellationToken cancellationToken)
    {
        if ((await circuit.ReadAsync(cancellationToken).ConfigureAwait(false)).Health != AcceptanceEngineHealth.Unhealthy ||
            PostLandingCanaryEmergencyCircuit.TryRead(events.Identity) is not null)
            return MainSuspectReleaseCheckOutcome.NotEligible;
        var failures = await ReadFailuresAsync(cancellationToken).ConfigureAwait(false);
        if (failures.Length == 0 || failures.Any(item =>
            item.Payload.FailureReason != ConductorAcceptanceCohortMainSuspect.FailureToken ||
            string.IsNullOrWhiteSpace(item.Payload.LandingSha) ||
            item.Payload.SharedFailingTests is not { Count: > 0 } ||
            item.Payload.SharedFailingTests.Any(string.IsNullOrWhiteSpace)))
            return MainSuspectReleaseCheckOutcome.NotEligible;

        var tip = ReadTip();
        if (string.IsNullOrWhiteSpace(tip)) return MainSuspectReleaseCheckOutcome.TipUnreadable;
        if (failures.Any(item => string.Equals(tip, item.Payload.LandingSha, StringComparison.OrdinalIgnoreCase)))
            return MainSuspectReleaseCheckOutcome.SuspectStillTip;
        if (_terminalTips.Contains(tip)) return MainSuspectReleaseCheckOutcome.AlreadyResolvedForTip;
        var attempts = _deferredAttempts.GetValueOrDefault(tip);
        if (attempts >= MaxReleaseProbeAttemptsPerTip) return MainSuspectReleaseCheckOutcome.AttemptsExhausted;

        var tests = failures.SelectMany(item => item.Payload.SharedFailingTests!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var suspects = string.Join(",", failures.Select(item => item.Payload.LandingSha!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal));
        MainSuspectReleaseProbeResult result;
        try { result = await probe.RunAsync(tip, tests, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            result = new(MainSuspectReleaseProbeOutcome.CouldNotRun, "unavailable", ex.GetType().Name);
        }

        switch (result.Outcome)
        {
            case MainSuspectReleaseProbeOutcome.Passed:
                // Do not release using stale evidence after main, the trip, or emergency state changes.
                if (!string.Equals(tip, ReadTip(), StringComparison.OrdinalIgnoreCase))
                {
                    _terminalTips.Add(tip);
                    return MainSuspectReleaseCheckOutcome.TipMovedDuringProbe;
                }
                var current = await ReadFailuresAsync(cancellationToken).ConfigureAwait(false);
                if (PostLandingCanaryEmergencyCircuit.TryRead(events.Identity) is not null ||
                    !failures.Select(item => item.EventId).SequenceEqual(current.Select(item => item.EventId)))
                {
                    _terminalTips.Add(tip);
                    return MainSuspectReleaseCheckOutcome.ReceiptsChangedDuringProbe;
                }
                if (string.IsNullOrWhiteSpace(result.ProbeReceipt))
                    return Defer(tip, suspects, attempts, "missing-probe-receipt");
                try
                {
                    // The durable note includes every identity; FormatTests is a bounded display helper.
                    var note = $"main-suspect auto-release: suspect={suspects} tip={tip} tests={string.Join(",", tests)} probe={result.ProbeReceipt}";
                    await circuit.AppendAutomaticReleaseAsync(tip, note, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { return Defer(tip, suspects, attempts, $"clear-append-{ex.GetType().Name}"); }
                _terminalTips.Add(tip);
                progress($"CANARY_GATE sha={tip} result=released reason=main-moved suspect={suspects}");
                return MainSuspectReleaseCheckOutcome.Released;
            case MainSuspectReleaseProbeOutcome.Failed:
                _terminalTips.Add(tip);
                progress($"CANARY_GATE sha={tip} result=still-failing suspect={suspects}");
                return MainSuspectReleaseCheckOutcome.StillFailing;
            case MainSuspectReleaseProbeOutcome.CouldNotRun:
                return Defer(tip, suspects, attempts, result.Reason);
            default:
                throw new InvalidOperationException($"Unknown release probe outcome: {result.Outcome}");
        }
    }

    private MainSuspectReleaseCheckOutcome Defer(string tip, string suspects, int attempts, string? reason)
    {
        _deferredAttempts[tip] = ++attempts;
        var token = string.Join("_", (reason ?? "unavailable").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        progress($"CANARY_GATE sha={tip} result=release-probe-deferred suspect={suspects} attempt={attempts} reason={token[..Math.Min(240, token.Length)]}");
        return MainSuspectReleaseCheckOutcome.Deferred;
    }

    private string? ReadTip()
    {
        try { return tipReader.ReadMainTip(); }
        catch { return null; }
    }

    private async Task<PostLandingCanaryEvent[]> ReadFailuresAsync(CancellationToken cancellationToken) =>
        (await events.ReadProjectionEventsAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => item.Kind == PostLandingCanaryEventKind.Failed).ToArray();
}
