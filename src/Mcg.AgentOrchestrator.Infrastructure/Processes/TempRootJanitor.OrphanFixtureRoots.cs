namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record OwnedFixtureRoot(int ProcessId, string Key, string Path, DateTimeOffset CreatedAt);

internal static partial class TempRootJanitor
{
    internal static IReadOnlyList<string> GetStandardSharedRoots() =>
        EnumerateStandardSharedRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static IReadOnlyList<OwnedFixtureRoot> ListOwnedFixtureRoots(
        IEnumerable<string> sharedRoots)
    {
        var result = new List<OwnedFixtureRoot>();
        foreach (var sharedRoot in sharedRoots)
        {
            string[] paths;
            try { paths = Directory.GetDirectories(sharedRoot, "*", SearchOption.TopDirectoryOnly); }
            catch (DirectoryNotFoundException) { continue; }
            foreach (var path in paths)
            {
                var key = Path.GetFileName(path);
                if (TryParseOwnedRootProcessId(key, out var processId))
                    result.Add(new OwnedFixtureRoot(processId, key, path,
                        new DateTimeOffset(Directory.GetCreationTimeUtc(path), TimeSpan.Zero)));
            }
        }
        return result;
    }
}
