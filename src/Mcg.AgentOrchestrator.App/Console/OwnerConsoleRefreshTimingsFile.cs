using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleRefreshTimingsFile(string path, TimeProvider clock, int processId)
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastWrite;

    internal void Write(string detail)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_lastWrite is { } last && now - last < TimeSpan.FromSeconds(60)) return;
            _lastWrite = now;
            try
            {
                File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture,
                    $"{now:O} pid={processId} {detail}{Environment.NewLine}"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
