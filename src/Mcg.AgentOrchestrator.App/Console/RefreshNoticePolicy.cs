using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// One problem/recovery cycle per console, including outcomes of detached refreshes.
internal sealed class RefreshNoticePolicy
{
    private readonly object _gate = new();
    private bool _problemReported;

    internal static bool ShouldAnnounceBusy(bool isBackgroundRefresh) => !isBackgroundRefresh;

    internal string? TimedOut(TimeSpan bound) => Problem(
        $"refresh did not finish within {bound.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s");

    internal string? Failed(string message) => Problem($"refresh error: {message}");

    internal string? Succeeded()
    {
        lock (_gate)
        {
            if (!_problemReported) return null;
            _problemReported = false;
            return "refresh recovered";
        }
    }

    private string? Problem(string message)
    {
        lock (_gate)
        {
            if (_problemReported) return null;
            _problemReported = true;
            return message;
        }
    }
}
