using System.Runtime.CompilerServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GateHeartbeatOwner
{
    private static readonly AsyncLocal<StrongBox<string?>?> OwnerOverride = new();
    private static string? _boundOwner;

    internal static string? Current => OwnerOverride.Value is { } scoped
        ? scoped.Value : Volatile.Read(ref _boundOwner);

    internal static void Bind(string stateDirectory)
    {
        var normalized = Normalize(stateDirectory);
        var previous = Interlocked.CompareExchange(ref _boundOwner, normalized, null);
        if (previous is not null && !string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The gate heartbeat owner is already bound to another state directory.");
    }

    internal static IDisposable Push(string? stateDirectory)
    {
        var previous = OwnerOverride.Value;
        OwnerOverride.Value = new StrongBox<string?>(stateDirectory is null ? null : Normalize(stateDirectory));
        return new RestoreAction(() => OwnerOverride.Value = previous);
    }

    internal static bool IsForeign(string? recorded)
    {
        if (string.IsNullOrWhiteSpace(recorded) || Current is not { } current) return false;
        try
        {
            return !string.Equals(Normalize(recorded), current, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unreadable recorded identity is unknown, so it must keep counting.
            return false;
        }
    }

    private static string Normalize(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private sealed class RestoreAction(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
