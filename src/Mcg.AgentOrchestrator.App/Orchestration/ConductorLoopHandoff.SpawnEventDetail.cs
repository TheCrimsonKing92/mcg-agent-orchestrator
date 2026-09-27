using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class ConductorLoopHandoff
{
    internal static string FormatWindowsSpawnEventDetail(ConductLoopLaunchRequest request, string consoleDetail)
    {
        var launchName = string.IsNullOrWhiteSpace(request.Name)
            ? "unnamed"
            : Regex.Replace(request.Name, @"\s+", "_");
        return $"{consoleDetail} launch={launchName}";
    }
}
