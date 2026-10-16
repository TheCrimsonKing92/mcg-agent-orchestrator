namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleInputTraceOptions
{
    internal const string Usage = "Usage: console [--plain] [--input-trace <new-json-path>]";

    internal static string? Parse(IReadOnlyList<string> args, bool inputRedirected, bool outputRedirected)
    {
        string? path = null;
        for (var index = 1; index < args.Count; index++)
        {
            if (args[index].Equals("--plain", StringComparison.OrdinalIgnoreCase)) continue;
            if (!args[index].Equals("--input-trace", StringComparison.OrdinalIgnoreCase) || path is not null ||
                ++index == args.Count || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            path = Path.GetFullPath(args[index]);
        }
        if (path is not null && OwnerConsoleModeSelector.Select(args, inputRedirected, outputRedirected) != OwnerConsoleMode.FullScreen)
            throw new ArgumentException("--input-trace requires an interactive full-screen console.");
        return path;
    }

    // Reserve a new destination before entering the UI; never truncate an existing receipt.
    internal static FileStream OpenDestination(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
}
