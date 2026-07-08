using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record WorkerProcessJobAccounting(
    long CpuMilliseconds,
    long PeakMemoryBytes,
    long IoBytes)
{
    public static WorkerProcessJobAccounting Empty { get; } = new(0, 0, 0);
}

public static class WorkerProcessJobs
{
    private const string ProtectedPidVariable = "MCG_ORCHESTRATOR_PROTECTED_PID";
    private static readonly ConcurrentDictionary<int, OwnedProcessGroup> Jobs = new();
    private static SpawnRegistry? Registry;

    internal static Func<int, bool> TryKillPidTree { get; set; } = DefaultTryKillPidTree;

    public static void ConfigureRegistry(string dbPath)
    {
        Registry = new SpawnRegistry(dbPath);
    }

    public static int SweepStartupOrphans()
    {
        var registry = Registry;
        if (registry is null)
        {
            return 0;
        }

        var reaped = 0;
        foreach (var entry in registry.ListActive())
        {
            if (!SpawnProcessIdentityReader.MatchesLiveProcess(entry, out var process))
            {
                registry.MarkReleased(entry.ProcessId, $"spawn_registry: already-dead-or-recycled pid={entry.ProcessId} owner={entry.OwnerId}");
                continue;
            }

            using (process)
            {
                if (IsProtectedProcessOrAncestor(entry.ProcessId) || ProtectedPidIsDescendantOf(entry.ProcessId))
                {
                    registry.RecordDiagnostic(entry.Id, $"spawn_registry: refused-protected pid={entry.ProcessId} owner={entry.OwnerId}");
                    continue;
                }

                if (TryKillOrFallback(entry.ProcessId))
                {
                    registry.MarkReleased(entry.ProcessId, $"spawn_registry: startup-reaped pid={entry.ProcessId} owner={entry.OwnerId}");
                    reaped++;
                }
                else
                {
                    registry.RecordDiagnostic(entry.Id, $"spawn_registry: startup-reap-failed pid={entry.ProcessId} owner={entry.OwnerId}");
                }
            }
        }

        return reaped;
    }

    public static bool TryRegister(Process process, string? ownerId = null)
    {
        if (IsProtectedProcessOrAncestor(process.Id))
        {
            return false;
        }

        try
        {
            var group = OwnedProcessGroup.Attach(process);
            if (Jobs.TryAdd(process.Id, group))
            {
                RegisterDurable(process, ownerId);
                return true;
            }

            group.Dispose();
        }
        catch (Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return false;
    }

    public static bool TryKillOrFallback(int processId)
    {
        if (IsProtectedProcessOrAncestor(processId) || ProtectedPidIsDescendantOf(processId))
        {
            return false;
        }

        if (Jobs.TryRemove(processId, out var group))
        {
            try
            {
                group.Kill();
                Registry?.MarkReleased(processId, $"spawn_registry: killed pid={processId}");
                return true;
            }
            catch
            {
                // Fall through to PID tree cleanup for legacy or already-exited records.
            }
            finally
            {
                group.Dispose();
            }
        }

        var fallbackKilled = TryKillPidTree(processId);
        if (fallbackKilled)
        {
            Registry?.MarkReleased(processId, $"spawn_registry: fallback-killed pid={processId}");
        }

        return fallbackKilled;
    }

    public static void Release(int processId)
    {
        Release(processId, out _);
    }

    public static void Release(int processId, out WorkerProcessJobAccounting? accounting)
    {
        accounting = null;
        if (Jobs.TryRemove(processId, out var group))
        {
            try
            {
                if (group.TryReadAccounting(out var capturedAccounting))
                {
                    accounting = capturedAccounting;
                }

                group.Kill();
            }
            catch
            {
                // Already-dead or access-denied races are diagnostics, not state-machine failures.
            }
            finally
            {
                group.Dispose();
            }
        }

        Registry?.MarkReleased(processId, $"spawn_registry: released pid={processId}");
    }

    internal static bool HasRegisteredJob(int processId) => Jobs.ContainsKey(processId);

    internal static IReadOnlyList<SpawnRegistryEntry> ListActiveRegistryEntriesForTests() =>
        Registry?.ListActive() ?? [];

    internal static void ClearRegistryForTests() => Registry = null;

    private static void RegisterDurable(Process process, string? ownerId)
    {
        var registry = Registry;
        if (registry is null || !SpawnProcessIdentityReader.TryRead(process, out var identity))
        {
            return;
        }

        try
        {
            registry.Register(
                string.IsNullOrWhiteSpace(ownerId) ? $"pid:{process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : ownerId,
                identity);
        }
        catch
        {
            // Registry durability is a lifecycle backstop; failed diagnostics must not prevent spawn.
        }
    }

    private static bool DefaultTryKillPidTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return TryTaskkillProcessTree(processId);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryTaskkillProcessTree(int processId)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process.StartInfo.ArgumentList.Add("/T");
            process.StartInfo.ArgumentList.Add("/F");
            process.StartInfo.ArgumentList.Add("/PID");
            process.StartInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!process.Start())
            {
                return false;
            }

            process.WaitForExit(5000);
            return process.ExitCode == 0 || !IsProcessRunning(processId);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProtectedProcessOrAncestor(int processId)
    {
        return TryGetProtectedPid(out var protectedPid) &&
            (processId == protectedPid || IsDescendantOf(processId, protectedPid));
    }

    private static bool ProtectedPidIsDescendantOf(int processId)
    {
        return TryGetProtectedPid(out var protectedPid) && IsDescendantOf(protectedPid, processId);
    }

    private static bool TryGetProtectedPid(out int processId)
    {
        var raw = Environment.GetEnvironmentVariable(ProtectedPidVariable);
        return int.TryParse(
            raw,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out processId) &&
            processId > 0;
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsDescendantOf(int processId, int ancestorProcessId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var current = processId;
        for (var i = 0; i < 64; i++)
        {
            if (!TryGetParentProcessId(current, out var parentProcessId))
            {
                return false;
            }

            if (parentProcessId == ancestorProcessId)
            {
                return true;
            }

            current = parentProcessId;
        }

        return false;
    }

    private static bool TryGetParentProcessId(int processId, out int parentProcessId)
    {
        parentProcessId = 0;
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return false;
            }

            do
            {
                if (entry.th32ProcessID == (uint)processId)
                {
                    parentProcessId = (int)entry.th32ParentProcessID;
                    return parentProcessId > 0;
                }
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
