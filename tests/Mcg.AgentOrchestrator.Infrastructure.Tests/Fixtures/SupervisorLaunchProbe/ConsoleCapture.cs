// Process-wide writer installation for the standalone, disposable supervisor probe.
// This assembly runs in its own child process, never in the xUnit host. RunAsync
// installs these writers once and owns them for the probe's lifetime.
internal static class ConsoleCapture
{
    internal static void Install(TextWriter stdout, TextWriter stderr)
    {
        Console.SetOut(stdout);
        Console.SetError(stderr);
    }
}
