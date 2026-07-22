using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    // codex-egress-proxy [--enforce] [--idle-ms N] [--connect-ms N] [--port N] [--duration-seconds N]
    // Runs the codex egress proxy standalone for manual observation/enforcement -- the same loopback
    // CONNECT proxy the dispatch host starts per Codex worker. Prints the endpoint to point HTTPS_PROXY
    // at, streams each closed tunnel (target/bytes/max-idle/close-reason), and blocks until Ctrl-C or the
    // optional duration. Use Observe (default) to size the idle window from real traffic before enabling
    // Enforce on live dispatches.
    private static bool RunCodexEgressProxyCommand(IReadOnlyList<string> parts)
    {
        var enforce = HasCliConfirmation(parts, "--enforce");
        var idleMs = ParseIntFlagOrDefault(parts, "--idle-ms", 120_000);
        var connectMs = ParseIntFlagOrDefault(parts, "--connect-ms", 15_000);
        var port = ParseIntFlagOrDefault(parts, "--port", 0);
        var durationSeconds = ParseIntFlagOrDefault(parts, "--duration-seconds", 0);
        var mode = enforce ? CodexEgressProxyMode.Enforce : CodexEgressProxyMode.Observe;

        var tunnelCount = 0;
        using var proxy = new CodexEgressProxy(
            mode,
            TimeSpan.FromMilliseconds(Math.Max(1_000, idleMs)),
            TimeSpan.FromMilliseconds(Math.Max(1_000, connectMs)),
            port,
            onTunnelClosed: record =>
            {
                Interlocked.Increment(ref tunnelCount);
                Console.WriteLine(
                    $"[tunnel] target={record.Target} dur={record.DurationMs}ms " +
                    $"up={record.BytesUpstream} down={record.BytesDownstream} " +
                    $"maxIdle={record.MaxIdleMs}ms close={record.CloseReason}");
            },
            onDiagnostic: message => Console.Error.WriteLine($"[codex-egress-proxy] {message}"));

        var endpoint = proxy.Start();
        var url = $"http://127.0.0.1:{endpoint.Port}";
        Console.WriteLine($"CODEX_EGRESS_PROXY {url}");
        Console.WriteLine(
            $"mode={mode} idle={idleMs}ms connect={connectMs}ms -- set HTTPS_PROXY={url} " +
            (durationSeconds > 0 ? $"(running {durationSeconds}s)." : "(Ctrl-C to stop)."));

        using var stop = new ManualResetEventSlim(false);
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Set(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            if (durationSeconds > 0)
            {
                stop.Wait(TimeSpan.FromSeconds(durationSeconds));
            }
            else
            {
                stop.Wait();
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        Console.WriteLine($"codex-egress-proxy stopped after {tunnelCount} tunnel(s).");
        return false;
    }

    private static int ParseIntFlagOrDefault(IReadOnlyList<string> parts, string flag, int fallback) =>
        int.TryParse(GetFlagValue(parts, flag), out var parsed) && parsed >= 0 ? parsed : fallback;
}
