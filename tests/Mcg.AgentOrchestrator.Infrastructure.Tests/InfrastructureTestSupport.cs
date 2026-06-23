global using static InfrastructureTestSupport;

using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

internal static class InfrastructureTestSupport
{
public static string CaptureConsole(Action action) => AsyncLocalConsoleRouter.Capture(action);

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

public static string BacklogStorePathFor(string root) =>
    Path.Combine(root, ".orchestrator", "backlog.db");

// Seeds the SQLite backlog store under <root> from a "## Heading\nbody" markdown string, so tests can
// keep their existing fixtures while the planners read from the store instead of a BACKLOG.md file.
public static void SeedBacklog(string root, string markdown)
{
    var store = new BacklogStore(BacklogStorePathFor(root));
    string? title = null;
    var body = new System.Text.StringBuilder();
    void Flush()
    {
        if (title is not null)
        {
            store.AddAsync(title, body.ToString().Trim()).GetAwaiter().GetResult();
        }
    }

    foreach (var raw in markdown.Split('\n'))
    {
        var line = raw.TrimEnd('\r');
        if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            Flush();
            title = line[3..].Trim();
            body.Clear();
        }
        else if (title is not null && !line.StartsWith("# ", StringComparison.Ordinal))
        {
            body.AppendLine(line);
        }
    }

    Flush();
}

public static Process StartPrototypeDashboardProcess(string appProject, string workingDirectory, string url) =>
    StartDashboardProcess(appProject, workingDirectory, "prototype-ui", url);

public static Process StartDashboardProcess(string appProject, string workingDirectory, string command, string url)
{
    var appAssembly = Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll");
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
    startInfo.EnvironmentVariables[OrchestratorWorkspace.RepoRootEnvironmentVariable] = workingDirectory;

    // Spawned-app dispatch starts must never launch real subscription CLIs:
    // codex/claude authenticate from account state, not the API keys pinned
    // below, so without this flag auto-handoff tests burn real usage and edit
    // real worktrees under the repository root.
    startInfo.EnvironmentVariables[BackgroundDispatchRunner.DisableDispatchStartVariable] = "1";

    // Pin provider credentials and model names so spawned-app assertions are
    // machine-independent regardless of what keys or models the host has set.
    startInfo.EnvironmentVariables["OPENAI_API_KEY"] = "test-openai-key";
    startInfo.EnvironmentVariables["ANTHROPIC_API_KEY"] = "test-anthropic-key";
    startInfo.EnvironmentVariables["OPENAI_MODEL"] = "test-openai-model";
    startInfo.EnvironmentVariables["ANTHROPIC_MODEL"] = "test-anthropic-model";
    startInfo.EnvironmentVariables["OLLAMA_MODEL"] = "test-ollama-model";

    startInfo.ArgumentList.Add(appAssembly);
    startInfo.ArgumentList.Add(command);
    startInfo.ArgumentList.Add(url);
    startInfo.ArgumentList.Add("--no-open");

    var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start prototype dashboard process.");
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return process;
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
            throw new InvalidOperationException($"Dashboard exited early with code {process.ExitCode}.");
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
    var candidates = new[]
    {
        Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable),
        Environment.CurrentDirectory,
        AppContext.BaseDirectory
    };

    foreach (var candidate in candidates)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            continue;
        }

        var directory = new DirectoryInfo(Path.GetFullPath(candidate));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }
    }

    throw new DirectoryNotFoundException("Could not locate repository root.");
}

}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
        => Xunit.Assert.Equal(expected, actual);

    public static void NotEqual<T>(T expected, T actual)
        => Xunit.Assert.NotEqual(expected, actual);

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

    // Delegating overloads so unqualified Assert.* in tests binds here instead of being shadowed away
    // from Xunit.Assert. The bespoke Contains(string, predicate) helper above stays; standard
    // collection/membership/type assertions forward to xUnit so they compile without Xunit.Assert prefixes.
    public static void Contains<T>(IEnumerable<T> collection, Predicate<T> filter)
        => Xunit.Assert.Contains(collection, filter);

    public static void Contains(string expectedSubstring, string actualString)
        => Xunit.Assert.Contains(expectedSubstring, actualString);

    public static void DoesNotContain<T>(IEnumerable<T> collection, Predicate<T> filter)
        => Xunit.Assert.DoesNotContain(collection, filter);

    public static void DoesNotContain(string expectedSubstring, string actualString)
        => Xunit.Assert.DoesNotContain(expectedSubstring, actualString);

    public static void Empty(System.Collections.IEnumerable collection)
        => Xunit.Assert.Empty(collection);

    public static void NotEmpty(System.Collections.IEnumerable collection)
        => Xunit.Assert.NotEmpty(collection);

    public static T Single<T>(IEnumerable<T> collection)
        => Xunit.Assert.Single(collection);

    public static void NotNull(object? @object)
        => Xunit.Assert.NotNull(@object);

    public static void Null(object? @object)
        => Xunit.Assert.Null(@object);

    public static T IsType<T>(object @object)
        => Xunit.Assert.IsType<T>(@object);

    public static void InRange<T>(T actual, T low, T high)
        where T : IComparable
        => Xunit.Assert.InRange(actual, low, high);

    // Faithful delegates to Xunit.Assert for standard overloads this shadow previously lacked (af94964c):
    // value-in-collection membership, collection equality, per-element inspection, and string prefix/
    // suffix. These forward to xUnit so workers' standard assertions compile and behave identically,
    // removing the overload-resolution friction without rewriting ~80 existing bespoke call sites.
    public static void Contains<T>(T expected, IEnumerable<T> collection)
        => Xunit.Assert.Contains(expected, collection);

    public static void Contains<T>(T expected, IEnumerable<T> collection, IEqualityComparer<T> comparer)
        => Xunit.Assert.Contains(expected, collection, comparer);

    public static void DoesNotContain<T>(T expected, IEnumerable<T> collection)
        => Xunit.Assert.DoesNotContain(expected, collection);

    public static void Equal<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        => Xunit.Assert.Equal(expected, actual);

    // Array args otherwise bind to the reference-equality Equal<T>(T,T) overload (T=T[]); this more-specific
    // array overload routes them to Xunit's element-wise comparison.
    public static void Equal<T>(T[] expected, T[] actual)
        => Xunit.Assert.Equal(expected, actual);

    public static void Collection<T>(IEnumerable<T> collection, params Action<T>[] elementInspectors)
        => Xunit.Assert.Collection(collection, elementInspectors);

    public static void StartsWith(string expectedStartString, string? actualString)
        => Xunit.Assert.StartsWith(expectedStartString, actualString);

    public static void EndsWith(string expectedEndString, string? actualString)
        => Xunit.Assert.EndsWith(expectedEndString, actualString);

    public static void StartsWith(string expectedStartString, string? actualString, StringComparison comparisonType)
        => Xunit.Assert.StartsWith(expectedStartString, actualString, comparisonType);

    public static void DoesNotContain(string expectedSubstring, string actualString, StringComparison comparisonType)
        => Xunit.Assert.DoesNotContain(expectedSubstring, actualString, comparisonType);

    public static void True(bool condition, string userMessage)
        => Xunit.Assert.True(condition, userMessage);
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




