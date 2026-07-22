using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Whether the proxy only measures idle gaps (<see cref="Observe"/>) or also closes idle tunnels
/// (<see cref="Enforce"/>). Observe is used first to size <see cref="CodexEgressProxy.IdleTimeout"/>
/// from real worker traffic before enabling cuts.
/// </summary>
public enum CodexEgressProxyMode
{
    Observe,
    Enforce,
}

/// <summary>Reason a tunnel closed, for observe-phase analysis.</summary>
public enum CodexEgressCloseReason
{
    /// <summary>A peer closed its side (normal end of the tunneled connection).</summary>
    Eof,

    /// <summary>Enforce mode cut the tunnel after <see cref="CodexEgressProxy.IdleTimeout"/> of zero bytes.</summary>
    IdleCut,

    /// <summary>The upstream TCP connect did not complete within the connect timeout.</summary>
    ConnectFailed,

    /// <summary>A socket/IO error tore the tunnel down.</summary>
    Error,
}

/// <summary>One closed tunnel's summary, surfaced to the caller for logging/aggregation.</summary>
public sealed record CodexEgressTunnelRecord(
    string Target,
    long DurationMs,
    long BytesUpstream,
    long BytesDownstream,
    long MaxIdleMs,
    CodexEgressCloseReason CloseReason);

/// <summary>
/// A tiny loopback HTTP CONNECT proxy that codex workers route through via <c>HTTPS_PROXY</c>. It
/// blind-tunnels TLS with no interception -- SNI, ALPN, HTTP/2, and ChatGPT OAuth all run end-to-end
/// -- so it needs no certificate and cannot see credentials.
///
/// Its sole job is to bound the openai/codex mid-session hang. Codex's reqwest client applies NO
/// request/read/connect timeout (its <c>Request.timeout</c> field is dead code), so a black-holed API
/// request -- TCP established but the server never sends a response -- hangs at ~0 CPU indefinitely on
/// every OS (reqwest's Linux-only <c>tcp_user_timeout</c> is opt-in and never set, and would not fire
/// on an ACKed-idle socket anyway). This proxy closes a tunnel after <see cref="IdleTimeout"/> of zero
/// bytes in EITHER direction; codex's reqwest then surfaces a retryable <c>TransportError::Network</c>
/// and retries the turn within seconds on a fresh connection, turning the 15-minute dispatch reap into
/// in-place recovery. A false cut (a legitimately long silent gap) costs only one retried turn, never
/// a failure -- which is why <see cref="CodexEgressProxyMode.Observe"/> is used first to measure real
/// gaps before <see cref="CodexEgressProxyMode.Enforce"/> starts cutting.
///
/// Binds loopback only. Accepts arbitrary CONNECT targets (codex's child git/tooling traffic tunnels
/// through harmlessly). Self-supervising per connection: one bad client never takes down the listener.
/// </summary>
public sealed class CodexEgressProxy : IDisposable
{
    private const int PumpBufferSize = 32 * 1024;

    private readonly TcpListener _listener;
    private readonly CodexEgressProxyMode _mode;
    private readonly Action<CodexEgressTunnelRecord>? _onTunnelClosed;
    private readonly Action<string>? _onDiagnostic;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;
    private int _disposed;

    public CodexEgressProxy(
        CodexEgressProxyMode mode,
        TimeSpan idleTimeout,
        TimeSpan connectTimeout,
        int port = 0,
        Action<CodexEgressTunnelRecord>? onTunnelClosed = null,
        Action<string>? onDiagnostic = null)
    {
        if (idleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), idleTimeout, "Idle timeout must be positive.");
        }

        if (connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout), connectTimeout, "Connect timeout must be positive.");
        }

        _mode = mode;
        IdleTimeout = idleTimeout;
        ConnectTimeout = connectTimeout;
        _onTunnelClosed = onTunnelClosed;
        _onDiagnostic = onDiagnostic;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>Zero bytes in either direction for longer than this closes the tunnel (Enforce mode).</summary>
    public TimeSpan IdleTimeout { get; }

    /// <summary>The upstream TCP connect must complete within this or the tunnel is refused.</summary>
    public TimeSpan ConnectTimeout { get; }

    /// <summary>The loopback endpoint workers should point <c>HTTPS_PROXY</c> at. Valid after <see cref="Start"/>.</summary>
    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>Binds the loopback listener and starts accepting connections in the background.</summary>
    public IPEndPoint Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        return Endpoint;
    }

    private async Task AcceptLoopAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // Listener transient error; keep serving.
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Fire-and-forget per client; failures are contained inside HandleClientAsync.
            _ = Task.Run(() => HandleClientAsync(client, shutdown));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken shutdown)
    {
        using var _ = client;
        client.NoDelay = true;
        string target = "unknown";
        try
        {
            var clientStream = client.GetStream();
            var request = await ReadConnectRequestAsync(clientStream, shutdown).ConfigureAwait(false);
            if (request is null)
            {
                await WriteStatusAsync(clientStream, "400 Bad Request", shutdown).ConfigureAwait(false);
                return;
            }

            target = request.Value.Authority;
            using var upstream = new TcpClient { NoDelay = true };
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                connectCts.CancelAfter(ConnectTimeout);
                await upstream.ConnectAsync(request.Value.Host, request.Value.Port, connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                await WriteStatusAsync(clientStream, "502 Bad Gateway", shutdown).ConfigureAwait(false);
                Report(new CodexEgressTunnelRecord(target, 0, 0, 0, 0, CodexEgressCloseReason.ConnectFailed));
                return;
            }

            await WriteStatusAsync(clientStream, "200 Connection Established", shutdown).ConfigureAwait(false);
            await TunnelAsync(target, clientStream, upstream.GetStream(), shutdown).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Client vanished mid-handshake or shutdown; nothing to recover.
        }
        catch (Exception ex)
        {
            _onDiagnostic?.Invoke($"CodexEgressProxy tunnel to {target} faulted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Pumps bytes both ways, tracking combined idle time and (in Enforce mode) cutting on a stall.</summary>
    private async Task TunnelAsync(string target, NetworkStream client, NetworkStream upstream, CancellationToken shutdown)
    {
        using var tunnelCts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var startTicks = Environment.TickCount64;
        var lastActivity = new StrongBox<long>(startTicks);
        var maxIdle = new StrongBox<long>(0);
        long bytesUpstream = 0;
        long bytesDownstream = 0;

        var toUpstream = PumpAsync(client, upstream, lastActivity, n => Interlocked.Add(ref bytesUpstream, n), tunnelCts);
        var toClient = PumpAsync(upstream, client, lastActivity, n => Interlocked.Add(ref bytesDownstream, n), tunnelCts);
        var watchdog = IdleWatchdogAsync(lastActivity, maxIdle, tunnelCts);

        // The tunnel ends when either direction closes (EOF/error) or the watchdog cuts it.
        var pumps = Task.WhenAny(toUpstream, toClient);
        await Task.WhenAny(pumps, watchdog).ConfigureAwait(false);

        var cutByWatchdog = watchdog.IsCompleted && !pumps.IsCompleted;
        await tunnelCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(toUpstream, toClient, watchdog).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Expected once we tear the sockets down.
        }

        var faulted = (toUpstream.IsFaulted || toClient.IsFaulted) && !cutByWatchdog;
        var closeReason = cutByWatchdog
            ? CodexEgressCloseReason.IdleCut
            : faulted ? CodexEgressCloseReason.Error : CodexEgressCloseReason.Eof;

        Report(new CodexEgressTunnelRecord(
            target,
            Environment.TickCount64 - startTicks,
            Interlocked.Read(ref bytesUpstream),
            Interlocked.Read(ref bytesDownstream),
            Math.Max(maxIdle.Value, cutByWatchdog ? (long)IdleTimeout.TotalMilliseconds : 0),
            closeReason));
    }

    private static async Task PumpAsync(
        NetworkStream from,
        NetworkStream to,
        StrongBox<long> lastActivity,
        Action<int> countBytes,
        CancellationTokenSource tunnelCts)
    {
        var buffer = new byte[PumpBufferSize];
        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer, tunnelCts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return; // Peer closed.
                }

                Volatile.Write(ref lastActivity.Value, Environment.TickCount64);
                countBytes(read);
                await to.WriteAsync(buffer.AsMemory(0, read), tunnelCts.Token).ConfigureAwait(false);
                Volatile.Write(ref lastActivity.Value, Environment.TickCount64);
            }
        }
        finally
        {
            // Ensure the peer direction unblocks promptly once this side ends.
            if (!tunnelCts.IsCancellationRequested)
            {
                await tunnelCts.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task IdleWatchdogAsync(StrongBox<long> lastActivity, StrongBox<long> maxIdle, CancellationTokenSource tunnelCts)
    {
        var probe = TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(100, IdleTimeout.TotalMilliseconds / 4)));
        var idleMs = (long)IdleTimeout.TotalMilliseconds;
        try
        {
            while (!tunnelCts.IsCancellationRequested)
            {
                await Task.Delay(probe, tunnelCts.Token).ConfigureAwait(false);
                var idle = Environment.TickCount64 - Volatile.Read(ref lastActivity.Value);
                if (idle > Volatile.Read(ref maxIdle.Value))
                {
                    Volatile.Write(ref maxIdle.Value, idle);
                }

                if (_mode == CodexEgressProxyMode.Enforce && idle >= idleMs)
                {
                    return; // Signal a stall cut; caller tears the tunnel down.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Tunnel ended first.
        }
    }

    /// <summary>Reads the CONNECT request line and headers (up to the blank line). Returns null if malformed.</summary>
    private static async Task<(string Authority, string Host, int Port)?> ReadConnectRequestAsync(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new StringBuilder(256);
        var buffer = new byte[1];
        var terminatorMatch = 0;
        // "\r\n\r\n" terminates the request head. Cap the size to reject junk that never terminates.
        while (header.Length < 8 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            var c = (char)buffer[0];
            header.Append(c);
            terminatorMatch = c switch
            {
                '\r' when terminatorMatch is 0 or 2 => terminatorMatch + 1,
                '\n' when terminatorMatch is 1 or 3 => terminatorMatch + 1,
                _ => 0,
            };
            if (terminatorMatch == 4)
            {
                break;
            }
        }

        var firstLineEnd = header.ToString().IndexOf("\r\n", StringComparison.Ordinal);
        if (firstLineEnd <= 0)
        {
            return null;
        }

        var requestLine = header.ToString(0, firstLineEnd);
        var tokens = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || !tokens[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var authority = tokens[1];
        var separator = authority.LastIndexOf(':');
        if (separator <= 0 || separator == authority.Length - 1)
        {
            return null;
        }

        var host = authority[..separator];
        if (!int.TryParse(authority[(separator + 1)..], out var port) || port is <= 0 or > 65535)
        {
            return null;
        }

        return (authority, host, port);
    }

    private static async Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private void Report(CodexEgressTunnelRecord record)
    {
        try
        {
            _onTunnelClosed?.Invoke(record);
        }
        catch (Exception ex)
        {
            _onDiagnostic?.Invoke($"CodexEgressProxy tunnel-record sink threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Already down.
        }

        _shutdown.Dispose();
    }
}
