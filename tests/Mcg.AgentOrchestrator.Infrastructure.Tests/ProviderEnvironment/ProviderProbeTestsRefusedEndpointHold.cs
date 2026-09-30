using System.Net;
using System.Net.Sockets;
using Xunit;

[Collection("ProviderEnvironment")]
public sealed class ProviderProbeTestsRefusedEndpointHold
{
    [Fact]
    public void HeldEndpoint_RefusesConnectAndBlocksCompetingBind()
    {
        using var endpoint = ReserveRefusingLoopbackEndpoint();
        using var competing = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        Assert.Equal($"http://127.0.0.1:{endpoint.Port}", endpoint.BaseUrl);

        var bindFailure = Assert.Throws<SocketException>(
            () => competing.Bind(new IPEndPoint(IPAddress.Loopback, endpoint.Port)));
        Assert.Equal(SocketError.AddressAlreadyInUse, bindFailure.SocketErrorCode);
        Assert.True(endpoint.IsHeld);

        var connectFailure = Assert.Throws<SocketException>(() => client.Connect(IPAddress.Loopback, endpoint.Port));
        Assert.Equal(SocketError.ConnectionRefused, connectFailure.SocketErrorCode);
        Assert.True(endpoint.IsHeld, "Endpoint port was released during the refused connection.");
    }

    [Fact]
    public void DisposedEndpoint_ReleasesPort()
    {
        using var endpoint = ReserveRefusingLoopbackEndpoint();
        var port = endpoint.Port;
        Assert.True(endpoint.IsHeld);

        endpoint.Dispose();

        Assert.False(endpoint.IsHeld);
        using var replacement = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        replacement.Bind(new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(replacement.IsBound);
        Assert.Equal(port, ((IPEndPoint)replacement.LocalEndPoint!).Port);
    }
}
