using Mcg.AgentOrchestrator.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

public sealed class CodexEgressProxyTests
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    [Xunit.Fact(DisplayName = "CodexEgressProxy_tunnels_bytes_end_to_end")]
    public async Task CodexEgressProxyTunnelsBytesEndToEnd()
    {
        using var echo = new LoopbackServer(echo: true);
        var records = new ConcurrentQueue<CodexEgressTunnelRecord>();
        using var proxy = new CodexEgressProxy(
            CodexEgressProxyMode.Observe, TimeSpan.FromSeconds(30), ConnectTimeout,
            onTunnelClosed: records.Enqueue);
        var endpoint = proxy.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        var stream = client.GetStream();
        await SendConnectAsync(stream, $"127.0.0.1:{echo.Port}");
        var status = await ReadStatusLineAsync(stream);
        Assert.Contains("200", status);

        var payload = Encoding.ASCII.GetBytes("hello-tunnel");
        await stream.WriteAsync(payload);
        var echoed = await ReadExactlyAsync(stream, payload.Length);
        Assert.Equal(payload, echoed);

        client.Close();
        var record = await WaitForRecordAsync(records, r => r.Target == $"127.0.0.1:{echo.Port}");
        Assert.Equal(CodexEgressCloseReason.Eof, record.CloseReason);
        Assert.True(record.BytesUpstream >= payload.Length);
    }

    [Xunit.Fact(DisplayName = "CodexEgressProxy_enforce_cuts_idle_tunnel")]
    public async Task CodexEgressProxyEnforceCutsIdleTunnel()
    {
        using var silent = new LoopbackServer(echo: false);
        var records = new ConcurrentQueue<CodexEgressTunnelRecord>();
        using var proxy = new CodexEgressProxy(
            CodexEgressProxyMode.Enforce, TimeSpan.FromMilliseconds(400), ConnectTimeout,
            onTunnelClosed: records.Enqueue);
        var endpoint = proxy.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        var stream = client.GetStream();
        await SendConnectAsync(stream, $"127.0.0.1:{silent.Port}");
        Assert.Contains("200", await ReadStatusLineAsync(stream));

        // Both sides now go silent; Enforce must cut the tunnel and close the client socket.
        var closed = await ReadReturnsClosedAsync(stream, TimeSpan.FromSeconds(10));
        Assert.True(closed, "Expected the idle tunnel to be cut and the client socket to close.");

        var record = await WaitForRecordAsync(records, r => r.Target == $"127.0.0.1:{silent.Port}");
        Assert.Equal(CodexEgressCloseReason.IdleCut, record.CloseReason);
    }

    [Xunit.Fact(DisplayName = "CodexEgressProxy_observe_does_not_cut_idle_tunnel")]
    public async Task CodexEgressProxyObserveDoesNotCutIdleTunnel()
    {
        using var echo = new LoopbackServer(echo: true);
        var records = new ConcurrentQueue<CodexEgressTunnelRecord>();
        using var proxy = new CodexEgressProxy(
            CodexEgressProxyMode.Observe, TimeSpan.FromMilliseconds(300), ConnectTimeout,
            onTunnelClosed: records.Enqueue);
        var endpoint = proxy.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        var stream = client.GetStream();
        await SendConnectAsync(stream, $"127.0.0.1:{echo.Port}");
        Assert.Contains("200", await ReadStatusLineAsync(stream));

        // Stay idle well past the (Observe-mode) idle timeout, then prove the tunnel is still alive.
        await Task.Delay(TimeSpan.FromSeconds(1));
        var payload = Encoding.ASCII.GetBytes("still-open");
        await stream.WriteAsync(payload);
        var echoed = await ReadExactlyAsync(stream, payload.Length);
        Assert.Equal(payload, echoed);

        client.Close();
        var record = await WaitForRecordAsync(records, r => r.Target == $"127.0.0.1:{echo.Port}");
        // Observe never cuts, but it must still have measured the >300ms silent gap.
        Assert.NotEqual(CodexEgressCloseReason.IdleCut, record.CloseReason);
        Assert.True(record.MaxIdleMs >= 300, $"Expected a measured idle gap >= 300ms, got {record.MaxIdleMs}ms.");
    }

    [Xunit.Fact(DisplayName = "CodexEgressProxy_rejects_non_connect_request")]
    public async Task CodexEgressProxyRejectsNonConnectRequest()
    {
        using var proxy = new CodexEgressProxy(
            CodexEgressProxyMode.Observe, TimeSpan.FromSeconds(30), ConnectTimeout);
        var endpoint = proxy.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes("GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n");
        await stream.WriteAsync(request);
        Assert.Contains("400", await ReadStatusLineAsync(stream));
    }

    [Xunit.Fact(DisplayName = "CodexEgressProxy_reports_502_on_upstream_connect_failure")]
    public async Task CodexEgressProxyReports502OnUpstreamConnectFailure()
    {
        var deadPort = ReserveThenFreePort();
        var records = new ConcurrentQueue<CodexEgressTunnelRecord>();
        using var proxy = new CodexEgressProxy(
            CodexEgressProxyMode.Observe, TimeSpan.FromSeconds(30), ConnectTimeout,
            onTunnelClosed: records.Enqueue);
        var endpoint = proxy.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        var stream = client.GetStream();
        await SendConnectAsync(stream, $"127.0.0.1:{deadPort}");
        Assert.Contains("502", await ReadStatusLineAsync(stream));

        var record = await WaitForRecordAsync(records, r => r.Target == $"127.0.0.1:{deadPort}");
        Assert.Equal(CodexEgressCloseReason.ConnectFailed, record.CloseReason);
    }

    [Xunit.Fact(DisplayName = "CodexEgressProxy_wiring_disabled_leaves_proxy_env_untouched")]
    public void CodexEgressProxyWiringDisabledLeavesProxyEnvUntouched()
    {
        var startInfo = new ProcessStartInfo();
        var parameters = MinimalCodexParameters(WorkerSandboxProvider.Codex, enabled: false);
        using var proxy = DispatchProcessHost.StartCodexEgressProxyIfEnabled(parameters, startInfo);
        Assert.Null(proxy);
        Assert.False(startInfo.Environment.ContainsKey("HTTPS_PROXY"));
    }

    [Xunit.Fact(DisplayName = "CodexEgressProxy_wiring_enabled_codex_points_https_proxy_at_loopback")]
    public void CodexEgressProxyWiringEnabledCodexPointsHttpsProxyAtLoopback()
    {
        var startInfo = new ProcessStartInfo();
        var parameters = MinimalCodexParameters(WorkerSandboxProvider.Codex, enabled: true);
        using var proxy = DispatchProcessHost.StartCodexEgressProxyIfEnabled(parameters, startInfo);
        Assert.NotNull(proxy);
        var expected = $"http://127.0.0.1:{proxy!.Endpoint.Port}";
        Assert.Equal(expected, startInfo.Environment["HTTPS_PROXY"]);
        Assert.Equal(expected, startInfo.Environment["HTTP_PROXY"]);
        Assert.Equal(expected, startInfo.Environment["ALL_PROXY"]);
    }

    [Xunit.Fact(DisplayName = "CodexEgressProxy_wiring_skips_non_codex_provider")]
    public void CodexEgressProxyWiringSkipsNonCodexProvider()
    {
        var startInfo = new ProcessStartInfo();
        var parameters = MinimalCodexParameters(WorkerSandboxProvider.Claude, enabled: true);
        using var proxy = DispatchProcessHost.StartCodexEgressProxyIfEnabled(parameters, startInfo);
        Assert.Null(proxy);
        Assert.False(startInfo.Environment.ContainsKey("HTTPS_PROXY"));
    }

    [Xunit.Fact(DisplayName = "CodexEgressProxyOptions_defaults_are_disabled_observe")]
    public void CodexEgressProxyOptionsDefaultsAreDisabledObserve()
    {
        var options = new CodexEgressProxyOptions();
        Assert.False(options.Enabled);
        Assert.False(options.Enforce);
        Assert.True(options.IdleTimeoutMs > 0);
        Assert.True(options.ConnectTimeoutMs > 0);
    }

    private static DispatchProcessHost.DispatchRunParameters MinimalCodexParameters(
        WorkerSandboxProvider provider, bool enabled)
    {
        var temp = Path.GetTempPath();
        var stamp = Guid.NewGuid().ToString("N");
        return new DispatchProcessHost.DispatchRunParameters(
            Command: "noop",
            WorkingDirectory: temp,
            StdoutPath: Path.Combine(temp, $"mcg-egress-{stamp}.out"),
            StderrPath: Path.Combine(temp, $"mcg-egress-{stamp}.err"),
            ExitCodePath: Path.Combine(temp, $"mcg-egress-{stamp}.exit"),
            HeartbeatPath: null,
            ShutdownBuildServerOnExit: false,
            DisableSharedCompilation: false,
            Provider: provider,
            CodexEgressProxyEnabled: enabled);
    }

    private static async Task SendConnectAsync(NetworkStream stream, string authority)
    {
        var line = Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");
        await stream.WriteAsync(line);
    }

    private static async Task<string> ReadStatusLineAsync(NetworkStream stream)
    {
        // Consume the entire CONNECT response head (status line + blank line, terminated by \r\n\r\n)
        // so the tunnel body that follows is not polluted by the trailing CRLF.
        var builder = new StringBuilder();
        var buffer = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!builder.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, cts.Token);
            if (read == 0)
            {
                break;
            }

            builder.Append((char)buffer[0]);
        }

        return builder.ToString();
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cts.Token);
            if (read == 0)
            {
                throw new IOException("Stream closed before the expected bytes arrived.");
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<bool> ReadReturnsClosedAsync(NetworkStream stream, TimeSpan timeout)
    {
        var buffer = new byte[64];
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0)
                {
                    return true; // Graceful close.
                }
                // Ignore any stray bytes and keep waiting for the close.
            }
        }
        catch (OperationCanceledException)
        {
            return false; // Timed out with the socket still open.
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            return true; // Reset counts as closed.
        }
    }

    private static async Task<CodexEgressTunnelRecord> WaitForRecordAsync(
        ConcurrentQueue<CodexEgressTunnelRecord> records, Func<CodexEgressTunnelRecord, bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!cts.IsCancellationRequested)
        {
            foreach (var record in records)
            {
                if (predicate(record))
                {
                    return record;
                }
            }

            await Task.Delay(25, cts.Token);
        }

        throw new TimeoutException("No matching tunnel record was reported in time.");
    }

    private static int ReserveThenFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public LoopbackServer(bool echo)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptAsync(echo, _cts.Token));
        }

        public int Port { get; }

        private async Task AcceptAsync(bool echo, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client, echo, token));
            }
        }

        private static async Task ServeAsync(TcpClient client, bool echo, CancellationToken token)
        {
            using var _ = client;
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                while (!token.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, token);
                    if (read == 0)
                    {
                        return;
                    }

                    if (echo)
                    {
                        await stream.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    // Silent server: drain and hold, never write back.
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // Peer went away.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
