using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class ConsoleAnnouncementFormatter
{
    internal static string Format(TimeProvider clock, string line) =>
        string.IsNullOrWhiteSpace(line) ? line :
        $"[{clock.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {line}";
}
