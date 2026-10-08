using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliAuthorDraftCommandTests
{
    internal const string ValidMarkdown = """
        # Check a brief

        ## Measured premise
        Observed in `docs/role-capability-matrix.md:1`.

        ## What to build
        Implement the requested slice.

        ## Acceptance criteria
        1. The receipt records the result. Developer owns; Acceptance executes. TEST-VERIFIABLE.
        2. The Developer reports `tests: deferred - ` followed, directly after the hyphen and comma-separated, by every test class it touched or added. The Tester's evidence_request runs them. Developer owns; Acceptance executes. TEST-VERIFIABLE.
        3. The reviewer checks scope. Reviewer owns; Reviewer executes. TEST-VERIFIABLE.

        ## Scope
        Drafting only.
        """;

    [Fact]
    public async Task Passing_draft_is_saved_with_all_checks_main_sha_and_filing_command_without_store_changes()
    {
        using var fixture = new Fixture();
        var intentsPath = Path.Combine(fixture.Workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName);
        Assert.False(File.Exists(intentsPath));
        var before = await fixture.Store.GetByIdPrefixAsync(fixture.Item.Id);
        var databaseBefore = File.ReadAllBytes(fixture.Workspace.BacklogStorePath);

        Assert.Equal(0, fixture.Run(Draft(ValidMarkdown)));

        var draft = Assert.Single(Directory.GetFiles(fixture.Drafts, "*.md"));
        Assert.Equal(ValidMarkdown, File.ReadAllText(draft));
        using var receipt = fixture.Receipt();
        var root = receipt.RootElement;
        Assert.Equal(fixture.Item.Id, root.GetProperty("backlogItemId").GetString());
        Assert.Equal(Fixture.MainSha, root.GetProperty("mainHead").GetString());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(new[] { "sections", "criteria-present", "owner-sentence", "premise-citations", "numbered-criteria", "developer-deferred-criterion", "build-item-count" },
            root.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("name").GetString()).ToArray());
        Assert.All(root.GetProperty("checks").EnumerateArray(), check => Assert.True(check.GetProperty("passed").GetBoolean()));
        Assert.Contains($"goal --brief-file \"{draft}\" --backlog-item {fixture.Item.Id} --backlog-coverage full",
            fixture.Output.ToString());
        Assert.Empty(fixture.Error.ToString());
        Assert.False(File.Exists(fixture.Workspace.SqliteStatePath));
        Assert.False(File.Exists(intentsPath));
        Assert.Equal(databaseBefore, File.ReadAllBytes(fixture.Workspace.BacklogStorePath));
        var after = await fixture.Store.GetByIdPrefixAsync(fixture.Item.Id);
        Assert.Equal(before!.Title, after!.Title);
        Assert.Equal(before.Body, after.Body);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Notes.ToArray(), after.Notes.ToArray());
        Assert.Single(await fixture.Store.ListAsync(includeAll: true));
    }

    [Theory]
    [InlineData("premise-citations", "missing/file.cs")]
    [InlineData("owner-sentence", "The receipt records the result.")]
    [InlineData("criteria-present", "Acceptance criteria")]
    public void Invalid_drafts_are_retained_with_only_the_expected_failed_check(string checkName, string offendingText)
    {
        using var fixture = new Fixture();
        var markdown = checkName switch
        {
            "premise-citations" => ValidMarkdown.Replace("docs/role-capability-matrix.md:1", "missing/file.cs:1"),
            "owner-sentence" => ValidMarkdown.Replace("The receipt records the result. Developer owns; Acceptance executes. TEST-VERIFIABLE.",
                "The receipt records the result."),
            _ => ValidMarkdown[..ValidMarkdown.IndexOf("1. The receipt", StringComparison.Ordinal)] + "\n## Scope\nDrafting only."
        };

        Assert.Equal(1, fixture.Run(Draft(markdown)));

        Assert.Equal(markdown, File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Drafts, "*.md"))));
        using var receipt = fixture.Receipt();
        Assert.Equal(7, receipt.RootElement.GetProperty("checks").GetArrayLength());
        var failed = Assert.Single(receipt.RootElement.GetProperty("checks").EnumerateArray()
            .Where(check => !check.GetProperty("passed").GetBoolean()));
        Assert.Equal(checkName, failed.GetProperty("name").GetString());
        Assert.Contains(offendingText, failed.GetProperty("detail").GetString());
        Assert.Contains($"Failed {checkName}:", fixture.Output.ToString());
        Assert.Contains(offendingText, fixture.Output.ToString());
    }

    [Fact]
    public void Stale_result_writes_only_a_receipt_and_prints_reason_and_preserves_evidence()
    {
        using var fixture = new Fixture();
        var json = JsonSerializer.Serialize(new
        {
            kind = "stale", reason = "The requested capability already exists.",
            evidenceReferences = new[] { "docs/role-capability-matrix.md:1" }
        });

        Assert.Equal(2, fixture.Run(json));

        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.md"));
        using var receipt = fixture.Receipt();
        Assert.Equal("stale", receipt.RootElement.GetProperty("kind").GetString());
        Assert.Equal("The requested capability already exists.", receipt.RootElement.GetProperty("staleReason").GetString());
        Assert.Equal("docs/role-capability-matrix.md:1",
            Assert.Single(receipt.RootElement.GetProperty("evidenceReferences").EnumerateArray()).GetString());
        Assert.Contains("The requested capability already exists.", fixture.Output.ToString());
        Assert.DoesNotContain("goal --brief-file", fixture.Output.ToString());
    }

    [Theory]
    [InlineData("not JSON", 0, "unparseable")]
    [InlineData("{\"kind\":\"draft\",\"markdown\":\"brief\"}", 17, "failed")]
    [InlineData("{\"kind\":\"stale\",\"reason\":\"old\"}", 0, "unparseable")]
    [InlineData("{\"kind\":\"draft\",\"markdown\":\"brief\"} {}", 0, "unparseable")]
    public void Round_failures_never_write_a_draft_and_record_failure(string json, int exitCode, string kind)
    {
        using var fixture = new Fixture();

        Assert.Equal(1, fixture.Run(json, exitCode));

        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.md"));
        using var receipt = fixture.Receipt();
        Assert.Equal(kind, receipt.RootElement.GetProperty("kind").GetString());
        Assert.Equal(exitCode, receipt.RootElement.GetProperty("exitCode").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(receipt.RootElement.GetProperty("failure").GetString()));
        Assert.Contains("Error:", fixture.Error.ToString());
    }

    [Fact]
    public void Round_exception_is_recorded_without_a_draft()
    {
        using var fixture = new Fixture();
        Assert.Equal(1, CliAuthorDraftCommand.Run(["author-draft", fixture.Item.Id], fixture.Workspace,
            new((_, _) => throw new InvalidOperationException("fake launch failure"), fixture.Repository),
            fixture.Output, fixture.Error));
        using var receipt = fixture.Receipt();
        Assert.Contains("fake launch failure", receipt.RootElement.GetProperty("failure").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.RootElement.GetProperty("exitCode").ValueKind);
        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.md"));
    }

    [Fact]
    public void Unknown_item_and_invalid_arguments_fail_before_running_a_model()
    {
        using var fixture = new Fixture();
        var calls = 0;
        var seams = new AuthorBriefDraftSeams((_, _) =>
        {
            calls++;
            return Task.FromResult(new WorkerProcessRunResult(0, Draft(ValidMarkdown), ""));
        }, fixture.Repository);
        foreach (var args in new string[][]
        {
            ["author-draft", "does-not-exist"], ["author-draft"],
            ["author-draft", fixture.Item.Id, "extra"], ["author-draft", fixture.Item.Id, "--unknown"]
        })
            Assert.Equal(1, CliAuthorDraftCommand.Run(args, fixture.Workspace, seams, fixture.Output, fixture.Error));
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(fixture.Drafts));
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["author-draft", "--unknown"]));
    }

    [Theory]
    [InlineData("docs/role-capability-matrix.md:0")]
    [InlineData("docs/role-capability-matrix.md:101")]
    [InlineData("docs/role-capability-matrix.md:9999999999999999999999")]
    public void Out_of_range_premise_lines_fail_with_the_offending_citation(string citation)
    {
        using var fixture = new Fixture();
        Assert.Equal(1, fixture.Run(Draft(ValidMarkdown.Replace("docs/role-capability-matrix.md:1", citation))));
        using var receipt = fixture.Receipt();
        var failed = Assert.Single(receipt.RootElement.GetProperty("checks").EnumerateArray()
            .Where(check => !check.GetProperty("passed").GetBoolean()));
        Assert.Equal("premise-citations", failed.GetProperty("name").GetString());
        Assert.Contains(citation, failed.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("LICENSE:1", null, "file is not tracked")]
    [InlineData("LICENSE:0", 2, "line is outside file")]
    [InlineData("LICENSE:3", 2, "line is outside file")]
    [InlineData("LICENSE:9999999999999999999999", 2, "line is outside file")]
    public void Extensionless_premise_citations_validate_tracking_and_line_bounds(
        string citation, int? trackedLines, string detail)
    {
        using var fixture = new Fixture();
        if (trackedLines is not null) fixture.Repository.TrackedFiles.Add("LICENSE", trackedLines.Value);
        var markdown = ValidMarkdown.Replace("docs/role-capability-matrix.md:1", citation);

        Assert.Equal(1, fixture.Run(Draft(markdown)));

        Assert.Equal(markdown, File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Drafts, "*.md"))));
        using var receipt = fixture.Receipt();
        Assert.Equal(7, receipt.RootElement.GetProperty("checks").GetArrayLength());
        var failed = Assert.Single(receipt.RootElement.GetProperty("checks").EnumerateArray()
            .Where(check => !check.GetProperty("passed").GetBoolean()));
        Assert.Equal("premise-citations", failed.GetProperty("name").GetString());
        Assert.Contains(citation, failed.GetProperty("detail").GetString());
        Assert.Contains(detail, failed.GetProperty("detail").GetString());
        Assert.Contains("Failed premise-citations:", fixture.Output.ToString());
        Assert.Contains(citation, fixture.Output.ToString());
        Assert.Empty(fixture.Error.ToString());
    }

    [Theory]
    [InlineData("LICENSE:1")]
    [InlineData("LICENSE:2")]
    public void Tracked_extensionless_premise_lines_pass(string citation)
    {
        using var fixture = new Fixture();
        fixture.Repository.TrackedFiles.Add("LICENSE", 2);
        var markdown = ValidMarkdown.Replace("docs/role-capability-matrix.md:1", citation);

        Assert.Equal(0, fixture.Run(Draft(markdown)));

        Assert.Equal(markdown, File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Drafts, "*.md"))));
        using var receipt = fixture.Receipt();
        Assert.Equal(7, receipt.RootElement.GetProperty("checks").GetArrayLength());
        Assert.All(receipt.RootElement.GetProperty("checks").EnumerateArray(),
            check => Assert.True(check.GetProperty("passed").GetBoolean()));
        Assert.Empty(fixture.Error.ToString());
    }

    [Fact]
    public void Missing_section_is_reported_and_citations_outside_premise_are_not_checked()
    {
        using var fixture = new Fixture();
        var markdown = ValidMarkdown.Replace("## What to build", "## Implementation") + "\n`missing/outside-premise.cs:1`";
        Assert.Equal(1, fixture.Run(Draft(markdown)));
        using var receipt = fixture.Receipt();
        var failed = Assert.Single(receipt.RootElement.GetProperty("checks").EnumerateArray()
            .Where(check => !check.GetProperty("passed").GetBoolean()));
        Assert.Equal("sections", failed.GetProperty("name").GetString());
        Assert.Contains("What to build", failed.GetProperty("detail").GetString());
    }

    [Fact]
    public void Changed_main_is_a_loud_failure_with_a_receipt()
    {
        using var fixture = new Fixture();
        var heads = 0;
        fixture.Repository.Head = () => heads++ == 0 ? Fixture.MainSha : new string('b', 40);
        Assert.Equal(1, fixture.Run(Draft(ValidMarkdown)));
        using var receipt = fixture.Receipt();
        Assert.Contains("Main HEAD changed", receipt.RootElement.GetProperty("failure").GetString());
        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.md"));
    }

    private static string Draft(string markdown) => JsonSerializer.Serialize(new { kind = "draft", markdown });

    internal sealed class Fixture : IDisposable
    {
        internal const string MainSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "author-draft-" + Guid.NewGuid().ToString("N"));
        internal OrchestratorWorkspace Workspace { get; }
        internal BacklogStore Store { get; }
        internal BacklogItem Item { get; }
        internal FakeRepository Repository { get; } = new();
        internal StringWriter Output { get; } = new();
        internal StringWriter Error { get; } = new();
        internal WorkerProcessRunRequest? Request { get; private set; }
        internal string Drafts => Path.Combine(Workspace.OrchestratorDirectory, "author-drafts");

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Store = new BacklogStore(Workspace.BacklogStorePath);
            Item = Store.AddAsync("Author draft title", "Author draft body").GetAwaiter().GetResult();
            Store.AppendNoteAsync(Item.Id, "Inspect the owning seam").GetAwaiter().GetResult();
        }

        internal int Run(string json, int exitCode = 0) => CliAuthorDraftCommand.Run(
            ["author-draft", Item.Id[..8]], Workspace, new((request, _) =>
            {
                Request = request;
                return Task.FromResult(new WorkerProcessRunResult(exitCode, json, "fake stderr"));
            }, Repository), Output, Error);

        internal JsonDocument Receipt() => JsonDocument.Parse(File.ReadAllText(
            Assert.Single(Directory.GetFiles(Drafts, "*.receipt.json"))));

        public void Dispose()
        {
            Output.Dispose();
            Error.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    internal sealed class FakeRepository : IAuthorBriefDraftRepository
    {
        internal Func<string> Head { get; set; } = () => Fixture.MainSha;
        internal Dictionary<string, int> TrackedFiles { get; } = new(StringComparer.Ordinal)
        {
            ["docs/role-capability-matrix.md"] = 100
        };
        public string ResolveMainHead() => Head();
        public int? TrackedLineCount(string sha, string path)
        {
            Assert.Equal(Fixture.MainSha, sha);
            return TrackedFiles.TryGetValue(path, out var count) ? count : null;
        }
    }
}
