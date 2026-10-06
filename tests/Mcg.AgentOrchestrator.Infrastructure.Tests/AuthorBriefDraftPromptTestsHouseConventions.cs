using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the command fixture owns its files, stores and process seam.
public sealed class AuthorBriefDraftPromptTestsHouseConventions
{
    [Fact]
    public void Command_prompt_requires_house_form_and_first_slice_scope()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        Assert.Equal(0, fixture.Run(JsonSerializer.Serialize(new
        {
            kind = "draft", markdown = CliAuthorDraftCommandTests.ValidMarkdown
        })));
        var request = Assert.IsType<WorkerProcessRunRequest>(fixture.Request);
        var prompt = request.StandardInput!.ReplaceLineEndings("\n");
        const string plannerSection = """
            ## Planner output format, read this first

            Begin each mapping line with the bare criterion number and a period, exactly `N. maps to <subject>. disposition=planned; plan=<text>` (the semicolon after the disposition value is required). Only `planned` and `undecidable` are accepted. The literal text `disposition=` must appear only on mapping lines. Only mapping lines may begin with a digit and a period. Cite files by full repository-relative path that exists at HEAD, or mark them as new; never cite a wildcard pattern. Do not use the words placeholder, TBD or TODO. The text after plan= must be a non-empty one-sentence summary on the same line; detail bullets may follow but must not begin with a digit and a period.
            """;
        Assert.Equal(plannerSection.ReplaceLineEndings("\n"), AuthorBriefDraftPrompt.PlannerFormatSection.ReplaceLineEndings("\n"));
        Assert.Contains(AuthorBriefDraftPrompt.PlannerFormatSection.ReplaceLineEndings("\n"), prompt);
        Assert.Contains("Directly after the title heading, place the following Planner format section, copied character for character:\n" +
            plannerSection.ReplaceLineEndings("\n"), prompt);
        Assert.Contains("Number acceptance criteria `1.`, `2.` and so on, with no bulleted criteria and no nested bullets.", prompt);
        Assert.Contains("The last two acceptance criteria are the following house Developer deferred-tests criterion, copied character for character, followed by one single-sentence Reviewer criterion:\n" +
            "The Developer reports `tests: deferred - ` followed, directly after the hyphen and comma-separated, by every test class it touched or added. The Tester's evidence_request runs them. Developer owns; Acceptance executes. TEST-VERIFIABLE.", prompt);
        Assert.Contains("Keep What to build to at most four numbered items.", prompt);
        Assert.Contains("When the backlog item needs more, draft only the first slice and list the remaining slices under Scope in a line beginning `Follow-up slices:`.", prompt);
        Assert.DoesNotContain("numbered or bulleted", prompt);
    }
}
