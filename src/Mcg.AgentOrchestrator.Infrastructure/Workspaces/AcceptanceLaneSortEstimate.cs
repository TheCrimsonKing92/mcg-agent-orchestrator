namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceLaneSortEstimate
{
    internal static double Resolve(
        double? currentHashMedian,
        IReadOnlyList<double> nameSamples,
        double manifestEstimate)
    {
        if (currentHashMedian is { } observed && double.IsFinite(observed) && observed > 0)
        {
            return observed;
        }

        Span<double> validSamples = stackalloc double[nameSamples.Count];
        var count = 0;
        for (var index = 0; index < nameSamples.Count; index++)
        {
            var seconds = nameSamples[index];
            if (double.IsFinite(seconds) && seconds > 0)
            {
                validSamples[count++] = seconds;
            }
        }

        if (count < AcceptanceLaneDurationStore.MinimumSamples)
        {
            return manifestEstimate;
        }

        var ordered = validSamples[..count];
        ordered.Sort();
        var middle = count / 2;
        return count % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2d;
    }
}
