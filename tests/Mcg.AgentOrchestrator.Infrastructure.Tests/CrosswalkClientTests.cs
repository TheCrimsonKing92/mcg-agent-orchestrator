using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every case owns its files and injects environment and process boundaries.
public sealed class CrosswalkClientTests : IDisposable
{
    private readonly DirectoryInfo _sandbox = Directory.CreateTempSubdirectory("crosswalk-client-'");
    private const string GoJson = """{"stack":"go","evidence":[{"path":"go.mod","reason":"found go marker"}]}""";

    [Theory]
    [InlineData(GoJson, Toolchain.Go, "go.mod", "found go marker", null)]
    [InlineData("{\n  \"stack\": \"go\",\n  \"evidence\": [\n    {\"path\": \"go.mod\", \"reason\": \"found go marker\"}\n  ]\n}",
        Toolchain.Go, "go.mod", "found go marker", null)]
    [InlineData("""{"stack":"node","evidence":[{"path":"package.json","reason":"found node marker"}],"testFramework":"vitest"}""",
        Toolchain.Node, "package.json", "found node marker", "vitest")]
    [InlineData("""{"stack":"unknown","evidence":[]}""", Toolchain.Unknown, null, null, null)]
    [InlineData("""{"stack":"dotnet","evidence":[],"extra":true}""", Toolchain.Dotnet, null, null, null)]
    [InlineData("""{"stack":"python","evidence":[]}""", Toolchain.Python, null, null, null)]
    public void TryParse_ValidShape_ReturnsTypedEvidence(
        string json, Toolchain stack, string? path, string? reason, string? framework)
    {
        Assert.True(CrosswalkDetectResult.TryParse(json, out var parsed));
        var result = Assert.IsType<CrosswalkDetectResult>(parsed);
        Assert.Equal(stack, result.Stack);
        Assert.Equal(framework, result.TestFramework);
        if (path is null)
            Assert.Empty(result.EvidenceItems);
        else
        {
            var evidence = Assert.Single(result.EvidenceItems);
            Assert.Equal(path, evidence.Path);
            Assert.Equal(reason, evidence.Reason);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"evidence":[]}""")]
    [InlineData("""{"stack":"go"}""")]
    [InlineData("""{"stack":"rust","evidence":[]}""")]
    [InlineData("""{"stack":"Go","evidence":[]}""")]
    [InlineData("""{"stack":"go","evidence":[{"path":1,"reason":"x"}]}""")]
    [InlineData("""{"stack":"go","evidence":[],"testFramework":3}""")]
    [InlineData("""{"stack":null,"evidence":[]}""")]
    [InlineData("""{"stack":"go","evidence":null}""")]
    [InlineData("""{"stack":"go","evidence":[null]}""")]
    [InlineData("""{"stack":"go","evidence":[{"path":"go.mod"}]}""")]
    [InlineData("""{"stack":"go","evidence":[{"path":"go.mod","reason":false}]}""")]
    [InlineData("""{"stack":"go","evidence":[],"testFramework":null}""")]
    public void TryParse_InvalidShape_ReturnsFalseWithoutResult(string? json)
    {
        Assert.False(CrosswalkDetectResult.TryParse(json, out var result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(null, "not-configured")]
    [InlineData("", "not-configured")]
    [InlineData("   ", "not-configured")]
    [InlineData("missing.exe", "missing")]
    public void Detect_UnavailablePath_DoesNotStartRunner(string? configured, string status)
    {
        var calls = 0;
        string? variable = null;
        var client = new CrosswalkClient(name =>
        {
            variable = name;
            return configured == "missing.exe" ? Path.Combine(_sandbox.FullName, configured) : configured;
        }, (_, _) => { calls++; return Task.FromResult(new WorkerProcessRunResult(0, GoJson, "")); });

        Assert.Equal(new CrosswalkDetectOutcome(status, null, ""), client.Detect(_sandbox.FullName));
        Assert.Equal(CrosswalkClient.PathVariable, variable);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Detect_ConfiguredPath_QuotesCommandAndUsesDefaultRequest()
    {
        var path = CreateExecutable();
        var root = Directory.CreateDirectory(Path.Combine(_sandbox.FullName, "root's folder")).FullName;
        WorkerProcessRunRequest? request = null;
        var calls = 0;
        var client = new CrosswalkClient(_ => path, (value, token) =>
        {
            Interlocked.Increment(ref calls);
            request = value;
            Assert.Equal(CancellationToken.None, token);
            return Task.FromResult(new WorkerProcessRunResult(0, GoJson, ""));
        });

        var outcome = client.Detect(root);

        Assert.Equal(CrosswalkDetectOutcome.Detected, outcome.Status);
        Assert.Equal(Toolchain.Go, Assert.IsType<CrosswalkDetectResult>(outcome.Result).Stack);
        Assert.Equal(1, calls);
        Assert.NotNull(request);
        Assert.Equal("& '" + path.Replace("'", "''") + "' detect '" + root.Replace("'", "''") +
            "'; exit $LASTEXITCODE", request.Command);
        Assert.Equal(root, request.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(5), CrosswalkClient.DefaultTimeout);
        Assert.Equal(CrosswalkClient.DefaultTimeout, request.Timeout);
        Assert.Null(request.StandardInput);
    }

    [Theory]
    [InlineData("timeout", false, "")]
    [InlineData("timeout", true, "")]
    [InlineData("start-failed", false, "InvalidOperationException")]
    [InlineData("start-failed", true, "InvalidOperationException")]
    [InlineData("exit-nonzero", false, "exit=2")]
    [InlineData("invalid-json", false, "")]
    public void Detect_RunnerFailure_ReturnsTypedOutcome(string status, bool faultedTask, string detail)
    {
        var path = CreateExecutable();
        var calls = 0;
        var timeout = TimeSpan.FromSeconds(17);
        WorkerProcessRunRequest? request = null;
        var client = new CrosswalkClient(_ => path, (value, _) =>
        {
            calls++;
            request = value;
            Exception? failure = status switch
            {
                "timeout" => new OperationCanceledException(),
                "start-failed" => new InvalidOperationException(),
                _ => null
            };
            if (failure is not null)
                return faultedTask ? Task.FromException<WorkerProcessRunResult>(failure) : throw failure;
            return Task.FromResult(new WorkerProcessRunResult(status == "exit-nonzero" ? 2 : 0, "garbage", ""));
        }, timeout);

        Assert.Equal(new CrosswalkDetectOutcome(status, null, detail), client.Detect(_sandbox.FullName));
        Assert.Equal(1, calls);
        Assert.Equal(timeout, Assert.IsType<WorkerProcessRunRequest>(request).Timeout);
    }

    [Fact]
    public void Detect_EnvironmentReaderThrows_ReturnsStartFailed()
    {
        var calls = 0;
        var client = new CrosswalkClient(_ => throw new InvalidOperationException(), (_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(0, GoJson, ""));
        });

        Assert.Equal(new CrosswalkDetectOutcome("start-failed", null, "InvalidOperationException"),
            client.Detect(_sandbox.FullName));
        Assert.Equal(0, calls);
    }

    private string CreateExecutable()
    {
        var path = Path.Combine(_sandbox.FullName, "crosswalk's tool.exe");
        File.WriteAllText(path, "");
        return path;
    }

    public void Dispose() => _sandbox.Delete(recursive: true);
}
