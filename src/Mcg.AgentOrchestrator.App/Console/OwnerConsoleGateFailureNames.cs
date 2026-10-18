using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleGateFailureNames
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (long Length, IReadOnlyList<string> Names)> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<string> Read(OwnerConsoleGateLane lane)
    {
        try
        {
            if (lane.HeartbeatPath is not { Length: > 0 } heartbeat || lane.Target is not { Length: > 0 } target) return [];
            var file = Path.GetFileName(heartbeat);
            var dot = file.IndexOf('.');
            if (dot <= 0) return [];
            var path = Path.Combine(Path.GetDirectoryName(heartbeat) ?? "",
                AcceptanceArtifactFileNames.BoundFileName(AcceptanceArtifactFileNames.SanitizeFileName(
                    file[..dot] + "." + AcceptanceArtifactFileNames.Slug(target)), ".trx"));
            lock (_sync)
            {
                var info = new FileInfo(path);
                if (!info.Exists) { _cache.Remove(path); return []; }
                var length = info.Length;
                if (_cache.TryGetValue(path, out var cached) && cached.Length == length) return cached.Names;
                var names = AcceptanceTrxFailureReader.Read(path).Failures.Select(failure =>
                    string.Join(".", (failure.TestName ?? "").Split('(')[0].Split('.').TakeLast(2))).ToArray();
                _cache[path] = (length, names);
                return names;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return []; }
    }
}
