using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGateFailureNames
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (long Length, DateTime Written, OwnerConsoleGateTrxOutcomes Results)> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<string> Read(OwnerConsoleGateLane lane) => ReadOutcomes(lane).FailureNames;

    internal OwnerConsoleGateTrxOutcomes ReadOutcomes(OwnerConsoleGateLane lane) => ReadLane(lane, confirmation: false);

    internal OwnerConsoleGateTrxOutcomes ReadConfirmation(OwnerConsoleGateLane rerun, OwnerConsoleGateLane lane) =>
        ReadLane(rerun, confirmation: true, originalTarget: lane.Target);

    private OwnerConsoleGateTrxOutcomes ReadLane(OwnerConsoleGateLane lane, bool confirmation, string? originalTarget = null)
    {
        try
        {
            if (lane.HeartbeatPath is not { Length: > 0 } heartbeat || lane.Target is not { Length: > 0 } target)
                return OwnerConsoleGateTrxOutcomes.Unavailable;
            var file = Path.GetFileName(heartbeat);
            var dot = file.IndexOf('.');
            if (dot <= 0) return OwnerConsoleGateTrxOutcomes.Unavailable;
            var suffix = confirmation ? Regex.Match(file, @"-run-\d+(?=\.gate-heartbeat\.json$)").Value : "";
            var stems = new List<string>();
            var stem = file[..dot] + "." + AcceptanceArtifactFileNames.Slug(target);
            if (suffix.Length > 0)
            {
                stems.Add(stem + suffix);
                // Some confirming attempts keep the original lane identity. Never use its original TRX as re-run proof.
                if (!string.IsNullOrWhiteSpace(originalTarget))
                    stems.Add(file[..dot] + "." + AcceptanceArtifactFileNames.Slug(originalTarget) + suffix);
            }
            stems.Add(stem);
            foreach (var candidate in stems.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var path = Path.Combine(Path.GetDirectoryName(heartbeat) ?? "",
                    AcceptanceArtifactFileNames.BoundFileName(AcceptanceArtifactFileNames.SanitizeFileName(candidate), ".trx"));
                if (File.Exists(path)) return ReadPath(path);
                lock (_sync) _cache.Remove(path);
            }
            return OwnerConsoleGateTrxOutcomes.Unavailable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return OwnerConsoleGateTrxOutcomes.Unavailable; }
    }

    private OwnerConsoleGateTrxOutcomes ReadPath(string path)
    {
        lock (_sync)
        {
            var info = new FileInfo(path);
            if (!info.Exists) { _cache.Remove(path); return OwnerConsoleGateTrxOutcomes.Unavailable; }
            var length = info.Length;
            var written = info.LastWriteTimeUtc;
            if (_cache.TryGetValue(path, out var cached) && cached.Length == length && cached.Written == written)
                return cached.Results;
            var results = OwnerConsoleGateTrxOutcomes.Read(path);
            if (results.Available) _cache[path] = (length, written, results);
            else _cache.Remove(path);
            return results;
        }
    }
}
