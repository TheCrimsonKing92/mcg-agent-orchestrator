global using static InfrastructureTestSupport;

using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

internal static class InfrastructureTestSupport
{
public static ModelRequest TestRequest(ModelOptions? options = null)
{
    return new ModelRequest(
        "system prompt",
        [new ModelMessage("user", "do work")],
        options ?? new ModelOptions(Temperature: 0.2, MaxOutputTokens: 123));
}

public static IEnumerable<string> GetRequiredHeader(HttpRequestMessage request, string name)
{
    return request.Headers.TryGetValues(name, out var values)
        ? values
        : throw new InvalidOperationException($"Header '{name}' was not found.");
}

public static string CreateTempDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "mcg-orchestrator-tests", Guid.NewGuid().ToString("n"));
    Directory.CreateDirectory(path);
    return path;
}

public static Process StartPrototypeDashboardProcess(string appProject, string workingDirectory, string url)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    // Pin Ollama to an unreachable endpoint so assertions are deterministic
    // regardless of whether a live Ollama server runs on this machine.
    startInfo.EnvironmentVariables["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";

    // Pin provider credentials and model names so spawned-app assertions are
    // machine-independent regardless of what keys or models the host has set.
    startInfo.EnvironmentVariables["OPENAI_API_KEY"] = "test-openai-key";
    startInfo.EnvironmentVariables["ANTHROPIC_API_KEY"] = "test-anthropic-key";
    startInfo.EnvironmentVariables["OPENAI_MODEL"] = "test-openai-model";
    startInfo.EnvironmentVariables["ANTHROPIC_MODEL"] = "test-anthropic-model";
    startInfo.EnvironmentVariables["OLLAMA_MODEL"] = "test-ollama-model";

    startInfo.ArgumentList.Add("run");
    startInfo.ArgumentList.Add("--no-build");
    startInfo.ArgumentList.Add("--project");
    startInfo.ArgumentList.Add(appProject);
    startInfo.ArgumentList.Add("--");
    startInfo.ArgumentList.Add("prototype-ui");
    startInfo.ArgumentList.Add(url);
    startInfo.ArgumentList.Add("--no-open");

    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start prototype dashboard process.");
}

public static async Task WaitForHealthAsync(HttpClient client, string url, Process process)
{
    var health = new Uri(new Uri(url), "health");
    var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
    Exception? lastError = null;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (process.HasExited)
        {
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Dashboard exited early with code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }

        try
        {
            var response = await client.GetAsync(health);
            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        await Task.Delay(200);
    }

    throw new TimeoutException($"Dashboard health endpoint did not respond at {health}. Last error: {lastError?.Message}");
}

public static int GetAvailablePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

public static string FindRepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Mcg.AgentOrchestrator.sln")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate repository root.");
}

}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    public static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected condition to be true.");
        }
    }

    public static void False(bool condition)
    {
        if (condition)
        {
            throw new InvalidOperationException("Expected condition to be false.");
        }
    }

    public static void Contains(string value, Func<string, bool> predicate)
    {
        if (!predicate(value))
        {
            throw new InvalidOperationException("Expected matching text was not found.");
        }
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }
}

internal sealed class CapturingHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

    public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return _responseFactory(request);
    }
}

internal sealed class FakeSmokeProvider : IModelProvider
{
    private readonly string _providerName;
    private readonly string _text;
    private readonly ModelUsage? _usage;
    private readonly string _stopReason;

    public FakeSmokeProvider(string text = "OK", ModelUsage? usage = null, string stopReason = "stop", string providerName = "Fake")
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




