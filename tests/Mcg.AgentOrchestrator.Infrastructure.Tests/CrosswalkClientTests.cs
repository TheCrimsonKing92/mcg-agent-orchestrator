using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: every case owns its files and injects environment and process boundaries.
public sealed class CrosswalkClientTests : IDisposable
{
    private readonly DirectoryInfo _sandbox = Directory.CreateTempSubdirectory("crosswalk-client-'");
    private const string GoJson = """{"stack":"go","evidence":[{"path":"go.mod","reason":"found go marker"}]}""";
    private const string ListJson = """
        [{"id":"first","name":"adds numbers","file":"math.test.ts"},
         {"id":"second","name":"subtracts numbers","file":"math.test.ts"}]
        """;
    private const string RunJson = """
        {"reportFormat":"CTRF","schemaVersion":"mcg-test-result/1","results":{
          "tool":{"name":"vitest"},"tests":[
            {"testId":"first","name":"adds numbers","status":"passed","duration":12,
             "filePath":"math.test.ts","suite":["math"]},
            {"testId":"second","name":"subtracts numbers","status":"failed",
             "message":"expected 1","trace":"assertion trace"}]}}
        """;

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

    [Fact]
    public void List_ConfiguredPath_ReturnsTypedRefsAndQuotesRequest()
    {
        var path = CreateExecutable();
        var root = Path.Combine(_sandbox.FullName, "root's folder");
        var timeout = TimeSpan.FromSeconds(17);
        WorkerProcessRunRequest? request = null;
        var calls = 0;
        var client = new CrosswalkClient(_ => path, (value, token) =>
        {
            calls++;
            request = value;
            Assert.Equal(CancellationToken.None, token);
            return Task.FromResult(new WorkerProcessRunResult(0, ListJson, "warning"));
        }, timeout);

        var outcome = client.List(root);

        Assert.Equal("detected", outcome.Status);
        Assert.Equal("", outcome.Detail);
        Assert.Equal(new[]
        {
            new CrosswalkTestRef("first", "adds numbers", "math.test.ts"),
            new CrosswalkTestRef("second", "subtracts numbers", "math.test.ts")
        }, outcome.Result);
        Assert.Equal(1, calls);
        Assert.NotNull(request);
        Assert.Equal("& '" + path.Replace("'", "''") + "' list '" + root.Replace("'", "''") +
            "'; exit $LASTEXITCODE", request.Command);
        Assert.Equal(root, request.WorkingDirectory);
        Assert.Equal(timeout, request.Timeout);
        Assert.Null(request.StandardInput);
    }

    [Fact]
    public void Run_RequestedIds_QuotesEachIdAndUsesExplicitTimeout()
    {
        var path = CreateExecutable();
        var root = Path.Combine(_sandbox.FullName, "root's folder");
        var timeout = TimeSpan.FromSeconds(71);
        WorkerProcessRunRequest? request = null;
        var calls = 0;
        var client = new CrosswalkClient(_ => path, (value, token) =>
        {
            calls++;
            request = value;
            Assert.Equal(CancellationToken.None, token);
            return Task.FromResult(new WorkerProcessRunResult(0, RunJson, "warning"));
        }, TimeSpan.FromSeconds(17));

        var outcome = client.Run(root, ["it's quoted", "with space", "", "with space"], timeout);

        Assert.Equal("detected", outcome.Status);
        Assert.Equal("", outcome.Detail);
        Assert.Collection(outcome.Result, first =>
        {
            Assert.Equal("first", first.Id);
            Assert.Equal("adds numbers", first.DisplayName);
            Assert.Equal(TestResultStatus.Passed, first.Status);
            Assert.Equal("math.test.ts", first.File);
            Assert.Equal(new[] { "math" }, first.SuitePath);
            Assert.Equal(12L, first.DurationMs);
            Assert.Equal("vitest", first.ReportedBy);
        }, second =>
        {
            Assert.Equal("second", second.Id);
            Assert.Equal("subtracts numbers", second.DisplayName);
            Assert.Equal(TestResultStatus.Failed, second.Status);
            Assert.Equal("expected 1", second.Message);
            Assert.Equal("assertion trace", second.Trace);
            Assert.Equal("vitest", second.ReportedBy);
        });
        Assert.Equal(1, calls);
        Assert.NotNull(request);
        Assert.Equal("& '" + path.Replace("'", "''") + "' run '" + root.Replace("'", "''") +
            "' --id 'it''s quoted' --id 'with space' --id '' --id 'with space'; exit $LASTEXITCODE", request.Command);
        Assert.Equal(root, request.WorkingDirectory);
        Assert.Equal(timeout, request.Timeout);
        Assert.Null(request.StandardInput);
    }

    [Fact]
    public void Run_EmptyIds_OmitsIdArguments()
    {
        var path = CreateExecutable();
        WorkerProcessRunRequest? request = null;
        var client = new CrosswalkClient(_ => path, (value, _) =>
        {
            request = value;
            return Task.FromResult(new WorkerProcessRunResult(0, RunJson, ""));
        });

        var outcome = client.Run(_sandbox.FullName, [], TimeSpan.FromMinutes(1));

        Assert.Equal("detected", outcome.Status);
        Assert.Equal(2, outcome.Result.Count);
        Assert.Equal("& '" + path.Replace("'", "''") + "' run '" + _sandbox.FullName.Replace("'", "''") +
            "'; exit $LASTEXITCODE", Assert.IsType<WorkerProcessRunRequest>(request).Command);
    }

    [Theory]
    [InlineData(null, "not-configured")]
    [InlineData("", "not-configured")]
    [InlineData("   ", "not-configured")]
    [InlineData("missing.exe", "missing")]
    public void ListAndRun_UnavailablePath_ReturnEmptyWithoutStartingRunner(string? configured, string status)
    {
        var calls = 0;
        var variables = new List<string>();
        var client = new CrosswalkClient(name =>
        {
            variables.Add(name);
            return configured == "missing.exe" ? Path.Combine(_sandbox.FullName, configured) : configured;
        }, (_, _) => { calls++; return Task.FromResult(new WorkerProcessRunResult(0, "", "")); });

        var list = client.List(_sandbox.FullName);
        var run = client.Run(_sandbox.FullName, [], TimeSpan.FromMinutes(1));

        Assert.Equal(status, list.Status);
        Assert.Equal(status, run.Status);
        Assert.Empty(list.Result);
        Assert.Empty(run.Result);
        Assert.Equal("", list.Detail);
        Assert.Equal("", run.Detail);
        Assert.Equal(new[] { CrosswalkClient.PathVariable, CrosswalkClient.PathVariable }, variables);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("timeout", false, "")]
    [InlineData("timeout", true, "")]
    [InlineData("start-failed", false, "InvalidOperationException")]
    [InlineData("start-failed", true, "InvalidOperationException")]
    [InlineData("invalid-json", false, "")]
    public void ListAndRun_RunnerFailure_ReturnMatchingStatusAndEmptyResults(
        string status, bool faultedTask, string detail)
    {
        var path = CreateExecutable();
        var requests = new List<WorkerProcessRunRequest>();
        var listTimeout = TimeSpan.FromSeconds(17);
        var runTimeout = TimeSpan.FromSeconds(31);
        var client = new CrosswalkClient(_ => path, (request, _) =>
        {
            requests.Add(request);
            Exception? failure = status switch
            {
                "timeout" => new OperationCanceledException(),
                "start-failed" => new InvalidOperationException(),
                _ => null
            };
            if (failure is not null)
                return faultedTask ? Task.FromException<WorkerProcessRunResult>(failure) : throw failure;
            return Task.FromResult(new WorkerProcessRunResult(0, "garbage", ""));
        }, listTimeout);

        var list = client.List(_sandbox.FullName);
        var run = client.Run(_sandbox.FullName, [], runTimeout);

        Assert.Equal(status, list.Status);
        Assert.Equal(status, run.Status);
        Assert.Equal(detail, list.Detail);
        Assert.Equal(detail, run.Detail);
        Assert.Empty(list.Result);
        Assert.Empty(run.Result);
        Assert.Collection(requests, request => Assert.Equal(listTimeout, request.Timeout),
            request => Assert.Equal(runTimeout, request.Timeout));
    }

    [Theory]
    [InlineData(2, "exit-2", "usage error\nunsupported stack\n")]
    [InlineData(7, "exit-nonzero", "exit=7")]
    public void ListAndRun_NonzeroExit_ClassifiesBeforeParsingAndPreservesDetail(int exit, string status, string detail)
    {
        var path = CreateExecutable();
        var calls = 0;
        var client = new CrosswalkClient(_ => path, (_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(exit, "not JSON", "usage error\nunsupported stack\n"));
        });

        var list = client.List(_sandbox.FullName);
        var run = client.Run(_sandbox.FullName, [], TimeSpan.FromMinutes(1));

        Assert.Equal(status, list.Status);
        Assert.Equal(status, run.Status);
        Assert.Equal(detail, list.Detail);
        Assert.Equal(detail, run.Detail);
        Assert.Empty(list.Result);
        Assert.Empty(run.Result);
        if (exit == 2)
        {
            Assert.NotEqual(CrosswalkDetectOutcome.ExitNonZero, list.Status);
            Assert.NotEqual(CrosswalkDetectOutcome.ExitNonZero, run.Status);
        }
        Assert.Equal(new CrosswalkDetectOutcome("exit-nonzero", null, $"exit={exit}"), client.Detect(_sandbox.FullName));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void ListAndRun_EnvironmentReaderThrows_ReturnStartFailedWithoutStartingRunner()
    {
        var calls = 0;
        var client = new CrosswalkClient(_ => throw new InvalidOperationException(), (_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(0, "", ""));
        });

        var list = client.List(_sandbox.FullName);
        var run = client.Run(_sandbox.FullName, [], TimeSpan.FromMinutes(1));

        Assert.Equal("start-failed", list.Status);
        Assert.Equal("start-failed", run.Status);
        Assert.Equal("InvalidOperationException", list.Detail);
        Assert.Equal("InvalidOperationException", run.Detail);
        Assert.Empty(list.Result);
        Assert.Empty(run.Result);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("""[{"id":"first","name":"valid","file":"test.ts"},{}]""")]
    [InlineData("""[{"id":null,"name":"test","file":"test.ts"}]""")]
    [InlineData("""[{"id":"id","name":42,"file":"test.ts"}]""")]
    [InlineData("""[{"id":"id","name":"test","file":false}]""")]
    [InlineData("""[{"id":"id","name":"test"}]""")]
    [InlineData("prefix []")]
    [InlineData("[] trailing")]
    [InlineData("[] []")]
    public void List_InvalidShape_ReturnsInvalidJsonWithoutPartialRefs(string json)
    {
        var path = CreateExecutable();
        var client = new CrosswalkClient(_ => path,
            (_, _) => Task.FromResult(new WorkerProcessRunResult(0, json, "")));

        var outcome = client.List(_sandbox.FullName);

        Assert.Equal("invalid-json", outcome.Status);
        Assert.Empty(outcome.Result);
    }

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("""[{"id":"","name":"","file":"","extra":true},{"id":"","name":"","file":""}]""", 2)]
    public void List_EmptyAndDuplicateRefs_PreservesAllowedValues(string json, int count)
    {
        var path = CreateExecutable();
        var client = new CrosswalkClient(_ => path,
            (_, _) => Task.FromResult(new WorkerProcessRunResult(0, json, "")));

        var outcome = client.List(_sandbox.FullName);

        Assert.Equal("detected", outcome.Status);
        Assert.Equal(count, outcome.Result.Count);
        Assert.All(outcome.Result, item => Assert.Equal(new CrosswalkTestRef("", "", ""), item));
    }

    [Theory]
    [InlineData("prefix ", "")]
    [InlineData("", " trailing")]
    [InlineData("", " {}")]
    public void Run_ExtraStdout_ReturnsInvalidJson(string prefix, string suffix)
    {
        var path = CreateExecutable();
        var client = new CrosswalkClient(_ => path,
            (_, _) => Task.FromResult(new WorkerProcessRunResult(0, prefix + RunJson + suffix, "")));

        var outcome = client.Run(_sandbox.FullName, [], TimeSpan.FromMinutes(1));

        Assert.Equal("invalid-json", outcome.Status);
        Assert.Empty(outcome.Result);
    }

    [Theory]
    [InlineData("""
        {"reportFormat":"CTRF","schemaVersion":"mcg-test-result/1","results":{
        "tool":{"name":"vitest"},"tests":[{"testId":"same","name":"first","status":"passed"},
        {"testId":"same","name":"second","status":"failed"}]}}
        """)]
    [InlineData("""
        {"reportFormat":"CTRF","results":{"tool":{"name":"vitest"},
        "tests":[{"testId":"first","name":"first","status":"passed"}]}}
        """)]
    public void Run_InvalidNormalizedRecord_ReturnsInvalidJsonWithoutPartialRecords(string json)
    {
        var path = CreateExecutable();
        var client = new CrosswalkClient(_ => path,
            (_, _) => Task.FromResult(new WorkerProcessRunResult(0, json, "")));

        var outcome = client.Run(_sandbox.FullName, [], TimeSpan.FromMinutes(1));

        Assert.Equal("invalid-json", outcome.Status);
        Assert.Empty(outcome.Result);
    }

    [Theory]
    [InlineData(0, false, false, null)]
    [InlineData(7, false, false, CrosswalkDiagnosticCategory.NonzeroExit)]
    [InlineData(2, false, false, CrosswalkDiagnosticCategory.UnsupportedAdapter)]
    [InlineData(0, true, false, CrosswalkDiagnosticCategory.Timeout)]
    [InlineData(0, false, true, CrosswalkDiagnosticCategory.CaptureLimited)]
    public async Task RunAsync_RetainsResultsAndInvocationFacts(int exit, bool timedOut, bool limited,
        CrosswalkDiagnosticCategory? category)
    {
        using var owner = new CancellationTokenSource();
        var client = new CrosswalkClient(_ => CreateExecutable(), (_, token) =>
        {
            Assert.Equal(owner.Token, token);
            return Task.FromResult(new WorkerProcessRunResult(exit, RunJson, "stderr receipt", timedOut, limited));
        });
        var outcome = await client.RunAsync(_sandbox.FullName, ["first"], TimeSpan.FromSeconds(30), owner.Token);
        Assert.Equal(2, outcome.Result.Count);
        Assert.Equal(exit, outcome.ExitCode);
        Assert.Equal("stderr receipt", outcome.StandardError);
        if (category is { } expected) Assert.Equal(expected, outcome.Diagnostics[0].Category);
        else Assert.Empty(outcome.Diagnostics);
    }

    [Theory]
    [InlineData("bad json", CrosswalkDiagnosticCategory.InvalidJson)]
    [InlineData("{}", CrosswalkDiagnosticCategory.MalformedResults)]
    public async Task RunAsync_SeparatesParsingFromPrimaryInvocationFailure(string json, CrosswalkDiagnosticCategory secondary)
    {
        var client = new CrosswalkClient(_ => CreateExecutable(), (_, _) =>
            Task.FromResult(new WorkerProcessRunResult(2, json, "unsupported adapter")));
        var outcome = await client.RunAsync(_sandbox.FullName, [], TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Equal("exit-2", outcome.Status);
        Assert.Equal(CrosswalkDiagnosticCategory.UnsupportedAdapter, outcome.Diagnostics[0].Category);
        Assert.Equal(secondary, outcome.Diagnostics[1].Category);
        Assert.Equal("unsupported adapter", outcome.StandardError);
    }

    private string CreateExecutable()
    {
        var path = Path.Combine(_sandbox.FullName, "crosswalk's tool.exe");
        File.WriteAllText(path, "");
        return path;
    }

    public void Dispose() => _sandbox.Delete(recursive: true);
}
