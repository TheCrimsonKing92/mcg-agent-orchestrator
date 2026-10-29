using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns the cached placement decisions and shared local finish estimate for one coordinator.
internal sealed class RemoteLaneOfferPlan(IReadOnlyDictionary<string, RemoteLaneOfferInputs> inputs)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RemoteLaneOfferDecision> _decisions = new(StringComparer.Ordinal);
    private double? _finish;

    internal RemoteLaneOfferDecision Decide(string lane, int concurrency, TimeSpan checkTimeout, Action<string> report)
    {
        lock (_sync)
        {
            if (_decisions.TryGetValue(lane, out var existing)) return existing;
            _finish ??= RemoteLaneOfferPolicy.ExpectedLocalFinishSeconds(inputs.Values.Select(input =>
                RemoteLaneOfferPolicy.ResolveLocalSeconds(input.LocalMedianSeconds, input.LocalSamples,
                    input.ManifestEstimateSeconds)), concurrency);
            var decision = RemoteLaneOfferPolicy.Decide(inputs[lane] with { ExpectedLocalFinishSeconds = _finish.Value });
            _decisions.Add(lane, decision);
            report(string.Create(CultureInfo.InvariantCulture,
                $"REMOTE_LANE_POLICY lane=\"{lane}\" decision={(decision.Offer ? "offer" : "keep-local")} reason={decision.Reason} L={decision.LocalSeconds:0.###} Ln={decision.Inputs.LocalSamples} R={decision.RemoteSeconds:0.###} Rn={decision.Inputs.RemoteSamples} F={_finish.Value:0.###}") + RemoteLaneOfferPolicy.FailureFields(decision) +
                string.Create(CultureInfo.InvariantCulture,
                    $" DeadlineSeconds={RemoteLaneDeadline.Resolve(decision, checkTimeout).TotalSeconds} CheckTimeoutSeconds={checkTimeout.TotalSeconds}"));
            return decision;
        }
    }

    internal TimeSpan Deadline(string lane, TimeSpan checkTimeout)
    {
        lock (_sync)
            return RemoteLaneDeadline.Resolve(_decisions.GetValueOrDefault(lane), checkTimeout);
    }
}
