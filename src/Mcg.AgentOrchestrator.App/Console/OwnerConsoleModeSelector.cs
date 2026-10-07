namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal enum OwnerConsoleMode { Plain, FullScreen }

internal sealed record OwnerConsoleModeRunners(
    Func<CancellationToken, Task<int>> Plain, Func<CancellationToken, Task<int>> FullScreen);

internal static class OwnerConsoleModeSelector
{
    internal static OwnerConsoleMode Select(IReadOnlyList<string> args, bool inputRedirected, bool outputRedirected) =>
        inputRedirected || outputRedirected || args.Contains("--plain", StringComparer.OrdinalIgnoreCase)
            ? OwnerConsoleMode.Plain : OwnerConsoleMode.FullScreen;

    internal static Task<int> RunAsync(IReadOnlyList<string> args, bool inputRedirected, bool outputRedirected,
        OwnerConsoleModeRunners runners, CancellationToken cancellationToken = default) =>
        (Select(args, inputRedirected, outputRedirected) == OwnerConsoleMode.Plain ? runners.Plain : runners.FullScreen)(cancellationToken);
}
