namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class HermeticSystemDrive
{
    internal static string Resolve(string? supplied, string systemDirectory) =>
        !string.IsNullOrWhiteSpace(supplied)
            ? supplied
            : Path.GetPathRoot(systemDirectory)!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal static IEnumerable<KeyValuePair<string, string?>> WithResolvedSystemDrive(
        IEnumerable<KeyValuePair<string, string?>> pairs,
        string systemDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return pairs;

        // Materialize before the caller clears the source environment dictionary.
        var resolved = new List<KeyValuePair<string, string?>>();
        var found = false;
        foreach (var pair in pairs)
        {
            if (string.Equals(pair.Key, "SystemDrive", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                resolved.Add(new(pair.Key, Resolve(pair.Value, systemDirectory)));
            }
            else
            {
                resolved.Add(pair);
            }
        }

        if (!found)
            resolved.Add(new("SystemDrive", Resolve(null, systemDirectory)));
        return resolved;
    }
}
