using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteLaneOfferInputs(string LaneName, double? LocalMedianSeconds,
    int LocalSamples, double ManifestEstimateSeconds, double? RemoteMedianSeconds,
    int RemoteSamples, double ExpectedLocalFinishSeconds, int ConsecutiveRemoteFailures = 0,
    DateTimeOffset? NewestRemoteFailureAt = null, DateTimeOffset DecisionTime = default);

internal sealed record RemoteLaneOfferDecision(bool Offer, string Reason, double LocalSeconds,
    double RemoteSeconds, RemoteLaneOfferInputs Inputs);

// Duration hints affect placement only; they never authorize execution or determine a verdict.
internal static class RemoteLaneOfferPolicy
{
    internal const int MinimumSamples = 3;
    internal const double ShortLaneSeconds = 60;
    internal const double PriorRemoteMultiplier = 2;
    internal const double PriorRemoteOverheadSeconds = 15;
    internal const int FailingThreshold = 3;
    internal static readonly TimeSpan FailingHold = TimeSpan.FromHours(6);

    internal static double ResolveLocalSeconds(double? median, int samples, double manifestEstimate)
    {
        var value = samples >= MinimumSamples && median is { } observed ? observed : manifestEstimate;
        return double.IsFinite(value) && value >= 0 ? value : 0;
    }

    internal static double ExpectedLocalFinishSeconds(IEnumerable<double> localSeconds, int concurrency)
    {
        var estimates = localSeconds.ToArray();
        return Math.Max(estimates.DefaultIfEmpty(0).Max(), estimates.Sum() / Math.Max(1, concurrency));
    }

    internal static RemoteLaneOfferDecision Decide(RemoteLaneOfferInputs inputs)
    {
        var local = ResolveLocalSeconds(inputs.LocalMedianSeconds, inputs.LocalSamples, inputs.ManifestEstimateSeconds);
        var remote = inputs.RemoteSamples >= MinimumSamples && inputs.RemoteMedianSeconds is { } observed &&
            double.IsFinite(observed) && observed > 0
            ? observed : PriorRemoteMultiplier * local + PriorRemoteOverheadSeconds;
        var reason = local < ShortLaneSeconds ? "short" :
            inputs.ConsecutiveRemoteFailures >= FailingThreshold && inputs.NewestRemoteFailureAt is { } newest &&
            inputs.DecisionTime - newest < FailingHold ? "remote-failing" :
            remote > inputs.ExpectedLocalFinishSeconds ? "remote-slower" : "fits";
        return new(reason == "fits", reason, local, remote, inputs);
    }

    internal static string FailureFields(RemoteLaneOfferDecision decision) =>
        string.Create(CultureInfo.InvariantCulture, $" Fn={decision.Inputs.ConsecutiveRemoteFailures}") +
        (decision.Reason == "remote-failing"
            ? string.Create(CultureInfo.InvariantCulture,
                $" Fage={(decision.Inputs.DecisionTime - decision.Inputs.NewestRemoteFailureAt!.Value).TotalHours:0.0}")
            : "");
}
