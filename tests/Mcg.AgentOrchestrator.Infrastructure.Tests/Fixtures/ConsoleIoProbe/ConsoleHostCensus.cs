internal static class ConsoleHostCensus
{
    internal const int MaxAttempts = 3;

    internal static ConsoleHostCensusCounts Count(uint totalProcesses, IReadOnlyList<bool> classifiedIsConhost)
    {
        ArgumentNullException.ThrowIfNull(classifiedIsConhost);
        var conhost = classifiedIsConhost.Count(isConhost => isConhost);
        return new ConsoleHostCensusCounts(classifiedIsConhost.Count - conhost, conhost,
            checked((int)totalProcesses - classifiedIsConhost.Count));
    }

    internal static async Task<(T Measurement, int Retakes)> MeasureAsync<T>(
        Func<Task<T>> measure, Func<T, int> unclassified, int maxAttempts)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(unclassified);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        for (var taken = 1; ; taken++)
        {
            var measurement = await measure();
            if (unclassified(measurement) == 0 || taken == maxAttempts)
                return (measurement, taken - 1);
        }
    }
}

internal sealed record ConsoleHostCensusCounts(int NonConhost, int Conhost, int Unclassified);
