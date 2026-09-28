using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorStewardModelRoundBundleTests
{
    [Xunit.Fact]
    public async Task Prompt_contains_bounded_failure_lines_and_tracked_stem_matches()
    {
        using var temp = TempDirectory.Create();
        WorkerProcessRunRequest? request = null;
        var lister = new FixedFiles();
        var round = new ClaudeConductorStewardModelRound(Path.Combine(temp.Path, "receipts"),
            (value, _) =>
            {
                request = value;
                return Task.FromResult(new WorkerProcessRunResult(0, "adjudication", ""));
            }, lister, (_, path) => path is "src/Present.cs" or "AGENTS.md");
        var trigger = Trigger("Stdout plan reason: rejected citation 'src/FooMissing.cs'\n" +
            "Assert.Equal() Failure: Expected: src/Present.cs | Actual: src/FooMissing.cs",
            "files: tests/FooMissingTests.cs, AGENTS.md");

        await round.DispatchAsync(trigger, temp.Path, CancellationToken.None);

        Xunit.Assert.NotNull(request);
        var bundle = Bundle(request!.StandardInput!);
        Xunit.Assert.StartsWith(ConductorStewardEvidenceBundle.Heading, bundle);
        Xunit.Assert.Contains("Stdout plan reason: rejected citation", bundle);
        Xunit.Assert.Contains("Assert.Equal() Failure:", bundle);
        Xunit.Assert.Contains("src/Present.cs: exists", bundle);
        Xunit.Assert.Contains("AGENTS.md: exists", bundle);
        Xunit.Assert.Contains("src/FooMissing.cs: missing", bundle);
        Xunit.Assert.Contains("src/FooMissingVariant.cs", bundle);
        Xunit.Assert.Contains("Candidate commit: candidate-sha", bundle);
        Xunit.Assert.Contains("FooMissing", lister.Stems);
        Xunit.Assert.Equal(temp.Path, lister.Worktree);
    }

    [Xunit.Fact]
    public async Task Bundle_is_capped_even_when_trigger_contains_many_failure_lines()
    {
        using var temp = TempDirectory.Create();
        WorkerProcessRunRequest? request = null;
        var round = new ClaudeConductorStewardModelRound(Path.Combine(temp.Path, "receipts"),
            (value, _) =>
            {
                request = value;
                return Task.FromResult(new WorkerProcessRunResult(0, "adjudication", ""));
            }, new FixedFiles(), (_, _) => true);
        var evidence = string.Join("\n", Enumerable.Repeat("Assertion failure: " + new string('x', 1000), 500));

        await round.DispatchAsync(Trigger(evidence), temp.Path, CancellationToken.None);

        var bundle = Bundle(request!.StandardInput!);
        Xunit.Assert.True(bundle.Length < 4500, $"Bundle length was {bundle.Length}.");
        Xunit.Assert.Contains("[failure lines truncated]", bundle);
    }

    [Xunit.Fact]
    public async Task File_lookup_failure_is_reported_without_aborting_round()
    {
        using var temp = TempDirectory.Create();
        WorkerProcessRunRequest? request = null;
        var round = new ClaudeConductorStewardModelRound(Path.Combine(temp.Path, "receipts"),
            (value, _) =>
            {
                request = value;
                return Task.FromResult(new WorkerProcessRunResult(0, "adjudication", ""));
            }, new ThrowingFiles(), (_, _) => false);

        var output = await round.DispatchAsync(Trigger("Failure in src/FooMissing.cs"),
            temp.Path, CancellationToken.None);

        Xunit.Assert.Equal("adjudication", output);
        Xunit.Assert.Contains("tracked-file lookup failed: IOException", Bundle(request!.StandardInput!));
    }

    private static string Bundle(string prompt)
    {
        var start = prompt.IndexOf(ConductorStewardEvidenceBundle.Heading, StringComparison.Ordinal);
        Xunit.Assert.True(start >= 0);
        var end = prompt.IndexOf("Current refined acceptance criteria:", start, StringComparison.Ordinal);
        Xunit.Assert.True(end > start);
        return prompt[start..end].Trim();
    }

    private static ConductorStewardTrigger Trigger(string evidence, string workerResult = "") => new(
        "goal", "task", "candidate-sha", ConductorStewardTriggerKind.PlannerOutputContractRejected,
        DateTimeOffset.UtcNow, evidence, workerResult, [], ["criterion"]);

    private sealed class FixedFiles : IConductorStewardTrackedFileLister
    {
        internal List<string> Stems { get; } = [];
        internal string? Worktree { get; private set; }
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem)
        {
            Worktree = worktree;
            Stems.Add(stem);
            return ["src/FooMissingVariant.cs", "tests/FooMissingTests.cs"];
        }
    }

    private sealed class ThrowingFiles : IConductorStewardTrackedFileLister
    {
        public IReadOnlyList<string> MatchingFiles(string worktree, string stem) =>
            throw new IOException("git unavailable");
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path) => Path = path;
        internal string Path { get; }
        internal static TempDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "mcg-steward-bundle-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
