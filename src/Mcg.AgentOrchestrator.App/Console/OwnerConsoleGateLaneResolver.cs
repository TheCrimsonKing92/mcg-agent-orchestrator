namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Matching structures and memo belong to one manifest and one board build.
internal sealed class OwnerConsoleGateLaneResolver
{
    private readonly Dictionary<string, string> exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> memo = new(StringComparer.Ordinal);
    private readonly (string Name, string Bare, string Prefixed)[] prefixes;
    private readonly List<string> computedTargets = [];

    internal OwnerConsoleGateLaneResolver(IReadOnlyList<OwnerConsoleGateManifestLane> manifest)
    {
        foreach (var lane in manifest)
        {
            exact.TryAdd(lane.Name, lane.Name);
            exact.TryAdd("infrastructure tests: " + lane.Name, lane.Name);
        }
        prefixes = manifest.OrderByDescending(lane => lane.Name.Length)
            .Select(lane => (lane.Name, lane.Name + ":", "infrastructure tests: " + lane.Name + ":")).ToArray();
    }

    internal int Computations => computedTargets.Count;
    internal IReadOnlyList<string> ComputedTargets => computedTargets;

    internal string Resolve(string target)
    {
        if (memo.TryGetValue(target, out var result)) return result;
        if (!exact.TryGetValue(target, out result))
        {
            result = target;
            foreach (var candidate in prefixes)
            {
                if (!target.StartsWith(candidate.Bare, StringComparison.Ordinal) &&
                    !target.StartsWith(candidate.Prefixed, StringComparison.Ordinal)) continue;
                result = candidate.Name;
                break;
            }
        }
        memo.Add(target, result);
        computedTargets.Add(target);
        return result;
    }
}
