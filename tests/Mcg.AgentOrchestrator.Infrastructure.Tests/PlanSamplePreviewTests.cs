using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: AsyncLocal console/runner scopes and a private workspace per test.
public sealed class PlanSamplePreviewTests
{
    private const string Direction = "Implement two disjoint feature slices";
    private const string TwoNodePlan = """
        ```json
        [{"id":"g1","objective":"Implement feature A.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureA/A.cs","dependsOn":[]},{"id":"g2","objective":"Implement feature B.\n\nTarget files/scopes:\nScope confidence: precise\nIncludes:\n- src/FeatureB/B.cs","dependsOn":[]}]
        ```
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_uses_subscription_stdin_and_the_workspace_catalog(bool bound)
    {
        using var fixture = new Fixture();
        var catalog = bound
            ? new ModelFunctionCatalog([
                ConductorRoundModelResolverTests.Binding(ModelFunctionPurposes.PlanSampler, effort: "high"),
                ConductorRoundModelResolverTests.Binding(ModelFunctionPurposes.ConductorAuthor, "author-decoy")])
            : ModelFunctionCatalog.Empty;
        if (bound) ModelFunctionCatalogStore.Save(fixture.Workspace.ModelFunctionCatalogPath, catalog);
        var requests = new List<WorkerProcessRunRequest>();
        WorkerProcessRunRequest? author = null;
        await AuthorBriefDraftRound.DispatchAsync("author prompt", fixture.Workspace.ExecutionDirectory, (request, _) =>
        {
            author = request;
            return Task.FromResult(new WorkerProcessRunResult(0, "", ""));
        }, ConductorRoundModelResolver.Resolve(catalog, ModelFunctionPurposes.PlanSampler));

        var output = fixture.Run((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new WorkerProcessRunResult(0, TwoNodePlan, ""));
        });

        Assert.NotNull(author);
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal(author.Command, request.Command);
            Assert.Equal(author.Timeout, request.Timeout);
            Assert.Contains(bound ? "--model claude-sonnet-5 --effort high"
                : $"--model {ConductorRoundModelResolver.DefaultModelAlias}", request.Command);
            Assert.Equal(fixture.Workspace.ExecutionDirectory, request.WorkingDirectory);
            Assert.NotNull(request.StandardInput);
            Assert.StartsWith($"Goal: {Direction}{Environment.NewLine}Task: ", request.StandardInput);
            Assert.Contains(GoalDagDecompositionPlanner.BuildPrompt(Direction, true), request.StandardInput);
        });
        Assert.Contains("Dormant slice-batch intake preview: 2 child node(s)", output);
        Assert.Contains("Confirm dormant intake: plan <direction> --slice-batch --confirm-plan", output);
        Assert.DoesNotContain("Validation errors:", output);
        Assert.Empty(SampleLines(output));
        Assert.False(Directory.Exists(fixture.RawDirectory));
        Assert.Empty(fixture.Kernel.Goals);
    }

    [Fact]
    public void Failed_exits_print_each_reason_save_raw_files_and_keep_validation()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var output = fixture.Run((_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(1, "partial stdout", "planner auth missing"));
        });

        Assert.Equal(3, calls);
        var lines = SampleLines(output);
        Assert.Equal(3, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            Assert.StartsWith($"Sample {index + 1}: exit 1: planner auth missing ", lines[index]);
            var raw = RawPath(lines[index]);
            Assert.Equal(fixture.RawDirectory, Path.GetDirectoryName(raw));
            Assert.True(File.Exists(raw));
            Assert.Contains("partial stdout", File.ReadAllText(raw));
            Assert.Contains("planner auth missing", File.ReadAllText(raw));
        }
        Assert.Equal(3, lines.Select(RawPath).Distinct().Count());
        Assert.Equal(3, Directory.GetFiles(fixture.RawDirectory, "*.raw.txt").Length);
        AssertFinalValidation(output);
        Assert.Empty(fixture.Kernel.Goals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unfenced_output_and_exceptions_get_one_diagnostic_per_sample(bool throws)
    {
        using var fixture = new Fixture();
        var output = fixture.Run((_, _) => throws
            ? Task.FromException<WorkerProcessRunResult>(new InvalidOperationException("boom"))
            : Task.FromResult(new WorkerProcessRunResult(0, "unfenced output", "")));

        var lines = SampleLines(output);
        Assert.Equal(3, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            Assert.StartsWith($"Sample {index + 1}: ", lines[index]);
            Assert.Contains(throws ? "exit none: InvalidOperationException: boom" : "no fenced JSON block", lines[index]);
            var raw = RawPath(lines[index]);
            Assert.True(File.Exists(raw));
            Assert.Contains(throws ? "InvalidOperationException: boom" : "unfenced output", File.ReadAllText(raw));
        }
        AssertFinalValidation(output);
    }

    [Fact]
    public void A_failed_sample_does_not_hide_a_later_valid_plan()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var output = fixture.Run((_, _) => Task.FromResult(++calls == 1
            ? new WorkerProcessRunResult(1, TwoNodePlan, "first sample failed")
            : new WorkerProcessRunResult(0, TwoNodePlan, "")));

        Assert.Equal(3, calls);
        Assert.StartsWith("Sample 1: exit 1: first sample failed", Assert.Single(SampleLines(output)));
        Assert.Contains("Dormant slice-batch intake preview: 2 child node(s)", output);
        Assert.DoesNotContain("Validation errors:", output);
        Assert.Single(Directory.GetFiles(fixture.RawDirectory, "*.raw.txt"));
    }

    [Fact]
    public void Raw_file_failure_preserves_the_sample_diagnosis_and_validation()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Workspace.OrchestratorDirectory);
        File.WriteAllText(fixture.RawDirectory, "directory blocked by a file");
        var output = fixture.Run((_, _) => Task.FromResult(new WorkerProcessRunResult(1, "", "planner auth missing")));

        var lines = SampleLines(output);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Contains("exit 1: planner auth missing", line);
            Assert.Contains("raw output: unsaved (", line);
        });
        AssertFinalValidation(output);
    }

    private static void AssertFinalValidation(string output)
    {
        Assert.Contains("Validation errors:", output);
        Assert.Contains("  ERROR: Worker output did not contain a fenced JSON block.", output);
        Assert.Contains("Slice-batch plan must contain 2-4 nodes; found 0", output);
        Assert.Contains("Fix the direction and re-run to preview before confirming.", output);
    }

    private static string[] SampleLines(string output) => output.Split(Environment.NewLine)
        .Where(line => line.StartsWith("Sample ", StringComparison.Ordinal)).ToArray();

    private static string RawPath(string line)
    {
        const string marker = "(raw output: ";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "Sample diagnostic must name its raw-output file.");
        return line[(start + marker.Length)..^1];
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("plan-preview-").FullName;
        internal OrchestratorWorkspace Workspace { get; }
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal string RawDirectory => Path.Combine(Workspace.OrchestratorDirectory, "plan-samples");

        internal Fixture() => Workspace = OrchestratorWorkspace.ForDirectory(_root);

        internal string Run(Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>> runner)
        {
            using var samples = PlanDecompositionSampleRound.PushProcessRunner(runner);
            IReadOnlyList<AgentDefinition> agents = [];
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            // An empty API registry also makes accidental API dispatch fail these success assertions.
            var providers = new InMemoryModelProviderRegistry([]);
            return CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
                ["plan", Direction, "--slice-batch"], Kernel, Workspace, ref agents,
                providers, ref profiles, ref currentGoal));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
