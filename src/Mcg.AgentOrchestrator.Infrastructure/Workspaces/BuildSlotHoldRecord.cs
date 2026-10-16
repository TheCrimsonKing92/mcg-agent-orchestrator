using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record BuildSlotHoldRecord(
    string? HolderLabel,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ReleasedAt,
    IReadOnlyList<string>? CompiledProjects)
{
    internal const string EventName = "LEASE_HELD";
    internal const string TimeoutEventName = "LEASE_WAIT_TIMEOUT";
    internal const string JournalFileName = "build-slot-holds.jsonl";
    internal const string Released = "released";

    internal static IReadOnlyList<string>? WitnessCompiledProjects(
        string? artifactsPath, DateTimeOffset since, DateTimeOffset until)
    {
        if (artifactsPath is null) return null;
        try
        {
            var obj = Path.Combine(artifactsPath, "obj");
            if (!Directory.Exists(obj)) return null;
            var compiled = new List<string>();
            foreach (var project in Directory.EnumerateDirectories(obj))
            foreach (var configuration in Directory.EnumerateDirectories(project))
            {
                var name = Path.GetFileName(project);
                var dll = Path.Combine(configuration, name + ".dll");
                if (!File.Exists(dll)) continue;
                var written = File.GetLastWriteTimeUtc(dll);
                if (written >= since.UtcDateTime && written <= until.UtcDateTime)
                    compiled.Add($"{name}/{Path.GetFileName(configuration)}@{written.Ticks.ToString(CultureInfo.InvariantCulture)}");
            }
            compiled.Sort(StringComparer.Ordinal);
            return compiled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal string FormatDetails()
    {
        var label = string.IsNullOrWhiteSpace(HolderLabel) ? "unlabeled" :
            new string(HolderLabel.Select(c => char.IsWhiteSpace(c) ? '_' : c).ToArray());
        var heldMs = Math.Max(0, (long)Math.Floor((ReleasedAt - AcquiredAt).TotalMilliseconds));
        var compiled = CompiledProjects is null ? "unknown" :
            CompiledProjects.Count == 0 ? "none" : string.Join('|', CompiledProjects);
        return $"holder={label} acquiredAt={AcquiredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)} " +
            $"heldMs={heldMs.ToString(CultureInfo.InvariantCulture)} compiled={compiled}";
    }
}
