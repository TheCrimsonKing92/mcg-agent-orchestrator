using System.Diagnostics;
using System.Globalization;

namespace Mcg.AgentOrchestrator.Infrastructure;

public readonly record struct ProtectedProcessIdentity(int ProcessId, long StartTimeUtcTicks)
{
    private static readonly object ParseLock = new();
    private static (string? Pid, string? Ticks, ProtectedProcessIdentity? Identity) _parsedEnvironment;
    public const string PidVariable = "MCG_ORCHESTRATOR_PROTECTED_PID";
    public const string StartTicksVariable = "MCG_ORCHESTRATOR_PROTECTED_PID_START_TICKS";

    public override string ToString() =>
        $"{ProcessId.ToString(CultureInfo.InvariantCulture)}@{StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? pid, string? ticks, out ProtectedProcessIdentity identity)
    {
        identity = default;
        if (!int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0 ||
            !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks) || startTicks <= 0)
            return false;
        identity = new ProtectedProcessIdentity(processId, startTicks);
        return true;
    }

    public static ProtectedProcessIdentity? ResolveAtStartup(
        string? inheritedPid, string? inheritedTicks, ProtectedProcessIdentity? current,
        Func<int, long?> readLiveStartTicks)
    {
        if (TryParse(inheritedPid, inheritedTicks, out var inherited) &&
            readLiveStartTicks(inherited.ProcessId) == inherited.StartTimeUtcTicks)
            return inherited;
        return current;
    }

    public static bool IsLive(ProtectedProcessIdentity identity, Func<int, long?> readLiveStartTicks) =>
        readLiveStartTicks(identity.ProcessId) == identity.StartTimeUtcTicks;

    public static ProtectedProcessIdentity? ReadEnvironment()
    {
        var pid = Environment.GetEnvironmentVariable(PidVariable);
        var ticks = Environment.GetEnvironmentVariable(StartTicksVariable);
        lock (ParseLock)
        {
            if (_parsedEnvironment.Pid == pid && _parsedEnvironment.Ticks == ticks)
                return _parsedEnvironment.Identity;
            var parsed = TryParse(pid, ticks, out var identity) ? identity : (ProtectedProcessIdentity?)null;
            _parsedEnvironment = (pid, ticks, parsed);
            return parsed;
        }
    }

    public static void Bind(ProtectedProcessIdentity? identity, int fallbackPid)
    {
        Environment.SetEnvironmentVariable(PidVariable,
            (identity?.ProcessId ?? fallbackPid).ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(StartTicksVariable,
            identity?.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    public static ProtectedProcessIdentity? Current()
    {
        using var process = Process.GetCurrentProcess();
        try { return new ProtectedProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    public static long? ReadLiveStartTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }
}
