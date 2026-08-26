using System.Globalization;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ProcessParentIdResolver
{
    public static int TryGetParentProcessId(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsNativeProcessInspection.TryGetParentProcessId(processId);
        }

        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var lastParen = stat.LastIndexOf(')');
            if (lastParen < 0)
            {
                return 0;
            }

            var fields = stat[(lastParen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2 && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentId)
                ? parentId
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
