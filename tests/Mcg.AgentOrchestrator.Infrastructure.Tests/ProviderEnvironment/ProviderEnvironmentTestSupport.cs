using Mcg.AgentOrchestrator.Core;
using System.Net;
using System.Net.Sockets;

internal static class ProviderEnvironmentTestSupport
{
    public static RefusingLoopbackEndpoint ReserveRefusingLoopbackEndpoint() => new();
}

internal sealed class RefusingLoopbackEndpoint : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    private bool _disposed;

    public RefusingLoopbackEndpoint()
    {
        try
        {
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public bool IsHeld => !_disposed && _socket.IsBound;

    public void Dispose()
    {
        _socket.Dispose();
        _disposed = true;
    }
}

internal sealed class FakeSmokeProvider : IModelProvider
{
    private readonly string _providerName;
    private readonly string _text;
    private readonly ModelUsage _usage;
    private readonly string _stopReason;

    public FakeSmokeProvider(
        string text = "OK",
        ModelUsage? usage = null,
        string stopReason = "stop",
        string providerName = "Fake")
    {
        _providerName = providerName;
        _text = text;
        _usage = usage ?? new ModelUsage(1, 2);
        _stopReason = stopReason;
    }

    public string ProviderName => _providerName;

    public ModelRequest? LastRequest { get; private set; }

    public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new ModelResponse(_text, _usage, _stopReason));
    }
}
