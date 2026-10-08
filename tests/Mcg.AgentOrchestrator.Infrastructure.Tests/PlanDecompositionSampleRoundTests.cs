using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: injected runners and per-test temporary directories only.
public sealed class PlanDecompositionSampleRoundTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "high")]
    public async Task Sampling_reuses_Author_command_and_resolves_its_own_binding(bool bound, string? effort)
    {
        var catalog = bound
            ? ConductorRoundModelResolverTests.Catalog(ModelFunctionPurposes.PlanSampler, effort)
            : ModelFunctionCatalog.Empty;
        WorkerProcessRunRequest? actual = null;
        using var cancellation = new CancellationTokenSource();
        var result = await PlanDecompositionSampleRound.RunAsync("decomposition prompt", "repository root", catalog,
            (request, token) =>
            {
                actual = request;
                Assert.Equal(cancellation.Token, token);
                return Task.FromResult(new WorkerProcessRunResult(0, "sample output", ""));
            }, cancellation.Token);
        WorkerProcessRunRequest? author = null;
        await AuthorBriefDraftRound.DispatchAsync("decomposition prompt", "repository root", (request, _) =>
        {
            author = request;
            return Task.FromResult(new WorkerProcessRunResult(0, "author output", ""));
        }, ConductorRoundModelResolver.Resolve(catalog, ModelFunctionPurposes.PlanSampler));

        Assert.Equal("sample output", Assert.IsType<PlanDecompositionSampleResult.Succeeded>(result).StandardOutput);
        Assert.NotNull(actual);
        Assert.NotNull(author);
        Assert.Equal(author, actual);
        Assert.Contains(bound ? "--model claude-sonnet-5" : $"--model {ConductorRoundModelResolver.DefaultModelAlias}",
            actual.Command);
        if (effort is not null) Assert.Contains("--effort high", actual.Command);
        Assert.Equal("decomposition prompt", actual.StandardInput);
        Assert.Equal("repository root", actual.WorkingDirectory);
    }

    [Fact]
    public async Task Nonzero_exit_keeps_stdout_and_bounds_stderr_to_one_line()
    {
        var stderr = new string('x', 600) + "\r\nplanner auth missing\n";
        var result = await PlanDecompositionSampleRound.RunAsync("prompt", "root", ModelFunctionCatalog.Empty,
            (_, _) => Task.FromResult(new WorkerProcessRunResult(1, "partial stdout", stderr)));

        var failure = Assert.IsType<PlanDecompositionSampleResult.Failed>(result);
        Assert.Equal(1, failure.ExitCode);
        Assert.Equal(500, failure.StandardErrorTail.Length);
        Assert.EndsWith(" planner auth missing", failure.StandardErrorTail);
        Assert.DoesNotContain("\r", failure.StandardErrorTail);
        Assert.DoesNotContain("\n", failure.StandardErrorTail);
        Assert.Contains("partial stdout", failure.RawOutput);
        Assert.Contains(stderr, failure.RawOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronous_and_async_runner_exceptions_become_failure_values(bool asyncFault)
    {
        var exception = new InvalidOperationException("planner failed\r\nboom");
        var result = await PlanDecompositionSampleRound.RunAsync("prompt", "root", ModelFunctionCatalog.Empty,
            (_, _) => asyncFault ? Task.FromException<WorkerProcessRunResult>(exception) : throw exception);

        var failure = Assert.IsType<PlanDecompositionSampleResult.Failed>(result);
        Assert.Null(failure.ExitCode);
        Assert.Equal("InvalidOperationException: planner failed boom", failure.StandardErrorTail);
        Assert.Equal(exception.ToString(), failure.RawOutput);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("other-profile")]
    public async Task Invalid_catalog_is_reported_without_calling_the_runner(string shape)
    {
        var calls = 0;
        var result = await PlanDecompositionSampleRound.RunAsync("prompt", "root",
            ConductorRoundModelResolverTests.InvalidCatalog(ModelFunctionPurposes.PlanSampler, shape), (_, _) =>
            {
                calls++;
                return Task.FromResult(new WorkerProcessRunResult(0, "unexpected", ""));
            });

        Assert.Equal(0, calls);
        var failure = Assert.IsType<PlanDecompositionSampleResult.Failed>(result);
        Assert.Null(failure.ExitCode);
        Assert.StartsWith("ConductorModelRoundException: model-binding-invalid:plan-sampler", failure.StandardErrorTail);
        Assert.Contains("model-binding-invalid:plan-sampler", failure.RawOutput);
    }

    [Fact]
    public void Raw_files_keep_the_last_64_KiB_and_preserve_colliding_runs()
    {
        var directory = Directory.CreateTempSubdirectory("plan-raw-").FullName;
        try
        {
            var stamp = new DateTimeOffset(2026, 10, 8, 17, 50, 12, TimeSpan.Zero);
            var raw = "discarded prefix" + new string('Ω', 40000);
            var first = PlanDecompositionSampleRound.SaveRawOutput(directory, stamp, 2, raw);
            var second = PlanDecompositionSampleRound.SaveRawOutput(directory, stamp, 2, "second run");

            Assert.Null(first.Error);
            Assert.Null(second.Error);
            Assert.NotNull(first.Path);
            Assert.NotNull(second.Path);
            Assert.Equal("20261008T175012Z-sample-2.raw.txt", Path.GetFileName(first.Path));
            Assert.NotEqual(first.Path, second.Path);
            Assert.Equal(Encoding.UTF8.GetBytes(raw)[^65536..], File.ReadAllBytes(first.Path));
            Assert.Equal("second run", File.ReadAllText(second.Path));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Nested_runner_scopes_restore_their_predecessor()
    {
        var original = PlanDecompositionSampleRound.ProcessRunner;
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> outer =
            (_, _) => Task.FromResult(new WorkerProcessRunResult(0, "outer", ""));
        Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> inner =
            (_, _) => Task.FromResult(new WorkerProcessRunResult(0, "inner", ""));
        using (PlanDecompositionSampleRound.PushProcessRunner(outer))
        {
            using (PlanDecompositionSampleRound.PushProcessRunner(inner))
                Assert.Same(inner, PlanDecompositionSampleRound.ProcessRunner);
            Assert.Same(outer, PlanDecompositionSampleRound.ProcessRunner);
        }
        Assert.Equal(original, PlanDecompositionSampleRound.ProcessRunner);
    }
}
