namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class StewardDispatcher
{
    private readonly IStewardTriageEngine _engine;
    private readonly IStewardTriageReceiptStore _receiptStore;
    private readonly IControlPlaneMessageTransport _transport;
    private readonly StewardDispatchOptions _options;
    private readonly StewardBypassPolicy _bypassPolicy;
    private readonly StewardShadowAdjudicator? _shadowAdjudicator;

    public StewardDispatcher(
        IStewardTriageEngine engine,
        IStewardTriageReceiptStore receiptStore,
        IControlPlaneMessageTransport transport,
        StewardDispatchOptions? options = null,
        StewardBypassPolicy? bypassPolicy = null,
        StewardShadowAdjudicator? shadowAdjudicator = null)
    {
        _engine = engine;
        _receiptStore = receiptStore;
        _transport = transport;
        _options = options ?? StewardDispatchOptions.Default;
        _bypassPolicy = bypassPolicy ?? new StewardBypassPolicy();
        _shadowAdjudicator = shadowAdjudicator;
    }

    public async Task<StewardDispatchResult> DispatchAsync(
        StewardBriefingBundle bundle,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var receipts = new List<StewardTriageReceipt>();
        foreach (var bypass in bundle.Escalations.Where(_bypassPolicy.ShouldBypass))
        {
            var rawReceipt = await SendRawAsync(
                StewardOutputKind.RawBypassEscalation,
                bundle,
                bypass,
                now,
                "bypass-list",
                cancellationToken);
            receipts.Add(rawReceipt);
        }

        var triageBundle = bundle with
        {
            Escalations = bundle.Escalations
                .Where(item => !_bypassPolicy.ShouldBypass(item))
                .ToList()
        };

        using var triageCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var batchTask = _engine.TriageAsync(triageBundle, now, triageCancellation.Token);
            var completed = await Task.WhenAny(batchTask, Task.Delay(_options.FailOpenTimeout, cancellationToken));
            if (completed != batchTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await triageCancellation.CancelAsync();
                receipts.AddRange(await FailOpenAsync(triageBundle, now, cancellationToken));
                await StoreAsync(receipts, cancellationToken);
                await RunShadowFailOpenAsync(bundle, now, cancellationToken);
                return new StewardDispatchResult([], receipts, FailedOpen: true);
            }

            var batch = await batchTask;
            receipts.AddRange(batch.Receipts);
            await StoreAsync(receipts, cancellationToken);
            await RunShadowFailOpenAsync(bundle, now, cancellationToken);
            return new StewardDispatchResult(batch.Cards, receipts, FailedOpen: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            receipts.AddRange(await FailOpenAsync(triageBundle, now, cancellationToken));
            await StoreAsync(receipts, cancellationToken);
            await RunShadowFailOpenAsync(bundle, now, cancellationToken);
            return new StewardDispatchResult([], receipts, FailedOpen: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            receipts.AddRange(await FailOpenAsync(triageBundle, now, cancellationToken));
            await StoreAsync(receipts, cancellationToken);
            await RunShadowFailOpenAsync(bundle, now, cancellationToken);
            return new StewardDispatchResult([], receipts, FailedOpen: true);
        }
    }

    private async Task RunShadowFailOpenAsync(
        StewardBriefingBundle bundle, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_shadowAdjudicator is null) return;
        try
        {
            var recorded = await _shadowAdjudicator.RecordAsync(bundle, now, cancellationToken);
            if (recorded.Error is not null) System.Diagnostics.Trace.TraceError(recorded.Error.ToString());
            var reconciled = await _shadowAdjudicator.ReconcileAsync(cancellationToken);
            if (reconciled.Error is not null) System.Diagnostics.Trace.TraceError(reconciled.Error.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError(ex.ToString()); }
    }

    private async Task<IReadOnlyList<StewardTriageReceipt>> FailOpenAsync(
        StewardBriefingBundle bundle,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var receipts = new List<StewardTriageReceipt>();
        foreach (var escalation in bundle.Escalations)
        {
            receipts.Add(await SendRawAsync(
                StewardOutputKind.FailOpenRawEscalation,
                bundle,
                escalation,
                now,
                "fail-open",
                cancellationToken));
        }

        return receipts;
    }

    private async Task<StewardTriageReceipt> SendRawAsync(
        StewardOutputKind kind,
        StewardBriefingBundle bundle,
        StewardEscalationItem escalation,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        var content = $"RAW {reason}: [{escalation.Kind}] {escalation.Title}\n" +
            $"goal={ShortGoal(escalation.GoalId)} cause={escalation.CauseFingerprint}\n" +
            escalation.EvidenceSummary;
        var messageId = await _transport.SendAsync(ControlPlaneDeliveryChannel.Decisions, content, [], cancellationToken);
        var rawReceiptId = $"raw-{messageId}";
        return StewardComposer.RawReceipt(kind, bundle, escalation, rawReceiptId, now, $"{reason} {escalation.Kind}");
    }

    private async Task StoreAsync(
        IReadOnlyList<StewardTriageReceipt> receipts,
        CancellationToken cancellationToken)
    {
        foreach (var receipt in receipts)
            await _receiptStore.AppendAsync(receipt, cancellationToken);
    }

    private static string ShortGoal(string goalId) =>
        string.IsNullOrWhiteSpace(goalId) ? "none" : goalId[..Math.Min(8, goalId.Length)];
}
