using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Net;
using System.Net.Sockets;
using System.Text;

[Xunit.Collection("ProviderEnvironment")]
public sealed class ProviderProbeTests
{
    [Xunit.Fact(DisplayName = "Provider_registry_probe_uses_openai_models_endpoint")]
    public async Task ProviderRegistryProbeUsesOpenAiModelsEndpoint()
    {
        await using var server = await ProbeServer.StartAsync(HttpStatusCode.OK, "{\"data\":[{\"id\":\"loaded-model\"}]}");

        var reachable = ProviderRegistryFactory.IsOllamaReachable(server.BaseUrl);

        Assert.True(reachable);
        Assert.Equal($"GET {OllamaDefaults.OpenAiModelsPath} HTTP/1.1", await server.RequestLine);
    }

    [Xunit.Fact(DisplayName = "Provider_registry_treats_empty_model_list_as_reachable")]
    public async Task ProviderRegistryTreatsEmptyModelListAsReachable()
    {
        await using var server = await ProbeServer.StartAsync(HttpStatusCode.OK, "{\"data\":[]}");

        using var _ = new EnvironmentVariableScope(("OLLAMA_BASE_URL", server.BaseUrl), ("OLLAMA_MODEL", "llama-server-model"));

        var provider = ProviderRegistryFactory.CreateDefaultProviders().GetRequired("Ollama");

        Assert.IsType<ChatCompletionsModelProvider>(provider);
        Assert.Equal($"GET {OllamaDefaults.OpenAiModelsPath} HTTP/1.1", await server.RequestLine);
    }

    [Xunit.Fact(DisplayName = "Provider_registry_falls_back_when_models_probe_returns_4xx")]
    public async Task ProviderRegistryFallsBackWhenModelsProbeReturns4xx()
    {
        await using var server = await ProbeServer.StartAsync(HttpStatusCode.NotFound, "{\"error\":\"not found\"}");

        using var _ = new EnvironmentVariableScope(("OLLAMA_BASE_URL", server.BaseUrl), ("OLLAMA_MODEL", "llama-server-model"));

        var provider = ProviderRegistryFactory.CreateDefaultProviders().GetRequired("Ollama");

        Assert.IsType<ScriptedModelProvider>(provider);
        Assert.Equal($"GET {OllamaDefaults.OpenAiModelsPath} HTTP/1.1", await server.RequestLine);
    }

    [Xunit.Fact(DisplayName = "Provider_registry_falls_back_when_models_probe_connection_is_refused")]
    public void ProviderRegistryFallsBackWhenModelsProbeConnectionIsRefused()
    {
        var baseUrl = $"http://127.0.0.1:{GetAvailablePort()}";

        using var _ = new EnvironmentVariableScope(("OLLAMA_BASE_URL", baseUrl), ("OLLAMA_MODEL", "llama-server-model"));

        var provider = ProviderRegistryFactory.CreateDefaultProviders().GetRequired("Ollama");

        Assert.IsType<ScriptedModelProvider>(provider);
    }

    [Xunit.Fact(DisplayName = "Provider_smoke_runner_accepts_openai_compatible_models_probe")]
    public async Task ProviderSmokeRunnerAcceptsOpenAiCompatibleModelsProbe()
    {
        await using var server = await ProbeServer.StartAsync(HttpStatusCode.OK, "{\"data\":[]}");

        using var _ = new EnvironmentVariableScope(("OLLAMA_BASE_URL", server.BaseUrl), ("OLLAMA_MODEL", "llama-server-model"));

        var created = ProviderSmokeRunner.TryCreateLiveProvider("ollama", out var provider, out var modelName, out var detail);

        Assert.True(created);
        Assert.IsType<ChatCompletionsModelProvider>(provider);
        Assert.Equal("llama-server-model", modelName);
        Assert.Equal(string.Empty, detail);
        Assert.Equal($"GET {OllamaDefaults.OpenAiModelsPath} HTTP/1.1", await server.RequestLine);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly List<(string Name, string? Previous)> _previous = [];

        public EnvironmentVariableScope(params (string Name, string? Value)[] values)
        {
            foreach (var (name, value) in values)
            {
                _previous.Add((name, Environment.GetEnvironmentVariable(name)));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, previous) in _previous)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }

    private sealed class ProbeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<string> _requestLine;
        private readonly CancellationTokenSource _cancellation = new();

        private ProbeServer(TcpListener listener, Task<string> requestLine)
        {
            _listener = listener;
            _requestLine = requestLine;
        }

        public string BaseUrl { get; private init; } = string.Empty;

        public Task<string> RequestLine => _requestLine;

        public static Task<ProbeServer> StartAsync(HttpStatusCode status, string body)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var requestLine = ServeOnceAsync(listener, status, body);
            return Task.FromResult(new ProbeServer(listener, requestLine)
            {
                BaseUrl = $"http://127.0.0.1:{port}"
            });
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                await _requestLine.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Test assertions read RequestLine for expected requests; disposal only cleans up unused servers.
            }

            _cancellation.Dispose();
        }

        private static async Task<string> ServeOnceAsync(TcpListener listener, HttpStatusCode status, string body)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream);
            var responseBody = Encoding.UTF8.GetBytes(body);
            var reason = status == HttpStatusCode.OK ? "OK" : "Not Found";
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(int)status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {responseBody.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(responseBody);
            return request.Split("\r\n", StringSplitOptions.None)[0];
        }

        private static async Task<string> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[1024];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            return request.ToString();
        }
    }
}
