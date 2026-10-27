using System.ComponentModel;
using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure.Acceptance.Tests;

internal static class BoundedDirectoryRemoval
{
    internal const int MaxAttempts = 8;
    private static readonly int[] BackoffMilliseconds = [25, 50, 100, 200, 200, 200, 200];

    internal static void Remove(string directory, IReadOnlyList<Process> processes,
        Action<int, IOException>? onSharingViolation = null)
    {
        var fullPath = Path.GetFullPath(directory);
        // Capture identity before retrying; a process can exit while removal is in progress.
        var identities = processes.Select(process => (Process: process,
            Id: Inspect(() => process.Id.ToString()), Name: Inspect(() => process.ProcessName))).ToArray();
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(fullPath, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32)
            {
                onSharingViolation?.Invoke(attempt, ex);
                if (attempt == MaxAttempts)
                {
                    var details = identities.Length == 0 ? "no tracked processes" : string.Join("; ",
                        identities.Select(identity => $"pid={identity.Id} name={identity.Name} " +
                            $"status={Inspect(() => identity.Process.HasExited ? "exited" : "running")}"));
                    throw new IOException($"Could not remove directory '{fullPath}' after {MaxAttempts} " +
                        $"sharing-violation attempts. Supplied processes: {details}.", ex);
                }
                Thread.Sleep(BackoffMilliseconds[attempt - 1]);
            }
        }
    }

    private static string Inspect(Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return "unavailable";
        }
    }
}
