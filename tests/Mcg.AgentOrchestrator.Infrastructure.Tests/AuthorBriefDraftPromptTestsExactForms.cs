using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: the command fixture owns its files, stores and process seam.
public sealed class AuthorBriefDraftPromptTestsExactForms
{
    [Fact]
    public void Command_prompt_lists_exact_owner_forms_and_every_lint_word()
    {
        using var fixture = new CliAuthorDraftCommandTests.Fixture();
        Assert.Equal(0, fixture.Run(JsonSerializer.Serialize(new
        {
            kind = "draft", markdown = CliAuthorDraftCommandTests.ValidMarkdown
        })));
        var request = Assert.IsType<WorkerProcessRunRequest>(fixture.Request);
        var lines = request.StandardInput!.ReplaceLineEndings("\n").Split('\n');
        var ownerIndex = Array.IndexOf(lines, "followed by TEST-VERIFIABLE or REAL-WORLD-DEPENDENT.");
        Assert.True(ownerIndex >= 0, "The existing verification-class owner line must be present.");
        Assert.Equal("End EVERY acceptance criterion with an owner sentence of the form \"X owns; Y executes.\"",
            lines[ownerIndex - 1]);
        string[] ownerLines =
        [
            "The owner sentence is the last sentence of its criterion and is exactly one of these four lines, copied character for character with nothing after it:",
            "Developer owns; Acceptance executes. TEST-VERIFIABLE.",
            "Tester owns; Acceptance executes. TEST-VERIFIABLE.",
            "Reviewer owns; Reviewer executes. TEST-VERIFIABLE.",
            "Operator owns; Operator executes. REAL-WORLD-DEPENDENT."
        ];
        Assert.Equal(ownerLines, lines.Skip(ownerIndex + 1).Take(ownerLines.Length));
        var lintLine = "Brief lint blocks a draft that contains any of these words anywhere, including code spans, quoted evidence and file paths, so never write them: " +
            string.Join(", ", GoalReadinessPreflight.HighRiskSignalWords) + ".";
        Assert.Equal(lintLine, lines[ownerIndex + 1 + ownerLines.Length]);
        Assert.Equal(15, GoalReadinessPreflight.HighRiskSignalWords.Count);
        foreach (var word in GoalReadinessPreflight.HighRiskSignalWords) Assert.Contains(word, lintLine);
        string[] guidance =
        [
            "Say the same thing in other words, and describe a file in prose instead of citing its path when the path contains one of those words.",
            "Cite every file by its full repository-relative path on every mention, never by bare file name, and cite a line range as path:start-end.",
            "In the Measured premise section put only repository files in code spans; name runtime files and stores in plain text."
        ];
        Assert.Equal(guidance, lines.Skip(ownerIndex + 2 + ownerLines.Length).Take(guidance.Length));
        foreach (var line in ownerLines.Concat([lintLine]).Concat(guidance)) Assert.Contains(line, lines);
    }
}
