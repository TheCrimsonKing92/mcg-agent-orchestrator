namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateWhereLine
{
    internal static IReadOnlyList<string> Lines(OwnerConsoleGateProgress gate,
        IReadOnlyList<OwnerConsoleGateLane> ordinary, int width)
    {
        if (gate.Capacity is not { } capacity)
        {
            var locations = ordinary.Where(row => !row.IsAttribution &&
                    row.State is OwnerConsoleGateLaneState.Running or OwnerConsoleGateLaneState.Done && row.Location.Length > 0)
                .GroupBy(row => row.Location, StringComparer.Ordinal).ToArray();
            return locations.Length == 0 ? [] :
                ["WHERE   " + string.Join("   ", locations.Select(group =>
                    (group.Key.StartsWith("local slot ", StringComparison.Ordinal) ? "local " + group.Key[11..] : group.Key) +
                    " " + Bar((double)group.Count(row => row.State == OwnerConsoleGateLaneState.Done) / group.Count())))];
        }

        var runningLanes = ordinary.Where(row => row.State == OwnerConsoleGateLaneState.Running && !row.IsAttribution &&
                !row.Name.EndsWith(" [trusted main baseline]", StringComparison.Ordinal))
            .ToArray();
        var running = runningLanes
            .GroupBy(row => row.Location, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var configured = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<string>();
        if (capacity.Owners is not null) groups.Add($"capacity owner: {OwnerConsoleHostRowOwner.Host}");
        string Owner(IEnumerable<OwnerConsoleGateLane> lanes) => capacity.Owners is null ? "" :
            $" owner: {capacity.Owners.ForRunningLanes(lanes)}";
        foreach (var executor in capacity.Executors)
            if (configured.Add(executor.Id))
                groups.Add(Occupancy(executor.Id, running.GetValueOrDefault(executor.Id), executor.Slots) +
                    Owner(runningLanes.Where(row => row.Location == executor.Id)));
        groups.AddRange(running.Where(pair => !IsLocal(pair.Key) && !configured.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} {pair.Value} running" +
                Owner(runningLanes.Where(row => row.Location == pair.Key))));
        groups.Add(Occupancy("local", running.Where(pair => IsLocal(pair.Key)).Sum(pair => pair.Value), capacity.LocalBudget) +
            Owner(runningLanes.Where(row => IsLocal(row.Location))));

        var lines = new List<string>();
        var current = "WHERE   " + groups[0];
        foreach (var group in groups.Skip(1))
        {
            if (current.Length + 3 + group.Length <= width) current += "   " + group;
            else
            {
                lines.Add(current);
                current = "        " + group;
            }
        }
        lines.Add(current);
        return lines;
    }

    private static bool IsLocal(string location) => location == "local" || location.StartsWith("local ", StringComparison.Ordinal);
    private static string Occupancy(string location, int running, int capacity) =>
        $"{location} {running}/{capacity} " + Bar(capacity > 0 ? (double)running / capacity : running > 0 ? 1 : 0);
    private static string Bar(double fraction)
    {
        var filled = double.IsFinite(fraction) ? (int)Math.Floor(Math.Clamp(fraction, 0, 1) * 10) : 0;
        return "[" + new string('#', filled) + new string('-', 10 - filled) + "]";
    }
}
