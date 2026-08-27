using System.Globalization;
using System.Runtime.InteropServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ProcessParentIdResolver
{
    public static int? TryGetParentProcessId(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            var result = WindowsNativeProcessInspection.ReadParentProcessId(processId);
            return result.Status == ProcessInspectionStatus.Available
                ? result.ParentProcessId
                : null;
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var lastParen = stat.LastIndexOf(')');
            if (lastParen < 0)
            {
                return null;
            }

            var fields = stat[(lastParen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 2 && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentId)
                ? parentId
                : null;
        }
        catch
        {
            return null;
        }
    }
}
