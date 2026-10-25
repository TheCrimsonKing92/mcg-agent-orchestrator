using System.Globalization;

internal static class HostReceiptClosureFileSet
{
    private const int SampleLimit = 8;
    private const int PathLimit = 120;
    private const int MessageLimit = 2499;

    internal sealed record Comparison(IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected);

    internal static Comparison Compare(string generation, string sealPath, IEnumerable<string> recordedRelativePaths)
    {
        var live = Directory.GetFiles(generation, "*", SearchOption.AllDirectories)
            .Where(path => path != sealPath && Path.GetFileName(path) is not ".mcg-build-receipt.txt" and not ".mcg-build-receipt.txt.tmp")
            .Select(path => WorkerBuildBridgeCandidateBindings.SafeRelative(generation, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recorded = recordedRelativePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new Comparison(
            Array.AsReadOnly(recorded.Except(live, StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(live.Except(recorded, StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray()));
    }

    internal static void RequireEqual(string generation, string sealPath, IEnumerable<string> recordedRelativePaths)
    {
        var comparison = Compare(generation, sealPath, recordedRelativePaths);
        if (comparison.Missing.Count == 0 && comparison.Unexpected.Count == 0) return;

        var unexpected = comparison.Unexpected.Take(SampleLimit).Select(path =>
        {
            var info = new FileInfo(Path.Combine(generation, path));
            return (Path: path, Metadata: string.Create(CultureInfo.InvariantCulture,
                $" (bytes={info.Length}, createdUtc={info.CreationTimeUtc:O}, lastWriteUtc={info.LastWriteTimeUtc:O})"));
        }).ToArray();
        var sampleCount = Math.Min(comparison.Missing.Count, SampleLimit) + unexpected.Length;
        var pathLimit = PathLimit;
        string message;
        do
        {
            message = "Host receipt closure file set mismatch. " +
                DescribePaths("missing", comparison.Missing, pathLimit) + " " +
                string.Create(CultureInfo.InvariantCulture, $"unexpected={comparison.Unexpected.Count}") +
                " [" + string.Join("; ", unexpected.Select(entry => Shorten(entry.Path, pathLimit) + entry.Metadata)) + "]";
            if (message.Length <= MessageLimit) break;
            // Keep every sampled entry and its metadata; spend the remaining budget on paths.
            pathLimit -= (int)Math.Ceiling((double)(message.Length - MessageLimit) / sampleCount);
        } while (true);

        throw new InvalidOperationException(message);
    }

    internal static string DescribeChanges<T>(IReadOnlyDictionary<string, T> before, IReadOnlyDictionary<string, T> after)
    {
        var beforePaths = before.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var afterPaths = after.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var added = afterPaths.Keys.Except(beforePaths.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var removed = beforePaths.Keys.Except(afterPaths.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var changed = beforePaths.Where(pair => afterPaths.TryGetValue(pair.Key, out var value) && !EqualityComparer<T>.Default.Equals(pair.Value, value))
            .Select(pair => pair.Key).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        return added.Length == 0 && removed.Length == 0 && changed.Length == 0 ? "" :
            DescribePaths("added", added, PathLimit) + " " + DescribePaths("removed", removed, PathLimit) + " " + DescribePaths("changed", changed, PathLimit);
    }

    private static string DescribePaths(string label, IReadOnlyList<string> paths, int pathLimit) =>
        string.Create(CultureInfo.InvariantCulture, $"{label}={paths.Count}") + " [" +
        string.Join("; ", paths.Take(SampleLimit).Select(path => Shorten(path, pathLimit))) + "]";

    private static string Shorten(string path, int limit) => path.Length <= limit ? path : path[..(limit - 3)] + "...";
}
