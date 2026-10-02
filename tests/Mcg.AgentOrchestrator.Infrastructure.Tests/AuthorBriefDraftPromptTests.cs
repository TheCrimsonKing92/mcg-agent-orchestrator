using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class AuthorBriefDraftPromptTests
{
    [Fact]
    public async Task Command_prompt_carries_backlog_annotations_lessons_rules_and_clarification_launcher_settings()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        using var lessons = new ConductorLessonTestStore();
        lessons.Add("brief-lesson", ["author:brief"], rule: "Prove every premise at HEAD");
        lessons.Add("general-lesson", ["author"], rule: "Name the evidence owner");
        lessons.Add("unrelated", ["steward"], rule: "Do not select this lesson");
        File.Copy(lessons.Path, fixture.Workspace.OperatorLessonsStorePath);

        Assert.Equal(0, fixture.Run(JsonSerializer.Serialize(new
        {
            kind = "draft", markdown = CliAuthorDraftCommandTests.ValidMarkdown
        })));
        var request = Assert.IsType<WorkerProcessRunRequest>(fixture.Request);
        var prompt = request.StandardInput!;
        Assert.Contains(fixture.Item.Title, prompt);
        Assert.Contains(fixture.Item.Body, prompt);
        Assert.Contains("Inspect the owning seam", prompt);
        Assert.Contains(CliAuthorDraftCommandTests.Fixture.MainSha, prompt);
        foreach (var section in new[] { "Measured premise", "What to build", "Acceptance criteria", "Scope" })
            Assert.Contains("## " + section, prompt);
        Assert.Contains("X owns; Y executes.", prompt);
        Assert.Contains("TEST-VERIFIABLE", prompt);
        Assert.Contains("REAL-WORLD-DEPENDENT", prompt);
        Assert.Contains("docs/role-capability-matrix.md", prompt);
        Assert.Contains("docs/worker-guidance-discipline.md", prompt);
        Assert.Contains("Prove every premise at HEAD", prompt);
        Assert.Contains("Name the evidence owner", prompt);
        Assert.DoesNotContain("Do not select this lesson", prompt);
        Assert.Contains("evidenceReferences", prompt);
        Assert.Contains("path:line", prompt);

        WorkerProcessRunRequest? clarificationRequest = null;
        var round = new ClaudeConductorAuthorModelRound(Path.Combine(fixture.Workspace.OrchestratorDirectory, "oracle-rounds"),
            (input, _) =>
            {
                clarificationRequest = input;
                return Task.FromResult(new WorkerProcessRunResult(0, "{}", ""));
            });
        await round.DispatchAsync(new ConductorAuthorRoundInput(
            new ConductorAuthorItem(OperatorAnswerTargetKind.Clarification, "item", "goal", "question", "brief"),
            "brief", "{}", null), fixture.Workspace.ExecutionDirectory, CancellationToken.None);
        var oracle = Assert.IsType<WorkerProcessRunRequest>(clarificationRequest);
        Assert.Equal(oracle.Command, request.Command);
        Assert.Contains("--permission-mode plan", request.Command);
        Assert.Equal(oracle.Timeout, request.Timeout);
        Assert.Equal(oracle.WorkingDirectory, request.WorkingDirectory);
    }
}
