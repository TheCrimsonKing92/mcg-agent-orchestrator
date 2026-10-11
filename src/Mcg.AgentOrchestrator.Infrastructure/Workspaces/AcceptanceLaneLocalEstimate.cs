namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceLaneLocalEstimate
{
    internal static (double? MedianSeconds, int Samples) Resolve(
        double? currentHashMedian,
        int currentHashSamples,
        IReadOnlyList<double> nameSamples)
    {
        if (currentHashMedian is { } observed && double.IsFinite(observed) && observed > 0)
        {
            return (observed, currentHashSamples);
        }

        var validCount = nameSamples.Count(seconds => double.IsFinite(seconds) && seconds > 0);
        return validCount >= AcceptanceLaneDurationStore.MinimumSamples
            ? (AcceptanceLaneSortEstimate.Resolve(null, nameSamples, double.NaN), validCount)
            : (null, currentHashSamples);
    }
}
