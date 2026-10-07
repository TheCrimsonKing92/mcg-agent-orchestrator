using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Advisory cross-process occupancy: a pid alone is never proof of a living owner.
internal static class RemoteExecutorOccupancy
{
    private sealed record Marker(int OwnerPid, DateTimeOffset OwnerStartUtc);
    private static string Folder(string root, string executor) =>
        Path.Combine(root, ".orchestrator", "remote-executor-occupancy", executor);
    private static bool SafeName(string value) => !string.IsNullOrWhiteSpace(value) &&
        value is not ("." or "..") && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    internal static DateTimeOffset? DefaultStartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception) { return null; }
    }

    internal static void Claim(string root, string executor, string attempt)
    {
        string? temporary = null;
        try
        {
            if (!SafeName(executor) || !SafeName(attempt) ||
                DefaultStartTime(Environment.ProcessId) is not { } start) return;
            var folder = Folder(root, executor);
            Directory.CreateDirectory(folder);
            temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Marker(Environment.ProcessId, start)));
            File.Move(temporary, Path.Combine(folder, attempt + ".json"), overwrite: true);
        }
        catch (Exception) { }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); }
            catch (Exception) { }
        }
    }

    internal static void Release(string root, string executor, string attempt)
    {
        try
        {
            if (SafeName(executor) && SafeName(attempt))
                File.Delete(Path.Combine(Folder(root, executor), attempt + ".json"));
        }
        catch (Exception) { }
    }

    internal static bool IsOccupied(string root, string executor, Func<int, DateTimeOffset?>? startTimeOf = null)
    {
        try
        {
            if (!SafeName(executor)) return false;
            var folder = Folder(root, executor);
            if (!Directory.Exists(folder)) return false;
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path));
                    if (marker is not null && (startTimeOf ?? DefaultStartTime)(marker.OwnerPid) is { } start &&
                        start == marker.OwnerStartUtc) return true;
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return false;
    }
}
