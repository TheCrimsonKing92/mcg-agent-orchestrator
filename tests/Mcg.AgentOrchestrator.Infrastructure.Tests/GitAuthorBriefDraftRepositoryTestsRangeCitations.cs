using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns a unique temporary Git repository and its configuration.
public sealed class GitAuthorBriefDraftRepositoryTestsRangeCitations
{
    private const string Ledger = "src/Mcg.AgentOrchestrator.Core/Reports/WorkerRoundLedger.cs";
    private const string Reports = "src/Mcg.AgentOrchestrator.Core/Reports";
    private const string Notes = "docs/architecture-notes.md";

    [Fact]
    public void File_ranges_and_single_lines_resolve_with_original_failure_text()
    {
        using var fixture = new RepositoryFixture();
        Assert.Equal(106, fixture.Repository.TrackedLineCount(fixture.Head, Ledger));
        foreach (var span in new[] { Ledger + ":13-30", "./" + Ledger + ":13-30", Ledger + ":29" })
            Assert.True(fixture.Check($"- `{span}`").Passed, span);
        foreach (var suffix in new[] { ":100-107", ":30-13", ":0-5", ":9999999999999999999999-30", ":1-9999999999999999999999" })
        {
            var span = Ledger + suffix;
            var result = fixture.Check($"- `{span}`");
            Assert.False(result.Passed);
            Assert.Equal($"{span}: line is outside file (line count 106)", result.Detail);
        }
    }

    [Fact]
    public void Continuations_use_the_preceding_checked_path_and_preserve_their_text()
    {
        using var fixture = new RepositoryFixture();
        Assert.Equal(203, fixture.Repository.TrackedLineCount(fixture.Head, Notes));
        Assert.True(fixture.Check($"- `{Notes}:21`, `symbol`, `:190`").Passed);
        Assert.True(fixture.Check($"- `{Notes}:21`, `:70-77`, `:190`").Passed);
        var outside = fixture.Check($"- `{Notes}:21`, `:204`");
        Assert.False(outside.Passed);
        Assert.Equal(":204: line is outside file (line count 203)", outside.Detail);
        var orphan = fixture.Check("- `symbol`, `:190`");
        Assert.False(orphan.Passed);
        Assert.Equal(":190: continuation has no preceding file citation", orphan.Detail);

        var immediate = fixture.Check($"- `{Notes}:21`, `{Ledger}:29`, `:107`");
        Assert.False(immediate.Passed);
        Assert.Equal(":107: line is outside file (line count 106)", immediate.Detail);
    }

    [Fact]
    public void Tracked_directories_pass_only_without_line_suffixes()
    {
        using var fixture = new RepositoryFixture();
        Assert.Equal("tree", fixture.Git("cat-file", "-t", $"{fixture.Head}:{Reports}"));
        Assert.True(fixture.Check($"- `{Reports}`").Passed);
        foreach (var span in new[] { "src/Mcg.AgentOrchestrator.Core/Report", Reports + ":3" })
        {
            var result = fixture.Check($"- `{span}`");
            Assert.False(result.Passed);
            Assert.Equal($"{span}: file is not tracked at {fixture.Head}", result.Detail);
        }
        Assert.True(fixture.Repository.IsTrackedDirectory(fixture.Head, Reports));
        foreach (var path in new[] { Ledger, "src/Mcg.AgentOrchestrator.Core/Report", Reports + ":3",
                     "src/../docs", "", ".", "..", "./docs", "src//Feature", "src\\Feature", fixture.Root })
            Assert.False(fixture.Repository.IsTrackedDirectory(fixture.Head, path), path);
    }

    [Fact]
    public void Bare_names_fail_and_extensionless_root_file_citations_pass()
    {
        using var fixture = new RepositoryFixture();
        foreach (var span in new[] { "WorkerRoundLedger.cs:29", "state.db" })
        {
            var result = fixture.Check($"- `{span}`");
            Assert.False(result.Passed);
            Assert.Equal($"{span}: file is not tracked at {fixture.Head}", result.Detail);
        }
        Assert.Equal(2, fixture.Repository.TrackedLineCount(fixture.Head, "LICENSE"));
        Assert.True(fixture.Check("- `LICENSE:1`").Passed);
        Assert.True(fixture.Check("- `./LICENSE`").Passed);
    }

    [Fact]
    public void First_draft_layout_reports_exactly_the_two_bare_file_names()
    {
        using var fixture = new RepositoryFixture();
        const string premise = """
            - `WorkerRoundRecord` carries usage fields (`src/Mcg.AgentOrchestrator.Core/Reports/WorkerRoundLedger.cs:13-30`). `UsageReported` is set at `WorkerRoundLedger.cs:29`.
            - `RoundValueReport.Build` aggregates one window (`src/Mcg.AgentOrchestrator.Core/Reports/RoundValueReport.cs:18-30`, `:70-77`), counted separately at `RoundValueReport.cs:76-77`. The `round-value` command takes `--since` (`src/Mcg.AgentOrchestrator.App/Cli/CliRoundValueQueryCommand.cs:9-30`).
            - A read of `src/Mcg.AgentOrchestrator.Core/Reports` and `src/Mcg.AgentOrchestrator.App/Cli` found no comparison report.
            - Candidate SHA lives on obligations (`src/Mcg.AgentOrchestrator.Core/Domain/CriterionEvidenceObligation.cs:43-75`).
            - The audit (`docs/triage/system-architecture-audit-2026-09-06.md`) measured coverage.
            - Slot planning sits at `src/Mcg.AgentOrchestrator.Core/Application/ParallelExecutionPlanner.cs:117-125`.
            - Identity slice `bbf6fe7c` is recorded (`docs/architecture-notes.md:21`, `:190`).
            """;
        var checks = AuthorBriefDraftChecks.Run(Draft(premise), fixture.Head, fixture.Repository);
        Assert.All(checks.Where(check => check.Name != "premise-citations"), check => Assert.True(check.Passed, check.Detail));
        var result = Assert.Single(checks, check => check.Name == "premise-citations");
        Assert.False(result.Passed);
        Assert.Equal($"WorkerRoundLedger.cs:29: file is not tracked at {fixture.Head} | RoundValueReport.cs:76-77: file is not tracked at {fixture.Head}", result.Detail);
    }

    private static string Draft(string premise) => $$"""
        # Citation fixture
        ## Measured premise
        {{premise}}
        ## What to build
        Check repository citations.
        ## Acceptance criteria
        1. Citations resolve. Developer owns; Acceptance executes. TEST-VERIFIABLE.
        2. The Developer reports `tests: deferred - ` followed, directly after the hyphen and comma-separated, by every test class it touched or added. The Tester's evidence_request runs them. Developer owns; Acceptance executes. TEST-VERIFIABLE.
        ## Scope
        Repository citations.
        """;

    private sealed class RepositoryFixture : IDisposable
    {
        internal string Root { get; }
        internal string Head { get; }
        internal GitAuthorBriefDraftRepository Repository { get; }

        internal RepositoryFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mcg-author-ranges-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Git("init", "-q");
            Root = Git("rev-parse", "--show-toplevel");
            Git("config", "user.email", "tests@example.com");
            Git("config", "user.name", "Author Range Tests");
            Git("config", "commit.gpgsign", "false");
            var files = new Dictionary<string, int>
            {
                [Ledger] = 106,
                [Reports + "/RoundValueReport.cs"] = 78,
                ["src/Mcg.AgentOrchestrator.App/Cli/CliRoundValueQueryCommand.cs"] = 108,
                ["src/Mcg.AgentOrchestrator.Core/Domain/CriterionEvidenceObligation.cs"] = 93,
                ["src/Mcg.AgentOrchestrator.Core/Application/ParallelExecutionPlanner.cs"] = 310,
                ["docs/triage/system-architecture-audit-2026-09-06.md"] = 247,
                [Notes] = 203,
                ["LICENSE"] = 2
            };
            foreach (var file in files)
            {
                var path = Path.Combine(Root, file.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Join('\n', Enumerable.Range(1, file.Value).Select(line => $"line {line}")) + "\n");
            }
            Git("add", ".");
            Git("commit", "-q", "-m", "Citation files");
            Git("checkout", "-q", "-B", "main");
            Head = Git("rev-parse", "HEAD");
            Repository = new(Root);
            Assert.Equal(Head, Repository.ResolveMainHead());
        }

        internal AuthorBriefDraftCheck Check(string premise) => Assert.Single(
            AuthorBriefDraftChecks.Run(Draft(premise), Head, Repository), check => check.Name == "premise-citations");

        internal string Git(params string[] arguments)
        {
            var result = GitCli.Run(Root, arguments);
            Assert.True(result.Succeeded && !result.DrainTimedOut,
                $"git {string.Join(' ', arguments)} failed: exit={result.ExitCode}; stderr={result.Error}");
            return result.Output.Trim();
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
