using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fixture owns a unique temporary workspace/database; git and model calls are fake.
public sealed class AuthorBriefDraftRevisionLoopTests
{
    private const string ValidDraft = CliAuthorDraftCommandTests.ValidMarkdown;
    private static string InvalidDraft => ValidDraft.Replace(
        "docs/role-capability-matrix.md:1", "docs/role-capability-matrix.md:101");

    [Fact]
    public void Failed_citation_is_revised_with_verbatim_feedback_and_passes()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var failed = Assert.Single(AuthorBriefDraftChecks.Run(InvalidDraft,
            CliAuthorDraftCommandTests.Fixture.MainSha, fixture.Repository).Where(check => !check.Passed));
        Assert.Equal("premise-citations", failed.Name);
        var requests = new List<WorkerProcessRunRequest>();

        var outcome = Run(fixture, requests, Draft(InvalidDraft), Draft(ValidDraft));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(2, requests.Count);
        Assert.Contains(requests[0].StandardInput!, requests[1].StandardInput!);
        Assert.Contains(InvalidDraft, requests[1].StandardInput!);
        Assert.Contains($"Failed {failed.Name}: {failed.Detail}", requests[1].StandardInput!);
        Assert.Equal(requests[0] with { StandardInput = requests[1].StandardInput }, requests[1]);
        Assert.Equal(ValidDraft, File.ReadAllText(outcome.DraftPath!));
        Assert.DoesNotContain("Failed ", fixture.Output.ToString());
        Assert.Empty(fixture.Error.ToString());
        using var receipt = fixture.Receipt();
        var rounds = Rounds(receipt, 2);
        AssertCheckedRound(rounds[0], 1, "premise-citations");
        AssertCheckedRound(rounds[1], 2);
    }

    [Fact]
    public void Exhausted_budget_prints_only_the_final_drafts_failures()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var finalDraft = InvalidDraft.Replace("docs/role-capability-matrix.md:101", "missing/final.cs:1");
        var requests = new List<WorkerProcessRunRequest>();

        var outcome = Run(fixture, requests, Draft(InvalidDraft), Draft(InvalidDraft), Draft(finalDraft));

        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(3, requests.Count);
        Assert.Equal(finalDraft, File.ReadAllText(outcome.DraftPath!));
        var failed = Assert.Single(outcome.Checks.Where(check => !check.Passed));
        Assert.Equal("premise-citations", failed.Name);
        Assert.Contains("missing/final.cs:1", failed.Detail);
        var printed = fixture.Output.ToString().ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.StartsWith("Failed ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(outcome.Checks.Where(check => !check.Passed)
            .Select(check => $"Failed {check.Name}: {check.Detail}").ToArray(), printed);
        Assert.DoesNotContain("docs/role-capability-matrix.md:101", fixture.Output.ToString());
        Assert.Empty(fixture.Error.ToString());
        using var receipt = fixture.Receipt();
        var rounds = Rounds(receipt, 3);
        for (var index = 0; index < rounds.Length; index++)
            AssertCheckedRound(rounds[index], index + 1, "premise-citations");
    }

    [Fact]
    public void Passing_first_draft_dispatches_once()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var requests = new List<WorkerProcessRunRequest>();

        var outcome = Run(fixture, requests, Draft(ValidDraft));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Single(requests);
        Assert.Equal(ValidDraft, File.ReadAllText(outcome.DraftPath!));
        Assert.NotEmpty(outcome.Checks);
        Assert.All(outcome.Checks, check => Assert.True(check.Passed, check.Detail));
        using var receipt = fixture.Receipt();
        AssertCheckedRound(Assert.Single(Rounds(receipt, 1)), 1);
    }

    [Theory]
    [InlineData("unparseable", "Author did not return one valid draft or stale JSON object.")]
    [InlineData("stale", "Capability already exists.")]
    [InlineData("model-failure", "Author model exited 17: fake model failure")]
    public void Failed_revision_stops_and_retains_previous_draft(string failure, string detail)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var revision = failure switch
        {
            "unparseable" => new WorkerProcessRunResult(0, "not JSON", ""),
            "stale" => new WorkerProcessRunResult(0, JsonSerializer.Serialize(new
            {
                kind = "stale", reason = detail,
                evidenceReferences = new[] { "docs/role-capability-matrix.md:1" }
            }), ""),
            _ => new WorkerProcessRunResult(17, Draft(ValidDraft).StandardOutput, "fake model failure")
        };
        var requests = new List<WorkerProcessRunRequest>();

        var outcome = Run(fixture, requests, Draft(InvalidDraft), revision, Draft(ValidDraft));

        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(2, requests.Count);
        Assert.Equal(InvalidDraft, File.ReadAllText(outcome.DraftPath!));
        Assert.Equal(outcome.DraftPath, Assert.Single(Directory.GetFiles(fixture.Drafts, "*.md")));
        Assert.Equal("premise-citations", Assert.Single(outcome.Checks.Where(check => !check.Passed)).Name);
        using var receipt = fixture.Receipt();
        var rounds = Rounds(receipt, 2);
        AssertCheckedRound(rounds[0], 1, "premise-citations");
        Assert.Equal(2, rounds[1].GetProperty("round").GetInt32());
        Assert.False(rounds[1].GetProperty("checksRun").GetBoolean());
        Assert.Equal(JsonValueKind.Null, rounds[1].GetProperty("failedChecks").ValueKind);
        Assert.Equal(failure, rounds[1].GetProperty("roundFailure").GetString());
        Assert.Equal(detail, rounds[1].GetProperty("roundFailureDetail").GetString());
    }

    [Theory]
    [InlineData("\nFollow-up slices: remaining work", "slice")]
    [InlineData("\n## Follow-up slices\nRemaining work.", "slice")]
    [InlineData("", "full")]
    [InlineData("\nfollow-up slices: remaining work", "full")]
    [InlineData("\n Follow-up slices: remaining work", "full")]
    [InlineData("\n### Follow-up slices", "full")]
    public void Final_draft_selects_command_and_receipt_coverage(string suffix, string coverage)
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var requests = new List<WorkerProcessRunRequest>();
        // Start with the opposite coverage to prove selection uses the revised draft.
        var firstDraft = InvalidDraft + (coverage == "full" ? "\nFollow-up slices: obsolete" : "");
        var finalDraft = ValidDraft + suffix;

        var outcome = Run(fixture, requests, Draft(firstDraft), Draft(finalDraft));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(2, requests.Count);
        Assert.Equal(finalDraft, File.ReadAllText(outcome.DraftPath!));
        Assert.Equal($"goal --brief-file \"{outcome.DraftPath}\" --backlog-item {fixture.Item.Id} --backlog-coverage {coverage}",
            fixture.Output.ToString().ReplaceLineEndings("\n").TrimEnd('\n').Split('\n')[^1]);
        using var receipt = fixture.Receipt();
        Assert.Equal(coverage, receipt.RootElement.GetProperty("backlogCoverage").GetString());
        AssertCheckedRound(Rounds(receipt, 2)[1], 2);
    }

    [Fact]
    public void Blocking_lint_feedback_is_revised_using_the_existing_checks()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var markdown = ValidDraft.Replace("Implement the requested slice.", "Create the .git-keep marker.");
        var finding = Assert.Single(BriefLint.Lint(markdown));
        Assert.Equal(BriefLintSeverity.BlocksDispatch, finding.Severity);
        var requests = new List<WorkerProcessRunRequest>();

        var outcome = Run(fixture, requests, Draft(markdown), Draft(ValidDraft));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(2, requests.Count);
        Assert.Contains($"Failed brief-lint:{finding.Kind}: {finding.Message} remedy: {finding.Remedy}",
            requests[1].StandardInput!);
        using var receipt = fixture.Receipt();
        var rounds = Rounds(receipt, 2);
        AssertCheckedRound(rounds[0], 1, $"brief-lint:{finding.Kind}");
        AssertCheckedRound(rounds[1], 2);
    }

    [Fact]
    public void Main_head_moving_during_revision_preserves_the_existing_failure_path()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        var heads = 0;
        fixture.Repository.Head = () => ++heads <= 2
            ? CliAuthorDraftCommandTests.Fixture.MainSha : new string('b', 40);
        var requests = new List<WorkerProcessRunRequest>();

        var outcome = Run(fixture, requests, Draft(InvalidDraft), Draft(ValidDraft));

        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal(2, requests.Count);
        Assert.Null(outcome.DraftPath);
        Assert.Empty(Directory.GetFiles(fixture.Drafts, "*.md"));
        Assert.Contains("Main HEAD changed during the drafting round", fixture.Error.ToString());
        using var receipt = fixture.Receipt();
        Assert.Contains("Main HEAD changed during the drafting round",
            receipt.RootElement.GetProperty("failure").GetString());
        Assert.Equal(2, receipt.RootElement.GetProperty("roundCount").GetInt32());
    }

    private static WorkerProcessRunResult Draft(string markdown) =>
        new(0, JsonSerializer.Serialize(new { kind = "draft", markdown }), "");

    private static AuthorBriefDraftOutcome Run(CliAuthorDraftCommandTests.Fixture fixture,
        List<WorkerProcessRunRequest> requests, params WorkerProcessRunResult[] results) =>
        AuthorBriefDraftService.Run(fixture.Item.Id, fixture.Workspace, new((request, _) =>
        {
            requests.Add(request);
            // Repeat the last response so an extra dispatch is visible to the count assertions.
            return Task.FromResult(results[Math.Min(requests.Count - 1, results.Length - 1)]);
        }, fixture.Repository), fixture.Output, fixture.Error);

    private static JsonElement[] Rounds(JsonDocument receipt, int count)
    {
        Assert.Equal(count, receipt.RootElement.GetProperty("roundCount").GetInt32());
        var rounds = receipt.RootElement.GetProperty("rounds").EnumerateArray().ToArray();
        Assert.Equal(count, rounds.Length);
        return rounds;
    }

    private static void AssertCheckedRound(JsonElement round, int number, params string[] failures)
    {
        Assert.Equal(number, round.GetProperty("round").GetInt32());
        Assert.True(round.GetProperty("checksRun").GetBoolean());
        Assert.Equal(failures, round.GetProperty("failedChecks").EnumerateArray()
            .Select(check => check.GetString()).ToArray());
        Assert.Equal(JsonValueKind.Null, round.GetProperty("roundFailure").ValueKind);
        Assert.Equal(JsonValueKind.Null, round.GetProperty("roundFailureDetail").ValueKind);
    }
}
